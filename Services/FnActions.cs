using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Windows.Devices.Radios;

namespace AorusMonitor.Services;

/// <summary>Acciones de las teclas Fn: brillo, Wi‑Fi, apagar pantalla y touchpad.</summary>
public static class FnActions
{
    // ------------------------------------------------------------------ brillo

    public static int? GetBrightness()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE");
            foreach (var o in s.Get()) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return null;
    }

    /// <summary>Cambia el brillo de la pantalla interna en pasos de 10 % y devuelve el nuevo valor.</summary>
    public static int? StepBrightness(int delta)
    {
        int? current = GetBrightness();
        if (current is null) return null;
        int target = Math.Clamp((int)Math.Round((current.Value + delta) / 10.0) * 10, 0, 100);
        try
        {
            using var cls = new ManagementClass(@"root\wmi", "WmiMonitorBrightnessMethods", null);
            foreach (ManagementObject o in cls.GetInstances())
            {
                o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)target });
                return target;
            }
        }
        catch { }
        return null;
    }

    // ------------------------------------------------------------------ Wi‑Fi

    /// <summary>Enciende o apaga la radio Wi‑Fi (igual que el ajuste rápido de Windows). Devuelve el nuevo estado.</summary>
    public static async Task<bool?> ToggleWifiAsync()
    {
        try
        {
            await Radio.RequestAccessAsync();
            var radios = await Radio.GetRadiosAsync();
            var wifi = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
            if (wifi is null) return null;
            var target = wifi.State == RadioState.On ? RadioState.Off : RadioState.On;
            var result = await wifi.SetStateAsync(target);
            return result == RadioAccessStatus.Allowed ? target == RadioState.On : null;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ pantalla

    /// <summary>
    /// Apaga la pantalla; se vuelve a encender al tocar una tecla o el ratón.
    /// La orden va a una única ventana propia: mandarla a todas (HWND_BROADCAST) hace que
    /// la pantalla se apague y encienda sola de forma errática.
    /// </summary>
    public static async Task ScreenOffAsync(IntPtr ownWindow)
    {
        await Task.Delay(700); // que al soltar la tecla no se vuelva a encender
        PostMessage(ownWindow, 0x0112 /* WM_SYSCOMMAND */, new IntPtr(0xF170 /* SC_MONITORPOWER */), new IntPtr(2));
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    // ------------------------------------------------------------------ touchpad

    private static (string Id, bool Enabled)? FindTouchpad()
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass='HIDClass'");
            foreach (var o in s.Get())
            {
                string name = o["Name"]?.ToString() ?? "";
                if (name.Contains("táctil", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("touch pad", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("touchpad", StringComparison.OrdinalIgnoreCase))
                {
                    // ConfigManagerErrorCode 22 = deshabilitado
                    return (o["PNPDeviceID"]!.ToString()!, Convert.ToInt32(o["ConfigManagerErrorCode"]) != 22);
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Activa o desactiva el touchpad. Devuelve el nuevo estado (true = activo).</summary>
    public static async Task<bool?> ToggleTouchpadAsync()
    {
        var tp = FindTouchpad();
        if (tp is null) return null;
        string verb = tp.Value.Enabled ? "/disable-device" : "/enable-device";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("pnputil.exe", $"{verb} \"{tp.Value.Id}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            });
            if (p is null) return null;
            await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
        }
        catch
        {
            return null;
        }
        return FindTouchpad()?.Enabled;
    }
}
