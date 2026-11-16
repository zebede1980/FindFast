using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace FastFind.App.Services;

/// <summary>Spots drives locked by BitLocker and shows the Windows prompt that unlocks them.</summary>
public static class BitLocker
{
    /// <summary>FVE_E_LOCKED_VOLUME: what any attempt to read a locked volume fails with.</summary>
    private const int LockedVolume = unchecked((int)0x80310000);
    private const uint InvalidAttributes = 0xFFFFFFFF;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string lpFileName);

    /// <summary>The drive a path is on ("E:\"), or null when it is not on a local lettered drive.</summary>
    private static string? LocalRoot(string path)
    {
        if (path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0])) return null;
        string root = $"{char.ToUpperInvariant(path[0])}:\\";
        try
        {
            // The drive type comes from the mount manager without touching the volume, so this cannot stall on a dead network drive.
            return new DriveInfo(root).DriveType is DriveType.Fixed or DriveType.Removable ? root : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The locked drive a path is on ("E:\"), or null when the path is reachable or is unavailable for some other reason.</summary>
    public static string? LockedRoot(string path)
    {
        if (LocalRoot(path) is not { } root) return null;
        if (GetFileAttributesW(root) != InvalidAttributes) return null;
        return Marshal.GetLastWin32Error() == LockedVolume ? root : null;
    }

    /// <summary>
    /// Shows the Windows unlock prompt for a drive (the one Explorer shows) and waits for the outcome.
    /// True once the drive can be read; false if the prompt was dismissed or could not be shown.
    /// </summary>
    public static async Task<bool> PromptUnlockAsync(string root)
    {
        Process? prompt;
        try
        {
            // The command behind Explorer's "Unlock drive" verb (HKCR\Drive\shell\unlock-bde).
            prompt = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "bdeunlock.exe"), root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn($"BitLocker unlock prompt failed for {root}: {ex.Message}");
            return false;
        }
        if (prompt == null) return false;
        using (prompt)
        {
            // Watch the drive rather than only the prompt: it is the drive opening that matters, however long the prompt lingers.
            while (!prompt.HasExited)
            {
                if (Directory.Exists(root)) return true;
                await Task.Delay(300);
            }
            // The volume can take a moment to mount after the prompt closes.
            for (int i = 0; i < 5 && LockedRoot(root) != null; i++) await Task.Delay(300);
            return Directory.Exists(root);
        }
    }
}
