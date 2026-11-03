using FastFind.Core.Search;
using Xunit;

namespace FastFind.Core.Tests;

public class HighlightTests
{
    private static List<(int Start, int Length)> Find(string query, string name, QueryOptions? options = null)
    {
        var q = QueryParser.Parse(query, options);
        var ranges = new List<(int, int)>();
        foreach (var t in q.TextTerms()) t.FindMatches(name, ranges);
        return ranges;
    }

    [Fact]
    public void SubstringTermsReportEveryOccurrenceCaseInsensitively()
    {
        var r = Find("bud", "Budget-budget.xlsx");
        Assert.Equal(new[] { (0, 3), (7, 3) }, r);
    }

    [Fact]
    public void NegatedTermsAreNotHighlighted()
    {
        Assert.Empty(Find("!tmp", "file.tmp"));
    }

    [Fact]
    public void WildcardsHighlightOnlyLiteralParts()
    {
        var r = Find("inv*2024.pdf", "inv_report_2024.pdf");
        Assert.Contains((0, 3), r);
        Assert.Contains((11, 8), r);
        Assert.DoesNotContain(r, x => x.Start == 3);
    }

    [Fact]
    public void RegexHighlightsMatches()
    {
        var r = Find("regex:\\d{4}", "report-2024-final-2025.txt", new QueryOptions());
        Assert.Equal(new[] { (7, 4), (18, 4) }, r);
    }

    [Fact]
    public void FunctionTermsContributeTextTerms()
    {
        Assert.Equal(new[] { (0, 5) }, Find("folder:build", "build"));
        Assert.Empty(Find("ext:txt size:>1kb", "notes.txt"));
    }
}
