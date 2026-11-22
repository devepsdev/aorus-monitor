using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AorusMonitor.Hardware;
using AorusMonitor.Services;
using AorusMonitor.Tray;

namespace AorusMonitor;

/// <summary>
/// Arranque y coordinación de ventana principal, miniventana e iconos de la bandeja.
/// </summary>
/// <remarks>
/// Argumentos:
///   --minimizado          arranca solo en la bandeja (lo usa el inicio con Windows)
///   --arranque si|no      crea o borra la tarea de inicio con Windows y sale (instalador)
///   --restaurar           devuelve los ventiladores a Normal y sale (desinstalador)
///   --probe archivo       diagnóstico de solo lectura
///   --snapshot carpeta    capturas PNG de la interfaz
///   --fan-test / --rgb-test archivo   pruebas reversibles de hardware
/// </remarks>
public partial class App : Application
{
    private const string MutexName = @"Local\AorusMonitor-Instancia-4C21";

    private string[] _args = [];
    private Mutex? _instance;
    private AppCore? _core;
    private MainWindow? _main;
    private MiniWindow? _mini;
    private TrayIcons? _tray;
    private InstanceMessenger? _messenger;
    private FnKeyListener? _fnKeys;
    private OsdWindow? _osd;
    private FanMode _modeBeforeFanMax = FanMode.Normal;
    private DateTime _lastScreenOff = DateTime.MinValue;
    private bool _startup;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _args = e.Args;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, ex) => ex.Handled = true;

        // ---- modos sin interfaz
        if (Value("--arranque") is { } arr)
        {
            Shutdown(SystemIntegration.SetStartup(arr == "si") ? 0 : 1);
            return;
        }
        if (Value("--tarea-abrir") is { } ta)
        {
            Shutdown(SystemIntegration.SetLaunchTask(ta == "si") ? 0 : 1);
            return;
        }
        if (Has("--restaurar"))
        {
            await Task.Run(() => new FanController(new GigabyteWmi()).Apply(FanMode.Normal));
            Shutdown();
            return;
        }
        foreach (var (flag, job) in new (string, Func<string>)[] { ("--probe", Probe), ("--rgb-test", RgbTest), ("--fan-test", FanTest) })
        {
            if (Value(flag) is { } file)
            {
                File.WriteAllText(file, await Task.Run(job));
                Shutdown();
                return;
            }
        }

        // ---- instancia única: si ya está abierta, se le pide que se muestre
        if (!TakeInstance())
        {
            InstanceMessenger.RequestShow();
            Shutdown();
            return;
        }

        // ---- siempre como administrador: primero la tarea elevada (sin aviso), si no, UAC
        if (!SystemIntegration.IsAdmin && !Has("--sin-elevar") && Value("--snapshot") is null)
        {
            ReleaseInstance();
            if (SystemIntegration.RunLaunchTask() || TryRunas())
            {
                Shutdown();
                return;
            }
            if (!TakeInstance()) { Shutdown(); return; }
        }

        await StartAsync();

        if (Value("--snapshot") is { } dir)
        {
            await Task.Delay(4000);
            await _main!.SaveSnapshotsAsync(dir);
            _mini!.Show();
            await Task.Delay(1500);
            SaveElement((FrameworkElement)_mini.Content, Path.Combine(dir, "5-mini.png"));
            TrayIcons.SaveSample(Path.Combine(dir, "bandeja-cpu.png"), "58", TrayIcons.Device.Cpu, 58);
            TrayIcons.SaveSample(Path.Combine(dir, "bandeja-gpu.png"), "47", TrayIcons.Device.Gpu, 47);
            TrayIcons.SaveSample(Path.Combine(dir, "bandeja-atencion.png"), "84", TrayIcons.Device.Cpu, 84);
            TrayIcons.SaveSample(Path.Combine(dir, "bandeja-alto.png"), "96", TrayIcons.Device.Cpu, 96);
            ExitApp();
        }
    }

    private async Task StartAsync()
    {
        _core = new AppCore();
        _main = new MainWindow(_core);
        _mini = new MiniWindow(_core);
        _startup = SystemIntegration.StartupEnabled();

        _tray = new TrayIcons(LoadAppIcon());
        _tray.ActionRequested += OnTrayAction;
        _tray.FanModeSelected += async m => await _core.ApplyFanAsync(m);
        _tray.RgbEffectSelected += async code => await _core.ApplyRgbAsync(_core.SavedRgb(effect: code));
        _tray.RgbBrightnessSelected += async b => await _core.ApplyRgbAsync(_core.SavedRgb(brightness: b));

        _messenger = new InstanceMessenger();
        _messenger.ShowRequested += ShowMain;

        _osd = new OsdWindow();
        _fnKeys = new FnKeyListener();
        _fnKeys.FnPressed += code => Dispatcher.BeginInvoke(() => OnFnKey(code));

        _main.MiniRequested += ToggleMini;
        _main.StartupToggled += SetStartup;
        _main.HiddenToTray += () =>
        {
            if (_core.Settings.TrayHintShown) return;
            _core.Settings.TrayHintShown = true;
            _core.Settings.Save();
            _tray?.Notify("AORUS Monitor sigue aquí", "Sigue funcionando junto al reloj. Para cerrarlo del todo: botón derecho → Salir.");
        };
        _mini.OpenMainRequested += ShowMain;
        _mini.CloseRequested += ToggleMini;

        _core.Updated += s => Dispatcher.BeginInvoke(() =>
        {
            if (_exiting) return;
            _tray?.Update(s, _core.Fans.Mode);
            if (_mini?.IsVisible == true) _mini.Render(s);
        });
        _core.StateChanged += () => Dispatcher.BeginInvoke(RefreshTray);
        _core.Message += m => Dispatcher.BeginInvoke(() => _tray?.Notify("Protección térmica", m, warning: true));
        SessionEnding += (_, _) => ExitApp();

        if (!Has("--minimizado")) _main.ShowFront();
        if (_core.Settings.MiniVisible) _mini.Show();

        await _core.StartAsync();
        RefreshTray();
    }

    private void OnTrayAction(TrayAction a)
    {
        switch (a)
        {
            case TrayAction.OpenMain: ShowMain(); break;
            case TrayAction.ToggleMini: ToggleMini(); break;
            case TrayAction.ToggleStartup: SetStartup(!_startup); break;
            case TrayAction.Exit: ExitApp(); break;
            case TrayAction.LightsSaved: _ = _core!.ApplyRgbAsync(_core.SavedRgb()); break;
            case TrayAction.LightsOff: _ = _core!.ApplyRgbAsync(_core.SavedRgb(effect: 1, brightness: 0), remember: false); break;
        }
    }

    /// <summary>Teclas Fn de Gigabyte que Windows no entiende por sí solo.</summary>
    private async void OnFnKey(byte code)
    {
        if (_osd is null || _core is null) return;
        switch (code)
        {
            case FnKeyListener.BrightnessDown:
            case FnKeyListener.BrightnessUp:
            {
                int delta = code == FnKeyListener.BrightnessUp ? 10 : -10;
                int? b = await Task.Run(() => FnActions.StepBrightness(delta));
                if (b.HasValue) _osd.Flash("", $"Brillo {b} %", b);
                break;
            }
            case FnKeyListener.WifiToggle:
            {
                bool? on = await FnActions.ToggleWifiAsync();
                _osd.Flash("", on switch { true => "Wi‑Fi activado", false => "Wi‑Fi desactivado", _ => "No se pudo cambiar el Wi‑Fi" });
                break;
            }
            case FnKeyListener.ScreenOff:
                // Una sola orden aunque la tecla se pulse varias veces seguidas
                if (DateTime.UtcNow - _lastScreenOff < TimeSpan.FromSeconds(3)) break;
                _lastScreenOff = DateTime.UtcNow;
                await FnActions.ScreenOffAsync(_fnKeys!.Handle);
                break;
            case FnKeyListener.Mute:
            case FnKeyListener.VolumeDown:
            case FnKeyListener.VolumeUp:
                break; // ya los gestiona Windows
            case FnKeyListener.TouchpadToggle:
            {
                bool? on = await FnActions.ToggleTouchpadAsync();
                _osd.Flash("", on switch { true => "Touchpad activado", false => "Touchpad desactivado", _ => "No se pudo cambiar el touchpad" });
                break;
            }
            case FnKeyListener.FanMax:
            {
                if (!_core.Fans.CanControl) break;
                bool toTurbo = _core.Fans.Mode != FanMode.Turbo;
                if (toTurbo) _modeBeforeFanMax = _core.Fans.Mode;
                var mode = toTurbo ? FanMode.Turbo : (_modeBeforeFanMax == FanMode.Turbo ? FanMode.Normal : _modeBeforeFanMax);
                await _core.ApplyFanAsync(mode);
                _osd.Flash("", toTurbo ? "Ventiladores al máximo" : $"Ventiladores: {mode}");
                break;
            }
            default:
                break; // tecla sin función: queda apuntada en teclas-fn.log
        }
    }

    private void ShowMain() => _main?.ShowFront();

    private void ToggleMini()
    {
        if (_mini is null || _core is null) return;
        if (_mini.IsVisible) _mini.Hide();
        else
        {
            _mini.Show();
            if (_core.Last is { } s) _mini.Render(s);
        }
        _core.Settings.MiniVisible = _mini.IsVisible;
        _core.Settings.Save();
        RefreshTray();
    }

    private void SetStartup(bool enable)
    {
        bool ok = SystemIntegration.SetStartup(enable);
        _startup = SystemIntegration.StartupEnabled();
        _main?.SetStartupChecked(_startup);
        if (!ok)
            MessageBox.Show("No se ha podido cambiar el inicio con Windows. Hace falta abrir AORUS Monitor como administrador.",
                "AORUS Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshTray();
    }

    private void RefreshTray()
    {
        if (_tray is null || _core is null) return;
        _tray.SetState(_mini?.IsVisible == true, _startup, SystemIntegration.IsAdmin,
            _core.Fans.Mode, _core.Fans.CanControl, _core.Keyboard.Current?.Type, _core.Keyboard.IsConnected);
    }

    public void RestartElevated()
    {
        ReleaseInstance();
        if (SystemIntegration.RunLaunchTask() || TryRunas())
            ExitApp();
        else
            TakeInstance(); // canceló el aviso de Windows
    }

    private bool TryRunas()
    {
        try
        {
            var psi = new ProcessStartInfo(SystemIntegration.ExePath) { UseShellExecute = true, Verb = "runas" };
            foreach (var a in _args) psi.ArgumentList.Add(a);
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _core?.Settings.Save();
        _tray?.Dispose();
        _messenger?.Dispose();
        _fnKeys?.Dispose();
        _osd?.Close();
        _mini?.Close();
        if (_main != null) { _main.AllowClose = true; _main.Close(); }
        _core?.Dispose();
        ReleaseInstance();
        Shutdown();
    }

    // ---------------------------------------------------------------- utilidades

    private bool Has(string arg) => _args.Contains(arg, StringComparer.OrdinalIgnoreCase);

    private string? Value(string arg)
    {
        int n = Array.FindIndex(_args, a => a.Equals(arg, StringComparison.OrdinalIgnoreCase));
        return n >= 0 && n + 1 < _args.Length ? _args[n + 1] : null;
    }

    private bool TakeInstance()
    {
        try
        {
            _instance = new Mutex(true, MutexName, out bool created);
            if (created) return true;
            _instance.Dispose();
            _instance = null;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void ReleaseInstance()
    {
        try { _instance?.ReleaseMutex(); } catch { }
        _instance?.Dispose();
        _instance = null;
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        var res = GetResourceStream(new Uri("pack://application:,,,/Assets/icono.ico"));
        using var stream = res!.Stream;
        return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
    }

    private static void SaveElement(FrameworkElement e, string path)
    {
        var rect = new Rect(0, 0, e.ActualWidth, e.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Fondo de escritorio simulado para ver la transparencia.
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x2B, 0x5D, 0x8C), Color.FromRgb(0x6A, 0x3D, 0x7A), 45), null, rect);
            dc.DrawRectangle(new VisualBrush(e) { Opacity = ((Window)e.Parent).Opacity }, null, rect);
        }
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(rect.Width), (int)Math.Ceiling(rect.Height), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        png.Save(f);
    }

    // ---------------------------------------------------------------- diagnóstico

    private static string FanTest()
    {
        var sb = new StringBuilder();
        var wmi = new GigabyteWmi();
        if (!wmi.CanWrite) return "Sin escritura WMI: " + wmi.Status;
        int Rpm(string m) { int v = wmi.Get(m) ?? 0; return ((v & 0xFF) << 8) | ((v >> 8) & 0xFF); }
        string State() =>
            $"RPM {Rpm("getRpm1")}/{Rpm("getRpm2")} fixed={wmi.Get("GetFixedFanStatus")} step={wmi.Get("GetStepFanStatus")} " +
            $"auto={wmi.Get("GetAutoFanStatus")} speed={wmi.Get("GetFixedFanSpeed")} cpu={wmi.Get("getCpuTemp")}°C";

        var fans = new FanController(wmi);
        sb.AppendLine("Antes:  " + State());
        sb.AppendLine($"Turbo aplicado={fans.Apply(FanMode.Turbo)}");
        for (int i = 0; i < 4; i++) { Thread.Sleep(2000); sb.AppendLine($"  +{(i + 1) * 2}s: " + State()); }
        sb.AppendLine($"Normal aplicado={fans.Apply(FanMode.Normal)}");
        Thread.Sleep(4000);
        sb.AppendLine("Después: " + State());
        return sb.ToString();
    }

    private static string RgbTest()
    {
        var sb = new StringBuilder();
        using var kb = new KeyboardRgb();
        if (!kb.Connect() || kb.Current is not { } original)
            return "Sin conexión: " + kb.Status;
        sb.AppendLine($"Original: {original}");
        bool ok = kb.Apply(new RgbSettings(8, KbColor.Aleatorio, 0, 0, 0, 80, 60, 0));
        sb.AppendLine($"Neón aplicado={ok} leído={kb.Current}");
        Thread.Sleep(3000);
        ok = kb.RestoreRaw(original);
        sb.AppendLine($"Restaurado={ok} leído={kb.Current}");
        return sb.ToString();
    }

    private static string Probe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Administrador: {SystemIntegration.IsAdmin}  PawnIO: {SystemIntegration.PawnIoInstalled()}  Inicio: {SystemIntegration.StartupEnabled()}");

        var wmi = new GigabyteWmi();
        sb.AppendLine($"WMI: {wmi.Status}");
        if (wmi.IsAvailable)
            foreach (var m in new[] { "getCpuTemp", "getGpuTemp1", "getGpuTemp2", "GetFixedFanStatus", "GetStepFanStatus", "GetAutoFanStatus", "GetFixedFanSpeed" })
                sb.AppendLine($"  {m} = {wmi.Get(m)?.ToString() ?? "null"}");

        using (var kb = new KeyboardRgb())
        {
            kb.Connect();
            sb.AppendLine($"Teclado: {kb.Status} {kb.DeviceDescription} actual={kb.Current}");
        }

        using var sensors = new SensorService(wmi);
        sensors.Open();
        sensors.Read();
        Thread.Sleep(1200);
        var s = sensors.Read();
        sb.AppendLine($"Sensores: {sensors.LhmStatus}");
        foreach (var p in typeof(SensorSnapshot).GetProperties())
        {
            var v = p.GetValue(s);
            sb.AppendLine($"  {p.Name} = {(v is System.Collections.IEnumerable en and not string ? string.Join(", ", en.Cast<object>()) : v)}");
        }
        return sb.ToString();
    }
}
