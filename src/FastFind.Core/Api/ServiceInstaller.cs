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
    /// Where the service runs from once installed: its own copy under Program Files, one per user it serves. Running
    /// from there rather than from beside FastFind.exe means a rebuild or update of FastFind is never blocked by the
    /// service holding its files open, and, because only administrators can write to Program Files, nothing running
    /// as an ordinary user can swap the program that Task Scheduler starts with administrator rights.
    /// </summary>
    public static string InstallDirectory(string userSid)
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FastFind", "Service", userSid);

    /// <summary>The files the service needs to run, out of a folder that may hold the rest of FastFind as well.</summary>
    public static IEnumerable<string> PayloadFiles(string folder)
        => Directory.EnumerateFiles(folder)
            .Where(f => Path.GetFileName(f) is { } name
                        && (name.StartsWith("FastFind.Service.", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("FastFind.Core.", StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Copies the service into <paramref name="targetFolder"/>, replacing an older copy. A copy that was only just
    /// stopped can hold its files for a moment after it is told to end, so a locked file is retried for a few seconds.
    /// </summary>
    public static void CopyPayload(string sourceFolder, string targetFolder)
    {
        if (SameFolder(sourceFolder, targetFolder)) return;
        Directory.CreateDirectory(targetFolder);
        foreach (string file in PayloadFiles(sourceFolder))
        {
            string destination = Path.Combine(targetFolder, Path.GetFileName(file));
            for (int attempt = 1; ; attempt++)
            {
                try { File.Copy(file, destination, overwrite: true); break; }
                catch (IOException) when (attempt < 40) { Thread.Sleep(250); }
            }
        }
    }

    /// <summary>True when every service file in <paramref name="targetFolder"/> matches the one in <paramref name="sourceFolder"/> (copying keeps the size and modified time).</summary>
    public static bool IsCopyCurrent(string sourceFolder, string targetFolder)
    {
        if (SameFolder(sourceFolder, targetFolder)) return true;
        foreach (string file in PayloadFiles(sourceFolder))
        {
            var source = new FileInfo(file);
            var copy = new FileInfo(Path.Combine(targetFolder, source.Name));
            if (!copy.Exists || copy.Length != source.Length || copy.LastWriteTimeUtc != source.LastWriteTimeUtc) return false;
        }
        return true;
    }

    /// <summary>
    /// False when the installed service is older than the one beside the running program (FastFind has been rebuilt or
    /// updated since), or was installed before it got its own folder. Installing again brings it up to date.
    /// </summary>
    public static bool IsInstalledCopyCurrent(string userSid)
        => FindServiceExe() is not { } exe || IsCopyCurrent(Path.GetDirectoryName(exe)!, InstallDirectory(userSid));

    private static bool SameFolder(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// Copies the service from beside <paramref name="exePath"/> into its own folder, then registers and starts the
    /// task. Installing over an existing service stops it first and updates it. Must run elevated. Returns an error
    /// message, or null on success.
    /// </summary>
    public static string? Install(string exePath, string userSid)
    {
        string? installer = WindowsIdentity.GetCurrent().User?.Value;
        bool runAsSystem = !string.Equals(installer, userSid, StringComparison.OrdinalIgnoreCase);
        string xmlPath = Path.Combine(Path.GetTempPath(), $"fastfind-task-{Guid.NewGuid():N}.xml");
        try
        {
            string folder = InstallDirectory(userSid);
            RunSchtasks("/end", "/tn", TaskName(userSid));
            CopyPayload(Path.GetDirectoryName(exePath)!, folder);
            File.WriteAllText(xmlPath, BuildTaskXml(Path.Combine(folder, ExeName), userSid, runAsSystem), Encoding.Unicode);
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

    /// <summary>Stops and removes the task and the service's installed copy. Must run elevated. Returns an error message, or null on success.</summary>
    public static string? Uninstall(string userSid)
    {
        RunSchtasks("/end", "/tn", TaskName(userSid));
        var (code, output) = RunSchtasks("/delete", "/tn", TaskName(userSid), "/f");
        if (code != 0) return $"Could not remove the scheduled task: {output}";
        RemoveInstalledCopy(InstallDirectory(userSid));
        return null;
    }

    /// <summary>
    /// Deletes the installed copy, and the FastFind folders above it once they are empty. Best effort: the task is
    /// already gone, so a copy that cannot be deleted does no harm and is replaced by the next install.
    /// </summary>
    private static void RemoveInstalledCopy(string folder)
    {
        for (int attempt = 1; attempt <= 40 && Directory.Exists(folder); attempt++)
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(250); }
        }
        try
        {
            for (string? parent = Path.GetDirectoryName(folder); parent != null && Path.GetFileName(parent) is "Service" or "FastFind"; parent = Path.GetDirectoryName(parent))
                if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
