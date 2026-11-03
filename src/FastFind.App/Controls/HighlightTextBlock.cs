using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using FastFind.Core.Search;

namespace FastFind.App.Controls;

/// <summary>Which text terms to light up in a column. Built once per search by the tab view model.</summary>
public sealed class Highlighter
{
    private readonly TextTerm[] _terms;
    public static readonly Highlighter None = new(Array.Empty<TextTerm>());

    public Highlighter(IEnumerable<TextTerm> terms) => _terms = terms.ToArray();
    public bool IsEmpty => _terms.Length == 0;

    public List<(int Start, int Length)> Find(string s)
    {
        var ranges = new List<(int Start, int Length)>();
        foreach (var t in _terms) t.FindMatches(s, ranges);
        if (ranges.Count < 2) return ranges;
        // merge overlaps
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(int Start, int Length)> { ranges[0] };
        for (int i = 1; i < ranges.Count; i++)
        {
            var last = merged[^1];
            var cur = ranges[i];
            if (cur.Start <= last.Start + last.Length)
                merged[^1] = (last.Start, Math.Max(last.Start + last.Length, cur.Start + cur.Length) - last.Start);
            else merged.Add(cur);
        }
        return merged;
    }
}

/// <summary>TextBlock that renders the parts of <see cref="SourceText"/> matched by <see cref="Highlighter"/> in bold accent colour.</summary>
public sealed class HighlightTextBlock : TextBlock
{
    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(
        nameof(SourceText), typeof(string), typeof(HighlightTextBlock), new FrameworkPropertyMetadata("", OnChanged));

    public static readonly DependencyProperty HighlighterProperty = DependencyProperty.Register(
        nameof(Highlighter), typeof(Highlighter), typeof(HighlightTextBlock), new FrameworkPropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty HighlightBrushProperty = DependencyProperty.Register(
        nameof(HighlightBrush), typeof(Brush), typeof(HighlightTextBlock), new FrameworkPropertyMetadata(null, OnChanged));

    public string SourceText { get => (string)GetValue(SourceTextProperty); set => SetValue(SourceTextProperty, value); }
    public Highlighter? Highlighter { get => (Highlighter?)GetValue(HighlighterProperty); set => SetValue(HighlighterProperty, value); }
    public Brush? HighlightBrush { get => (Brush?)GetValue(HighlightBrushProperty); set => SetValue(HighlightBrushProperty, value); }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((HighlightTextBlock)d).Rebuild();

    private void Rebuild()
    {
        string text = SourceText ?? "";
        var highlighter = Highlighter;
        if (highlighter == null || highlighter.IsEmpty || text.Length == 0)
        {
            Text = text;
            return;
        }
        var ranges = highlighter.Find(text);
        if (ranges.Count == 0)
        {
            Text = text;
            return;
        }
        var brush = HighlightBrush;
        Inlines.Clear();
        int pos = 0;
        foreach (var (start, length) in ranges)
        {
            if (start > pos) Inlines.Add(new Run(text[pos..start]));
            var run = new Run(text.Substring(start, length)) { FontWeight = FontWeights.SemiBold };
            if (brush != null) run.Foreground = brush;
            Inlines.Add(run);
            pos = start + length;
        }
        if (pos < text.Length) Inlines.Add(new Run(text[pos..]));
    }
}
