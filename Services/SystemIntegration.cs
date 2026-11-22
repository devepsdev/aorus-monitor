using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace AorusMonitor.Services;

/// <summary>Arranque con Windows (tarea programada elevada) y driver PawnIO.</summary>
public static class SystemIntegration
{
    public const string TaskName = "AORUS Monitor";

    public static string ExePath => Environment.ProcessPath ?? "AorusMonitor.exe";

    public static bool IsAdmin
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>PawnIO: driver que usa LibreHardwareMonitor para frecuencia, consumo y núcleos de la CPU.</summary>
    public static bool PawnIoInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
        return key is not null;
    }

    public static void InstallPawnIo()
    {
        try
        {
            Process.Start(new ProcessStartInfo("winget",
                "install --id namazso.PawnIO -e --accept-package-agreements --accept-source-agreements") { UseShellExecute = true });
        }
        catch (Win32Exception) { }
    }

    // Tarea programada con «privilegios más altos»: arranca como administrador al iniciar sesión
    // sin aviso de Windows. Crearla o borrarla requiere ser administrador.

    /// <summary>Tarea sin disparador que abre la app elevada sin aviso UAC (la usan los accesos directos).</summary>
    public const string LaunchTaskName = "AORUS Monitor (abrir)";

    public static bool StartupEnabled() => Schtasks($"/query /tn \"{TaskName}\"") == 0;

    public static bool SetStartup(bool enable) =>
        enable ? CreateTask(TaskName, "Abre AORUS Monitor en la bandeja al iniciar sesión", logon: true, "--minimizado")
               : Schtasks($"/delete /f /tn \"{TaskName}\"") == 0;

    public static bool SetLaunchTask(bool enable) =>
        enable ? CreateTask(LaunchTaskName, "Abre AORUS Monitor como administrador sin aviso de Windows", logon: false, "")
               : Schtasks($"/delete /f /tn \"{LaunchTaskName}\"") == 0;

    /// <summary>Lanza la app elevada mediante la tarea (sin UAC). False si la tarea no existe.</summary>
    public static bool RunLaunchTask() =>
        Schtasks($"/query /tn \"{LaunchTaskName}\"") == 0 && Schtasks($"/run /tn \"{LaunchTaskName}\"") == 0;

    private static bool CreateTask(string name, string description, bool logon, string arguments)
    {
        string user = Escape(WindowsIdentity.GetCurrent().Name);
        string triggers = logon
            ? $"<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>"
            : "<Triggers/>";
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>{Escape(description)}</Description></RegistrationInfo>
              {triggers}
              <Principals>
                <Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{Escape(ExePath)}</Command><Arguments>{Escape(arguments)}</Arguments></Exec>
              </Actions>
            </Task>
            """;

        string file = Path.Combine(Path.GetTempPath(), $"AorusMonitor-tarea-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            return Schtasks($"/create /f /tn \"{name}\" /xml \"{file}\"") == 0;
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    private static string Escape(string s) => System.Security.SecurityElement.Escape(s) ?? s;

    private static int Schtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return -1;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
