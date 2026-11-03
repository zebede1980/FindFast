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

    private static readonly string FilePath = Path.Combine(Logger.Directory, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Options) ?? new Settings();
        }
        catch (Exception ex) { Logger.Warn($"Settings load failed: {ex.Message}"); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Logger.Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) { Logger.Warn($"Settings save failed: {ex.Message}"); }
    }
}

public sealed class ColumnSetting
{
    public string Key { get; set; } = "";
    public bool Visible { get; set; } = true;
    public double Width { get; set; }
    public int DisplayIndex { get; set; }
}
