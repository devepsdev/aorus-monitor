using HidSharp;

namespace AorusMonitor.Hardware;

/// <summary>Colores predefinidos del firmware del teclado (efectos de hardware).</summary>
public enum KbColor : byte
{
    Rojo = 1, Verde = 2, Amarillo = 3, Azul = 4, Naranja = 5, Morado = 6, Blanco = 7, Aleatorio = 8,
}

public sealed record RgbEffect(byte Code, string Name, bool UsesColor, bool UsesSpeed, bool UsesDirection, bool IsCustomRgb = false)
{
    public override string ToString() => Name;
}

public sealed record RgbSettings(
    byte EffectCode, KbColor Color, byte R, byte G, byte B, int Brightness, int Speed, int Direction);

/// <summary>
/// Teclado RGB de los AORUS/AERO con controlador ITE 829x (USB 1044:7A3x).
/// Interfaz vendor (usage page 0xFF01) con feature reports de 9 bytes y checksum,
/// y output reports de 65 bytes para la imagen por tecla. Protocolo documentado por
/// AeroCtl (gitlab.com/wtwrp/aeroctl).
/// </summary>
public sealed class KeyboardRgb : IDisposable
{
    public const int VendorId = 0x1044;
    private static readonly int[] KnownProducts = { 0x7A38, 0x7A39, 0x7A3A, 0x7A3B, 0x7A3C, 0x7A3D, 0x7A3E, 0x7A3F };

    private const byte CmdFirmware = 0x80;
    private const byte CmdSetEffect = 0x08;
    private const byte CmdGetEffect = 0x88;
    private const byte CmdSetImage = 0x12;
    private const byte CustomSlot0 = 51;
    public const int DeviceBrightnessMax = 50;
    public static readonly string[] DirectionNames = { "Derecha", "Izquierda", "Arriba", "Abajo" };

    public static readonly IReadOnlyList<RgbEffect> Effects = new[]
    {
        new RgbEffect(CustomSlot0, "Color libre (RGB)", UsesColor: false, UsesSpeed: false, UsesDirection: false, IsCustomRgb: true),
        new RgbEffect(1,  "Estático",              true,  false, false),
        new RgbEffect(2,  "Respiración",           true,  true,  false),
        new RgbEffect(8,  "Neón (ciclo arcoíris)", false, true,  false),
        new RgbEffect(3,  "Ola arcoíris",          false, true,  true),
        new RgbEffect(9,  "Marquesina arcoíris",   false, true,  true),
        new RgbEffect(5,  "Marquesina",            true,  true,  true),
        new RgbEffect(11, "Marquesina circular",   true,  true,  false),
        new RgbEffect(13, "Rotación",              true,  true,  false),
        new RgbEffect(12, "Seto",                  true,  true,  false),
        new RgbEffect(10, "Gotas de lluvia",       true,  true,  false),
        new RgbEffect(6,  "Ondas (al pulsar)",     true,  true,  false),
        new RgbEffect(4,  "Fundido al pulsar",     true,  true,  false),
        new RgbEffect(7,  "Destello al pulsar",    true,  true,  false),
    };

    private HidStream? _stream;
    private readonly object _lock = new();

    public bool IsConnected => _stream != null;
    public string Status { get; private set; } = "Sin comprobar";
    public string? DeviceDescription { get; private set; }
    public string? Firmware { get; private set; }

    /// <summary>Efecto actual leído del teclado (código, velocidad, brillo, color, dirección).</summary>
    public (byte Type, byte Speed, byte Brightness, byte Color, byte Direction)? Current { get; private set; }

    public bool Connect()
    {
        lock (_lock)
        {
            Close();
            try
            {
                var candidates = DeviceList.Local.GetHidDevices(VendorId)
                    .Where(d => KnownProducts.Contains(d.ProductID))
                    .Where(d => SafeLen(d.GetMaxFeatureReportLength) == 9 && SafeLen(d.GetMaxOutputReportLength) == 65)
                    .ToList();

                if (candidates.Count == 0)
                {
                    Status = "No se encontró el controlador RGB del teclado (USB 1044:7A3x).";
                    return false;
                }

                foreach (var dev in candidates)
                {
                    if (!dev.TryOpen(out HidStream stream)) continue;
                    stream.ReadTimeout = 600;
                    stream.WriteTimeout = 600;
                    _stream = stream;

                    var fw = Request(CmdFirmware);
                    var fx = Request(CmdGetEffect);
                    if (fw != null && fx != null && fx[1] == CmdGetEffect)
                    {
                        Firmware = fw[3] >= 10 ? $"{fw[2]}.{fw[3] / 10}.{fw[3] % 10}" : $"{fw[2]}.{fw[3]}";
                        Current = (fx[3], fx[4], fx[5], fx[6], fx[7]);
                        DeviceDescription = $"USB {dev.VendorID:X4}:{dev.ProductID:X4}, firmware {Firmware}";
                        Status = "Teclado RGB conectado.";
                        return true;
                    }
                    Close();
                }
                Status = "El teclado no respondió a la lectura de estado; no envío cambios.";
                return false;
            }
            catch (Exception ex)
            {
                Close();
                Status = "Error al abrir el teclado: " + ex.Message;
                return false;
            }
        }
    }

    private static int SafeLen(Func<int> f)
    {
        try { return f(); } catch { return 0; }
    }

    private static byte[] Packet(byte cmd, byte b2 = 0, byte b3 = 0, byte b4 = 0, byte b5 = 0, byte b6 = 0, byte b7 = 0)
    {
        var p = new byte[] { 0x00, cmd, b2, b3, b4, b5, b6, b7, 0 };
        int sum = 0;
        for (int i = 0; i < 8; i++) sum += p[i];
        p[8] = (byte)((0xFF - sum) & 0xFF);
        return p;
    }

    /// <summary>Envía una petición de lectura y devuelve la respuesta de 9 bytes.</summary>
    private byte[]? Request(byte cmd)
    {
        try
        {
            _stream!.SetFeature(Packet(cmd));
            var rsp = new byte[9];
            _stream.GetFeature(rsp);
            return rsp;
        }
        catch
        {
            return null;
        }
    }

    private void SetEffect(byte type, int speed, int brightness, byte color, int direction)
    {
        _stream!.SetFeature(Packet(CmdSetEffect, 0, type, (byte)speed, (byte)brightness, color, (byte)direction));
        Thread.Sleep(5);
    }

    /// <summary>Sube una imagen de 128 teclas (índice, R, G, B) al perfil personalizado.</summary>
    private void SetImage(int slot, byte r, byte g, byte b)
    {
        var image = new byte[512];
        for (int i = 0; i < 128; i++)
        {
            image[4 * i + 0] = (byte)i;
            image[4 * i + 1] = r;
            image[4 * i + 2] = g;
            image[4 * i + 3] = b;
        }
        _stream!.SetFeature(Packet(CmdSetImage, 0, (byte)slot, 8));
        var chunk = new byte[65];
        for (int i = 0; i < 8; i++)
        {
            Array.Copy(image, i * 64, chunk, 1, 64);
            _stream.Write(chunk, 0, chunk.Length);
        }
    }

    /// <summary>Velocidad de la interfaz (0-100, más = más rápido) a valor del firmware (10 lento … 1 rápido).</summary>
    private static int ToDeviceSpeed(int ui) => Math.Clamp(10 - (int)Math.Round(ui / 100.0 * 9), 1, 10);

    private static int ToDeviceBrightness(int ui) =>
        (int)Math.Round(Math.Clamp(ui, 0, 100) / 100.0 * DeviceBrightnessMax);

    public bool Apply(RgbSettings s)
    {
        lock (_lock)
        {
            if (_stream == null && !Connect()) return false;
            var fx = Effects.FirstOrDefault(e => e.Code == s.EffectCode) ?? Effects[1];
            try
            {
                int bright = ToDeviceBrightness(s.Brightness);
                int speed = fx.UsesSpeed ? ToDeviceSpeed(s.Speed) : 0;
                int dir = fx.UsesDirection ? Math.Clamp(s.Direction, 0, 3) + 1 : 0;

                if (fx.IsCustomRgb)
                {
                    SetEffect(CustomSlot0, 0, bright, 0, 0);
                    SetImage(0, s.R, s.G, s.B);
                }
                else
                {
                    byte color = fx.UsesColor ? (byte)s.Color : (byte)0;
                    SetEffect(fx.Code, speed, bright, color, dir);
                }

                if (Request(CmdGetEffect) is { } rsp && rsp[1] == CmdGetEffect)
                    Current = (rsp[3], rsp[4], rsp[5], rsp[6], rsp[7]);
                Status = $"Aplicado: {fx.Name}.";
                return true;
            }
            catch (Exception ex)
            {
                Status = "Error al enviar al teclado: " + ex.Message;
                Close();
                return false;
            }
        }
    }

    /// <summary>Restaura un estado leído previamente con <see cref="Current"/> (valores crudos del firmware).</summary>
    public bool RestoreRaw((byte Type, byte Speed, byte Brightness, byte Color, byte Direction) st)
    {
        lock (_lock)
        {
            if (_stream == null && !Connect()) return false;
            try
            {
                SetEffect(st.Type, st.Speed, st.Brightness, st.Color, st.Direction);
                if (Request(CmdGetEffect) is { } rsp && rsp[1] == CmdGetEffect)
                    Current = (rsp[3], rsp[4], rsp[5], rsp[6], rsp[7]);
                return true;
            }
            catch { Close(); return false; }
        }
    }

    private void Close()
    {
        try { _stream?.Dispose(); } catch { }
        _stream = null;
    }

    public void Dispose()
    {
        lock (_lock) Close();
    }
}
