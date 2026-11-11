using System.Text.RegularExpressions;
using FastFind.Core.Indexing;

namespace FastFind.Core.Search;

/// <summary>A parsed query: a conjunction of clauses, each clause a disjunction of terms (Everything precedence: OR binds tighter than AND).</summary>
public sealed class SearchQuery
{
    public List<OrClause> Clauses { get; } = new();
    public bool IsEmpty => Clauses.Count == 0;
    public string? Error { get; set; }

    public bool Matches(VolumeIndex volume, int index, string name)
    {
        foreach (var clause in Clauses)
        {
            if (!clause.Matches(volume, index, name)) return false;
        }
        return true;
    }

    /// <summary>All positive text terms in the query (used for result highlighting).</summary>
    public IEnumerable<TextTerm> TextTerms()
    {
        foreach (var c in Clauses)
            foreach (var t in c.Alternatives)
            {
                if (t is TextTerm tt && !tt.Negated) yield return tt;
                else if (t is IHasInnerTerms inner)
                    foreach (var x in inner.InnerTerms()) if (x is TextTerm it && !it.Negated) yield return it;
            }
    }

    /// <summary>Orders clauses so cheap name/metadata checks run before path building.</summary>
    public void Optimise()
    {
        foreach (var c in Clauses) c.Alternatives.Sort((a, b) => a.Cost.CompareTo(b.Cost));
        Clauses.Sort((a, b) => a.Alternatives.Max(t => t.Cost).CompareTo(b.Alternatives.Max(t => t.Cost)));
    }
}

public sealed class OrClause
{
    public List<Term> Alternatives { get; } = new();

    public bool Matches(VolumeIndex volume, int index, string name)
    {
        var alts = Alternatives;
        for (int i = 0; i < alts.Count; i++)
        {
            if (alts[i].Matches(volume, index, name)) return true;
        }
        return false;
    }
}

public interface IHasInnerTerms
{
    IEnumerable<Term> InnerTerms();
}

public abstract class Term
{
    public bool Negated { get; set; }
    public abstract int Cost { get; }

    public bool Matches(VolumeIndex volume, int index, string name)
        => MatchesCore(volume, index, name) != Negated;

    protected abstract bool MatchesCore(VolumeIndex volume, int index, string name);
}

public enum TextMatchMode { Substring, Wildcard, Regex, Exact, WholeWord }

/// <summary>Matches against the file name, or the full path when <see cref="MatchPath"/> is set.</summary>
public sealed class TextTerm : Term
{
    public string Text { get; }
    public TextMatchMode Mode { get; }
    public bool CaseSensitive { get; }
    public bool MatchPath { get; }
    private readonly Regex? _regex;
    private readonly StringComparison _comparison;

    public override int Cost => MatchPath ? 50 : (Mode == TextMatchMode.Regex || Mode == TextMatchMode.Wildcard ? 5 : 1);

    public TextTerm(string text, TextMatchMode mode, bool caseSensitive, bool matchPath)
    {
        Text = text;
        CaseSensitive = caseSensitive;
        MatchPath = matchPath;
        _comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (mode == TextMatchMode.Substring && (text.Contains('*') || text.Contains('?'))) mode = TextMatchMode.Wildcard;
        Mode = mode;
        var options = RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        var timeout = TimeSpan.FromMilliseconds(250);
        _regex = mode switch
        {
            TextMatchMode.Wildcard => new Regex(WildcardToRegex(text, matchPath), options, timeout),
            TextMatchMode.Regex => new Regex(text, options, timeout),
            _ => null,
        };
    }

    protected override bool MatchesCore(VolumeIndex volume, int index, string name)
    {
        if (!MatchPath)
            return MatchString(name);

        // Match against "<parent dir>\<name>" without allocating the full path unless the term could straddle the separator.
        string dir = volume.GetParentPath(index);
        switch (Mode)
        {
            case TextMatchMode.Substring:
                return PathContains(dir, name, Text, _comparison);
            case TextMatchMode.Exact:
                return VolumeIndex.Combine(dir, name).Equals(Text, _comparison);
            default:
                return MatchString(VolumeIndex.Combine(dir, name));
        }
    }

    private bool MatchString(string s)
    {
        switch (Mode)
        {
            case TextMatchMode.Substring:
                return s.Contains(Text, _comparison);
            case TextMatchMode.Exact:
                return s.Equals(Text, _comparison);
            case TextMatchMode.WholeWord:
                return ContainsWholeWord(s, Text, _comparison);
            default:
                try { return _regex!.IsMatch(s); }
                catch (RegexMatchTimeoutException) { return false; }
        }
    }

    /// <summary>Character ranges in <paramref name="s"/> that this term matches, for highlighting. Empty for negated terms.</summary>
    public void FindMatches(string s, List<(int Start, int Length)> output)
    {
        if (Negated || s.Length == 0) return;
        switch (Mode)
        {
            case TextMatchMode.Substring:
            case TextMatchMode.WholeWord:
            {
                if (Text.Length == 0) return;
                int start = 0;
                while (start <= s.Length - Text.Length)
                {
                    int i = s.IndexOf(Text, start, _comparison);
                    if (i < 0) break;
                    output.Add((i, Text.Length));
                    start = i + Text.Length;
                }
                break;
            }
            case TextMatchMode.Exact:
                if (s.Equals(Text, _comparison)) output.Add((0, s.Length));
                break;
            default:
                try
                {
                    foreach (System.Text.RegularExpressions.Match m in _regex!.Matches(s))
                    {
                        if (m.Length == 0) continue;
                        // Wildcard patterns match the whole string; highlight only the literal parts so * does not light up everything.
                        if (Mode == TextMatchMode.Wildcard) AddLiteralParts(s, m.Index, m.Length, output);
                        else output.Add((m.Index, m.Length));
                    }
                }
                catch (RegexMatchTimeoutException) { }
                break;
        }
    }

    private void AddLiteralParts(string s, int matchStart, int matchLength, List<(int Start, int Length)> output)
    {
        foreach (var literal in Text.Split(new[] { '*', '?' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int from = matchStart;
            int end = matchStart + matchLength;
            while (from < end)
            {
                int i = s.IndexOf(literal, from, end - from, _comparison);
                if (i < 0) break;
                output.Add((i, literal.Length));
                from = i + literal.Length;
            }
        }
    }

    public static bool PathContains(string dir, string name, string text, StringComparison comparison)
    {
        if (name.Contains(text, comparison)) return true;
        if (dir.Length == 0) return false;
        if (dir.Contains(text, comparison)) return true;
        if (text.Length <= 1) return false;
        // straddle check: last (len-1) chars of dir + '\' + first (len-1) chars of name
        int tail = Math.Min(dir.Length, text.Length - 1);
        int head = Math.Min(name.Length, text.Length - 1);
        string joint = dir[^1] == '\\'
            ? string.Concat(dir.AsSpan(dir.Length - tail), name.AsSpan(0, head))
            : string.Concat(dir.AsSpan(dir.Length - tail), "\\", name.AsSpan(0, head));
        return joint.Contains(text, comparison);
    }

    public static bool ContainsWholeWord(string s, string word, StringComparison comparison)
    {
        int start = 0;
        while (start <= s.Length - word.Length)
        {
            int i = s.IndexOf(word, start, comparison);
            if (i < 0) return false;
            bool leftOk = i == 0 || !char.IsLetterOrDigit(s[i - 1]);
            bool rightOk = i + word.Length >= s.Length || !char.IsLetterOrDigit(s[i + word.Length]);
            if (leftOk && rightOk) return true;
            start = i + 1;
        }
        return false;
    }

    public static string WildcardToRegex(string pattern, bool matchPath)
    {
        var sb = new System.Text.StringBuilder("^");
        foreach (char c in pattern)
        {
            sb.Append(c switch
            {
                '*' => matchPath ? ".*" : "[^\\\\]*",
                '?' => matchPath ? "." : "[^\\\\]",
                _ => Regex.Escape(c.ToString()),
            });
        }
        return sb.Append('$').ToString();
    }
}

public sealed class ExtensionTerm : Term
{
    private readonly HashSet<string> _extensions;
    public override int Cost => 2;

    public ExtensionTerm(IEnumerable<string> extensions)
    {
        _extensions = new HashSet<string>(extensions.Select(e => e.Trim().TrimStart('.').ToLowerInvariant()).Where(e => e.Length > 0));
    }

    protected override bool MatchesCore(VolumeIndex volume, int index, string name)
    {
        if (volume.MetaArray[index].IsDirectory) return false;
        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return _extensions.Contains("");
        return _extensions.Contains(name.AsSpan(dot + 1).ToString().ToLowerInvariant());
    }
}

public enum EntryKind { Any, File, Folder }

public sealed class KindTerm : Term
{
    public EntryKind Kind { get; }
    public override int Cost => 0;
    public KindTerm(EntryKind kind) => Kind = kind;

    protected override bool MatchesCore(VolumeIndex volume, int index, string name)
    {
        bool isDir = volume.MetaArray[index].IsDirectory;
        return Kind switch { EntryKind.File => !isDir, EntryKind.Folder => isDir, _ => true };
    }
}

public sealed class SizeTerm : Term
{
    public long Min { get; }
    public long Max { get; }
    public override int Cost => 0;
    /// <summary>When set the term matches folders by their aggregated size instead of files.</summary>
    public bool Folders { get; }
    public SizeTerm(long min, long max, bool folders = false) { Min = min; Max = max; Folders = folders; }

    protected override bool MatchesCore(VolumeIndex volume, int index, string name)
    {
        ref var m = ref volume.MetaArray[index];
        if (m.IsDirectory != Folders) return false;
        return m.Size >= Min && m.Size <= Max;
    }
}

public enum DateField { Modified, Created }

public sealed class DateTerm : Term
{
    public DateField Field { get; }
    public long MinFileTime { get; }
    public long MaxFileTime { get; }
    public override int Cost => 0;

    public DateTerm(DateField field, DateTime minUtc, DateTime maxUtcExclusive)
    {
        Field = field;
        MinFileTime = ToFileTime(minUtc);
        MaxFileTime = ToFileTime(maxUtcExclusive);
    }

    private static long ToFileTime(DateTime utc)
    {
        if (utc <= DateTime.FromFileTimeUtc(0)) return 0;
        if (utc >= DateTime.MaxValue.AddDays(-1)) return long.MaxValue;
        return utc.ToFileTimeUtc();
    }

    protected override bool MatchesCore(VolumeIndex volume, int index, string name)
    {
        ref var m = ref volume.MetaArray[index];
        long t = Field == DateField.Modified ? m.ModifiedFileTime : m.CreatedFileTime;
        return t >= MinFileTime && t < MaxFileTime;
    }
}

/// <summary>Restricts results to a folder (optionally recursive).</summary>
public sealed class FolderScopeTerm : Term
{
    private readonly string _folder;        // normalised, trailing backslash
    private readonly bool _recursive;
    public override int Cost => 40;

    public FolderScopeTerm(string folder, bool recursive)
    {
        _folder = folder.TrimEnd('\\') + "\\";
        _recursive = recursive;
    }

    protected override bool MatchesCore(VolumeIndex volume, int index, string name)
    {
        string dir = volume.GetParentPath(index);
        if (dir.Length == 0) return false;
        if (_recursive)
        {
            if (dir.Length + 1 < _folder.Length) return false;
            return dir.StartsWith(_folder, StringComparison.OrdinalIgnoreCase)
                   || (dir.Length + 1 == _folder.Length && _folder.StartsWith(dir, StringComparison.OrdinalIgnoreCase));
        }
        return dir.Length + 1 == _folder.Length
            ? _folder.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
            : dir.Equals(_folder, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class AttributeTerm : Term
{
    private readonly FileAttributes _mask;
    public override int Cost => 0;
    public AttributeTerm(FileAttributes mask) => _mask = mask;
    protected override bool MatchesCore(VolumeIndex volume, int index, string name)
        => (volume.MetaArray[index].Attributes & _mask) == _mask;
}

public sealed class EmptyTerm : Term
{
    public override int Cost => 0;
    protected override bool MatchesCore(VolumeIndex volume, int index, string name)
    {
        ref var m = ref volume.MetaArray[index];
        return !m.IsDirectory && m.Size == 0;
    }
}

/// <summary>A term that matches nothing (used when a function value could not be parsed).</summary>
public sealed class NeverTerm : Term
{
    public override int Cost => 0;
    protected override bool MatchesCore(VolumeIndex volume, int index, string name) => false;
}
