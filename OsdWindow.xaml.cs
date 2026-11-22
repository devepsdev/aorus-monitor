using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace AorusMonitor;

/// <summary>Indicador en pantalla (brillo, Wi‑Fi, touchpad…) que no roba el foco ni recibe clics.</summary>
public partial class OsdWindow : Window
{
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromMilliseconds(1600) };

    public OsdWindow()
    {
        InitializeComponent();
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) => { if (Opacity == 0) Hide(); };
            BeginAnimation(OpacityProperty, fade);
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(hwnd, -20);
            // WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            SetWindowLong(hwnd, -20, ex | 0x20 | 0x80 | 0x08000000);
        };
    }

    public void Flash(string glyph, string text, int? percent = null)
    {
        Glyph.Text = glyph;
        Label.Text = text;
        BarBox.Visibility = percent.HasValue ? Visibility.Visible : Visibility.Collapsed;

        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        if (!IsVisible) Show();
        UpdateLayout();

        if (percent.HasValue)
            Bar.Width = Math.Max(0, (BarBox.ActualWidth) * Math.Clamp(percent.Value, 0, 100) / 100.0);

        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - ActualWidth) / 2;
        Top = work.Bottom - ActualHeight - 48;

        _hide.Stop();
        _hide.Start();
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
