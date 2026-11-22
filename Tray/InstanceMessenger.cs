using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AorusMonitor.Tray;

/// <summary>
/// Ventana invisible que recibe el aviso «muéstrate» de una segunda copia del programa
/// (por ejemplo, al abrir el acceso directo con la app ya en la bandeja).
/// </summary>
public sealed class InstanceMessenger : IDisposable
{
    private const uint MsgfltAllow = 1;
    private static readonly IntPtr HwndBroadcast = new(0xffff);
    public static readonly int ShowMessage = RegisterWindowMessage("AorusMonitor-Mostrar-4C21");

    private readonly HwndSource _window;

    public event Action? ShowRequested;

    public InstanceMessenger()
    {
        _window = new HwndSource(new HwndSourceParameters("AorusMonitor-Mensajes") { WindowStyle = 0, Width = 0, Height = 0 });
        _window.AddHook(Hook);
        ChangeWindowMessageFilterEx(_window.Handle, (uint)ShowMessage, MsgfltAllow, IntPtr.Zero);
    }

    public static void RequestShow() => PostMessage(HwndBroadcast, ShowMessage, IntPtr.Zero, IntPtr.Zero);

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == ShowMessage)
        {
            ShowRequested?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _window.RemoveHook(Hook);
        _window.Dispose();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr change);
}
