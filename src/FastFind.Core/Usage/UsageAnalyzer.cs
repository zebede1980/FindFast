using FastFind.Core.Browse;
using FastFind.Core.Indexing;
using FastFind.Core.Search;

namespace FastFind.Core.Usage;

/// <summary>Where things live on this machine. Passed in rather than looked up, so the analysis can be tested against made-up trees.</summary>
public sealed class UsageEnvironment
{
    public string UserProfile { get; init; } = "";
    public string LocalAppData { get; init; } = "";
    public string RoamingAppData { get; init; } = "";
    public string LocalLowAppData { get; init; } = "";
    public string Temp { get; init; } = "";
    public string Downloads { get; init; } = "";
    public string ProgramFiles { get; init; } = "";
    public string ProgramFilesX86 { get; init; } = "";
    public string ProgramData { get; init; } = "";
    public string Windows { get; init; } = "";
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;

    public static UsageEnvironment Current(string? downloads = null)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new UsageEnvironment
        {
            UserProfile = profile,
            LocalAppData = local,
            RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            LocalLowAppData = local.Length > 0 ? Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow") : "",
            // The TEMP variable is often in its short 8.3 form, which matches nothing in a tree of real names.
            Temp = local.Length > 0 ? Path.Combine(local, "Temp") : "",
            Downloads = string.IsNullOrEmpty(downloads) ? Path.Combine(profile, "Downloads") : downloads,
            ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        };
    }

    /// <summary>True for a path FastFind must never delete from: Windows itself, installed programs and data shared by all users.</summary>
    public bool IsSystemPath(string path)
        => Inside(path, Windows) || Inside(path, ProgramFiles) || Inside(path, ProgramFilesX86) || Inside(path, ProgramData);

    /// <summary>True for a path in this user's own application data, where caches and temporary files live.</summary>
    public bool IsUserAppData(string path)
        => Inside(path, LocalAppData) || Inside(path, RoamingAppData) || Inside(path, LocalLowAppData);

    private static bool Inside(string path, string folder) => folder.Length > 0 && PathUtil.IsSameOrInside(path, folder);
}

/// <summary>How much thought clearing something deserves.</summary>
public enum CleanupSafety
{
    /// <summary>Caches and temporary files: nothing is lost, programs make them again as they need them.</summary>
    Safe,
    /// <summary>Can be downloaded or built again, at the cost of some time.</summary>
    Rebuildable,
    /// <summary>Belongs to Windows; its own clean-up tools are the right way to remove it.</summary>
    Windows,
    /// <summary>The user's own files: only they can say whether these are still wanted.</summary>
    Review,
}

/// <summary>What the button beside a candidate does.</summary>
public enum CleanupAction
{
    /// <summary>FastFind deletes it.</summary>
    Clear,
    /// <summary>Point it out on the map and in the tree, and leave the decision to the user.</summary>
    Show,
    EmptyRecycleBin,
    /// <summary>Open Windows' Disk Clean-up.</summary>
    DiskCleanup,
    /// <summary>Offer a command to copy (for things only an administrator command can remove).</summary>
    CopyCommand,
}

/// <summary>Something that could be removed to free space.</summary>
public sealed class CleanupCandidate
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required CleanupSafety Safety { get; init; }
    public required CleanupAction Action { get; init; }
    /// <summary>The folders or files concerned, as nodes of the tree that was analysed.</summary>
    public required IReadOnlyList<int> Nodes { get; init; }
    public long Bytes { get; init; }
    public int Files { get; init; }
    /// <summary>Clearing empties the folders but leaves the folders themselves (a cache folder its program expects to find).</summary>
    public bool ContentsOnly { get; init; }
    /// <summary>Only files at least this old are cleared (temporary files a running program may still be using are left).</summary>
    public int MinAgeDays { get; init; }
    public string? Command { get; init; }
}

/// <summary>The space one application takes, wherever its parts are kept.</summary>
public sealed class AppUsage
{
    public required string Name { get; init; }
    /// <summary>The program itself: what its installer put in Program Files.</summary>
    public long Program { get; internal set; }
    /// <summary>Settings, documents and databases the application keeps.</summary>
    public long Data { get; internal set; }
    public long Caches { get; internal set; }
    public long Logs { get; internal set; }
    public long Total => Program + Data + Caches + Logs;
    public int Files { get; internal set; }
    /// <summary>The application's folders.</summary>
    public List<int> Roots { get; } = new();
    /// <summary>Cache folders in the user's own application data, which can be emptied safely.</summary>
    public List<int> ClearableCaches { get; } = new();
    public long ClearableBytes { get; internal set; }
    public int ClearableFiles { get; internal set; }
}

public sealed class UsageReport
{
    public required IReadOnlyList<AppUsage> Apps { get; init; }
    public required IReadOnlyList<CleanupCandidate> Candidates { get; init; }
    /// <summary>Total of the candidates FastFind can clear with nothing lost.</summary>
    public long SafeBytes => Candidates.Where(c => c.Safety == CleanupSafety.Safe).Sum(c => c.Bytes);
    public long AppBytes => Apps.Sum(a => a.Total);
}

/// <summary>
/// Looks through a <see cref="SizeTree"/> for the things worth knowing when a disk is full: how much each application
/// takes (its program, its data, its caches and its logs, gathered from the several places Windows scatters them) and
/// what could be removed. It only reads the tree; nothing here touches the disk. Everything is judged by where it is
/// and what it is called, using conventions that hold across Windows programs, and errs on the side of not offering.
/// </summary>
public static class UsageAnalyzer
{
    private const long MB = 1024 * 1024;

    /// <summary>
    /// Folder names that mean "cache" by firm convention. Kept to exact names: a looser match (anything ending in
    /// "cache") would catch folders that hold real data.
    /// </summary>
    private static readonly HashSet<string> CacheNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Caches", "Code Cache", "GPUCache", "ShaderCache", "GrShaderCache", "GraphiteDawnCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache",
        "CacheStorage", "ScriptCache", "CachedData", "CachedExtensionVSIXs", "CachedProfilesData", "Cache_Data", "cache2", "startupCache", "jumpListCache",
        "INetCache", "Media Cache", "Application Cache", "component_crx_cache", "extensions_crx_cache", "D3DSCache", "http-cache", "v3-cache", "_cacache",
        "Temp", "tmp", "SquirrelTemp",
    };

    private static readonly HashSet<string> LogNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "logs", "log", "Crashpad", "CrashDumps", "Crash Reports", "crashes", "minidumps", "dumps",
    };

    private static readonly HashSet<string> LogExtensions = new(StringComparer.OrdinalIgnoreCase) { "log", "dmp", "mdmp", "hdmp", "etl" };

    /// <summary>Folders that hold several separate products, so the application is one level further down.</summary>
    private static readonly HashSet<string> Vendors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Google", "JetBrains", "Adobe", "Mozilla", "Apple", "Intel", "NVIDIA", "NVIDIA Corporation", "AMD", "Dell", "HP", "Lenovo", "Amazon",
        "Atlassian", "Oracle", "Autodesk", "Realtek", "Common Files", "Microsoft Shared",
    };

    /// <summary>Entries under the application folders that are not applications.</summary>
    private static readonly HashSet<string> NotApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Temp", "Programs", "Packages", "VirtualStore", "Application Data", "History", "Temporary Internet Files", "CrashDumps", "D3DSCache",
        "PlaceholderTileLogoFolder", "ConnectedDevicesPlatform", "Comms", "PeerDistRepub", "Publishers", "Package Cache", "USOShared", "USOPrivate",
        "SoftwareDistribution", "Documents", "Desktop", "Start Menu", "Templates", "Uninstall Information", "ModifiableWindowsApps", "WindowsApps",
        "Local Settings", "Favorites",
        // FastFind's own settings and index cache: clearing those from inside FastFind helps nobody.
        "FastFind",
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code"] = "Visual Studio Code", ["microsoftvscode"] = "Visual Studio Code", ["vscode"] = "Visual Studio Code",
        ["microsoftvisualstudio"] = "Visual Studio", ["visualstudio"] = "Visual Studio",
    };

    private static readonly string[] ProjectFiles = { "csproj", "vbproj", "fsproj", "vcxproj", "sqlproj" };
    private static readonly HashSet<string> InstallerExtensions = new(StringComparer.OrdinalIgnoreCase) { "exe", "msi", "msix", "msixbundle", "appx", "iso", "img" };

    public static UsageReport Analyze(SizeTree tree, UsageEnvironment env)
    {
        var analysis = new Analysis(tree, env);
        analysis.FindApps();
        analysis.FindCandidates();
        return analysis.Report();
    }

    /// <summary>Total size and number of the files beneath a node that were last changed before <paramref name="cutoffFileTime"/>.</summary>
    public static (long Bytes, int Files) OlderThan(SizeTree tree, int node, long cutoffFileTime)
    {
        long bytes = 0;
        int files = 0;
        var stack = new Stack<int>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            int n = stack.Pop();
            foreach (int child in tree.Children(n))
            {
                if (tree.IsDirectory(child)) { stack.Push(child); continue; }
                long modified = tree.ModifiedFileTime(child);
                if (modified > 0 && modified < cutoffFileTime) { bytes += tree.Size(child); files++; }
            }
        }
        return (bytes, files);
    }

    private sealed class Analysis
    {
        private readonly SizeTree _tree;
        private readonly UsageEnvironment _env;
        private readonly Dictionary<string, AppUsage> _apps = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<CleanupCandidate> _candidates = new();

        public Analysis(SizeTree tree, UsageEnvironment env)
        {
            _tree = tree;
            _env = env;
        }

        public UsageReport Report() => new()
        {
            Apps = _apps.Values.Where(a => a.Total >= MB).OrderByDescending(a => a.Total).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            Candidates = _candidates,
        };

        private int Find(string path) => path.Length == 0 ? -1 : _tree.Find(path);
        private int Find(string folder, string relative) => folder.Length == 0 ? -1 : _tree.Find(VolumeIndex.Combine(folder, relative));
        private string Size(long bytes) => SizeParser.Format(bytes);

        // ---- applications ----

        public void FindApps()
        {
            ProgramRoot(Find(_env.ProgramFiles));
            ProgramRoot(Find(_env.ProgramFilesX86));
            ProgramRoot(Find(_env.LocalAppData, "Programs"));
            int store = Find(_env.ProgramFiles, "WindowsApps");
            if (store >= 0)
                foreach (int package in _tree.Children(store))
                    if (_tree.IsDirectory(package)) App(PackageName(_tree.Name(package))).AddProgram(_tree, package);

            DataRoot(Find(_env.LocalAppData), clearable: true);
            DataRoot(Find(_env.RoamingAppData), clearable: true);
            DataRoot(Find(_env.LocalLowAppData), clearable: true);
            DataRoot(Find(_env.ProgramData), clearable: false);
            int packages = Find(_env.LocalAppData, "Packages");
            if (packages >= 0)
                foreach (int package in _tree.Children(packages))
                    if (_tree.IsDirectory(package)) AddData(App(PackageName(_tree.Name(package))), package, clearable: true);

            // Tools that keep their things in a dot-folder of the user's profile (.nuget, .vscode, .docker and the like).
            int profile = Find(_env.UserProfile);
            if (profile >= 0)
                foreach (int child in _tree.Children(profile))
                    if (_tree.IsDirectory(child) && _tree.Name(child).Length > 1 && _tree.Name(child)[0] == '.')
                        AddData(App(_tree.Name(child)), child, clearable: false);
        }

        private void ProgramRoot(int root)
        {
            if (root < 0) return;
            foreach (int child in _tree.Children(root))
            {
                if (!_tree.IsDirectory(child)) continue;
                string name = _tree.Name(child);
                if (NotApps.Contains(name)) continue;
                if (Vendors.Contains(name))
                {
                    foreach (int product in _tree.Children(child))
                        if (_tree.IsDirectory(product)) App(ProductName(name, _tree.Name(product))).AddProgram(_tree, product);
                }
                else if (name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) && !name.Equals("Windows Kits", StringComparison.OrdinalIgnoreCase) && root != Find(_env.LocalAppData, "Programs"))
                {
                    // Windows Defender, Windows Mail, WindowsPowerShell...: parts of Windows rather than programs anyone installed.
                    App("Windows components").AddProgram(_tree, child);
                }
                else App(name).AddProgram(_tree, child);
            }
        }

        private void DataRoot(int root, bool clearable)
        {
            if (root < 0) return;
            foreach (int child in _tree.Children(root))
            {
                if (!_tree.IsDirectory(child)) continue;
                string name = _tree.Name(child);
                if (NotApps.Contains(name)) continue;
                if (Vendors.Contains(name))
                {
                    foreach (int product in _tree.Children(child))
                        if (_tree.IsDirectory(product)) AddData(App(ProductName(name, _tree.Name(product))), product, clearable);
                }
                else AddData(App(name), child, clearable);
            }
        }

        /// <summary>Sorts everything in one of an application's data folders into caches, logs and the rest.</summary>
        private void AddData(AppUsage app, int folder, bool clearable)
        {
            app.Roots.Add(folder);
            app.Files += _tree.FileCount(folder);
            var stack = new Stack<int>();
            stack.Push(folder);
            while (stack.Count > 0)
            {
                int node = stack.Pop();
                foreach (int child in _tree.Children(node))
                {
                    long size = _tree.Size(child);
                    if (!_tree.IsDirectory(child))
                    {
                        if (LogExtensions.Contains(_tree.Extension(child))) app.Logs += size;
                        else app.Data += size;
                    }
                    else if (CacheNames.Contains(_tree.Name(child)))
                    {
                        app.Caches += size;
                        if (clearable && size > 0)
                        {
                            app.ClearableCaches.Add(child);
                            app.ClearableBytes += size;
                            app.ClearableFiles += _tree.FileCount(child);
                        }
                    }
                    else if (LogNames.Contains(_tree.Name(child))) app.Logs += size;
                    else stack.Push(child);
                }
            }
        }

        private AppUsage App(string name)
        {
            string key = new(name.Where(char.IsLetterOrDigit).ToArray());
            if (Aliases.TryGetValue(key, out var alias)) { name = alias; key = new string(alias.Where(char.IsLetterOrDigit).ToArray()); }
            if (key.Length == 0) key = name;
            if (!_apps.TryGetValue(key, out var app)) _apps[key] = app = new AppUsage { Name = name };
            return app;
        }

        private static string ProductName(string vendor, string product)
        {
            if (vendor.Equals("Common Files", StringComparison.OrdinalIgnoreCase) || vendor.Equals("Microsoft Shared", StringComparison.OrdinalIgnoreCase)) return product;
            if (vendor.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) && product.Equals("Windows", StringComparison.OrdinalIgnoreCase)) return "Windows";
            return product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) ? product : vendor + " " + product;
        }

        /// <summary>"Microsoft.WindowsCalculator_11.2405.2.0_x64__8wekyb3d8bbwe" is Microsoft WindowsCalculator.</summary>
        private static string PackageName(string folder)
        {
            int underscore = folder.IndexOf('_');
            string name = underscore > 0 ? folder[..underscore] : folder;
            return name.Replace('.', ' ');
        }

        // ---- things that could go ----

        public void FindCandidates()
        {
            SafeToClear();
            Rebuildable();
            WindowsManaged();
            OwnFiles();
        }

        private void Add(string id, string title, string detail, CleanupSafety safety, CleanupAction action, IReadOnlyList<int> nodes,
            bool contentsOnly = false, int minAgeDays = 0, long? bytes = null, int? files = null, long atLeast = MB, string? command = null)
        {
            nodes = nodes.Where(n => _tree.Size(n) > 0).ToList(); // nothing to gain from an empty folder
            long total = bytes ?? nodes.Sum(_tree.Size);
            if (nodes.Count == 0 || total < atLeast) return;
            int count = files ?? nodes.Sum(n => _tree.IsDirectory(n) ? _tree.FileCount(n) : 1);
            _candidates.Add(new CleanupCandidate
            {
                Id = id, Title = title, Detail = detail, Safety = safety, Action = action, Nodes = nodes, Bytes = total, Files = count,
                ContentsOnly = contentsOnly, MinAgeDays = minAgeDays, Command = command,
            });
        }

        private void SafeToClear()
        {
            int temp = Find(_env.Temp);
            if (temp >= 0)
            {
                // Anything recent may belong to a program that is running, so only what has sat untouched for a couple of days is counted.
                const int days = 2;
                var (bytes, files) = OlderThan(_tree, temp, _env.NowUtc.AddDays(-days).ToFileTimeUtc());
                Add("temp", "Temporary files", $"Left behind in your Temp folder by installers and programs. Only files more than {days} days old are cleared; anything in use is skipped.",
                    CleanupSafety.Safe, CleanupAction.Clear, new[] { temp }, contentsOnly: true, minAgeDays: days, bytes: bytes, files: files);
            }

            var withCaches = _apps.Values.Where(a => a.ClearableBytes > 0).OrderByDescending(a => a.ClearableBytes).ToList();
            if (withCaches.Count > 0)
            {
                string biggest = string.Join(", ", withCaches.Take(4).Select(a => $"{a.Name} {Size(a.ClearableBytes)}"));
                Add("app-caches", "Application caches",
                    $"Copies that browsers and other programs keep to save time, from {withCaches.Count:N0} applications. They are made again as needed. Biggest: {biggest}.",
                    CleanupSafety.Safe, CleanupAction.Clear, withCaches.SelectMany(a => a.ClearableCaches).ToList(), contentsOnly: true);
            }

            int dumps = Find(_env.LocalAppData, "CrashDumps");
            if (dumps >= 0)
                Add("crash-dumps", "Crash dumps", "Memory snapshots saved when a program crashed. Only useful for diagnosing that crash.",
                    CleanupSafety.Safe, CleanupAction.Clear, new[] { dumps }, contentsOnly: true);
        }

        private void Rebuildable()
        {
            void Store(string id, int node, string title, string detail)
            {
                if (node >= 0) Add(id, title, detail, CleanupSafety.Rebuildable, CleanupAction.Clear, new[] { node }, contentsOnly: true, atLeast: 50 * MB);
            }
            Store("nuget", Find(_env.UserProfile, @".nuget\packages"), "NuGet packages", "Every package version .NET projects have ever restored. Downloaded again the next time a project is built.");
            Store("gradle", Find(_env.UserProfile, @".gradle\caches"), "Gradle caches", "Dependencies and build caches for Gradle projects. Rebuilt on the next build.");
            Store("maven", Find(_env.UserProfile, @".m2\repository"), "Maven repository", "Libraries Maven has downloaded. Fetched again on the next build.");
            Store("cargo", Find(_env.UserProfile, @".cargo\registry"), "Cargo registry", "Crates Rust has downloaded. Fetched again on the next build.");
            Store("playwright", Find(_env.LocalAppData, "ms-playwright"), "Playwright browsers", "Browser builds downloaded for automated testing. Installed again with 'playwright install'.");

            DeveloperFolders();
        }

        /// <summary>
        /// Build output and node_modules folders in the user's own projects. Only inside a git working copy: that is
        /// what says "this is source someone builds", as opposed to a released package or a vendor's sample that
        /// happens to ship with folders of the same names and cannot be rebuilt. Never looked for inside Windows,
        /// program folders, application data or dot-folders, for the same reason.
        /// </summary>
        private void DeveloperFolders()
        {
            var modules = new List<(int Node, int Project)>();
            var builds = new List<(List<int> Nodes, int Project)>();
            var old = new List<int>();
            long oldCutoff = _env.NowUtc.AddYears(-2).ToFileTimeUtc();

            // The measured folder is itself part of Windows, a program or application data: nothing of the user's own in it.
            if (_env.IsSystemPath(_tree.RootPath) || _env.IsUserAppData(_tree.RootPath)) return;
            var offLimits = new HashSet<int>
            {
                Find(_env.Windows), Find(_env.ProgramFiles), Find(_env.ProgramFilesX86), Find(_env.ProgramData), Find(_env.UserProfile, "AppData"),
            };

            var stack = new Stack<(int Folder, bool InRepository)>();
            stack.Push((SizeTree.Root, false));
            while (stack.Count > 0)
            {
                var (folder, inRepository) = stack.Pop();
                bool hasPackageJson = false, hasProject = false;
                foreach (int child in _tree.Children(folder))
                {
                    // A .git folder marks a working copy; a .git file marks a worktree or submodule of one.
                    if (_tree.Name(child).Equals(".git", StringComparison.OrdinalIgnoreCase)) inRepository = true;
                    if (_tree.IsDirectory(child)) continue;
                    if (_tree.Name(child).Equals("package.json", StringComparison.OrdinalIgnoreCase)) hasPackageJson = true;
                    else if (Array.IndexOf(ProjectFiles, _tree.Extension(child)) >= 0) hasProject = true;
                    // While here: big files nobody has touched for years.
                    if (_tree.Size(child) >= 250 * MB && _tree.ModifiedFileTime(child) > 0 && _tree.ModifiedFileTime(child) < oldCutoff) old.Add(child);
                }
                List<int>? output = null;
                foreach (int child in _tree.Children(folder))
                {
                    if (!_tree.IsDirectory(child) || _tree.Size(child) == 0) continue;
                    string name = _tree.Name(child);
                    if (name.Length == 0 || name[0] == '.' || name[0] == '$') continue;
                    if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                    {
                        if (inRepository && hasPackageJson) modules.Add((child, folder));
                        continue; // never looked inside: what is in there belongs to the packages
                    }
                    if (inRepository && hasProject && (name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("obj", StringComparison.OrdinalIgnoreCase)))
                    {
                        (output ??= new()).Add(child);
                        continue;
                    }
                    if (offLimits.Contains(child) || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) continue;
                    stack.Push((child, inRepository));
                }
                if (output != null) builds.Add((output, folder));
            }

            const int listed = 6;
            var bigModules = modules.Where(m => _tree.Size(m.Node) >= 50 * MB).OrderByDescending(m => _tree.Size(m.Node)).ToList();
            foreach (var (node, project) in bigModules.Take(listed))
                Add("modules:" + node, $"node_modules in {_tree.Name(project)}", $"{_tree.GetPath(project)}. Put back by running 'npm install' in that folder.",
                    CleanupSafety.Rebuildable, CleanupAction.Clear, new[] { node });
            var otherModules = modules.Except(bigModules.Take(listed)).Where(m => _tree.Size(m.Node) > 0).Select(m => m.Node).ToList();
            Add("modules-rest", $"{otherModules.Count:N0} more node_modules folders", "In your other JavaScript projects. Each is put back by running 'npm install' in its project.",
                CleanupSafety.Rebuildable, CleanupAction.Show, otherModules, atLeast: 20 * MB);

            var bigBuilds = builds.Where(b => b.Nodes.Sum(_tree.Size) >= 50 * MB).OrderByDescending(b => b.Nodes.Sum(_tree.Size)).ToList();
            foreach (var (nodes, project) in bigBuilds.Take(listed))
                Add("build:" + project, $"Build output of {_tree.Name(project)}", $"{_tree.GetPath(project)}: its bin and obj folders. Made again the next time the project is built.",
                    CleanupSafety.Rebuildable, CleanupAction.Clear, nodes);
            var otherBuilds = builds.Except(bigBuilds.Take(listed)).SelectMany(b => b.Nodes).ToList();
            Add("build-rest", $"Build output of {builds.Count - Math.Min(listed, bigBuilds.Count):N0} more projects", "bin and obj folders beside project files. Made again when each project is next built.",
                CleanupSafety.Rebuildable, CleanupAction.Show, otherBuilds, atLeast: 20 * MB);

            var largest = old.OrderByDescending(_tree.Size).Take(25).ToList();
            Add("old-large", "Large files not changed in over two years", $"{largest.Count:N0} files of 250 MB or more that have sat untouched since {_env.NowUtc.AddYears(-2):MMMM yyyy}. Worth a look; they may still matter.",
                CleanupSafety.Review, CleanupAction.Show, largest);
        }

        private void WindowsManaged()
        {
            // A whole drive was measured when the tree's root is a drive root; these things live at or near it.
            string drive = _tree.RootPath.Length <= 3 ? _tree.RootPath : "";
            void Tool(string id, string title, string detail, params int[] nodes)
                => Add(id, title, detail, CleanupSafety.Windows, CleanupAction.DiskCleanup, nodes.Where(n => n >= 0).ToList(), atLeast: 10 * MB);

            int bin = Find(drive, "$Recycle.Bin");
            if (bin >= 0)
                Add("recycle-bin", "Recycle Bin", "Files already deleted, kept in case you want them back. Emptying it is what actually frees their space.",
                    CleanupSafety.Review, CleanupAction.EmptyRecycleBin, new[] { bin }, atLeast: MB);

            Tool("windows-update", "Windows Update downloads", "Update files already installed. Disk Clean-up removes them ('Windows Update Clean-up' and 'Delivery Optimisation Files').",
                Find(_env.Windows, @"SoftwareDistribution\Download"),
                Find(_env.Windows, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"));
            Tool("windows-old", "Previous Windows installation", "Kept after an upgrade so it can be rolled back. Disk Clean-up removes it ('Previous Windows installations').",
                Find(drive, "Windows.old"), Find(drive, "$Windows.~BT"), Find(drive, "$Windows.~WS"));
            Tool("windows-temp", "Windows temporary files", "Temporary files belonging to Windows and to installers that ran as administrator.", Find(_env.Windows, "Temp"));
            Tool("windows-dumps", "System error memory dumps", "Snapshots of memory saved when Windows itself crashed.",
                Find(_env.Windows, "MEMORY.DMP"), Find(_env.Windows, "Minidump"), Find(_env.Windows, "LiveKernelReports"));
            Tool("windows-wer", "Windows error reports", "Queued and archived problem reports.", Find(_env.ProgramData, @"Microsoft\Windows\WER"));

            int hibernation = Find(drive, "hiberfil.sys");
            if (hibernation >= 0)
                Add("hibernation", "Hibernation file", "Where Windows saves memory when the PC hibernates, and what makes fast start-up work. Turning hibernation off removes it; "
                    + "run the command in a terminal opened as administrator. Sleep still works without it.",
                    CleanupSafety.Windows, CleanupAction.CopyCommand, new[] { hibernation }, atLeast: 100 * MB, command: "powercfg /hibernate off");
        }

        private void OwnFiles()
        {
            int downloads = Find(_env.Downloads);
            if (downloads < 0) return;
            long cutoff = _env.NowUtc.AddDays(-90).ToFileTimeUtc();
            var stale = new List<int>();
            var installers = new List<int>();
            foreach (int child in _tree.Children(downloads))
            {
                if (_tree.Size(child) == 0) continue;
                if (!_tree.IsDirectory(child) && InstallerExtensions.Contains(_tree.Extension(child))) installers.Add(child);
                else if (_tree.ModifiedFileTime(child) > 0 && _tree.ModifiedFileTime(child) < cutoff) stale.Add(child);
            }
            Add("downloads-installers", "Installers in Downloads", $"{installers.Count:N0} set-up programs and disk images. Once something is installed its installer is rarely needed again.",
                CleanupSafety.Review, CleanupAction.Show, installers, atLeast: 20 * MB);
            Add("downloads-old", "Downloads older than three months", $"{stale.Count:N0} items in your Downloads folder not changed in the last 90 days.",
                CleanupSafety.Review, CleanupAction.Show, stale, atLeast: 20 * MB);
        }
    }

    private static void AddProgram(this AppUsage app, SizeTree tree, int folder)
    {
        app.Roots.Add(folder);
        app.Program += tree.Size(folder);
        app.Files += tree.FileCount(folder);
    }
}
