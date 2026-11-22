using System.Management;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using AorusMonitor.Hardware;
using AorusMonitor.Services;

namespace AorusMonitor;

public partial class MainWindow : Window
{
    private readonly AppCore _core;
    private AppSettings _settings => _core.Settings;
    private GigabyteWmi _wmi => _core.Wmi;
    private SensorService _sensors => _core.Sensors;
    private FanController _fans => _core.Fans;
    private KeyboardRgb _kb => _core.Keyboard;

    /// <summary>Lo pone App al salir de verdad (si no, cerrar la ventana la esconde en la bandeja).</summary>
    public bool AllowClose { get; set; }
    public event Action? MiniRequested;
    public event Action? HiddenToTray;
    public event Action<bool>? StartupToggled;

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private bool _loadingUi = true;
    private bool _updatingColor;
    private Action? _bannerAction;

    private static readonly (string Name, string Hex)[] Presets =
    {
        ("Naranja AORUS", "#FF6A00"), ("Rojo", "#FF0000"), ("Ámbar", "#FFB000"), ("Amarillo", "#FFE600"),
        ("Verde", "#00FF40"), ("Cian", "#00E5FF"), ("Azul", "#0050FF"), ("Morado", "#8A2BE2"),
        ("Rosa", "#FF1493"), ("Blanco", "#FFFFFF"),
    };

    public MainWindow(AppCore core)
    {
        _core = core;
        InitializeComponent();
        _core.Message += msg => Dispatcher.BeginInvoke(() => ShowToast(msg));
        _core.Updated += snap => Dispatcher.BeginInvoke(() => { if (IsVisible) Render(snap); });
        _core.StateChanged += () => Dispatcher.BeginInvoke(RefreshState);
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };

        SourceInitialized += (_, _) => UseDarkTitleBar();
        Loaded += (_, _) => RefreshState();
        // Minimizar se comporta como siempre (queda en la barra de tareas); solo cerrar la esconde en la bandeja.
        Closing += OnClosing;

        InitFanUi();
        InitRgbUi();
        InitSystemUi();
        _loadingUi = false;
    }

    /// <summary>Muestra la ventana y la trae al frente.</summary>
    public void ShowFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        if (_core.Last is { } s) Render(s);
    }

    private void HideToTray()
    {
        Hide();
        WindowState = WindowState.Normal;
        HiddenToTray?.Invoke();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (AllowClose || !_settings.CloseToTray) return;
        e.Cancel = true;
        HideToTray();
    }

    private void RefreshState()
    {
        CheckMode(_fans.Mode);
        UpdateStatusIndicators();
    }

    private void Mini_Click(object sender, RoutedEventArgs e) => MiniRequested?.Invoke();

    // ------------------------------------------------------------------ panel

    private static string F(float? v, string fmt, string unit = "") => v.HasValue ? v.Value.ToString(fmt) + unit : "--";

    private Brush TempBrush(float? t) => t switch
    {
        null => (Brush)FindResource("Text"),
        >= 90 => (Brush)FindResource("Hot"),
        >= 78 => (Brush)FindResource("Warn"),
        _ => (Brush)FindResource("Text"),
    };

    private void Render(SensorSnapshot s)
    {
        CpuTemp.Text = F(s.CpuTemp, "0");
        CpuTemp.Foreground = TempBrush(s.CpuTemp);
        CpuLoad.Text = F(s.CpuLoad, "0", " %");
        CpuClock.Text = s.CpuClock.HasValue ? $"{s.CpuClock / 1000:0.00} GHz" : "--";
        CpuPower.Text = F(s.CpuPower, "0.0", " W");
        CpuSpark.Push(s.CpuTemp);

        GpuTemp.Text = F(s.GpuTemp, "0");
        GpuTemp.Foreground = TempBrush(s.GpuTemp);
        GpuHot.Text = s.GpuAsleep ? "en reposo · apagada para ahorrar energía"
            : s.GpuHotSpot.HasValue ? $"punto caliente {s.GpuHotSpot:0} °C" : "";
        GpuLoad.Text = F(s.GpuLoad, "0", " %");
        GpuClock.Text = s.GpuClock.HasValue ? $"{s.GpuClock:0} / {F(s.GpuMemClock, "0")} MHz" : "--";
        GpuPower.Text = F(s.GpuPower, "0.0", " W");
        GpuVram.Text = s.GpuVramUsed.HasValue
            ? $"{s.GpuVramUsed / 1024:0.0} / {(s.GpuVramTotal ?? 8192) / 1024:0} GB" : "--";
        GpuSpark.Push(s.GpuTemp);

        FanCpu.Text = s.CpuFanRpm?.ToString() ?? "--";
        FanGpu.Text = s.GpuFanRpm?.ToString() ?? "--";
        FanModeSmall.Text = _fans.CanControl ? ModeName(_fans.Mode) + (_fans.EmergencyActive ? " · ¡PROTECCIÓN!" : "") : "";
        FanHint.Text = _wmi.IsAvailable ? "" : "Registra la interfaz WMI (Sistema) para ver RPM y controlar los ventiladores.";
        FanSpark.Push(s.CpuFanRpm.HasValue || s.GpuFanRpm.HasValue ? Math.Max(s.CpuFanRpm ?? 0, s.GpuFanRpm ?? 0) : null);

        RamPct.Text = F(s.RamLoad, "0");
        RamText.Text = s.RamUsed.HasValue ? $"{s.RamUsed:0.0} GB usados de {s.RamTotal:0.0} GB" : "--";
        SetBar(RamBar, s.RamLoad);

        BatPct.Text = F(s.BatteryLevel, "0");
        SetBar(BatBar, s.BatteryLevel);
        string state = s.OnAcPower == true ? "Conectado a la corriente" : s.OnAcPower == false ? "Con batería" : "";
        string rate = s.BatteryRate is float r && Math.Abs(r) > 0.1
            ? (r > 0 ? $" · cargando a {r:0.0} W" : $" · consumo {-r:0.0} W") : "";
        BatText.Text = state + rate;

        var disks = s.Disks.Select(d => new KeyValuePair<string, string>(d.Name, $"{d.Temp:0} °C")).ToList();
        DiskList.ItemsSource = disks;
        DiskEmpty.Visibility = disks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Página de ventiladores
        FansLiveTemp.Text = s.HottestTemp.HasValue ? $"{s.HottestTemp:0} °C" : "--";
        FansLiveTemp.Foreground = TempBrush(s.HottestTemp);
        FansLiveRpm.Text = $"{s.CpuFanRpm?.ToString() ?? "--"} / {s.GpuFanRpm?.ToString() ?? "--"}";
        Curve.SetLiveTemp(s.HottestTemp);

        PanelSubtitle.Text = s.CpuTemp is null && s.CpuLoad is null
            ? "Esperando sensores…"
            : "Lecturas en tiempo real cada segundo";
    }

    private static void SetBar(Border bar, float? pct)
    {
        if (bar.Parent is Grid g && g.ActualWidth > 0)
            bar.Width = g.ActualWidth * Math.Clamp((pct ?? 0) / 100.0, 0, 1);
    }

    // ------------------------------------------------------------------ navegación / estado

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PagePanel == null) return;
        PagePanel.Visibility = NavPanel.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageFans.Visibility = NavFans.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageRgb.Visibility = NavRgb.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSys.Visibility = NavSys.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStatusIndicators()
    {
        var good = (Brush)FindResource("Good");
        var warn = (Brush)FindResource("Warn");
        var bad = (Brush)FindResource("Hot");

        bool admin = SystemIntegration.IsAdmin;
        DotAdmin.Fill = admin ? good : warn;
        AdminText.Text = admin ? "Administrador" : "Sin administrador";
        DotWmi.Fill = _wmi.CanWrite ? good : _wmi.IsAvailable ? warn : bad;
        DotKb.Fill = _kb.IsConnected ? good : bad;

        WmiStatusText.Text = _wmi.Status;
        bool pawn = SystemIntegration.PawnIoInstalled();
        SensorStatusText.Text = "Sensores: " + _sensors.LhmStatus +
            (admin ? "" : " Sin administrador no hay temperaturas ni consumo de CPU.") +
            (pawn ? " Driver PawnIO instalado." : " Falta el driver PawnIO (frecuencia y consumo de CPU).");
        PawnIoButton.Visibility = pawn ? Visibility.Collapsed : Visibility.Visible;
        StartupCheck.IsEnabled = admin;
        KbStatusText.Text = "Teclado: " + _kb.Status + (_kb.DeviceDescription is { } d ? $" ({d})" : "");
        RgbStatus.Text = _kb.Status;

        bool canFan = _fans.CanControl;
        ModeGrid.IsEnabled = canFan;
        ManualApply.IsEnabled = canFan;
        CurveApply.IsEnabled = canFan;
        if (!canFan) FanStatus.Text = "Control no disponible: " + _wmi.Status;

        if (!admin)
            ShowBanner("La app no tiene permisos de administrador: sin control de ventiladores ni temperaturas de CPU.",
                       "Reiniciar como administrador", () => ((App)Application.Current).RestartElevated());
        else if (!_wmi.IsAvailable)
            ShowBanner("La interfaz de ventiladores no está registrada. Vuelve a ejecutar el instalador de AORUS Monitor para repararla.");
        else
            Banner.Visibility = Visibility.Collapsed;
    }

    private void ShowBanner(string text, string? button = null, Action? action = null)
    {
        BannerText.Text = text;
        BannerButton.Content = button;
        BannerButton.Visibility = button is null ? Visibility.Collapsed : Visibility.Visible;
        _bannerAction = action;
        Banner.Visibility = Visibility.Visible;
    }

    private void BannerButton_Click(object sender, RoutedEventArgs e) => _bannerAction?.Invoke();

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ------------------------------------------------------------------ ventiladores

    private static string ModeName(FanMode m) => m switch
    {
        FanMode.Silencioso => "Silencioso",
        FanMode.Gaming => "Gaming",
        FanMode.Turbo => "Turbo",
        FanMode.Manual => "Manual",
        FanMode.Curva => "Curva",
        _ => "Normal",
    };

    private void InitFanUi()
    {
        ManualSlider.Value = _settings.ManualPercent;
        ManualValue.Text = _settings.ManualPercent.ToString();
        Curve.Points = FanController.SanitizeCurve(_settings.Curve);
        CheckMode(_settings.FanMode);
    }

    private void CheckMode(FanMode mode)
    {
        foreach (var rb in ModeGrid.Children.OfType<RadioButton>())
            rb.IsChecked = (string)rb.Tag == mode.ToString();
    }

    private async Task ApplyFan(FanMode mode)
    {
        if (!_fans.CanControl) return;
        int manual = (int)ManualSlider.Value;
        var curve = Curve.Points.ToList();
        FanStatus.Text = "Aplicando…";
        bool ok = await _core.ApplyFanAsync(mode, manual, curve);
        CheckMode(mode);
        FanStatus.Text = ok ? $"Modo {ModeName(mode)} aplicado." : "El firmware rechazó alguna orden. Revisa la interfaz WMI.";
    }

    private async void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && Enum.TryParse<FanMode>((string)rb.Tag, out var mode))
            await ApplyFan(mode);
    }

    private void ManualSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ManualValue != null) ManualValue.Text = ((int)e.NewValue).ToString();
    }

    private async void ManualApply_Click(object sender, RoutedEventArgs e) => await ApplyFan(FanMode.Manual);
    private async void CurveApply_Click(object sender, RoutedEventArgs e) => await ApplyFan(FanMode.Curva);

    private void CurveReset_Click(object sender, RoutedEventArgs e) => Curve.Points = FanController.DefaultCurve;

    // ------------------------------------------------------------------ iluminación

    private static readonly Dictionary<KbColor, string> PresetHex = new()
    {
        [KbColor.Rojo] = "#FF0000", [KbColor.Verde] = "#00FF40", [KbColor.Amarillo] = "#FFE600",
        [KbColor.Azul] = "#0050FF", [KbColor.Naranja] = "#FF6A00", [KbColor.Morado] = "#8A2BE2",
        [KbColor.Blanco] = "#FFFFFF",
    };

    private KbColor _presetColor;

    private void InitRgbUi()
    {
        EffectList.ItemsSource = KeyboardRgb.Effects;
        BuildSwatches(Swatches1, hex => SetColor1(hex));
        BuildPresetButtons();

        SetColor1(_settings.Color1);
        BrightSlider.Value = _settings.RgbBrightness;
        SpeedSlider.Value = _settings.RgbSpeed;
        EffectList.SelectedItem = KeyboardRgb.Effects.FirstOrDefault(x => x.Code == _settings.RgbEffect) ?? KeyboardRgb.Effects[3];
    }

    private void BuildPresetButtons()
    {
        _presetColor = _settings.RgbColor;
        foreach (KbColor color in Enum.GetValues<KbColor>())
        {
            var dot = new Ellipse { Width = 12, Height = 12, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.Fill = PresetHex.TryGetValue(color, out var hex)
                ? new SolidColorBrush(ParseColor(hex))
                : RainbowBrush(new Point(0, 0), new Point(1, 1));
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(dot);
            content.Children.Add(new TextBlock { Text = color == KbColor.Aleatorio ? "Aleatorio" : color.ToString() });

            var rb = new RadioButton
            {
                Content = content, GroupName = "Preset", Style = (Style)FindResource("Segment"),
                IsChecked = color == _presetColor, Tag = color,
            };
            rb.Checked += (_, _) => { _presetColor = color; UpdatePreview(); };
            PresetButtons.Children.Add(rb);
        }
    }

    private static LinearGradientBrush RainbowBrush(Point start, Point end)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        string[] hues = { "#FF0000", "#FF8000", "#FFE600", "#00FF40", "#00E5FF", "#0050FF", "#8A2BE2", "#FF1493" };
        for (int i = 0; i < hues.Length; i++)
            brush.GradientStops.Add(new GradientStop(ParseColor(hues[i]), i / (double)(hues.Length - 1)));
        return brush;
    }

    private void BuildSwatches(WrapPanel panel, Action<string> onPick)
    {
        foreach (var (name, hex) in Presets)
        {
            var b = new Button
            {
                Width = 30, Height = 30, Padding = new Thickness(0), Margin = new Thickness(0, 0, 8, 8),
                ToolTip = name, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
                Content = new Ellipse
                {
                    Width = 24, Height = 24, Fill = new SolidColorBrush(ParseColor(hex)),
                    Stroke = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), StrokeThickness = 1,
                },
            };
            b.Click += (_, _) => onPick(hex);
            panel.Children.Add(b);
        }
    }

    private static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex.StartsWith('#') ? hex : "#" + hex); }
        catch { return Color.FromRgb(0xFF, 0x6A, 0x00); }
    }

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private Color Color1 => Color.FromRgb((byte)R1.Value, (byte)G1.Value, (byte)B1.Value);

    private void SetColor1(string hex)
    {
        var c = ParseColor(hex);
        _updatingColor = true;
        R1.Value = c.R; G1.Value = c.G; B1.Value = c.B;
        Hex1.Text = ToHex(c);
        _updatingColor = false;
        UpdatePreview();
    }

    private void Color_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingColor || Hex1 == null) return;
        Hex1.Text = ToHex(Color1);
        UpdatePreview();
    }

    private void Hex_LostFocus(object sender, RoutedEventArgs e) => SetColor1(Hex1.Text);

    private void Hex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Hex_LostFocus(sender, e);
    }

    private RgbEffect SelectedEffect => EffectList.SelectedItem as RgbEffect ?? KeyboardRgb.Effects[0];

    private void EffectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var fx = SelectedEffect;
        PresetPanel.Visibility = fx.UsesColor ? Visibility.Visible : Visibility.Collapsed;
        ColorPanel.Visibility = fx.IsCustomRgb ? Visibility.Visible : Visibility.Collapsed;
        SpeedPanel.Visibility = fx.UsesSpeed ? Visibility.Visible : Visibility.Hidden;
        DirPanel.Visibility = fx.UsesDirection ? Visibility.Visible : Visibility.Collapsed;

        if (DirButtons.Children.Count == 0)
        {
            for (int i = 0; i < KeyboardRgb.DirectionNames.Length; i++)
            {
                int idx = i;
                var rb = new RadioButton
                {
                    Content = KeyboardRgb.DirectionNames[i], GroupName = "Dir", Style = (Style)FindResource("Segment"),
                    IsChecked = i == _settings.RgbDirection,
                };
                rb.Checked += (_, _) => _settings.RgbDirection = idx;
                DirButtons.Children.Add(rb);
            }
        }
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (PreviewBrush == null || EffectList.SelectedItem == null) return;
        var fx = SelectedEffect;
        PreviewText.Text = fx.Name;
        PreviewBrush.GradientStops.Clear();

        bool rainbow = (!fx.UsesColor && !fx.IsCustomRgb) || (fx.UsesColor && _presetColor == KbColor.Aleatorio);
        if (rainbow)
        {
            foreach (var stop in RainbowBrush(new Point(0, 0), new Point(1, 0)).GradientStops)
                PreviewBrush.GradientStops.Add(stop.Clone());
            return;
        }

        var c = fx.IsCustomRgb ? Color1 : ParseColor(PresetHex.GetValueOrDefault(_presetColor, "#FF6A00"));
        PreviewBrush.GradientStops.Add(new GradientStop(c, 0));
        PreviewBrush.GradientStops.Add(new GradientStop(c, 1));
    }

    private RgbSettings CurrentRgb(int? brightness = null)
    {
        var c1 = Color1;
        return new RgbSettings(SelectedEffect.Code, _presetColor, c1.R, c1.G, c1.B,
            brightness ?? (int)BrightSlider.Value, (int)SpeedSlider.Value, _settings.RgbDirection);
    }

    private async void RgbApply_Click(object sender, RoutedEventArgs e)
    {
        var s = CurrentRgb();
        RgbApply.IsEnabled = false;
        await _core.ApplyRgbAsync(s);
        RgbApply.IsEnabled = true;
    }

    private async void RgbOff_Click(object sender, RoutedEventArgs e)
    {
        var s = CurrentRgb(brightness: 0) with { EffectCode = 1 };
        await _core.ApplyRgbAsync(s, remember: false);
    }

    private async void RgbReconnect_Click(object sender, RoutedEventArgs e)
    {
        await Task.Run(() => _kb.Connect());
        UpdateStatusIndicators();
    }

    // ------------------------------------------------------------------ sistema

    private void InitSystemUi()
    {
        ReapplyCheck.IsChecked = _settings.ReapplyFanModeOnStart;
        TrayCheck.IsChecked = _settings.CloseToTray;
        StartupCheck.IsChecked = SystemIntegration.StartupEnabled();
        DutyMaxBox.Text = _settings.DutyMax.ToString();
        SysInfo.ItemsSource = ReadSystemInfo();
    }

    private static List<KeyValuePair<string, string>> ReadSystemInfo()
    {
        var list = new List<KeyValuePair<string, string>>();
        void Add(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) list.Add(new(k, v.Trim())); }
        string? Q(string cls, string prop)
        {
            try
            {
                using var s = new ManagementObjectSearcher($"SELECT {prop} FROM {cls}");
                return string.Join(" · ", s.Get().Cast<ManagementBaseObject>().Select(o => o[prop]?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)));
            }
            catch { return null; }
        }
        Add("Modelo", $"{Q("Win32_ComputerSystem", "Manufacturer")} {Q("Win32_ComputerSystem", "Model")}");
        Add("BIOS", Q("Win32_BIOS", "SMBIOSBIOSVersion"));
        Add("Procesador", Q("Win32_Processor", "Name"));
        Add("Gráficas", Q("Win32_VideoController", "Name"));
        if (double.TryParse(Q("Win32_ComputerSystem", "TotalPhysicalMemory"), out var ram))
            Add("Memoria", $"{ram / 1024 / 1024 / 1024:0.0} GB");
        Add("Windows", Q("Win32_OperatingSystem", "Caption"));
        return list;
    }

    private void ReapplyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        _settings.ReapplyFanModeOnStart = ReapplyCheck.IsChecked == true;
        _settings.Save();
    }

    private void TrayCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        _settings.CloseToTray = TrayCheck.IsChecked == true;
        _settings.Save();
    }

    private void StartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        StartupToggled?.Invoke(StartupCheck.IsChecked == true);
    }

    /// <summary>Lo llama App después de cambiar la tarea de inicio.</summary>
    public void SetStartupChecked(bool on)
    {
        _loadingUi = true;
        StartupCheck.IsChecked = on;
        _loadingUi = false;
    }

    private void PawnIo_Click(object sender, RoutedEventArgs e)
    {
        SystemIntegration.InstallPawnIo();
        ShowToast("Instalando PawnIO con winget. Cuando termine, reinicia AORUS Monitor.");
    }

    private void DutyMax_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(DutyMaxBox.Text, out var v) && v is >= 100 and <= 255)
        {
            _settings.DutyMax = v;
            _fans.DutyMax = v;
            _settings.Save();
        }
        DutyMaxBox.Text = _settings.DutyMax.ToString();
    }

    // ------------------------------------------------------------------ utilidades

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void UseDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
        }
        catch { }
    }

    /// <summary>Renderiza cada página a PNG (para comprobar la interfaz sin abrirla a mano).</summary>
    public async Task SaveSnapshotsAsync(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        var pages = new[] { (NavPanel, "1-panel"), (NavFans, "2-ventiladores"), (NavRgb, "3-iluminacion"), (NavSys, "4-sistema") };
        foreach (var (nav, name) in pages)
        {
            nav.IsChecked = true;
            await Task.Delay(400);
            UpdateLayout();
            var root = (FrameworkElement)Content;
            var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = System.IO.File.Create(System.IO.Path.Combine(dir, name + ".png"));
            enc.Save(fs);
        }
    }
}
