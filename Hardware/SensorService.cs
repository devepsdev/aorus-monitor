using LibreHardwareMonitor.Hardware;

namespace AorusMonitor.Hardware;

public sealed record DiskTemp(string Name, float Temp);

/// <summary>Lectura de todos los sensores en un instante.</summary>
public sealed record SensorSnapshot
{
    public float? CpuTemp { get; init; }
    public float? CpuLoad { get; init; }
    public float? CpuClock { get; init; }
    public float? CpuPower { get; init; }
    public float? CpuCoreMax { get; init; }

    public float? GpuTemp { get; init; }
    public float? GpuHotSpot { get; init; }
    public float? GpuLoad { get; init; }
    public float? GpuClock { get; init; }
    public float? GpuMemClock { get; init; }
    public float? GpuPower { get; init; }
    public float? GpuVramUsed { get; init; }
    public float? GpuVramTotal { get; init; }

    public float? RamUsed { get; init; }
    public float? RamTotal { get; init; }
    public float? RamLoad { get; init; }

    public float? BatteryLevel { get; init; }
    public float? BatteryRate { get; init; }   // W, positivo = cargando, negativo = descargando
    public bool? OnAcPower { get; init; }

    public int? CpuFanRpm { get; init; }
    public int? GpuFanRpm { get; init; }

    public IReadOnlyList<DiskTemp> Disks { get; init; } = Array.Empty<DiskTemp>();

    /// <summary>La GPU NVIDIA está apagada (D3) para ahorrar energía; no se leen sus sensores.</summary>
    public bool GpuAsleep { get; init; }

    /// <summary>Temperatura más alta entre CPU y GPU (lo que manda en la curva de ventiladores).</summary>
    public float? HottestTemp =>
        CpuTemp is null && GpuTemp is null ? null : Math.Max(CpuTemp ?? 0, GpuTemp ?? 0);
}

/// <summary>
/// Recoge sensores de LibreHardwareMonitor (CPU, GPU NVIDIA, RAM, batería, discos) y
/// de la WMI de Gigabyte (RPM de ventiladores y temperaturas del EC como respaldo).
/// </summary>
public sealed class SensorService : IDisposable
{
    private readonly Computer _computer;
    private readonly GigabyteWmi _wmi;
    private readonly object _lock = new();
    private readonly GpuPowerState _gpuPower = new();
    private DateTime _lastGpuRead = DateTime.MinValue;
    private float _lastGpuLoad;
    private bool _opened;

    public string LhmStatus { get; private set; } = "Iniciando sensores…";

    public SensorService(GigabyteWmi wmi)
    {
        _wmi = wmi;
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsBatteryEnabled = true,
            IsStorageEnabled = true,
        };
    }

    public void Open()
    {
        try
        {
            _computer.Open();
            _opened = true;
            LhmStatus = "Sensores activos.";
        }
        catch (Exception ex)
        {
            LhmStatus = "LibreHardwareMonitor no pudo iniciarse: " + ex.Message;
        }
    }

    public SensorSnapshot Read()
    {
        lock (_lock)
        {
            // La NVIDIA solo se lee si ya está encendida: leerla la despierta y la mantiene a ~25 W.
            // Encendida pero sin carga, se lee cada 15 s para dejar que vuelva a apagarse.
            bool gpuAwake = _gpuPower.IsAwake();
            bool readGpu = gpuAwake &&
                           (_lastGpuLoad > 5 || DateTime.UtcNow - _lastGpuRead >= TimeSpan.FromSeconds(15));

            var hw = new List<IHardware>();
            if (_opened)
            {
                foreach (var h in _computer.Hardware)
                {
                    bool isNvidia = h.HardwareType == HardwareType.GpuNvidia;
                    if (isNvidia && !gpuAwake) continue;
                    try
                    {
                        if (!isNvidia || readGpu) UpdateRecursive(h);
                        hw.Add(h);
                    }
                    catch { /* sensor puntual caído */ }
                }
            }

            var cpu = hw.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            var gpu = hw.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia)
                      ?? hw.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd);
            var mem = hw.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);
            var bat = hw.FirstOrDefault(h => h.HardwareType == HardwareType.Battery);

            float? cpuTemp = Value(cpu, SensorType.Temperature, "CPU Package", "Core Max", "Core Average");
            float? gpuTemp = Value(gpu, SensorType.Temperature, "GPU Core");

            // Respaldo: temperaturas del EC vía WMI de Gigabyte (no necesitan driver de bajo nivel).
            int? ecCpu = _wmi.IsAvailable ? _wmi.Get("getCpuTemp") : null;
            if (cpuTemp is null && ecCpu is > 0 and < 120) cpuTemp = ecCpu;
            if (readGpu)
            {
                _lastGpuRead = DateTime.UtcNow;
                _lastGpuLoad = Value(gpu, SensorType.Load, "GPU Core") ?? 0;
            }
            if (gpuTemp is null && gpuAwake && _wmi.IsAvailable)
            {
                int g = Math.Max(_wmi.Get("getGpuTemp1") ?? 0, _wmi.Get("getGpuTemp2") ?? 0);
                if (g is > 0 and < 120) gpuTemp = g;
            }

            float? vramUsed = Value(gpu, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
            float? vramTotal = Value(gpu, SensorType.SmallData, "GPU Memory Total");

            float? ramUsed = Value(mem, SensorType.Data, "Memory Used");
            float? ramAvail = Value(mem, SensorType.Data, "Memory Available");

            float? charge = Value(bat, SensorType.Power, "Charge Rate");
            float? discharge = Value(bat, SensorType.Power, "Discharge Rate");
            float? rate = charge is > 0 ? charge : discharge is > 0 ? -discharge : charge ?? (discharge.HasValue ? -discharge : null);

            var disks = hw.Where(h => h.HardwareType == HardwareType.Storage)
                .Select(h => (h.Name, T: Value(h, SensorType.Temperature, "Temperature", "Composite Temperature")
                                         ?? h.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature)?.Value))
                .Where(d => d.T.HasValue)
                .Select(d => new DiskTemp(d.Name, d.T!.Value))
                .ToList();

            // El EC devuelve las RPM con los bytes invertidos (big-endian).
            int? rpm1 = SwapBytes(_wmi.IsAvailable ? _wmi.Get("getRpm1") : null);
            int? rpm2 = SwapBytes(_wmi.IsAvailable ? _wmi.Get("getRpm2") : null);

            float? cpuClock = Average(cpu, SensorType.Clock, "Core #") ?? CounterClock();
            float? cpuPower = Value(cpu, SensorType.Power, "CPU Package");
            if (cpuPower is <= 0) cpuPower = null;

            return new SensorSnapshot
            {
                CpuTemp = cpuTemp,
                CpuLoad = Value(cpu, SensorType.Load, "CPU Total"),
                CpuClock = cpuClock,
                CpuPower = cpuPower,
                CpuCoreMax = Value(cpu, SensorType.Temperature, "Core Max"),

                GpuTemp = gpuTemp,
                GpuHotSpot = Value(gpu, SensorType.Temperature, "GPU Hot Spot"),
                GpuLoad = Value(gpu, SensorType.Load, "GPU Core"),
                GpuClock = Value(gpu, SensorType.Clock, "GPU Core"),
                GpuMemClock = Value(gpu, SensorType.Clock, "GPU Memory"),
                GpuPower = Value(gpu, SensorType.Power, "GPU Package", "GPU Power"),
                GpuVramUsed = vramUsed,
                GpuVramTotal = vramTotal,

                RamUsed = ramUsed,
                RamTotal = ramUsed + ramAvail,
                RamLoad = Value(mem, SensorType.Load, "Memory"),

                BatteryLevel = Value(bat, SensorType.Level, "Charge Level"),
                BatteryRate = rate,
                OnAcPower = ReadAcLine(),

                CpuFanRpm = rpm1 is >= 0 and < 20000 ? rpm1 : null,
                GpuFanRpm = rpm2 is >= 0 and < 20000 ? rpm2 : null,
                Disks = disks,
                GpuAsleep = !gpuAwake,
            };
        }
    }

    private static int? SwapBytes(int? v) => v is null ? null : ((v.Value & 0xFF) << 8) | ((v.Value >> 8) & 0xFF);

    // Frecuencia de CPU sin driver: % de rendimiento del procesador × frecuencia base.
    private System.Diagnostics.PerformanceCounter? _perfCounter;
    private bool _perfTried;
    private float _baseMhz;

    private float? CounterClock()
    {
        if (!_perfTried)
        {
            _perfTried = true;
            foreach (var (cat, name) in new[]
                     {
                         ("Processor Information", "% Processor Performance"),
                         ("Información del procesador", "% de rendimiento del procesador"),
                     })
            {
                try
                {
                    _perfCounter = new System.Diagnostics.PerformanceCounter(cat, name, "_Total", true);
                    _perfCounter.NextValue();
                    break;
                }
                catch { _perfCounter = null; }
            }
            try
            {
                using var s = new System.Management.ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor");
                foreach (var o in s.Get()) { _baseMhz = Convert.ToSingle(o["MaxClockSpeed"]); break; }
            }
            catch { }
        }
        if (_perfCounter == null || _baseMhz <= 0) return null;
        try
        {
            float pct = _perfCounter.NextValue();
            return pct > 0 ? pct / 100f * _baseMhz : null;
        }
        catch { return null; }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    private static bool? ReadAcLine() =>
        GetSystemPowerStatus(out var st) ? st.ACLineStatus switch { 1 => true, 0 => false, _ => null } : null;

    private static void UpdateRecursive(IHardware h)
    {
        h.Update();
        foreach (var sub in h.SubHardware) UpdateRecursive(sub);
    }

    private static IEnumerable<ISensor> AllSensors(IHardware h) =>
        h.Sensors.Concat(h.SubHardware.SelectMany(AllSensors));

    private static float? Value(IHardware? h, SensorType type, params string[] names)
    {
        if (h == null) return null;
        var sensors = AllSensors(h).Where(s => s.SensorType == type).ToList();
        foreach (var name in names)
        {
            var s = sensors.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && x.Value.HasValue);
            if (s != null) return s.Value;
        }
        return null;
    }

    private static float? Average(IHardware? h, SensorType type, string prefix)
    {
        if (h == null) return null;
        var values = AllSensors(h)
            .Where(s => s.SensorType == type && s.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase) && s.Value is > 0)
            .Select(s => s.Value!.Value)
            .ToList();
        return values.Count == 0 ? null : values.Average();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_opened) { try { _computer.Close(); } catch { } }
            _opened = false;
        }
    }
}
