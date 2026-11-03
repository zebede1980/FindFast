using System.Security.Principal;

namespace FastFind.Core.Native;

public static class Elevation
{
    private static readonly Lazy<bool> _isElevated = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    });

    /// <summary>True when the process runs with an administrator token (needed for raw NTFS access).</summary>
    public static bool IsElevated => _isElevated.Value;
}
