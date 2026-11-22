using AorusMonitor.Hardware;

namespace AorusMonitor.Services;

/// <summary>
/// Núcleo compartido: sensores, ventiladores y teclado. La ventana principal, la miniventana
/// y los iconos de la bandeja leen de aquí y piden acciones aquí.
/// </summary>
public sealed class AppCore : IDisposable
{
    public AppSettings Settings { get; } = AppSettings.Load();
    public GigabyteWmi Wmi { get; }
    public SensorService Sensors { get; }
    public FanController Fans { get; }
    public KeyboardRgb Keyboard { get; } = new();

    public SensorSnapshot? Last { get; private set; }

    /// <summary>Nueva lectura de sensores (hilo de fondo).</summary>
    public event Action<SensorSnapshot>? Updated;
    /// <summary>Cambió el modo de ventilador, el efecto de luces o el estado de los dispositivos.</summary>
    public event Action? StateChanged;
    /// <summary>Mensaje para el usuario (protección térmica, etc.).</summary>
    public event Action<string>? Message;

    private readonly CancellationTokenSource _cts = new();

    public AppCore()
    {
        Wmi = new GigabyteWmi();
        Sensors = new SensorService(Wmi);
        Fans = new FanController(Wmi) { DutyMax = Settings.DutyMax };
        Fans.Message += m => Message?.Invoke(m);
    }

    public async Task StartAsync()
    {
        await Task.Run(() =>
        {
            Sensors.Open();
            Keyboard.Connect();
        });

        if (Settings.ReapplyFanModeOnStart && Fans.CanControl && Settings.FanMode != FanMode.Normal)
            await Task.Run(() => Fans.Apply(Settings.FanMode, Settings.ManualPercent, Settings.Curve));

        StateChanged?.Invoke();
        _ = Task.Run(() => Loop(_cts.Token));
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snap = Sensors.Read();
                Last = snap;
                Fans.Tick(snap);
                Updated?.Invoke(snap);
            }
            catch { /* una lectura fallida no debe parar el bucle */ }

            try { await Task.Delay(1000, ct); } catch (TaskCanceledException) { break; }
        }
    }

    public async Task<bool> ApplyFanAsync(FanMode mode, int? manualPercent = null, IReadOnlyList<CurvePoint>? curve = null)
    {
        if (!Fans.CanControl) return false;
        bool ok = await Task.Run(() => Fans.Apply(mode, manualPercent ?? Settings.ManualPercent, curve ?? Settings.Curve));
        Settings.FanMode = mode;
        Settings.ManualPercent = Fans.ManualPercent;
        Settings.Curve = Fans.Curve.ToList();
        Settings.Save();
        StateChanged?.Invoke();
        return ok;
    }

    public async Task<bool> ApplyRgbAsync(RgbSettings s, bool remember = true)
    {
        bool ok = await Task.Run(() => Keyboard.Apply(s));
        if (ok && remember)
        {
            Settings.RgbEffect = s.EffectCode;
            Settings.RgbColor = s.Color;
            Settings.Color1 = $"#{s.R:X2}{s.G:X2}{s.B:X2}";
            Settings.RgbBrightness = s.Brightness;
            Settings.RgbSpeed = s.Speed;
            Settings.RgbDirection = s.Direction;
            Settings.Save();
        }
        StateChanged?.Invoke();
        return ok;
    }

    /// <summary>Ajustes de luces guardados, con otro efecto y/o brillo.</summary>
    public RgbSettings SavedRgb(byte? effect = null, int? brightness = null)
    {
        var c = System.Windows.Media.ColorConverter.ConvertFromString(Settings.Color1) is System.Windows.Media.Color col
            ? col : System.Windows.Media.Color.FromRgb(0xFF, 0x6A, 0x00);
        return new RgbSettings(effect ?? Settings.RgbEffect, Settings.RgbColor, c.R, c.G, c.B,
            brightness ?? Settings.RgbBrightness, Settings.RgbSpeed, Settings.RgbDirection);
    }

    public void NotifyStateChanged() => StateChanged?.Invoke();

    public void Dispose()
    {
        _cts.Cancel();
        Settings.Save();
        try { Fans.RestoreOnExit(); } catch { }
        Keyboard.Dispose();
        Sensors.Dispose();
    }
}
