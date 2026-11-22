using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AorusMonitor.Hardware;

namespace AorusMonitor.Controls;

/// <summary>Editor de curva de ventilador: arrastra los puntos (temperatura × velocidad).</summary>
public sealed class CurveEditor : FrameworkElement
{
    private const double TMin = 30, TMax = 100;
    private const double PadL = 40, PadB = 26, PadT = 10, PadR = 12;

    private List<CurvePoint> _points = FanController.DefaultCurve.ToList();
    private int _drag = -1;
    private int _hover = -1;

    public double? LiveTemp { get; private set; }

    public event Action? CurveChanged;

    public IReadOnlyList<CurvePoint> Points
    {
        get => _points;
        set { _points = value.OrderBy(p => p.Temp).ToList(); InvalidateVisual(); }
    }

    public void SetLiveTemp(double? t) { LiveTemp = t; InvalidateVisual(); }

    public CurveEditor()
    {
        Cursor = Cursors.Arrow;
        Focusable = false;
    }

    private Rect Plot => new(PadL, PadT, Math.Max(1, ActualWidth - PadL - PadR), Math.Max(1, ActualHeight - PadT - PadB));

    private Point ToScreen(double temp, double pct)
    {
        var r = Plot;
        return new Point(r.Left + (temp - TMin) / (TMax - TMin) * r.Width, r.Bottom - pct / 100 * r.Height);
    }

    private (int Temp, int Pct) FromScreen(Point p)
    {
        var r = Plot;
        double t = TMin + (p.X - r.Left) / r.Width * (TMax - TMin);
        double pct = (r.Bottom - p.Y) / r.Height * 100;
        return ((int)Math.Round(t), (int)Math.Round(pct));
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < _points.Count; i++)
            if ((ToScreen(_points[i].Temp, _points[i].Percent) - p).Length < 11) return i;
        return -1;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _drag = HitTest(e.GetPosition(this));
        if (_drag >= 0) CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (_drag >= 0)
        {
            var (t, pct) = FromScreen(pos);
            int minT = _drag > 0 ? _points[_drag - 1].Temp + 2 : (int)TMin;
            int maxT = _drag < _points.Count - 1 ? _points[_drag + 1].Temp - 2 : (int)TMax;
            t = Math.Clamp(t, minT, maxT);
            pct = Math.Clamp(pct, FanController.MinPercent, 100);
            _points[_drag] = new CurvePoint(t, pct);
            InvalidateVisual();
            CurveChanged?.Invoke();
        }
        else
        {
            int h = HitTest(pos);
            if (h != _hover) { _hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Arrow; InvalidateVisual(); }
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag >= 0)
        {
            _drag = -1;
            ReleaseMouseCapture();
            Points = FanController.SanitizeCurve(_points);
            CurveChanged?.Invoke();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var r = Plot;
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), 1);
        var muted = new SolidColorBrush(Color.FromRgb(0x8B, 0x91, 0xA1));
        var accent = new SolidColorBrush(Color.FromRgb(0xFF, 0x6A, 0x00));
        var typeface = new Typeface("Segoe UI");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        void Label(string s, Point p, bool alignRight = false, bool center = false)
        {
            var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, muted, dpi);
            double x = alignRight ? p.X - ft.Width : center ? p.X - ft.Width / 2 : p.X;
            dc.DrawText(ft, new Point(x, p.Y - ft.Height / 2));
        }

        for (int pct = 0; pct <= 100; pct += 25)
        {
            var a = ToScreen(TMin, pct);
            dc.DrawLine(gridPen, a, ToScreen(TMax, pct));
            Label($"{pct}%", new Point(r.Left - 6, a.Y), alignRight: true);
        }
        for (int t = 30; t <= 100; t += 10)
        {
            var a = ToScreen(t, 0);
            dc.DrawLine(gridPen, a, ToScreen(t, 100));
            Label($"{t}°", new Point(a.X, r.Bottom + 13), center: true);
        }

        // Zona de peligro
        var danger = new SolidColorBrush(Color.FromArgb(22, 255, 77, 77));
        var d0 = ToScreen(90, 100);
        dc.DrawRectangle(danger, null, new Rect(d0, ToScreen(TMax, 0)));

        if (_points.Count > 0)
        {
            var geo = new StreamGeometry();
            var area = new StreamGeometry();
            var first = ToScreen(TMin, _points[0].Percent);
            var last = ToScreen(TMax, _points[^1].Percent);
            using (var g = geo.Open())
            {
                g.BeginFigure(first, false, false);
                foreach (var p in _points) g.LineTo(ToScreen(p.Temp, p.Percent), true, true);
                g.LineTo(last, true, true);
            }
            using (var g = area.Open())
            {
                g.BeginFigure(ToScreen(TMin, 0), true, true);
                g.LineTo(first, false, false);
                foreach (var p in _points) g.LineTo(ToScreen(p.Temp, p.Percent), false, false);
                g.LineTo(last, false, false);
                g.LineTo(ToScreen(TMax, 0), false, false);
            }
            dc.DrawGeometry(new LinearGradientBrush(Color.FromArgb(70, 255, 106, 0), Color.FromArgb(0, 255, 106, 0), 90), null, area);
            dc.DrawGeometry(null, new Pen(accent, 2.2) { LineJoin = PenLineJoin.Round }, geo);

            for (int i = 0; i < _points.Count; i++)
            {
                var c = ToScreen(_points[i].Temp, _points[i].Percent);
                double rad = i == _drag || i == _hover ? 8 : 6;
                dc.DrawEllipse(Brushes.White, new Pen(accent, 3), c, rad, rad);
                if (i == _drag || i == _hover)
                    Label($"{_points[i].Temp}° · {_points[i].Percent}%", new Point(c.X + 12, c.Y - 14));
            }
        }

        if (LiveTemp is double lt && lt >= TMin && lt <= TMax)
        {
            var a = ToScreen(lt, 0);
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0xA0, 0xFF)), 1.5) { DashStyle = DashStyles.Dash };
            dc.DrawLine(pen, a, ToScreen(lt, 100));
            int pct = FanController.Evaluate(_points, (float)lt);
            var dot = ToScreen(lt, pct);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x3A, 0xA0, 0xFF)), null, dot, 5, 5);
            Label($"{lt:0}° → {pct}%", new Point(dot.X + 8, r.Top + 8));
        }
    }
}
