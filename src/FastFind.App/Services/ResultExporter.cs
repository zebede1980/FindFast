using System.Globalization;
using System.IO;
using System.Text;
using FastFind.Core.Indexing;
using FastFind.Core.Search;

namespace FastFind.App.Services;

/// <summary>Writes search results to CSV, a plain list of paths, or an Everything-compatible EFU file list.</summary>
public static class ResultExporter
{
    public const string DialogFilter = "CSV (*.csv)|*.csv|Text, one full path per line (*.txt)|*.txt|Everything file list (*.efu)|*.efu";

    /// <summary>Exports the given hit positions (or every hit when null). Runs on the calling thread; call from a background task.</summary>
    public static int Export(SearchResult result, IReadOnlyList<int>? positions, string path, CancellationToken ct = default)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16);
        int count = positions?.Count ?? result.Count;

        if (ext == ".csv") writer.WriteLine("Name,Path,Full Path,Size,Date Modified,Date Created,Type");
        else if (ext == ".efu") writer.WriteLine("Filename,Size,Date Modified,Date Created,Attributes");

        foreach (var vol in result.Volumes) vol.Lock.EnterReadLock();
        try
        {
            for (int n = 0; n < count; n++)
            {
                if ((n & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
                int pos = positions?[n] ?? n;
                var hit = result.Hits[pos];
                var vol = result.Volumes[hit.Volume];
                string? name = vol.GetName(hit.Index);
                if (name == null) continue;
                ref var m = ref vol.GetMeta(hit.Index);
                string dir = vol.GetParentPath(hit.Index);
                string full = VolumeIndex.Combine(dir, name);

                switch (ext)
                {
                    case ".csv":
                        writer.Write(Csv(name)); writer.Write(',');
                        writer.Write(Csv(dir)); writer.Write(',');
                        writer.Write(Csv(full)); writer.Write(',');
                        writer.Write(m.IsDirectory ? "" : m.Size.ToString(CultureInfo.InvariantCulture)); writer.Write(',');
                        writer.Write(Iso(m.ModifiedFileTime)); writer.Write(',');
                        writer.Write(Iso(m.CreatedFileTime)); writer.Write(',');
                        writer.WriteLine(m.IsDirectory ? "Folder" : Path.GetExtension(name).TrimStart('.').ToUpperInvariant());
                        break;
                    case ".efu":
                        // Everything's format: FILETIME integers, numeric attribute flags.
                        writer.Write(Csv(full)); writer.Write(',');
                        writer.Write(m.IsDirectory ? "" : m.Size.ToString(CultureInfo.InvariantCulture)); writer.Write(',');
                        writer.Write(m.ModifiedFileTime > 0 ? m.ModifiedFileTime.ToString(CultureInfo.InvariantCulture) : ""); writer.Write(',');
                        writer.Write(m.CreatedFileTime > 0 ? m.CreatedFileTime.ToString(CultureInfo.InvariantCulture) : ""); writer.Write(',');
                        writer.WriteLine(((int)m.Attributes).ToString(CultureInfo.InvariantCulture));
                        break;
                    default:
                        writer.WriteLine(full);
                        break;
                }
            }
        }
        finally
        {
            foreach (var vol in result.Volumes) vol.Lock.ExitReadLock();
        }
        return count;
    }

    private static string Csv(string s)
        => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";

    private static string Iso(long fileTime)
        => fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "";
}
