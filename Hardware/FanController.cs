namespace AorusMonitor.Hardware;

public enum FanMode
{
    Normal,      // curva por defecto del EC (BIOS)
    Silencioso,  // EC en modo super silencioso
    Gaming,      // EC con gobernador automático (sube antes)
    Turbo,       // ventiladores al máximo
    Manual,      // velocidad fija elegida por el usuario
    Curva,       // curva personalizada calculada por la app
}

public sealed record CurvePoint(int Temp, int Percent);

/// <summary>
/// Control de ventiladores vía GB_WMIACPI_Set. Las secuencias siguen las que usan
/// proyectos probados en el AORUS 15G (alfc) y otros modelos AORUS/AERO.
/// </summary>
public sealed class FanController
{
    public const int MinPercent = 20;
    public const int WatchdogTemp = 95;
    public const int WatchdogRelease = 80;

    private readonly GigabyteWmi _wmi;
    private readonly object _lock = new();

    private int _lastWrittenPercent = -1;
    private int _pendingUp, _pendingDown;
    private DateTime _lastInit = DateTime.MinValue;
    private int _hotSeconds;

    public FanMode Mode { get; private set; } = FanMode.Normal;
    public int ManualPercent { get; private set; } = 50;
    public IReadOnlyList<CurvePoint> Curve { get; private set; } = DefaultCurve;

    /// <summary>Valor de registro que equivale al 100 % (229 en el AORUS 15G).</summary>
    public int DutyMax { get; set; } = 229;

    /// <summary>Vigilante térmico activo (forzó Turbo por temperatura).</summary>
    public bool EmergencyActive { get; private set; }
    public int CurrentCurvePercent => _lastWrittenPercent;

    public event Action<string>? Message;

    public static readonly IReadOnlyList<CurvePoint> DefaultCurve = new[]
    {
        new CurvePoint(40, 25), new CurvePoint(55, 35), new CurvePoint(65, 50),
        new CurvePoint(75, 65), new CurvePoint(85, 85), new CurvePoint(95, 100),
    };

    public FanController(GigabyteWmi wmi) => _wmi = wmi;

    public bool CanControl => _wmi.CanWrite;

    public bool Apply(FanMode mode, int? manualPercent = null, IReadOnlyList<CurvePoint>? curve = null)
    {
        if (!_wmi.CanWrite) return false;
        lock (_lock)
        {
            if (manualPercent.HasValue) ManualPercent = Math.Clamp(manualPercent.Value, MinPercent, 100);
            if (curve != null) Curve = SanitizeCurve(curve);
            Mode = mode;
            EmergencyActive = false;
            _hotSeconds = 0;
            _lastWrittenPercent = -1;
            return ApplyHardware(mode);
        }
    }

    private bool ApplyHardware(FanMode mode)
    {
        bool ok = true;
        switch (mode)
        {
            case FanMode.Normal:
                ok &= Seq(("SetSuperQuiet", 0), ("SetCurrentFanStep", 0), ("SetFixedFanStatus", 0),
                          ("SetStepFanStatus", 0), ("SetAutoFanStatus", 0));
                break;
            case FanMode.Silencioso:
                ok &= Seq(("SetCurrentFanStep", 0), ("SetFixedFanStatus", 0), ("SetStepFanStatus", 0),
                          ("SetAutoFanStatus", 0), ("SetSuperQuiet", 1));
                break;
            case FanMode.Gaming:
                ok &= Seq(("SetSuperQuiet", 0), ("SetCurrentFanStep", 0), ("SetFixedFanStatus", 0),
                          ("SetStepFanStatus", 0), ("SetAutoFanStatus", 1));
                break;
            case FanMode.Turbo:
                ok &= WriteFixed(100);
                break;
            case FanMode.Manual:
                ok &= WriteFixed(ManualPercent);
                break;
            case FanMode.Curva:
                // El primer Tick escribirá la velocidad según la temperatura.
                ok &= WriteFixed(Curve[0].Percent);
                break;
        }
        _lastInit = DateTime.UtcNow;
        return ok;
    }

    /// <summary>Modo fijo: misma velocidad en CPU y GPU (comparten heatpipes).</summary>
    private bool WriteFixed(int percent)
    {
        int duty = ToDuty(percent);
        bool ok = Seq(("SetSuperQuiet", 0), ("SetCurrentFanStep", 0), ("SetAutoFanStatus", 0),
                      ("SetFixedFanSpeed", duty), ("SetGPUFanDuty", duty),
                      ("SetStepFanStatus", 1), ("SetFixedFanStatus", 1));
        if (ok) _lastWrittenPercent = percent;
        return ok;
    }

    private bool WriteSpeedOnly(int percent)
    {
        int duty = ToDuty(percent);
        bool ok = Seq(("SetFixedFanSpeed", duty), ("SetGPUFanDuty", duty));
        if (ok) _lastWrittenPercent = percent;
        return ok;
    }

    private int ToDuty(int percent) =>
        (int)Math.Ceiling(Math.Clamp(percent, 0, 100) / 100.0 * DutyMax);

    private bool Seq(params (string Method, int Value)[] steps)
    {
        bool ok = true;
        foreach (var (method, value) in steps)
        {
            ok &= _wmi.Set(method, value);
            Thread.Sleep(60);
        }
        return ok;
    }

    /// <summary>Se llama una vez por segundo con la lectura de sensores.</summary>
    public void Tick(SensorSnapshot s)
    {
        if (!_wmi.CanWrite) return;
        lock (_lock)
        {
            float? hottest = s.HottestTemp;
            Watchdog(hottest);
            if (EmergencyActive) return;

            if (Mode is FanMode.Manual or FanMode.Turbo or FanMode.Curva &&
                DateTime.UtcNow - _lastInit > TimeSpan.FromMinutes(5))
            {
                // El EC puede volver a su curva tras suspender; reafirmamos el modo.
                _lastWrittenPercent = -1;
                ApplyHardware(Mode);
            }

            if (Mode == FanMode.Curva && hottest.HasValue)
            {
                int target = Evaluate(Curve, hottest.Value);
                if (_lastWrittenPercent < 0) { WriteFixed(target); return; }

                if (target > _lastWrittenPercent)
                {
                    _pendingDown = 0;
                    if (++_pendingUp >= 2) { WriteSpeedOnly(target); _pendingUp = 0; }
                }
                else if (target <= _lastWrittenPercent - 5)
                {
                    _pendingUp = 0;
                    if (++_pendingDown >= 8) { WriteSpeedOnly(target); _pendingDown = 0; }
                }
                else
                {
                    _pendingUp = _pendingDown = 0;
                }
            }
        }
    }

    private void Watchdog(float? hottest)
    {
        if (hottest is null) return;
        // Todos salvo Turbo, que ya va al 100 %. Gaming incluido: la curva automática
        // del EC no está verificada que llegue al máximo.
        bool risky = Mode != FanMode.Turbo;

        if (!EmergencyActive)
        {
            _hotSeconds = risky && hottest >= WatchdogTemp ? _hotSeconds + 1 : 0;
            if (_hotSeconds >= 5)
            {
                EmergencyActive = true;
                WriteFixed(100);
                Message?.Invoke($"Temperatura de {hottest:0} °C: ventiladores al máximo hasta bajar de {WatchdogRelease} °C.");
            }
        }
        else if (hottest <= WatchdogRelease)
        {
            EmergencyActive = false;
            _hotSeconds = 0;
            _lastWrittenPercent = -1;
            ApplyHardware(Mode);
            Message?.Invoke("Temperatura normalizada: vuelvo al modo elegido.");
        }
    }

    /// <summary>Al cerrar: los modos fijos/curva quedan grabados en el EC, así que volvemos a Normal.</summary>
    public void RestoreOnExit()
    {
        if (!_wmi.CanWrite) return;
        lock (_lock)
        {
            if (Mode is FanMode.Manual or FanMode.Curva || EmergencyActive)
                ApplyHardware(FanMode.Normal);
        }
    }

    public static int Evaluate(IReadOnlyList<CurvePoint> curve, float temp)
    {
        if (temp <= curve[0].Temp) return curve[0].Percent;
        for (int i = 1; i < curve.Count; i++)
        {
            var a = curve[i - 1];
            var b = curve[i];
            if (temp <= b.Temp)
            {
                double t = (temp - a.Temp) / Math.Max(1.0, b.Temp - a.Temp);
                return (int)Math.Round(a.Percent + t * (b.Percent - a.Percent));
            }
        }
        return curve[^1].Percent;
    }

    /// <summary>
    /// Ordena la curva, la hace no decreciente y aplica mínimos de seguridad:
    /// ≥20 % siempre, ≥60 % a 80 °C, ≥90 % a 90 °C y 100 % desde 95 °C.
    /// </summary>
    public static IReadOnlyList<CurvePoint> SanitizeCurve(IReadOnlyList<CurvePoint> input)
    {
        var pts = input
            .Select(p => new CurvePoint(Math.Clamp(p.Temp, 30, 100), Math.Clamp(p.Percent, MinPercent, 100)))
            .OrderBy(p => p.Temp)
            .ToList();
        if (pts.Count == 0) return DefaultCurve;

        int prev = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            int floor = pts[i].Temp >= 95 ? 100 : pts[i].Temp >= 90 ? 90 : pts[i].Temp >= 80 ? 60 : MinPercent;
            int pct = Math.Max(Math.Max(pts[i].Percent, prev), floor);
            pts[i] = pts[i] with { Percent = pct };
            prev = pct;
        }
        if (pts[^1].Temp < 95) pts.Add(new CurvePoint(95, 100));
        return pts;
    }
}
