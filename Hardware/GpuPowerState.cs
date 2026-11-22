using System.Management;
using System.Runtime.InteropServices;

namespace AorusMonitor.Hardware;

/// <summary>
/// Estado de energía de la GPU NVIDIA (D0 encendida … D3 apagada) leído de Windows.
/// Consultarlo no despierta la gráfica, a diferencia de leer sus sensores con NVAPI/NVML,
/// que la mantienen encendida (~25 W) aunque no se use.
/// </summary>
public sealed class GpuPowerState
{
    private readonly uint _devInst;
    public bool Available { get; }

    public GpuPowerState()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_VideoController");
            foreach (var o in s.Get())
            {
                if (o["Name"]?.ToString()?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true &&
                    o["PNPDeviceID"]?.ToString() is { } id &&
                    CM_Locate_DevNodeW(out _devInst, id, 0) == 0)
                {
                    Available = true;
                    break;
                }
            }
        }
        catch { }
    }

    /// <summary>True si la GPU está encendida (D0). Si no se puede saber, se asume encendida.</summary>
    public bool IsAwake()
    {
        if (!Available) return true;
        try
        {
            var key = new DevPropKey { FmtId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 32 };
            var buffer = new byte[64];
            uint size = (uint)buffer.Length;
            if (CM_Get_DevNode_PropertyW(_devInst, ref key, out _, buffer, ref size, 0) != 0 || size < 8) return true;
            // CM_POWER_DATA: PD_Size (4 bytes), PD_MostRecentPowerState (1 = D0 … 4 = D3)
            return BitConverter.ToInt32(buffer, 4) == 1;
        }
        catch
        {
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FmtId;
        public uint Pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint type,
        byte[] buffer, ref uint size, uint flags);
}
