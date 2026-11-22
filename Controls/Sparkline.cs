using System.Windows;
using System.Windows.Media;

namespace AorusMonitor.Controls;

/// <summary>Mini-gráfica de historial (últimos N valores) con relleno degradado.</summary>
public sealed class Sparkline : FrameworkElement
{
    private readonly Queue<double> _values = new();

    public int Capacity { get; set; } = 90;
    public double Min { get; set; } = 0;
    public double Max { get; set; } = 100;

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public void Push(double? value)
    {
        if (value is null || double.IsNaN(value.Value)) return;
        _values.Enqueue(value.Value);
        while (_values.Count > Capacity) _values.Dequeue();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var grid = new Pen(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), 1);
        for (int i = 1; i < 4; i++) dc.DrawLine(grid, new Point(0, h * i / 4), new Point(w, h * i / 4));

        if (_values.Count < 2) return;
        var vals = _values.ToArray();
        double step = w / (Capacity - 1);
        double x0 = w - (vals.Length - 1) * step;
        Point P(int i) => new(x0 + i * step, h - (Math.Clamp(vals[i], Min, Max) - Min) / (Max - Min) * (h - 2) - 1);

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(P(0), false, false);
            for (int i = 1; i < vals.Length; i++) g.LineTo(P(i), true, true);
        }
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(x0, h), true, true);
            for (int i = 0; i < vals.Length; i++) g.LineTo(P(i), false, false);
            g.LineTo(new Point(w, h), false, false);
        }

        var color = (Stroke as SolidColorBrush)?.Color ?? Colors.Orange;
        var fill = new LinearGradientBrush(Color.FromArgb(90, color.R, color.G, color.B),
                                           Color.FromArgb(0, color.R, color.G, color.B), 90);
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(Stroke, 1.8) { LineJoin = PenLineJoin.Round }, line);
    }
}
