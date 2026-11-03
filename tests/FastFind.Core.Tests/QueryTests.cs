using FastFind.Core.Indexing;
using FastFind.Core.Search;
using Xunit;

namespace FastFind.Core.Tests;

public class QueryTests
{
    private static VolumeIndex BuildSample()
    {
        var idx = VolumeIndex.CreateEmpty(@"C:\", IndexMode.DirectoryWalk);
        long now = DateTime.UtcNow.ToFileTimeUtc();
        long old = DateTime.UtcNow.AddYears(-3).ToFileTimeUtc();
        int users = idx.Add(0, "Users", idx.RootIndex, FileAttributes.Directory, 0, now, now);
        int joe = idx.Add(0, "joe", users, FileAttributes.Directory, 0, now, now);
        int docs = idx.Add(0, "Documents", joe, FileAttributes.Directory, 0, now, now);
        idx.Add(0, "Budget 2024.xlsx", docs, FileAttributes.Normal, 120_000, now, now);
        idx.Add(0, "budget-final.pdf", docs, FileAttributes.Normal, 2_500_000, old, old);
        idx.Add(0, "notes.txt", docs, FileAttributes.Normal, 0, now, now);
        idx.Add(0, "photo.JPG", joe, FileAttributes.Normal, 4_000_000, now, now);
        idx.Add(0, "secret.txt", joe, FileAttributes.Hidden, 10, now, now);
        int win = idx.Add(0, "Windows", idx.RootIndex, FileAttributes.Directory, 0, now, now);
        idx.Add(0, "notepad.exe", win, FileAttributes.Normal, 300_000, old, old);
        idx.Lock.EnterReadLock();
        try { idx.EnsureNameOrder(); } finally { idx.Lock.ExitReadLock(); }
        return idx;
    }

    private static string[] Run(string query, QueryOptions? options = null, FilterSet? filters = null, SortField sort = SortField.Name, bool desc = false)
    {
        var idx = BuildSample();
        var result = SearchEngine.Search(new[] { idx }, QueryParser.Parse(query, options, filters), sort, desc);
        return result.Hits.Select(h => idx.GetName(h.Index)!).ToArray();
    }

    [Fact]
    public void EmptyQueryReturnsEverythingSortedByName()
    {
        var names = Run("");
        Assert.Equal(10, names.Length);
        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), names);
    }

    [Fact]
    public void SubstringIsCaseInsensitiveAndSpaceMeansAnd()
    {
        Assert.Equal(new[] { "Budget 2024.xlsx", "budget-final.pdf" }, Run("budget"));
        Assert.Equal(new[] { "budget-final.pdf" }, Run("budget final"));
        Assert.Empty(Run("budget", new QueryOptions { MatchCase = true }).Where(n => n.StartsWith('B')));
    }

    [Fact]
    public void OrBindsTighterThanAnd()
    {
        // budget AND (xlsx OR pdf) -> both budget files; "notes" excluded
        Assert.Equal(new[] { "Budget 2024.xlsx", "budget-final.pdf" }, Run("budget xlsx|pdf"));
        Assert.Equal(new[] { "budget-final.pdf", "photo.JPG" }, Run("*.pdf|*.jpg"));
    }

    [Fact]
    public void NegationAndQuotes()
    {
        Assert.Equal(new[] { "Budget 2024.xlsx" }, Run("budget !pdf"));
        Assert.Equal(new[] { "Budget 2024.xlsx" }, Run("\"budget 2024\""));
    }

    [Fact]
    public void WildcardsMatchWholeName()
    {
        Assert.Equal(new[] { "notes.txt", "secret.txt" }, Run("*.txt"));
        Assert.Empty(Run("note?.tx"));
        Assert.Equal(new[] { "notes.txt" }, Run("note?.txt"));
    }

    [Fact]
    public void Functions()
    {
        Assert.Equal(new[] { "notes.txt", "secret.txt" }, Run("ext:txt"));
        Assert.Equal(new[] { "budget-final.pdf", "photo.JPG" }, Run("size:>1mb"));
        Assert.Equal(new[] { "notes.txt" }, Run("empty:"));
        Assert.Equal(new[] { "Documents", "joe", "Users", "Windows" }, Run("folder:"));
        Assert.Equal(new[] { "budget-final.pdf", "notepad.exe" }, Run("dm:<" + DateTime.Today.AddYears(-1).ToString("yyyy-MM-dd")));
        Assert.Equal(new[] { "Budget 2024.xlsx", "budget-final.pdf", "notes.txt" }, Run(@"infolder:C:\Users\joe\Documents"));
        Assert.Equal(new[] { "Documents", "photo.JPG", "secret.txt" }, Run(@"parent:C:\Users\joe"));
        Assert.Equal(new[] { "secret.txt" }, Run("attrib:h"));
        Assert.Equal(new[] { "Budget 2024.xlsx", "budget-final.pdf", "notes.txt", "photo.JPG", "secret.txt" }, Run("type:documents|type:images"));
        Assert.Equal(new[] { "notepad.exe" }, Run(@"regex:^note.*\.exe$"));
        Assert.Equal(new[] { "Budget 2024.xlsx", "budget-final.pdf", "Documents", "notes.txt" }, Run(@"path:joe\Documents"));
    }

    [Fact]
    public void PathMatchingHandlesSeparatorStraddle()
    {
        Assert.Equal(new[] { "Budget 2024.xlsx", "budget-final.pdf", "notes.txt" }, Run(@"Documents\", new QueryOptions { MatchPath = true }));
        Assert.Equal(new[] { "notes.txt" }, Run(@"documents\notes", new QueryOptions { MatchPath = true }));
    }

    [Fact]
    public void FiltersFromUiAreApplied()
    {
        var filters = new FilterSet { Kind = EntryKind.File, MinSize = 100_000, HideHiddenAndSystem = true };
        Assert.Equal(new[] { "Budget 2024.xlsx", "budget-final.pdf", "notepad.exe", "photo.JPG" }, Run("", filters: filters));
        Assert.Equal(new[] { "budget-final.pdf" }, Run("", filters: new FilterSet { Extensions = new[] { "pdf" } }));
    }

    [Fact]
    public void SortingBySizeDescending()
    {
        var names = Run("file:", sort: SortField.Size, desc: true);
        Assert.Equal("photo.JPG", names[0]);
        Assert.Equal("budget-final.pdf", names[1]);
    }

    [Fact]
    public void LiveUpdatesAreVisibleToSearches()
    {
        var idx = BuildSample();
        idx.Lock.EnterWriteLock();
        try
        {
            int docs = idx.FindDirectory(@"C:\Users\joe\Documents");
            Assert.True(docs >= 0);
            int n = idx.Add(0, "new-report.docx", docs, FileAttributes.Normal, 5, 0, 0);
            idx.Rename(n, "renamed-report.docx", docs);
            int notes = idx.FindChild(docs, "notes.txt");
            idx.Remove(notes);
        }
        finally { idx.Lock.ExitWriteLock(); }
        var r = SearchEngine.Search(new[] { idx }, QueryParser.Parse("report|notes"));
        Assert.Equal(new[] { "renamed-report.docx" }, r.Hits.Select(h => idx.GetName(h.Index)).ToArray());
        Assert.Equal(@"C:\Users\joe\Documents\renamed-report.docx", idx.GetFullPath(r.Hits[0].Index));
    }

    [Fact]
    public void RemoveSubtreeDropsDescendants()
    {
        var idx = BuildSample();
        idx.Lock.EnterWriteLock();
        try { idx.RemoveSubtree(idx.FindDirectory(@"C:\Users")); }
        finally { idx.Lock.ExitWriteLock(); }
        Assert.Equal(new[] { "notepad.exe", "Windows" }, Run2(idx, ""));
    }

    private static string[] Run2(VolumeIndex idx, string q)
        => SearchEngine.Search(new[] { idx }, QueryParser.Parse(q)).Hits.Select(h => idx.GetName(h.Index)!).ToArray();

    [Theory]
    [InlineData("10mb", 10L * 1024 * 1024, 1024L * 1024)]
    [InlineData("1.5 GB", 1_610_612_736L, 1024L * 1024 * 1024)]
    [InlineData("500k", 512_000L, 1024L)]
    [InlineData("2048", 2048L, 1L)]
    public void SizeParsing(string text, long expectedBytes, long expectedUnit)
    {
        Assert.True(SizeParser.TryParseSize(text, out long bytes, out long unit));
        Assert.Equal(expectedBytes, bytes);
        Assert.Equal(expectedUnit, unit);
    }

    [Fact]
    public void SizeRanges()
    {
        Assert.True(SizeParser.TryParseRange(">10mb", out long min, out long max));
        Assert.Equal(10L * 1024 * 1024 + 1, min);
        Assert.Equal(long.MaxValue, max);
        Assert.True(SizeParser.TryParseRange("1mb..5mb", out min, out max));
        Assert.Equal(1L << 20, min);
        Assert.Equal(5L << 20, max);
        Assert.True(SizeParser.TryParseRange("large", out min, out max));
        Assert.Equal(1L << 20, min);
        Assert.Equal(16L << 20, max);
    }

    [Fact]
    public void DateRanges()
    {
        var now = new DateTime(2026, 11, 3, 10, 0, 0);
        Assert.True(DateRangeParser.TryParse("today", now, out var lo, out var hi));
        Assert.Equal(new DateTime(2026, 11, 3).ToUniversalTime(), lo);
        Assert.Equal(new DateTime(2026, 11, 4).ToUniversalTime(), hi);
        Assert.True(DateRangeParser.TryParse("2024-05", now, out lo, out hi));
        Assert.Equal(new DateTime(2024, 5, 1).ToUniversalTime(), lo);
        Assert.Equal(new DateTime(2024, 6, 1).ToUniversalTime(), hi);
        Assert.True(DateRangeParser.TryParse("last7days", now, out lo, out hi));
        Assert.Equal(new DateTime(2026, 10, 28).ToUniversalTime(), lo);
        Assert.True(DateRangeParser.TryParse(">2024", now, out lo, out hi));
        Assert.Equal(new DateTime(2025, 1, 1).ToUniversalTime(), lo);
        Assert.False(DateRangeParser.TryParse("nonsense", now, out _, out _));
    }

    [Fact]
    public void InvalidRegexReportsErrorInsteadOfThrowing()
    {
        var q = QueryParser.Parse("regex:[unclosed");
        Assert.NotNull(q.Error);
        Assert.Empty(Run("regex:[unclosed"));
    }
}
