using Microsoft.Win32;

namespace FastFind.App.Services;

/// <summary>
/// The optional "Open in FastFind" entry on the right-click menu of folders, drives and folder backgrounds in
/// Windows. Written under the current user's own registry hive, so it needs no administrator rights and affects
/// nobody else; removing it deletes exactly the keys it created.
/// </summary>
public static class ExplorerIntegration
{
    private const string Verb = "FastFind.Open";

    // (registry class, placeholder Explorer substitutes for the clicked item)
    private static readonly (string Class, string Argument)[] Targets =
    {
        ("Directory", "%1"),
        ("Directory\\Background", "%V"),
        ("Drive", "%1"),
    };

    private static string KeyPath(string cls) => $@"Software\Classes\{cls}\shell\{Verb}";

    public static bool IsRegistered
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath("Directory"));
                return key != null;
            }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Adds the menu entries. Returns an error message, or null on success.</summary>
    public static string? Register()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return "Could not work out where FastFind.exe is.";
        try
        {
            foreach (var (cls, argument) in Targets)
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath(cls));
                key.SetValue("", "Open in FastFind");
                key.SetValue("Icon", $"\"{exe}\",0");
                using var command = key.CreateSubKey("command");
                command.SetValue("", $"\"{exe}\" \"{argument}\"");
            }
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Explorer menu registration failed: {ex.Message}");
            return ex.Message;
        }
    }

    /// <summary>Removes the menu entries. Returns an error message, or null on success.</summary>
    public static string? Unregister()
    {
        try
        {
            foreach (var (cls, _) in Targets)
                Registry.CurrentUser.DeleteSubKeyTree(KeyPath(cls), throwOnMissingSubKey: false);
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Explorer menu removal failed: {ex.Message}");
            return ex.Message;
        }
    }
}
