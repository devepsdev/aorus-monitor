using System.Management;

namespace AorusMonitor.Hardware;

/// <summary>
/// Acceso a la interfaz ACPI-WMI de Gigabyte (root\WMI: GB_WMIACPI_Get / GB_WMIACPI_Set),
/// la misma que usa Gigabyte Control Center para ventiladores, temperaturas y modos.
/// Las clases solo existen si el driver/MOF de Gigabyte está instalado, y los métodos
/// requieren un proceso elevado.
/// </summary>
public sealed class GigabyteWmi
{
    private ManagementObject? _get;
    private ManagementObject? _set;
    private readonly object _lock = new();

    public bool IsAvailable => _get != null;
    public bool CanWrite => _set != null;
    public string Status { get; private set; } = "Sin comprobar";

    public GigabyteWmi() => Connect();

    public void Connect()
    {
        lock (_lock)
        {
            _get = _set = null;
            try
            {
                var scope = new ManagementScope(@"\\.\root\WMI", new ConnectionOptions
                {
                    EnablePrivileges = true,
                    Impersonation = ImpersonationLevel.Impersonate,
                });
                scope.Connect();
                _get = FirstInstance(scope, "GB_WMIACPI_Get");
                _set = FirstInstance(scope, "GB_WMIACPI_Set");

                Status = _get == null
                    ? "Interfaz GB_WMIACPI no registrada: vuelve a ejecutar el instalador."
                    : _set == null ? "Solo lectura (GB_WMIACPI_Set no disponible)." : "Conectado a GB_WMIACPI.";
            }
            catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.InvalidClass or ManagementStatus.NotFound)
            {
                Status = "Interfaz GB_WMIACPI no registrada: vuelve a ejecutar el instalador.";
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
            {
                Status = "Acceso denegado: ejecuta la aplicación como administrador.";
            }
            catch (UnauthorizedAccessException)
            {
                Status = "Acceso denegado: ejecuta la aplicación como administrador.";
            }
            catch (Exception ex)
            {
                Status = "Error WMI: " + ex.Message;
            }
        }
    }

    private static ManagementObject? FirstInstance(ManagementScope scope, string className)
    {
        using var cls = new ManagementClass(scope, new ManagementPath(className), null);
        foreach (ManagementObject instance in cls.GetInstances())
            return instance;
        return null;
    }

    /// <summary>Llama a un método Get sin parámetros y devuelve la salida "Data".</summary>
    public int? Get(string method)
    {
        lock (_lock)
        {
            if (_get == null) return null;
            try
            {
                using var result = _get.InvokeMethod(method, null, null);
                var data = result?["Data"];
                return data == null ? null : Convert.ToInt32(data);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Llama a un método Set con un único parámetro "Data".</summary>
    public bool Set(string method, int data) =>
        Invoke(method, new Dictionary<string, object> { ["Data"] = data });

    public bool Invoke(string method, IReadOnlyDictionary<string, object> args)
    {
        lock (_lock)
        {
            if (_set == null) return false;
            try
            {
                using var inParams = _set.GetMethodParameters(method);
                foreach (var (name, value) in args)
                    inParams[name] = ConvertTo(inParams.Properties[name].Type, value);
                using var _ = _set.InvokeMethod(method, inParams, null);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static object ConvertTo(CimType type, object value) => type switch
    {
        CimType.UInt8 => Convert.ToByte(value),
        CimType.UInt16 => Convert.ToUInt16(value),
        CimType.UInt32 => Convert.ToUInt32(value),
        CimType.SInt8 => Convert.ToSByte(value),
        CimType.SInt16 => Convert.ToInt16(value),
        CimType.SInt32 => Convert.ToInt32(value),
        _ => value,
    };
}
