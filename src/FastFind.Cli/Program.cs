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

//   FastFind.Cli --usage <folder> [--analyse] [--walk] [--max N]
// Adds up the space under a folder (from the index service, or from disk with --walk) and prints its largest contents;
// with --analyse, also the space each application takes and what could be removed.

bool forceWalk = false;
bool useService = false;
int max = 20;
string? root = null;
string? usage = null;
bool analyse = false;
var queryParts = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--usage" when i + 1 < args.Length: usage = args[++i]; break;
        case "--analyse": case "--analyze": analyse = true; break;
        case "--walk": forceWalk = true; break;
        case "--service": useService = true; break;
        case "--max" when i + 1 < args.Length: int.TryParse(args[++i], out max); break;
        case "--root" when i + 1 < args.Length: root = args[++i]; break;
        default: queryParts.Add(args[i]); break;
    }
}

if (usage != null)
{
    string folder = FastFind.Core.Browse.PathUtil.Normalize(usage);
    if (folder.Length == 0 || !Directory.Exists(folder)) { Console.Error.WriteLine($"'{usage}' is not a folder."); return 2; }
    var sw = Stopwatch.StartNew();
    FastFind.Core.Usage.SizeTree? tree = null;
    if (!forceWalk)
    {
        using var client = FastFind.Core.Api.PipeIndexClient.TryConnect(FastFind.Core.Api.PipeProtocol.PipeName(FastFind.Core.Api.PipeProtocol.CurrentUserSid()), 2000);
        if (client != null && client.Volumes.Any(v => v.Covers(folder)))
            tree = await FastFind.Core.Usage.SizeTreeLoader.FromIndexAsync(client, folder);
        if (tree == null) Console.Error.WriteLine("The index service has nothing for this folder; reading the disk instead.");
    }
    bool fromDisk = tree == null;
    tree ??= FastFind.Core.Usage.SizeTreeLoader.FromDisk(folder);
    Console.Error.WriteLine($"{tree.Count - 1:N0} entries {(fromDisk ? "read from disk" : "from the index")} in {sw.Elapsed.TotalSeconds:F2}s");
    Console.WriteLine($"{SizeParser.Format(tree.Size(0)),10}  {tree.FileCount(0),9:N0} files  {tree.RootPath}");
    foreach (int child in tree.Children(0)[..Math.Min(max, tree.ChildCount(0))])
        Console.WriteLine($"{SizeParser.Format(tree.Size(child)),10}  {(tree.IsDirectory(child) ? tree.FileCount(child).ToString("N0") + " files" : ""),15}  {tree.Name(child)}");
    if (tree.CloudFiles > 0) Console.WriteLine($"plus {SizeParser.Format(tree.CloudBytes)} in {tree.CloudFiles:N0} online-only files that take no space on this disk");
    Console.WriteLine();
    foreach (var stat in tree.ExtensionStats().Take(10))
        Console.WriteLine($"{SizeParser.Format(stat.Bytes),10}  {stat.Files,9:N0} files  .{stat.Extension,-10} {FastFind.Core.Usage.FileCategories.Name(stat.Category)}");
    if (analyse)
    {
        sw.Restart();
        var report = FastFind.Core.Usage.UsageAnalyzer.Analyze(tree, FastFind.Core.Usage.UsageEnvironment.Current());
        Console.Error.WriteLine($"analysed in {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"\nApplications: {report.Apps.Count:N0} take {SizeParser.Format(report.AppBytes)}");
        foreach (var app in report.Apps.Take(max))
            Console.WriteLine($"{SizeParser.Format(app.Total),10}  {app.Name,-38} program {SizeParser.Format(app.Program),9}  data {SizeParser.Format(app.Data),9}  caches {SizeParser.Format(app.Caches),9}  logs {SizeParser.Format(app.Logs),9}"
                + (app.ClearableBytes > 0 ? $"  (clear {SizeParser.Format(app.ClearableBytes)})" : ""));
        Console.WriteLine($"\nCould be removed ({SizeParser.Format(report.SafeBytes)} of it safely):");
        // Each with the biggest of the folders it covers, so the judgement can be checked.
        int shown = int.TryParse(Environment.GetEnvironmentVariable("FASTFIND_CLI_FOLDERS"), out int n) ? n : 3;
        foreach (var c in report.Candidates.OrderBy(c => c.Safety).ThenByDescending(c => c.Bytes))
        {
            Console.WriteLine($"{SizeParser.Format(c.Bytes),10}  {c.Files,8:N0} files  [{c.Safety}/{c.Action}] {c.Title}");
            foreach (int node in c.Nodes.OrderByDescending(tree.Size).Take(shown))
                Console.WriteLine($"{"",12}{SizeParser.Format(tree.Size(node)),10}  {tree.GetPath(node)}");
        }
    }
    return 0;
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
