namespace FastFind.Core.Search;

public sealed class QueryOptions
{
    public bool MatchCase { get; set; }
    public bool UseRegex { get; set; }
    public bool WholeWord { get; set; }
    public bool MatchPath { get; set; }
}

/// <summary>Structured filters from the UI, merged into the query as extra AND clauses.</summary>
public sealed class FilterSet
{
    public EntryKind Kind { get; set; } = EntryKind.Any;
    public IReadOnlyCollection<string>? Extensions { get; set; }
    public long? MinSize { get; set; }
    public long? MaxSize { get; set; }
    public DateTime? ModifiedFromUtc { get; set; }
    public DateTime? ModifiedToUtcExclusive { get; set; }
    public DateTime? CreatedFromUtc { get; set; }
    public DateTime? CreatedToUtcExclusive { get; set; }
    public string? ScopeFolder { get; set; }
    public bool ScopeRecursive { get; set; } = true;
    public bool HideHiddenAndSystem { get; set; }

    public bool IsEmpty => Kind == EntryKind.Any && (Extensions == null || Extensions.Count == 0) && MinSize == null && MaxSize == null
        && ModifiedFromUtc == null && ModifiedToUtcExclusive == null && CreatedFromUtc == null && CreatedToUtcExclusive == null
        && string.IsNullOrWhiteSpace(ScopeFolder) && !HideHiddenAndSystem;
}

/// <summary>
/// Parses Everything-style search text. Space = AND, | = OR (binds tighter), ! = NOT, "quotes" for phrases,
/// * and ? wildcards, and functions such as ext: size: dm: dc: path: folder: file: infolder: parent: regex: case: attrib:.
/// </summary>
public static class QueryParser
{
    private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ext", "size", "dm", "datemodified", "dc", "datecreated", "path", "name", "folder", "file", "infolder", "parent",
        "regex", "case", "nocase", "wfn", "wholeword", "ww", "empty", "attrib", "attributes", "type", "kind",
    };

    public static SearchQuery Parse(string? text, QueryOptions? options = null, FilterSet? filters = null, DateTime? now = null)
    {
        options ??= new QueryOptions();
        var query = new SearchQuery();
        var tokens = Tokenize(text ?? "");

        OrClause? current = null;
        bool joinNext = false;
        foreach (var token in tokens)
        {
            if (token.IsOr)
            {
                joinNext = current != null;
                continue;
            }
            Term term;
            try { term = BuildTerm(token, options, now ?? DateTime.Now); }
            catch (ArgumentException ex)
            {
                query.Error ??= "Invalid pattern: " + ex.Message;
                term = new NeverTerm();
            }
            if (joinNext && current != null)
            {
                current.Alternatives.Add(term);
                joinNext = false;
            }
            else
            {
                current = new OrClause();
                current.Alternatives.Add(term);
                query.Clauses.Add(current);
            }
        }

        if (filters != null) AppendFilters(query, filters);
        query.Optimise();
        return query;
    }

    private static void AppendFilters(SearchQuery query, FilterSet f)
    {
        void And(Term t) { var c = new OrClause(); c.Alternatives.Add(t); query.Clauses.Add(c); }

        if (f.Kind != EntryKind.Any) And(new KindTerm(f.Kind));
        if (f.Extensions is { Count: > 0 }) And(new ExtensionTerm(f.Extensions));
        if (f.MinSize != null || f.MaxSize != null) And(new SizeTerm(f.MinSize ?? 0, f.MaxSize ?? long.MaxValue));
        if (f.ModifiedFromUtc != null || f.ModifiedToUtcExclusive != null)
            And(new DateTerm(DateField.Modified, f.ModifiedFromUtc ?? DateTime.MinValue, f.ModifiedToUtcExclusive ?? DateTime.MaxValue));
        if (f.CreatedFromUtc != null || f.CreatedToUtcExclusive != null)
            And(new DateTerm(DateField.Created, f.CreatedFromUtc ?? DateTime.MinValue, f.CreatedToUtcExclusive ?? DateTime.MaxValue));
        if (!string.IsNullOrWhiteSpace(f.ScopeFolder)) And(new FolderScopeTerm(f.ScopeFolder, f.ScopeRecursive));
        if (f.HideHiddenAndSystem)
        {
            And(new AttributeTerm(FileAttributes.Hidden) { Negated = true });
            And(new AttributeTerm(FileAttributes.System) { Negated = true });
        }
    }

    private static Term BuildTerm(Token token, QueryOptions options, DateTime now)
    {
        string text = token.Text;
        bool negated = false;
        if (!token.Quoted && text.StartsWith('!') && text.Length > 1)
        {
            negated = true;
            text = text[1..];
        }

        Term term;
        int colon = token.Quoted ? -1 : text.IndexOf(':');
        if (colon > 0 && Functions.Contains(text[..colon]))
        {
            string fn = text[..colon].ToLowerInvariant();
            string value = text[(colon + 1)..];
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            term = BuildFunction(fn, value, options, now);
        }
        else
        {
            term = BuildTextTerm(text, options, options.MatchCase, options.MatchPath);
        }
        term.Negated = negated;
        return term;
    }

    private static Term BuildTextTerm(string text, QueryOptions options, bool caseSensitive, bool matchPath)
    {
        var mode = options.UseRegex ? TextMatchMode.Regex : options.WholeWord ? TextMatchMode.WholeWord : TextMatchMode.Substring;
        if (mode == TextMatchMode.WholeWord && (text.Contains('*') || text.Contains('?'))) mode = TextMatchMode.Substring;
        return new TextTerm(text, mode, caseSensitive, matchPath);
    }

    private static Term BuildFunction(string fn, string value, QueryOptions options, DateTime now)
    {
        switch (fn)
        {
            case "ext":
                return new ExtensionTerm(value.Split(';', ',', '|'));
            case "size":
                return SizeParser.TryParseRange(value, out long min, out long max) ? new SizeTerm(min, max) : new NeverTerm();
            case "dm":
            case "datemodified":
                return DateRangeParser.TryParse(value, now, out var dmLo, out var dmHi) ? new DateTerm(DateField.Modified, dmLo, dmHi) : new NeverTerm();
            case "dc":
            case "datecreated":
                return DateRangeParser.TryParse(value, now, out var dcLo, out var dcHi) ? new DateTerm(DateField.Created, dcLo, dcHi) : new NeverTerm();
            case "path":
                return BuildTextTerm(value, options, options.MatchCase, matchPath: true);
            case "name":
                return BuildTextTerm(value, options, options.MatchCase, matchPath: false);
            case "folder":
                return value.Length == 0 ? new KindTerm(EntryKind.Folder) : Combine(new KindTerm(EntryKind.Folder), BuildTextTerm(value, options, options.MatchCase, options.MatchPath));
            case "file":
                return value.Length == 0 ? new KindTerm(EntryKind.File) : Combine(new KindTerm(EntryKind.File), BuildTextTerm(value, options, options.MatchCase, options.MatchPath));
            case "type":
            case "kind":
                return value.ToLowerInvariant() switch
                {
                    "folder" or "folders" or "dir" or "directory" => new KindTerm(EntryKind.Folder),
                    "file" or "files" => new KindTerm(EntryKind.File),
                    _ => FileKinds.TryGet(value, out var exts) ? new ExtensionTerm(exts) : new NeverTerm(),
                };
            case "infolder":
                return new FolderScopeTerm(value, recursive: true);
            case "parent":
                return new FolderScopeTerm(value, recursive: false);
            case "regex":
                return new TextTerm(value, TextMatchMode.Regex, options.MatchCase, options.MatchPath);
            case "case":
                return BuildTextTerm(value, options, caseSensitive: true, options.MatchPath);
            case "nocase":
                return BuildTextTerm(value, options, caseSensitive: false, options.MatchPath);
            case "wfn":
                return new TextTerm(value, value.Contains('*') || value.Contains('?') ? TextMatchMode.Wildcard : TextMatchMode.Exact, options.MatchCase, matchPath: false);
            case "wholeword":
            case "ww":
                return new TextTerm(value, TextMatchMode.WholeWord, options.MatchCase, options.MatchPath);
            case "empty":
                return new EmptyTerm();
            case "attrib":
            case "attributes":
            {
                FileAttributes mask = 0;
                foreach (char c in value.ToLowerInvariant())
                {
                    mask |= c switch
                    {
                        'h' => FileAttributes.Hidden, 's' => FileAttributes.System, 'r' => FileAttributes.ReadOnly,
                        'a' => FileAttributes.Archive, 'd' => FileAttributes.Directory, 'c' => FileAttributes.Compressed,
                        'e' => FileAttributes.Encrypted, 'l' => FileAttributes.ReparsePoint, 't' => FileAttributes.Temporary,
                        'o' => FileAttributes.Offline, 'n' => FileAttributes.NotContentIndexed, 'p' => FileAttributes.SparseFile,
                        _ => 0,
                    };
                }
                return mask == 0 ? new NeverTerm() : new AttributeTerm(mask);
            }
            default:
                return new NeverTerm();
        }
    }

    private static Term Combine(Term a, Term b) => new AndTerm(a, b);

    private sealed class AndTerm : Term
    {
        private readonly Term _a, _b;
        public AndTerm(Term a, Term b) { _a = a; _b = b; }
        public override int Cost => Math.Max(_a.Cost, _b.Cost);
        protected override bool MatchesCore(Indexing.VolumeIndex volume, int index, string name)
            => _a.Matches(volume, index, name) && _b.Matches(volume, index, name);
    }

    internal readonly record struct Token(string Text, bool Quoted, bool IsOr);

    internal static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;
        bool sawQuote = false;

        void Flush()
        {
            if (sb.Length > 0 || sawQuote)
            {
                tokens.Add(new Token(sb.ToString(), sawQuote && !sb.ToString().Contains(':'), false));
                sb.Clear();
            }
            sawQuote = false;
        }

        foreach (char c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                sawQuote = true;
                continue;
            }
            if (inQuotes) { sb.Append(c); continue; }
            if (char.IsWhiteSpace(c)) { Flush(); continue; }
            if (c == '|')
            {
                Flush();
                tokens.Add(new Token("|", false, true));
                continue;
            }
            sb.Append(c);
        }
        Flush();
        return tokens;
    }
}

/// <summary>Named groups of extensions used by the "Kind" filter and the type: function.</summary>
public static class FileKinds
{
    public static readonly IReadOnlyDictionary<string, string[]> Groups = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Documents"] = new[] { "doc", "docx", "docm", "xls", "xlsx", "xlsm", "ppt", "pptx", "pdf", "txt", "rtf", "odt", "ods", "odp", "md", "csv", "one", "vsd", "vsdx", "msg", "eml" },
        ["Images"] = new[] { "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "svg", "ico", "heic", "raw", "psd", "cr2", "nef", "dng" },
        ["Audio"] = new[] { "mp3", "wav", "flac", "aac", "ogg", "wma", "m4a", "opus", "aiff", "mid" },
        ["Video"] = new[] { "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpg", "mpeg", "ts", "3gp" },
        ["Archives"] = new[] { "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "cab", "tgz", "nupkg" },
        ["Executables"] = new[] { "exe", "msi", "bat", "cmd", "ps1", "com", "scr", "msix", "appx" },
        ["Code"] = new[] { "cs", "js", "ts", "tsx", "jsx", "py", "java", "cpp", "c", "h", "hpp", "go", "rs", "rb", "php", "html", "css", "scss", "json", "xml", "yaml", "yml", "sql", "sh", "xaml", "csproj", "sln", "tf", "ps1" },
    };

    public static bool TryGet(string name, out string[] extensions)
    {
        if (Groups.TryGetValue(name, out var e)) { extensions = e; return true; }
        extensions = Array.Empty<string>();
        return false;
    }
}
