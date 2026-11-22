using System.IO;
using FastFind.App.Services;
using Xunit;

namespace FastFind.App.Tests;

/// <summary>Saving and loading settings, including a file emptied by a restart part-way through a save.</summary>
public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("FastFindSettings").FullName;
    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static Settings WithFavourite(string folder) => new() { Favourites = { folder } };

    [Fact]
    public void Saved_settings_load_back()
    {
        var saved = WithFavourite(@"C:\Work");
        saved.CustomActions.Add(new CustomAction { Name = "Edit", AppliesTo = "txt", Program = "notepad.exe" });
        saved.Save(SettingsPath);

        var loaded = Settings.Load(SettingsPath);

        Assert.Equal(new[] { @"C:\Work" }, loaded.Favourites);
        Assert.Equal("Edit", Assert.Single(loaded.CustomActions).Name);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void Saving_again_keeps_the_previous_file_as_a_backup()
    {
        WithFavourite(@"C:\First").Save(SettingsPath);
        WithFavourite(@"C:\Second").Save(SettingsPath);

        Assert.Equal(new[] { @"C:\Second" }, Settings.Load(SettingsPath).Favourites);
        Assert.Equal(new[] { @"C:\First" }, Settings.Load(SettingsPath + ".bak").Favourites);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ \"Favourites\": [ \"C:\\\\Cut")]
    public void An_unreadable_file_falls_back_to_the_backup_and_is_kept_aside(string broken)
    {
        WithFavourite(@"C:\Work").Save(SettingsPath + ".bak");
        File.WriteAllText(SettingsPath, broken);

        var loaded = Settings.Load(SettingsPath);

        Assert.Equal(new[] { @"C:\Work" }, loaded.Favourites);
        Assert.False(File.Exists(SettingsPath));
        Assert.Equal(broken, File.ReadAllText(SettingsPath + ".corrupt"));
    }

    [Fact]
    public void An_unreadable_file_with_no_backup_gives_the_defaults()
    {
        File.WriteAllText(SettingsPath, "");

        var loaded = Settings.Load(SettingsPath);

        Assert.Empty(loaded.Favourites);
        Assert.True(File.Exists(SettingsPath + ".corrupt"));
    }

    [Fact]
    public void A_missing_file_with_a_backup_uses_the_backup()
    {
        WithFavourite(@"C:\Work").Save(SettingsPath + ".bak");

        Assert.Equal(new[] { @"C:\Work" }, Settings.Load(SettingsPath).Favourites);
    }
}
