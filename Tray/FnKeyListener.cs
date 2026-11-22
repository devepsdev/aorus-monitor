using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AorusMonitor.Tray;

/// <summary>
/// Escucha las teclas Fn propias de Gigabyte. El teclado (USB 1044:7A3x) las manda por colecciones
/// HID de fabricante como un informe de 4 bytes «04 00 00 XX», donde XX es la tecla.
/// Sin Gigabyte Control Center nadie las atiende; aquí se reciben con Raw Input.
/// </summary>
public sealed class FnKeyListener : IDisposable
{
    public const byte WifiToggle = 0x7C;
    public const byte BrightnessDown = 0x7D;
    public const byte BrightnessUp = 0x7E;
    public const byte ScreenOff = 0x80;
    public const byte TouchpadToggle = 0x81;
    public const byte FanMax = 0x84;
    // Silencio, bajar y subir volumen: el teclado los manda también como teclas multimedia
    // estándar y Windows ya los atiende; aquí llega solo una copia que se ignora.
    public const byte Mute = 0x85;
    public const byte VolumeDown = 0x86;
    public const byte VolumeUp = 0x87;

    /// <summary>Ventana oculta propia (para mandarle órdenes como apagar la pantalla).</summary>
    public IntPtr Handle => _window.Handle;

    private const int WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeHid = 2;
    private const uint RidevInputSink = 0x00000100;
    private static readonly int HeaderSize = Marshal.SizeOf<RawInputHeader>();

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AorusMonitor", "teclas-fn.log");

    private readonly HwndSource _window;

    public bool Registered { get; }
    public event Action<byte>? FnPressed;

    public FnKeyListener()
    {
        _window = new HwndSource(new HwndSourceParameters("AorusMonitor-TeclasFn") { WindowStyle = 0, Width = 0, Height = 0 });
        _window.AddHook(Hook);

        var devices = new[]
        {
            new RawInputDevice { UsagePage = 0xFF00, Usage = 0xFF00, Flags = RidevInputSink, Target = _window.Handle },
            new RawInputDevice { UsagePage = 0xFF01, Usage = 0x2209, Flags = RidevInputSink, Target = _window.Handle },
            new RawInputDevice { UsagePage = 0xFF02, Usage = 0x0001, Flags = RidevInputSink, Target = _window.Handle },
        };
        Registered = RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>());
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmInput) return IntPtr.Zero;
        try
        {
            uint size = 0;
            GetRawInputData(lParam, RidInput, null, ref size, (uint)HeaderSize);
            if (size == 0 || size > 4096) return IntPtr.Zero;
            var buffer = new byte[size];
            if (GetRawInputData(lParam, RidInput, buffer, ref size, (uint)HeaderSize) != size) return IntPtr.Zero;

            if (BitConverter.ToUInt32(buffer, 0) != RimTypeHid) return IntPtr.Zero;
            int sizeHid = BitConverter.ToInt32(buffer, HeaderSize);
            int count = BitConverter.ToInt32(buffer, HeaderSize + 4);
            int offset = HeaderSize + 8;
            for (int i = 0; i < count && offset + sizeHid <= buffer.Length; i++, offset += sizeHid)
            {
                var report = buffer.AsSpan(offset, sizeHid);
                Log(report);
                if (sizeHid == 4 && report[0] == 0x04 && report[1] == 0 && report[2] == 0)
                    FnPressed?.Invoke(report[3]);
            }
        }
        catch { /* un informe malformado no debe tumbar la app */ }
        return IntPtr.Zero;
    }

    private static void Log(ReadOnlySpan<byte> report)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 256 * 1024) File.Delete(LogPath);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {Convert.ToHexString(report)}{Environment.NewLine}");
        }
        catch { }
    }

    public void Dispose()
    {
        _window.RemoveHook(Hook);
        _window.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, byte[]? data, ref uint size, uint headerSize);
}
