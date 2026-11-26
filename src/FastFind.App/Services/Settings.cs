using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FastFind.App.Services;

public sealed class Settings
{
    public string Theme { get; set; } = "System";
    public bool IncludeRemovable { get; set; }
    public bool IncludeNetwork { get; set; }
    public List<string> ExcludedRoots { get; set; } = new();
    public bool HideHiddenSystemByDefault { get; set; }
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 760;
    public bool WindowMaximized { get; set; }
    public bool ShowCreatedColumn { get; set; }
    /// <summary>Closing the window hides it to the tray so the in-memory index stays warm.</summary>
    public bool MinimizeToTray { get; set; } = true;
    /// <summary>System-wide shortcut that shows/hides the window; empty disables it.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+F";
    /// <summary>Launching a second copy brings the running one to the front instead.</summary>
    public bool SingleInstance { get; set; } = true;
    /// <summary>UNC paths (or drive roots) indexed on a schedule.</summary>
    public List<string> NetworkLocations { get; set; } = new();
    public double NetworkRescanHours { get; set; } = 6;
    /// <summary>Saved results-grid layout: one entry per column.</summary>
    public List<ColumnSetting> Columns { get; set; } = new();
    public string SortField { get; set; } = "Name";
    public bool SortDescending { get; set; }

    /// <summary>Two panes side by side.</summary>
    public bool DualPane { get; set; } = true;
    public bool SidebarVisible { get; set; } = true;
    public double SidebarWidth { get; set; } = 220;
    public bool PreviewVisible { get; set; }
    public double PreviewWidth { get; set; } = 320;
    /// <summary>Folders pinned to the sidebar by the user.</summary>
    public List<string> Favourites { get; set; } = new();
    /// <summary>Open tabs, restored at the next launch. Null until the app has been closed once.</summary>
    public SessionState? Session { get; set; }
    /// <summary>Show each folder's total size (taken from the index) in the Size column.</summary>
    public bool ShowFolderSizes { get; set; } = true;
    /// <summary>Ask before sending items to the Recycle Bin.</summary>
    public bool ConfirmRecycle { get; set; } = true;

    /// <summary>The disk usage window: its size, and whether the biggest folders are named on the map.</summary>
    public double UsageWindowWidth { get; set; } = 1280;
    public double UsageWindowHeight { get; set; } = 860;
    public bool UsageWindowMaximized { get; set; }
    public bool UsageShowNames { get; set; } = true;

    /// <summary>The duplicate files window: its size, and the smallest file it compares (in bytes).</summary>
    public double DuplicatesWindowWidth { get; set; } = 1100;
    public double DuplicatesWindowHeight { get; set; } = 720;
    public bool DuplicatesWindowMaximized { get; set; }
    public long DuplicatesMinSize { get; set; } = 1024 * 1024;

    /// <summary>The user's own right-click entries, in the order they appear in the menu.</summary>
    public List<CustomAction> CustomActions { get; set; } = new();

    private static readonly string FilePath = Path.Combine(Logger.Directory, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static Settings Load() => Load(FilePath);

    /// <summary>
    /// Reads the settings, falling back to the copy kept by the previous save when the file is missing or unreadable.
    /// An unreadable file is moved aside to ".corrupt" rather than left to be saved over, so nothing is lost silently.
    /// </summary>
    public static Settings Load(string path)
    {
        string backup = path + ".bak";
        if (TryRead(path, out var settings)) return settings;
        if (File.Exists(path))
        {
            try { File.Move(path, path + ".corrupt", overwrite: true); }
            catch (Exception ex) { Logger.Warn($"Could not move the unreadable settings aside: {ex.Message}"); }
        }
        if (TryRead(backup, out settings))
        {
            Logger.Info($"Settings restored from {backup}");
            return settings;
        }
        return new Settings();
    }

    private static bool TryRead(string path, out Settings settings)
    {
        settings = null!;
        if (!File.Exists(path)) return false;
        try
        {
            if (JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Options) is not { } read) return false;
            settings = read;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Settings load failed for {path}: {ex.Message}");
            return false;
        }
    }

    public void Save() => Save(FilePath);

    /// <summary>
    /// Writes the settings so that a crash or a forced restart part-way through leaves the previous file whole: the new
    /// contents go to a temporary file, are flushed to disk, and only then take the old file's place. The old file is
    /// kept as ".bak".
    /// </summary>
    public void Save(string path)
    {
        string temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(this, Options);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(temp, path);
        }
        catch (Exception ex) { Logger.Warn($"Settings save failed: {ex.Message}"); }
    }
}

public sealed class SessionState
{
    public List<TabState> Left { get; set; } = new();
    public List<TabState> Right { get; set; } = new();
    public int LeftSelected { get; set; }
    public int RightSelected { get; set; }
    public bool RightActive { get; set; }
}

public sealed class TabState
{
    /// <summary>Folder shown in the tab; empty means "everywhere" (a search across the whole index).</summary>
    public string Location { get; set; } = "";
    public string Query { get; set; } = "";
    public bool Recursive { get; set; }
    public bool Locked { get; set; }
    /// <summary>Shown as thumbnails rather than the detailed list.</summary>
    public bool Tiles { get; set; }
}

/// <summary>A right-click entry the user made: a program to start with the selected items. See <see cref="CustomActions"/>.</summary>
public sealed class CustomAction
{
    /// <summary>The text of the menu entry.</summary>
    public string Name { get; set; } = "";
    /// <summary>What it is offered for: extensions ("ps1; psm1"), kinds ("video"), "folder", or "*" for any file.</summary>
    public string AppliesTo { get; set; } = "*";
    public string Program { get; set; } = "";
    /// <summary>The command line after the program, with placeholders such as {path}. Empty passes everything selected.</summary>
    public string Arguments { get; set; } = "";
    /// <summary>The folder the program starts in; empty for the folder the item is in.</summary>
    public string StartIn { get; set; } = "";
    /// <summary>Start the program elevated (Windows asks first). FastFind itself stays as it is.</summary>
    public bool RunAsAdmin { get; set; }
}

public sealed class ColumnSetting
{
    public string Key { get; set; } = "";
    public bool Visible { get; set; } = true;
    public double Width { get; set; }
    public int DisplayIndex { get; set; }
}
