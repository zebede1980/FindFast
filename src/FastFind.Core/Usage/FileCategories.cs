using FastFind.Core.Search;

namespace FastFind.Core.Usage;

/// <summary>The broad kinds of file the disk usage map tells apart by colour. The order is fixed: it is also the legend order.</summary>
public enum FileCategory : byte
{
    Other = 0,
    /// <summary>Libraries, drivers and the other parts programs and Windows are made of.</summary>
    System,
    Archives,
    Applications,
    Data,
    Images,
    Code,
    Documents,
    /// <summary>Video and audio.</summary>
    Media,
}

public static class FileCategories
{
    public const int Count = 9;

    private static readonly string[] Names = { "Other", "System", "Archives", "Applications", "Data", "Images", "Code", "Documents", "Media" };

    private static readonly Dictionary<string, FileCategory> ByExtension = Build();
    private static readonly Dictionary<string, FileCategory>.AlternateLookup<ReadOnlySpan<char>> Lookup = ByExtension.GetAlternateLookup<ReadOnlySpan<char>>();

    public static string Name(FileCategory category) => Names[(int)category];

    /// <summary>The category of an extension given without its dot ("mp4"). Anything unrecognised is <see cref="FileCategory.Other"/>.</summary>
    public static FileCategory Of(ReadOnlySpan<char> extension)
        => extension.Length > 0 && Lookup.TryGetValue(extension, out var category) ? category : FileCategory.Other;

    private static Dictionary<string, FileCategory> Build()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        // First claim wins, so an extension two groups share (ts, ps1) goes to the group listed first.
        void Add(FileCategory category, IEnumerable<string> extensions)
        {
            foreach (var e in extensions) map.TryAdd(e, category);
        }
        Add(FileCategory.Code, FileKinds.Groups["Code"]);
        Add(FileCategory.Code, new[]
        {
            "vb", "fs", "kt", "swift", "lua", "pl", "r", "m", "cshtml", "razor", "vue", "less", "toml", "ini", "config", "props", "targets", "resx", "map",
            "lock", "gitignore", "ipynb", "bat", "cmd", "psm1", "psd1", "mjs", "cjs", "pyc", "pyi", "class",
        });
        Add(FileCategory.Documents, FileKinds.Groups["Documents"]);
        Add(FileCategory.Documents, new[] { "epub", "mobi", "pages", "numbers", "key", "xps", "chm" });
        Add(FileCategory.Images, FileKinds.Groups["Images"]);
        Add(FileCategory.Images, new[] { "avif", "jfif", "xcf", "ai", "eps", "arw", "orf", "rw2" });
        Add(FileCategory.Media, FileKinds.Groups["Video"]);
        Add(FileCategory.Media, FileKinds.Groups["Audio"]);
        Add(FileCategory.Media, new[] { "vob", "m2ts", "mts", "ogv", "asf", "rm" });
        Add(FileCategory.Archives, FileKinds.Groups["Archives"]);
        Add(FileCategory.Archives, new[] { "zst", "lz", "lzma", "jar", "war", "wim", "esd", "msu", "msp", "vsix", "apk", "deb", "rpm", "dmg", "ppkg", "msi", "msix", "appx", "msixbundle", "appxbundle" });
        Add(FileCategory.Applications, FileKinds.Groups["Executables"]);
        Add(FileCategory.System, new[]
        {
            "dll", "sys", "ocx", "drv", "cpl", "mui", "efi", "so", "dylib", "pyd", "node", "winmd", "lib", "obj", "pdb", "cat", "tlb", "ax", "rll",
            "nls", "ttf", "otf", "ttc", "fon", "inf", "manifest", "mum", "pri", "tlog", "ilk", "pch", "ipch", "a", "o",
        });
        Add(FileCategory.Data, new[]
        {
            "mdf", "ldf", "ndf", "bak", "db", "sqlite", "sqlite3", "db3", "mdb", "accdb", "dbf", "edb", "pst", "ost", "nsf", "dat", "bin", "data", "blob",
            "vhd", "vhdx", "vmdk", "vdi", "qcow2", "img", "dmp", "mdmp", "hdmp", "etl", "evtx", "log", "pak", "cache", "idx", "pack", "parquet", "tmp", "trn", "dacpac", "bacpac",
        });
        return map;
    }
}
