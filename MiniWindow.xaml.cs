using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AorusMonitor.Hardware;
using AorusMonitor.Services;

namespace AorusMonitor;

/// <summary>Miniventana flotante, semitransparente y siempre encima con CPU, GPU, ventiladores y memoria.</summary>
public partial class MiniWindow : Window
{
    private readonly AppCore _core;

    public event Action? OpenMainRequested;
    public event Action? CloseRequested;

    private static readonly Brush Normal = new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF7));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20));
    private static readonly Brush Hot = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4D));

    public MiniWindow(AppCore core)
    {
        _core = core;
        InitializeComponent();
        Opacity = Math.Clamp(core.Settings.MiniOpacity, 0.4, 1.0);
        Loaded += (_, _) => Place();
    }

    public void Render(SensorSnapshot s)
    {
        CpuValue.Text = s.CpuTemp.HasValue ? $"{s.CpuTemp:0} °C" : "--";
        CpuValue.Foreground = TempBrush(s.CpuTemp);
        CpuSub.Text = $"{s.CpuLoad:0} %" + (s.CpuClock.HasValue ? $" · {s.CpuClock / 1000:0.0} GHz" : "");

        GpuValue.Text = s.GpuTemp.HasValue ? $"{s.GpuTemp:0} °C" : s.GpuAsleep ? "Reposo" : "--";
        GpuValue.Foreground = TempBrush(s.GpuTemp);
        GpuSub.Text = s.GpuAsleep ? "apagada · 0 W"
            : $"{s.GpuLoad:0} %" + (s.GpuPower.HasValue ? $" · {s.GpuPower:0} W" : "");

        int? rpm = s.CpuFanRpm.HasValue || s.GpuFanRpm.HasValue ? Math.Max(s.CpuFanRpm ?? 0, s.GpuFanRpm ?? 0) : null;
        FanValue.Text = rpm?.ToString() ?? "--";
        FanSub.Text = s.CpuFanRpm.HasValue ? $"RPM · {s.CpuFanRpm}/{s.GpuFanRpm}" : "RPM";

        RamValue.Text = s.RamLoad.HasValue ? $"{s.RamLoad:0} %" : "--";
        RamSub.Text = s.RamUsed.HasValue ? $"{s.RamUsed:0.0} / {s.RamTotal:0} GB" : "";

        ModeText.Text = _core.Fans.CanControl
            ? (_core.Fans.EmergencyActive ? "¡PROTECCIÓN!" : ModeName(_core.Fans.Mode))
            : "";
    }

    private static string ModeName(FanMode m) => m switch
    {
        FanMode.Silencioso => "Silencioso",
        FanMode.Gaming => "Gaming",
        FanMode.Turbo => "Turbo",
        FanMode.Manual => "Manual",
        FanMode.Curva => "Curva",
        _ => "Normal",
    };

    private static Brush TempBrush(float? t) => t switch
    {
        >= 90 => Hot,
        >= 80 => Warn,
        _ => Normal,
    };

    /// <summary>Donde se dejó o, la primera vez, arriba a la derecha. Nunca fuera de la pantalla.</summary>
    private void Place()
    {
        var st = _core.Settings;
        double left = SystemParameters.VirtualScreenLeft, top = SystemParameters.VirtualScreenTop;
        double right = left + SystemParameters.VirtualScreenWidth, bottom = top + SystemParameters.VirtualScreenHeight;
        bool saved = !double.IsNaN(st.MiniX) && !double.IsNaN(st.MiniY) &&
                     st.MiniX >= left && st.MiniX + 60 <= right && st.MiniY >= top && st.MiniY + 40 <= bottom;
        if (saved)
        {
            Left = st.MiniX;
            Top = st.MiniY;
        }
        else
        {
            var work = SystemParameters.WorkArea;
            Left = work.Right - ActualWidth - 16;
            Top = work.Top + 16;
        }
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == CloseX) return;
        if (e.ClickCount == 2)
        {
            OpenMainRequested?.Invoke();
            return;
        }
        DragMove();
        _core.Settings.MiniX = Left;
        _core.Settings.MiniY = Top;
        _core.Settings.Save();
    }

    private void Open_Click(object sender, RoutedEventArgs e) => OpenMainRequested?.Invoke();
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    private void CloseX_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => CloseRequested?.Invoke();

    private void Opacity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && double.TryParse(tag, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var op))
        {
            Opacity = op;
            _core.Settings.MiniOpacity = op;
            _core.Settings.Save();
        }
    }
}
