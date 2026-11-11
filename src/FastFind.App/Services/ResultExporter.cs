using System.Globalization;
using System.IO;
using System.Text;
using FastFind.Core.Api;

namespace FastFind.App.Services;

/// <summary>Writes the rows of a pane to CSV, a plain list of paths, or an Everything-compatible EFU file list.</summary>
public static class ResultExporter
{
    public const string DialogFilter = "CSV (*.csv)|*.csv|Text, one full path per line (*.txt)|*.txt|Everything file list (*.efu)|*.efu";
    private const int Page = 4096;

    /// <summary>Exports the given row positions (or every row when null). Runs on the calling thread; call from a background task.</summary>
    public static int Export(IResultSet result, IReadOnlyList<int>? positions, string path, CancellationToken ct = default)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16);

        if (ext == ".csv") writer.WriteLine("Name,Path,Full Path,Size,Date Modified,Date Created,Type");
        else if (ext == ".efu") writer.WriteLine("Filename,Size,Date Modified,Date Created,Attributes");

        int written = 0;
        if (positions == null)
        {
            for (int offset = 0; offset < result.Count; offset += Page)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var e in result.GetRows(offset, Page))
                    if (Write(writer, ext, e)) written++;
            }
        }
        else
        {
            // Selected rows: fetch each run of consecutive positions in one call.
            int i = 0;
            while (i < positions.Count)
            {
                ct.ThrowIfCancellationRequested();
                int start = positions[i];
                int length = 1;
                while (i + length < positions.Count && positions[i + length] == start + length && length < Page) length++;
                foreach (var e in result.GetRows(start, length))
                    if (Write(writer, ext, e)) written++;
                i += length;
            }
        }
        return written;
    }

    private static bool Write(StreamWriter writer, string ext, in FileEntry e)
    {
        if (e.Name == null) return false;
        string full = e.FullPath;
        switch (ext)
        {
            case ".csv":
                writer.Write(Csv(e.Name)); writer.Write(',');
                writer.Write(Csv(e.Directory)); writer.Write(',');
                writer.Write(Csv(full)); writer.Write(',');
                writer.Write(e.IsDirectory && e.Size == 0 ? "" : e.Size.ToString(CultureInfo.InvariantCulture)); writer.Write(',');
                writer.Write(Iso(e.ModifiedFileTime)); writer.Write(',');
                writer.Write(Iso(e.CreatedFileTime)); writer.Write(',');
                writer.WriteLine(e.IsDirectory ? "Folder" : Path.GetExtension(e.Name).TrimStart('.').ToUpperInvariant());
                break;
            case ".efu":
                // Everything's format: FILETIME integers, numeric attribute flags, no size for folders.
                writer.Write(Csv(full)); writer.Write(',');
                writer.Write(e.IsDirectory ? "" : e.Size.ToString(CultureInfo.InvariantCulture)); writer.Write(',');
                writer.Write(e.ModifiedFileTime > 0 ? e.ModifiedFileTime.ToString(CultureInfo.InvariantCulture) : ""); writer.Write(',');
                writer.Write(e.CreatedFileTime > 0 ? e.CreatedFileTime.ToString(CultureInfo.InvariantCulture) : ""); writer.Write(',');
                writer.WriteLine(((int)e.Attributes).ToString(CultureInfo.InvariantCulture));
                break;
            default:
                writer.WriteLine(full);
                break;
        }
        return true;
    }

    private static string Csv(string s)
        => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";

    private static string Iso(long fileTime)
        => fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "";
}
