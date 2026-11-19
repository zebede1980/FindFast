using FastFind.App.ViewModels;
using FastFind.Core.Duplicates;
using Xunit;

namespace FastFind.App.Tests;

/// <summary>The list behind the duplicates window: what gets ticked, and the rule that one copy of everything stays.</summary>
public class DuplicateListTests
{
    private static readonly DateTime Day = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static DuplicateGroup Group(long size, params (string Path, int Age)[] files)
        => new(size, files.Select(f => new DuplicateFile(f.Path, size, Day.AddDays(-f.Age))).ToList());

    /// <summary>
    /// Photos: three copies of 1000 bytes (the one in Backup is the oldest, the one on the Desktop the newest).
    /// Report: two copies of 400 bytes, both in Backup.
    /// </summary>
    private static DuplicateList Sample() => new(new[]
    {
        Group(1000, (@"C:\Backup\photo.jpg", 30), (@"C:\Desktop\photo.jpg", 1), (@"C:\Pictures\photo.jpg", 10)),
        Group(400, (@"C:\Backup\old\report.pdf", 5), (@"C:\Backup\report.pdf", 2)),
    });

    private static string[] Ticked(DuplicateList list) => list.TickedPaths().Order().ToArray();
    private static DuplicateRow Row(DuplicateList list, string path) => list.Rows.Single(r => r.File?.Path == path);

    [Fact]
    public void RowsAreAHeadingThenTheCopiesOfEachGroup()
    {
        var list = Sample();
        Assert.Equal(new[] { true, false, false, false, true, false, false }, list.Rows.Select(r => r.IsHeader).ToArray());
        Assert.Equal("photo.jpg", list.Rows[0].Name);
        Assert.Contains("3 copies", list.Rows[0].Detail);
        Assert.Equal(@"C:\Backup", list.Rows[1].Detail);
        Assert.Equal(2, list.GroupCount);
        Assert.Equal(3, list.Surplus);
        Assert.Equal(2400, list.Wasted);
        Assert.Equal(0, list.TickedCount);
    }

    [Fact]
    public void TheLastUntickedCopyCannotBeTicked()
    {
        var list = Sample();
        int changes = 0;
        list.TicksChanged += () => changes++;

        Assert.True(list.SetTicked(Row(list, @"C:\Backup\photo.jpg"), true));
        Assert.True(list.SetTicked(Row(list, @"C:\Desktop\photo.jpg"), true));
        Assert.False(list.SetTicked(Row(list, @"C:\Pictures\photo.jpg"), true));
        Assert.False(Row(list, @"C:\Pictures\photo.jpg").IsTicked);
        Assert.Equal(2, changes);
        Assert.Equal((2, 2000L), (list.TickedCount, list.TickedBytes));

        // A heading is not a file.
        Assert.False(list.SetTicked(list.Rows[0], true));

        // Unticking one frees the other to be ticked.
        Assert.True(list.SetTicked(Row(list, @"C:\Backup\photo.jpg"), false));
        Assert.True(list.SetTicked(Row(list, @"C:\Pictures\photo.jpg"), true));
        Assert.Equal(new[] { @"C:\Desktop\photo.jpg", @"C:\Pictures\photo.jpg" }, Ticked(list));
    }

    [Fact]
    public void KeepingTheNewestOrOldestTicksTheRestOfEveryGroup()
    {
        var list = Sample();
        list.KeepNewest();
        Assert.Equal(new[] { @"C:\Backup\old\report.pdf", @"C:\Backup\photo.jpg", @"C:\Pictures\photo.jpg" }, Ticked(list));
        Assert.Equal((3, 2400L), (list.TickedCount, list.TickedBytes));

        list.KeepOldest();
        Assert.Equal(new[] { @"C:\Backup\report.pdf", @"C:\Desktop\photo.jpg", @"C:\Pictures\photo.jpg" }, Ticked(list));

        list.KeepOnly(Row(list, @"C:\Pictures\photo.jpg"));
        Assert.Equal(new[] { @"C:\Backup\photo.jpg", @"C:\Backup\report.pdf", @"C:\Desktop\photo.jpg" }, Ticked(list));

        list.ClearTicks();
        Assert.Empty(Ticked(list));
    }

    [Fact]
    public void TickingAFolderLeavesACopyWhereEveryCopyIsInIt()
    {
        var list = Sample();
        // The photo has copies elsewhere, so the Backup one can go. Both reports are in Backup: one has to stay.
        Assert.Equal(2, list.TickInFolder(@"C:\Backup"));
        Assert.Equal(new[] { @"C:\Backup\photo.jpg", @"C:\Backup\report.pdf" }, Ticked(list));
        // Asked again, there is nothing more it may tick.
        Assert.Equal(0, list.TickInFolder(@"C:\Backup"));

        // A folder whose name merely starts the same is a different folder.
        list.ClearTicks();
        Assert.Equal(0, list.TickInFolder(@"C:\Back"));

        // If the copies outside are already ticked, the folder's copy is the survivor.
        list.SetTicked(Row(list, @"C:\Desktop\photo.jpg"), true);
        list.SetTicked(Row(list, @"C:\Pictures\photo.jpg"), true);
        list.TickInFolder(@"C:\Backup");
        Assert.False(Row(list, @"C:\Backup\photo.jpg").IsTicked);
    }

    [Fact]
    public void DeletedCopiesLeaveTheListAndAGroupOfOneIsNoLongerAGroup()
    {
        var list = Sample();
        list.KeepNewest();
        var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Backup\photo.jpg", @"C:\Backup\old\report.pdf" };

        long freed = list.RemoveGone(p => !gone.Contains(p));

        Assert.Equal(1400, freed);
        Assert.Equal(1, list.GroupCount);
        Assert.Equal(new[] { true, false, false }, list.Rows.Select(r => r.IsHeader).ToArray());
        Assert.Contains("2 copies", list.Rows[0].Detail);
        // The Pictures copy was ticked but its delete did not happen: it stays in the list, still ticked.
        Assert.Equal(new[] { @"C:\Pictures\photo.jpg" }, Ticked(list));
        Assert.Equal((1, 1000L), (list.TickedCount, list.TickedBytes));
        Assert.Equal(1000, list.Wasted);
    }

    [Fact]
    public void AGroupNeverEndsUpWithEverySurvivorTicked()
    {
        var list = Sample();
        list.KeepNewest(); // keeps the Desktop photo
        // The kept copy vanishes by some other hand, leaving two ticked copies.
        list.RemoveGone(p => p != @"C:\Desktop\photo.jpg");
        var photos = list.Rows.Where(r => r.IsFile && r.Name == "photo.jpg").ToList();
        Assert.Equal(2, photos.Count);
        Assert.Contains(photos, r => !r.IsTicked);
    }
}
