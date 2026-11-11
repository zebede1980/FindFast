using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace FastFind.Core.Api;

/// <summary>
/// Registers the index service as a scheduled task that starts at sign-in with the highest available privileges,
/// which is how it gets to read the NTFS Master File Table without the UI itself running elevated.
/// </summary>
public static class ServiceInstaller
{
    public const string ExeName = "FastFind.Service.exe";

    public static string TaskName(string userSid) => $@"FastFind\Index Service {userSid}";

    /// <summary>The service executable that ships beside the running program, or null if it is missing.</summary>
    public static string? FindServiceExe()
    {
        string path = Path.Combine(AppContext.BaseDirectory, ExeName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// The task definition. When the service is installed by the same account it serves, it runs as that user
    /// (elevated), so it can reach the user's network shares. When a different administrator account installs it,
    /// it runs as SYSTEM and serves local volumes only.
    /// </summary>
    public static string BuildTaskXml(string exePath, string userSid, bool runAsSystem)
    {
        string arguments = $"run --user {userSid}";
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
        sb.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
        sb.AppendLine("  <RegistrationInfo>");
        sb.AppendLine("    <Description>Keeps the FastFind file index in memory and up to date so searches are instant.</Description>");
        sb.AppendLine("  </RegistrationInfo>");
        sb.AppendLine("  <Triggers>");
        if (runAsSystem)
        {
            sb.AppendLine("    <BootTrigger><Enabled>true</Enabled></BootTrigger>");
        }
        else
        {
            sb.AppendLine("    <LogonTrigger>");
            sb.AppendLine("      <Enabled>true</Enabled>");
            sb.AppendLine($"      <UserId>{SecurityElement.Escape(AccountName(userSid))}</UserId>");
            sb.AppendLine("    </LogonTrigger>");
        }
        sb.AppendLine("  </Triggers>");
        sb.AppendLine("  <Principals>");
        sb.AppendLine("    <Principal id=\"Author\">");
        if (runAsSystem)
        {
            sb.AppendLine("      <UserId>S-1-5-18</UserId>");
        }
        else
        {
            sb.AppendLine($"      <UserId>{SecurityElement.Escape(userSid)}</UserId>");
            sb.AppendLine("      <LogonType>InteractiveToken</LogonType>");
        }
        sb.AppendLine("      <RunLevel>HighestAvailable</RunLevel>");
        sb.AppendLine("    </Principal>");
        sb.AppendLine("  </Principals>");
        sb.AppendLine("  <Settings>");
        sb.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
        // Task Scheduler's defaults would stop the index on battery and kill it after three days.
        sb.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
        sb.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
        sb.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");
        sb.AppendLine("    <StartWhenAvailable>true</StartWhenAvailable>");
        sb.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
        sb.AppendLine("    <Enabled>true</Enabled>");
        sb.AppendLine("    <Hidden>false</Hidden>");
        sb.AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
        sb.AppendLine("    <Priority>6</Priority>");
        sb.AppendLine("  </Settings>");
        sb.AppendLine("  <Actions Context=\"Author\">");
        sb.AppendLine("    <Exec>");
        sb.AppendLine($"      <Command>{SecurityElement.Escape(exePath)}</Command>");
        sb.AppendLine($"      <Arguments>{SecurityElement.Escape(arguments)}</Arguments>");
        sb.AppendLine("    </Exec>");
        sb.AppendLine("  </Actions>");
        sb.AppendLine("</Task>");
        return sb.ToString();
    }

    /// <summary>DOMAIN\user for a SID (what Task Scheduler writes in a trigger), or the SID itself if it cannot be resolved.</summary>
    public static string AccountName(string userSid)
    {
        try { return new SecurityIdentifier(userSid).Translate(typeof(NTAccount)).Value; }
        catch (Exception) { return userSid; }
    }

    public static (int ExitCode, string Output) RunSchtasks(params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in arguments) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30_000)) { try { p.Kill(); } catch (Exception) { } return (-1, "schtasks timed out"); }
            return (p.ExitCode, output.Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    public static bool IsInstalled(string userSid) => RunSchtasks("/query", "/tn", TaskName(userSid)).ExitCode == 0;

    /// <summary>Asks Task Scheduler to start the service now. Needs no elevation for the user the task belongs to.</summary>
    public static bool TryStart(string userSid) => RunSchtasks("/run", "/tn", TaskName(userSid)).ExitCode == 0;

    /// <summary>Registers and starts the task. Must run elevated. Returns an error message, or null on success.</summary>
    public static string? Install(string exePath, string userSid)
    {
        string? installer = WindowsIdentity.GetCurrent().User?.Value;
        bool runAsSystem = !string.Equals(installer, userSid, StringComparison.OrdinalIgnoreCase);
        string xmlPath = Path.Combine(Path.GetTempPath(), $"fastfind-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xmlPath, BuildTaskXml(exePath, userSid, runAsSystem), Encoding.Unicode);
            var (code, output) = RunSchtasks("/create", "/tn", TaskName(userSid), "/xml", xmlPath, "/f");
            if (code != 0) return $"Could not register the scheduled task: {output}";
            (code, output) = RunSchtasks("/run", "/tn", TaskName(userSid));
            return code == 0 ? null : $"The task was registered but did not start: {output}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch (Exception) { }
        }
    }

    /// <summary>Stops and removes the task. Must run elevated. Returns an error message, or null on success.</summary>
    public static string? Uninstall(string userSid)
    {
        RunSchtasks("/end", "/tn", TaskName(userSid));
        var (code, output) = RunSchtasks("/delete", "/tn", TaskName(userSid), "/f");
        return code == 0 ? null : $"Could not remove the scheduled task: {output}";
    }
}
