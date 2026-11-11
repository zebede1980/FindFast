using System.IO;
using System.IO.Compression;

namespace FastFind.App.Services;

/// <summary>Zip and unzip using the framework's own compression, so no archiver needs to be installed.</summary>
public static class ZipOps
{
    public static bool IsZip(string path) => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Compresses the items into a new .zip in <paramref name="folder"/>, named after the item (or the folder when
    /// there are several). Returns the archive's path. Runs on the calling thread; call from a background task.
    /// </summary>
    public static string Compress(IReadOnlyList<string> paths, string folder, CancellationToken ct = default)
    {
        string baseName = paths.Count == 1 ? Path.GetFileNameWithoutExtension(paths[0].TrimEnd('\\')) : Path.GetFileName(folder.TrimEnd('\\'));
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "Archive";
        string zipPath = Path.Combine(folder, FileOperations.UniqueName(folder, baseName, ".zip"));
        string temp = zipPath + ".partial";
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                foreach (var path in paths)
                {
                    ct.ThrowIfCancellationRequested();
                    if (Directory.Exists(path)) AddDirectory(archive, path, Path.GetFileName(path.TrimEnd('\\')), ct);
                    else if (File.Exists(path)) archive.CreateEntryFromFile(path, Path.GetFileName(path), CompressionLevel.Optimal);
                }
            }
            File.Move(temp, zipPath);
            return zipPath;
        }
        catch
        {
            try { File.Delete(temp); } catch (Exception) { }
            throw;
        }
    }

    private static void AddDirectory(ZipArchive archive, string directory, string entryPrefix, CancellationToken ct)
    {
        bool any = false;
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            ct.ThrowIfCancellationRequested();
            archive.CreateEntryFromFile(file, entryPrefix + "/" + Path.GetFileName(file), CompressionLevel.Optimal);
            any = true;
        }
        foreach (var sub in Directory.EnumerateDirectories(directory))
        {
            // Following a junction or symlink could loop for ever or pull in an unrelated tree.
            if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
            AddDirectory(archive, sub, entryPrefix + "/" + Path.GetFileName(sub), ct);
            any = true;
        }
        if (!any) archive.CreateEntry(entryPrefix + "/"); // keep empty folders
    }

    /// <summary>Extracts an archive into a new folder beside it, named after the archive. Returns the folder's path.</summary>
    public static string Extract(string zipPath)
    {
        string parent = Path.GetDirectoryName(zipPath) ?? throw new IOException("Archive has no containing folder.");
        string target = Path.Combine(parent, FileOperations.UniqueName(parent, Path.GetFileNameWithoutExtension(zipPath)));
        // ExtractToDirectory refuses entries that would land outside the target folder.
        ZipFile.ExtractToDirectory(zipPath, target);
        return target;
    }
}
