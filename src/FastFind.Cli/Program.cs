using System.Diagnostics;
using FastFind.Core.Indexing;
using FastFind.Core.Mft;
using FastFind.Core.Native;
using FastFind.Core.Search;

// Usage:
//   FastFind.Cli [--walk] [--root C:\] [query...]
// Builds an index (MFT when elevated, folder walk otherwise), prints stats, runs the query and an interactive loop.

//   FastFind.Cli --service [--max N] query...
// Sends the query to the running index service over its named pipe instead of building an index here: the same
// API the FastFind window uses, so scripts and other tools can search instantly too.

bool forceWalk = false;
bool useService = false;
int max = 20;
string? root = null;
var queryParts = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--walk": forceWalk = true; break;
        case "--service": useService = true; break;
        case "--max" when i + 1 < args.Length: int.TryParse(args[++i], out max); break;
        case "--root" when i + 1 < args.Length: root = args[++i]; break;
        default: queryParts.Add(args[i]); break;
    }
}

if (useService)
{
    using var client = FastFind.Core.Api.PipeIndexClient.TryConnect(FastFind.Core.Api.PipeProtocol.PipeName(FastFind.Core.Api.PipeProtocol.CurrentUserSid()), 2000);
    if (client == null)
    {
        Console.Error.WriteLine("The FastFind index service is not running. Install it from FastFind's Settings, or start FastFind.Service.exe.");
        return 2;
    }
    foreach (var v in client.Volumes) Console.Error.WriteLine($"{v.Root} {v.Status} {v.Entries:N0} entries ({v.Mode})");
    using var set = await client.SearchAsync(new FastFind.Core.Api.SearchRequest { Text = string.Join(' ', queryParts) });
    Console.Error.WriteLine($"{set.Count:N0} results in {set.Elapsed.TotalMilliseconds:F1} ms{(set.Error != null ? " (" + set.Error + ")" : "")}");
    // Paths go to standard output (one per line) so the command can be piped; everything else goes to standard error.
    foreach (var e in set.GetRows(0, max <= 0 ? set.Count : max))
        if (e.Name != null) Console.WriteLine(e.FullPath);
    return 0;
}

Console.WriteLine($"Elevated: {Elevation.IsElevated}");
var progress = new Progress<IndexProgress>(p => Console.Write($"\r{p.Root} {p.Phase} {p.Processed:N0}/{p.Total:N0}      "));
var indexes = new List<VolumeIndex>();
var roots = root != null ? new[] { root } : DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.Name).ToArray();

foreach (var r in roots)
{
    var sw = Stopwatch.StartNew();
    VolumeIndex index;
    bool isDriveRoot = r.Length == 3 && r[1] == ':';
    if (isDriveRoot && Elevation.IsElevated && !forceWalk)
    {
        try
        {
            index = MftReader.Read(r, progress);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nMFT read failed for {r}: {ex.Message}; falling back to folder walk");
            index = DirectoryWalkIndexer.Build(r, progress);
        }
    }
    else
    {
        index = DirectoryWalkIndexer.Build(r, progress);
    }
    Console.WriteLine($"\n{r}: {index.LiveCount:N0} entries in {sw.Elapsed.TotalSeconds:F2}s ({index.Mode})");
    sw.Restart();
    index.Lock.EnterReadLock();
    try { index.EnsureNameOrder(); } finally { index.Lock.ExitReadLock(); }
    Console.WriteLine($"   name order built in {sw.ElapsedMilliseconds} ms");
    indexes.Add(index);
}

string? line = queryParts.Count > 0 ? string.Join(' ', queryParts) : null;
while (true)
{
    if (line == null)
    {
        Console.Write("\nquery> ");
        line = Console.ReadLine();
        if (line == null || line == "exit") break;
    }
    var query = QueryParser.Parse(line);
    var result = SearchEngine.Search(indexes, query);
    Console.WriteLine($"{result.Count:N0} results in {result.Elapsed.TotalMilliseconds:F1} ms{(result.Error != null ? " (" + result.Error + ")" : "")}");
    foreach (var hit in result.Hits.Take(20))
    {
        var v = result.Volumes[hit.Volume];
        ref var m = ref v.GetMeta(hit.Index);
        string size = m.IsDirectory ? "<DIR>" : SizeParser.Format(m.Size);
        string date = m.ModifiedFileTime > 0 ? DateTime.FromFileTimeUtc(m.ModifiedFileTime).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
        Console.WriteLine($"  {size,10}  {date,16}  {v.GetFullPath(hit.Index)}");
    }
    if (queryParts.Count > 0) break;
    line = null;
}
return 0;
