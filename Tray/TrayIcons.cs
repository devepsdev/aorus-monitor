using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AorusMonitor.Hardware;

namespace AorusMonitor.Tray;

public enum TrayAction
{
    OpenMain, ToggleMini, ToggleStartup, Exit, LightsOff, LightsSaved,
}

/// <summary>
/// Iconos junto al reloj con la temperatura de CPU y GPU (fondo naranja/verde, ámbar o rojo según
/// la temperatura) y un menú oscuro con ventiladores, iluminación y opciones.
/// </summary>
public sealed class TrayIcons : IDisposable
{
    private readonly NotifyIcon _cpu = new() { Text = "CPU" };
    private readonly NotifyIcon _gpu = new() { Text = "GPU" };
    private readonly NotifyIcon _app = new() { Text = "AORUS Monitor" };
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _miniItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _fanMenu;
    private readonly ToolStripMenuItem _rgbMenu;
    private readonly Dictionary<NotifyIcon, (string Key, IntPtr Handle)> _current = new();

    public event Action<TrayAction>? ActionRequested;
    public event Action<FanMode>? FanModeSelected;
    public event Action<byte>? RgbEffectSelected;
    public event Action<int>? RgbBrightnessSelected;

    private static readonly Color MenuBack = Color.FromArgb(0x1A, 0x1D, 0x24);
    private static readonly Color MenuHover = Color.FromArgb(0x2C, 0x30, 0x3A);
    private static readonly Color MenuBorder = Color.FromArgb(0x33, 0x38, 0x44);
    private static readonly Color MenuText = Color.FromArgb(0xE9, 0xEB, 0xF1);
    private static readonly Color Accent = Color.FromArgb(0xFF, 0x6A, 0x00);

    public TrayIcons(Icon appIcon)
    {
        _app.Icon = appIcon;

        _menu.Renderer = new DarkRenderer();
        _menu.ShowImageMargin = true;
        _menu.ForeColor = MenuText;
        _menu.BackColor = MenuBack;
        _menu.Font = new Font("Segoe UI", 9.5f);

        var open = Item("Abrir AORUS Monitor", () => ActionRequested?.Invoke(TrayAction.OpenMain));
        open.Font = new Font(_menu.Font, FontStyle.Bold);
        _menu.Items.Add(open);
        _miniItem = Item("Mini monitor", () => ActionRequested?.Invoke(TrayAction.ToggleMini));
        _menu.Items.Add(_miniItem);
        _menu.Items.Add(new ToolStripSeparator());

        _fanMenu = Item("Ventiladores", null);
        foreach (var (mode, name) in new[]
                 {
                     (FanMode.Normal, "Normal"), (FanMode.Silencioso, "Silencioso"), (FanMode.Gaming, "Gaming"),
                     (FanMode.Turbo, "Turbo (100 %)"), (FanMode.Manual, "Manual"), (FanMode.Curva, "Curva personalizada"),
                 })
        {
            var it = Item(name, () => FanModeSelected?.Invoke(mode));
            it.Tag = mode;
            _fanMenu.DropDownItems.Add(it);
        }
        _menu.Items.Add(_fanMenu);

        _rgbMenu = Item("Iluminación", null);
        _rgbMenu.DropDownItems.Add(Item("Mi efecto guardado", () => ActionRequested?.Invoke(TrayAction.LightsSaved)));
        _rgbMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var code in new byte[] { 8, 3, 9, 2, 1, 10, 6 })
        {
            var fx = KeyboardRgb.Effects.First(e => e.Code == code);
            var it = Item(fx.Name, () => RgbEffectSelected?.Invoke(code));
            it.Tag = code;
            _rgbMenu.DropDownItems.Add(it);
        }
        _rgbMenu.DropDownItems.Add(new ToolStripSeparator());
        var bright = Item("Brillo", null);
        foreach (int b in new[] { 25, 50, 75, 100 })
            bright.DropDownItems.Add(Item($"{b} %", () => RgbBrightnessSelected?.Invoke(b)));
        _rgbMenu.DropDownItems.Add(bright);
        _rgbMenu.DropDownItems.Add(Item("Apagar luces", () => ActionRequested?.Invoke(TrayAction.LightsOff)));
        _menu.Items.Add(_rgbMenu);

        _menu.Items.Add(new ToolStripSeparator());
        _startupItem = Item("Iniciar con Windows", () => ActionRequested?.Invoke(TrayAction.ToggleStartup));
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(Item("Salir", () => ActionRequested?.Invoke(TrayAction.Exit)));

        foreach (var icon in new[] { _cpu, _gpu, _app })
        {
            icon.ContextMenuStrip = _menu;
            icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ActionRequested?.Invoke(TrayAction.OpenMain); };
        }
        _app.Visible = true;
    }

    private ToolStripMenuItem Item(string text, System.Action? onClick)
    {
        var it = new ToolStripMenuItem(text) { ForeColor = MenuText, BackColor = MenuBack };
        if (onClick != null) it.Click += (_, _) => onClick();
        it.DropDown.BackColor = MenuBack;
        return it;
    }

    public void SetState(bool miniVisible, bool startup, bool canStartup, FanMode fanMode, bool fanAvailable,
                         byte? rgbEffect, bool rgbAvailable)
    {
        _miniItem.Checked = miniVisible;
        _startupItem.Checked = startup;
        _startupItem.Enabled = canStartup;
        _fanMenu.Enabled = fanAvailable;
        foreach (var it in _fanMenu.DropDownItems.OfType<ToolStripMenuItem>())
            it.Checked = it.Tag is FanMode m && m == fanMode;
        _rgbMenu.Enabled = rgbAvailable;
        foreach (var it in _rgbMenu.DropDownItems.OfType<ToolStripMenuItem>())
            it.Checked = it.Tag is byte c && c == rgbEffect;
    }

    public void Update(SensorSnapshot s, FanMode mode)
    {
        bool showCpu = s.CpuTemp.HasValue, showGpu = s.GpuTemp.HasValue || s.GpuAsleep;
        string fans = s.CpuFanRpm.HasValue ? $"\nVentiladores {s.CpuFanRpm}/{s.GpuFanRpm} RPM · {mode}" : "";

        if (showCpu)
            Paint(_cpu, s.CpuTemp!.Value, Device.Cpu,
                $"CPU {s.CpuTemp:0} °C · carga {s.CpuLoad:0} %" + (s.CpuPower.HasValue ? $" · {s.CpuPower:0} W" : "") + fans);
        if (s.GpuTemp.HasValue)
            Paint(_gpu, s.GpuTemp.Value, Device.Gpu,
                $"GPU {s.GpuTemp:0} °C · carga {s.GpuLoad:0} %" + (s.GpuPower.HasValue ? $" · {s.GpuPower:0} W" : ""));
        else if (s.GpuAsleep)
            Paint(_gpu, -1, Device.Gpu, "GPU en reposo (apagada, 0 W)");

        // Orden en la bandeja: se muestran GPU y luego CPU para que la CPU quede a la izquierda.
        _gpu.Visible = showGpu;
        _cpu.Visible = showCpu;
        _app.Visible = !showCpu && !showGpu;
    }

    public void Notify(string title, string text, bool warning = false)
    {
        var icon = _cpu.Visible ? _cpu : _gpu.Visible ? _gpu : _app;
        icon.ShowBalloonTip(6000, title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    public enum Device { Cpu, Gpu }

    private void Paint(NotifyIcon icon, float value, Device device, string tooltip)
    {
        icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        string text = value < 0 ? "–" : Math.Round(value).ToString("0");
        string key = text + device + Level(value);
        if (_current.TryGetValue(icon, out var prev) && prev.Key == key) return;

        var handle = CreateIcon(text, device, value);
        icon.Icon = Icon.FromHandle(handle);
        if (prev.Handle != IntPtr.Zero) DestroyIcon(prev.Handle);
        _current[icon] = (key, handle);
    }

    private static int Level(float t) => t >= 90 ? 2 : t >= 80 ? 1 : 0;

    /// <summary>Guarda en PNG un icono de muestra (para revisar el diseño).</summary>
    public static void SaveSample(string path, string text, Device device, float value, int size = 32)
    {
        var handle = CreateIcon(text, device, value, size);
        using (var icon = Icon.FromHandle(handle))
        using (var bmp = icon.ToBitmap())
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        DestroyIcon(handle);
    }

    private static IntPtr CreateIcon(string text, Device device, float value, int? forcedSize = null)
    {
        int side = forcedSize ?? Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bmp = new Bitmap(side, side);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        int level = Level(value);
        Color back = value < 0 ? Color.FromArgb(0x4A, 0x4F, 0x5C) : level switch
        {
            2 => Color.FromArgb(0xE5, 0x39, 0x35),
            1 => Color.FromArgb(0xFF, 0xB0, 0x20),
            _ => device == Device.Cpu ? Color.FromArgb(0xFF, 0x6A, 0x00) : Color.FromArgb(0x5E, 0x9E, 0x00),
        };
        using (var brush = new LinearGradientBrush(new Rectangle(0, 0, side, side), ControlPaint.Light(back, 0.15f), back, 90f))
        using (var path = Rounded(new RectangleF(0, 0, side, side), side * 0.22f))
            g.FillPath(brush, path);

        Color fore = level == 1 ? Color.FromArgb(0x3A, 0x22, 0x00) : Color.White;
        float size = side * (text.Length >= 3 ? 0.52f : 0.74f);
        using var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(fore);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, textBrush, new RectangleF(-side * 0.1f, 0, side * 1.2f, side + 1), fmt);
        return bmp.GetHicon();
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        foreach (var icon in new[] { _cpu, _gpu, _app })
        {
            icon.Visible = false;
            icon.Dispose();
        }
        foreach (var (_, h) in _current.Values)
            if (h != IntPtr.Zero) DestroyIcon(h);
        _menu.Dispose();
    }

    // ---------------------------------------------------------------- menú oscuro

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => MenuBack;
        public override Color MenuBorder => TrayIcons.MenuBorder;
        public override Color MenuItemBorder => MenuHover;
        public override Color MenuItemSelected => MenuHover;
        public override Color MenuItemSelectedGradientBegin => MenuHover;
        public override Color MenuItemSelectedGradientEnd => MenuHover;
        public override Color MenuItemPressedGradientBegin => MenuHover;
        public override Color MenuItemPressedGradientEnd => MenuHover;
        public override Color ImageMarginGradientBegin => MenuBack;
        public override Color ImageMarginGradientMiddle => MenuBack;
        public override Color ImageMarginGradientEnd => MenuBack;
        public override Color SeparatorDark => TrayIcons.MenuBorder;
        public override Color SeparatorLight => TrayIcons.MenuBorder;
        public override Color CheckBackground => Color.FromArgb(0x55, 0xFF, 0x6A, 0x00);
        public override Color CheckSelectedBackground => Color.FromArgb(0x77, 0xFF, 0x6A, 0x00);
        public override Color CheckPressedBackground => Color.FromArgb(0x77, 0xFF, 0x6A, 0x00);
    }

    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? MenuText : Color.FromArgb(0x6B, 0x70, 0x7C);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = MenuText;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = new Rectangle(e.ImageRectangle.X + 2, e.ImageRectangle.Y + 2, e.ImageRectangle.Width - 4, e.ImageRectangle.Height - 4);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(Accent);
            float d = Math.Min(r.Width, r.Height) * 0.5f;
            e.Graphics.FillEllipse(b, r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
        }
    }
}
