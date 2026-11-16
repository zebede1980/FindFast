using System.IO;
using FastFind.App.Services;
using Xunit;

namespace FastFind.App.Tests;

/// <summary>
/// Only the "not locked" side can be checked without a locked drive to hand: places that are readable, missing or
/// not on a local drive must never be mistaken for one, or clicking them would raise an unlock prompt.
/// </summary>
public sealed class BitLockerTests
{
    [Fact]
    public void A_readable_drive_is_not_locked()
    {
        string system = Path.GetPathRoot(Environment.SystemDirectory)!;
        Assert.Null(BitLocker.LockedRoot(system));
        Assert.Null(BitLocker.LockedRoot(Environment.SystemDirectory));
    }

    [Fact]
    public void A_drive_letter_nothing_is_mounted_on_is_not_locked()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        char free = Enumerable.Range('D', 23).Select(c => (char)c).First(c => !used.Contains(c));
        Assert.Null(BitLocker.LockedRoot($"{free}:\\"));
        Assert.Null(BitLocker.LockedRoot($"{free}:\\some\\folder"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("C")]
    [InlineData(@"\\server\share\folder")]
    [InlineData("shell:RecycleBinFolder")]
    [InlineData("1:\\")]
    public void Places_that_are_not_on_a_lettered_drive_are_not_locked(string path)
        => Assert.Null(BitLocker.LockedRoot(path));
}
