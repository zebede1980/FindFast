using System.Text;

namespace FastFind.Core.Indexing;

/// <summary>
/// Binary snapshot of a directory-walk index so the next launch is searchable immediately
/// while a fresh scan runs in the background. (MFT-indexed volumes rebuild in seconds and skip the cache.)
/// </summary>
public static class IndexCache
{
    private const int Magic = 0x46464349; // "ICFF"
    private const int Version = 1;

    public static string PathFor(string cacheDirectory, string root)
        => Path.Combine(cacheDirectory, $"{root.TrimEnd('\\', ':').Replace('\\', '_').Replace('/', '_')}.idx");

    public static void Save(VolumeIndex index, string cacheDirectory)
    {
        Directory.CreateDirectory(cacheDirectory);
        string path = PathFor(cacheDirectory, index.Root);
        string temp = path + ".tmp";
        index.Lock.EnterReadLock();
        try
        {
            using var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            using var w = new BinaryWriter(stream, Encoding.UTF8);
            w.Write(Magic);
            w.Write(Version);
            w.Write(index.Root);
            w.Write(index.BuiltAtUtc.Ticks);
            var names = index.NamesArray;
            var meta = index.MetaArray;
            int count = index.Count;
            // Remap live slots to a dense range so tombstones are dropped.
            var remap = new int[count];
            int live = 0;
            for (int i = 0; i < count; i++) remap[i] = names[i] != null ? live++ : -1;
            w.Write(live);
            for (int i = 0; i < count; i++)
            {
                if (names[i] == null) continue;
                ref var m = ref meta[i];
                w.Write(names[i]!);
                w.Write(m.Parent < 0 ? -1 : remap[m.Parent]);
                w.Write((int)m.Attributes);
                w.Write(m.Size);
                w.Write(m.ModifiedFileTime);
                w.Write(m.CreatedFileTime);
            }
        }
        finally
        {
            index.Lock.ExitReadLock();
        }
        File.Move(temp, path, overwrite: true);
    }

    public static VolumeIndex? TryLoad(string cacheDirectory, string root, TimeSpan maxAge)
    {
        string path = PathFor(cacheDirectory, root);
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var r = new BinaryReader(stream, Encoding.UTF8);
            if (r.ReadInt32() != Magic || r.ReadInt32() != Version) return null;
            string storedRoot = r.ReadString();
            if (!string.Equals(storedRoot, root, StringComparison.OrdinalIgnoreCase)) return null;
            var builtAt = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
            if (DateTime.UtcNow - builtAt > maxAge) return null;
            int count = r.ReadInt32();
            if (count < 1 || count > 200_000_000) return null;

            var names = new string?[count + count / 8 + 64];
            var meta = new EntryMeta[names.Length];
            var byKey = new Dictionary<ulong, int>();
            for (int i = 0; i < count; i++)
            {
                names[i] = r.ReadString();
                meta[i] = new EntryMeta
                {
                    Key = (ulong)i + 1,
                    Parent = r.ReadInt32(),
                    Attributes = (FileAttributes)r.ReadInt32(),
                    Size = r.ReadInt64(),
                    ModifiedFileTime = r.ReadInt64(),
                    CreatedFileTime = r.ReadInt64(),
                };
                if (meta[i].Parent >= count) return null;
            }
            var index = new VolumeIndex(root, names, meta, count, byKey, 0, IndexMode.DirectoryWalk) { BuiltAtUtc = builtAt };
            return index;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
