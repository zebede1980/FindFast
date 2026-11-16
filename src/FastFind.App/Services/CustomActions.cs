using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using FastFind.Core.Search;

namespace FastFind.App.Services;

/// <summary>
/// The user's own right-click entries: deciding which ones fit what is selected, turning one into the command lines
/// to run, and running them.
/// </summary>
public static class CustomActions
{
    /// <summary>Something an action can be run on.</summary>
    public readonly record struct Target(string Path, bool IsDirectory);

    /// <summary>One program start.</summary>
    public sealed record Launch(string Program, string Arguments, string WorkingDirectory, bool Elevated);

    /// <summary>What <see cref="Start"/> returns when the user turned down the administrator prompt.</summary>
    public const string Cancelled = "Cancelled.";

    /// <summary>The placeholders an action's program, arguments and start folder can use.</summary>
    private static readonly Regex Placeholder = new(@"\{(path|paths|folder|name|basename)\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The longest command line Windows will start a program with, less some room for the program's own path.</summary>
    private const int MaxArguments = 32000;

    /// <summary>An action appears in menus only once it has a name and a program.</summary>
    public static bool IsUsable(CustomAction action) => action.Name.Trim().Length > 0 && action.Program.Trim().Length > 0;

    // ---- which actions fit ----

    /// <summary>What an "applies to" text allows, read once so a large selection is checked quickly.</summary>
    private sealed class Filter
    {
        public bool AnyFile, Folders;
        public readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase);

        public bool Allows(Target t)
        {
            if (t.IsDirectory) return Folders;
            if (AnyFile) return true;
            string ext = System.IO.Path.GetExtension(t.Path);
            return ext.Length > 1 && Extensions.Contains(ext[1..]);
        }
    }

    private static Filter Parse(string appliesTo)
    {
        var filter = new Filter();
        foreach (string raw in appliesTo.Split(new[] { ';', ',', ' ', '\t', '|' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw.Trim();
            if (token is "*" or "*.*") filter.AnyFile = true;
            else if (token.Equals("folder", StringComparison.OrdinalIgnoreCase) || token.Equals("folders", StringComparison.OrdinalIgnoreCase)) filter.Folders = true;
            else if (FileKinds.TryGet(token, out var kind) || FileKinds.TryGet(token + "s", out kind)) filter.Extensions.UnionWith(kind); // "video", "image" or "images"
            else
            {
                string ext = token.TrimStart('*').TrimStart('.'); // "ps1", ".ps1" and "*.ps1" all mean the same
                if (ext.Length > 0) filter.Extensions.Add(ext);
            }
        }
        return filter;
    }

    /// <summary>True when the action is offered for this selection: every item must be of a kind the action applies to.</summary>
    public static bool Matches(CustomAction action, IReadOnlyList<Target> items)
    {
        if (items.Count == 0 || !IsUsable(action)) return false;
        var filter = Parse(action.AppliesTo);
        foreach (var item in items)
            if (!filter.Allows(item)) return false;
        return true;
    }

    /// <summary>The actions to offer for a selection, in the user's order.</summary>
    public static List<CustomAction> For(IEnumerable<CustomAction> actions, IReadOnlyList<Target> items)
        => actions.Where(a => Matches(a, items)).ToList();

    // ---- the command lines ----

    /// <summary>True when the program is started once with everything selected, rather than once for each item.</summary>
    public static bool RunsOnce(CustomAction action) => action.Arguments.Trim().Length == 0 || action.Arguments.Contains("{paths}", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The program starts an action comes to for a selection. With {paths} in the arguments (or none at all) that is one
    /// start with everything selected; otherwise one start per item.
    /// </summary>
    /// <param name="error">Why it cannot be run, if it cannot.</param>
    public static IReadOnlyList<Launch> Plan(CustomAction action, IReadOnlyList<Target> items, out string? error)
    {
        error = null;
        var launches = new List<Launch>();
        if (items.Count == 0) return launches;
        string template = action.Arguments.Trim().Length == 0 ? "{paths}" : action.Arguments.Trim();
        foreach (var item in RunsOnce(action) ? items.Take(1) : items)
        {
            string arguments = ExpandArguments(template, item, items);
            if (arguments.Length > MaxArguments)
            {
                error = $"Too much is selected to hand to {action.Name.Trim()} in one go. Select fewer items.";
                return Array.Empty<Launch>();
            }
            string startIn = action.StartIn.Trim().Length == 0
                ? (item.IsDirectory ? item.Path : FolderOf(item.Path))
                : Environment.ExpandEnvironmentVariables(ExpandPlain(action.StartIn.Trim(), item, items));
            string program = Environment.ExpandEnvironmentVariables(ExpandPlain(action.Program.Trim().Trim('"'), item, items));
            launches.Add(new Launch(program, arguments, startIn, action.RunAsAdmin));
        }
        return launches;
    }

    private static string FolderOf(string path) => System.IO.Path.GetDirectoryName(path.TrimEnd('\\')) ?? path;

    private static string Value(string placeholder, Target item) => placeholder.ToLowerInvariant() switch
    {
        "path" => item.Path,
        "folder" => FolderOf(item.Path),
        "name" => System.IO.Path.GetFileName(item.Path.TrimEnd('\\')) is { Length: > 0 } name ? name : item.Path,
        "basename" => item.IsDirectory ? System.IO.Path.GetFileName(item.Path.TrimEnd('\\')) : System.IO.Path.GetFileNameWithoutExtension(item.Path),
        _ => "",
    };

    /// <summary>Wraps a value in quotes so a program sees it as one argument. A backslash just before the closing quote would swallow it, so those are doubled.</summary>
    private static string Quote(string value)
    {
        int trailing = value.Length - value.TrimEnd('\\').Length;
        return "\"" + value + new string('\\', trailing) + "\"";
    }

    /// <summary>Fills in placeholders with nothing added: for the program and the start folder, which are not command lines.</summary>
    private static string ExpandPlain(string text, Target item, IReadOnlyList<Target> all)
        => Placeholder.Replace(text, m => m.Groups[1].Value.Equals("paths", StringComparison.OrdinalIgnoreCase)
            ? string.Join(" ", all.Select(t => t.Path))
            : Value(m.Groups[1].Value, item));

    /// <summary>
    /// Fills in the arguments. Quoting is taken care of so that paths with spaces arrive in one piece: each argument that
    /// the user has not quoted themselves is quoted when it needs to be, and what they did quote is left as they wrote it.
    /// </summary>
    private static string ExpandArguments(string template, Target item, IReadOnlyList<Target> all)
    {
        var result = new StringBuilder();
        foreach (string token in SplitArguments(template))
        {
            if (result.Length > 0) result.Append(' ');
            result.Append(ExpandArgument(token, item, all));
        }
        return result.ToString();
    }

    private static string ExpandArgument(string token, Target item, IReadOnlyList<Target> all)
    {
        bool quotedByUser = token.Contains('"');
        bool hasPaths = false;
        string expanded = Placeholder.Replace(token, m =>
        {
            if (m.Groups[1].Value.Equals("paths", StringComparison.OrdinalIgnoreCase))
            {
                hasPaths = true;
                return string.Join(" ", all.Select(t => Quote(t.Path)));
            }
            string value = Value(m.Groups[1].Value, item);
            // Inside the user's own quotes, "C:\" would end in an escaped quote; give the backslash its partner.
            int after = m.Index + m.Length;
            if (quotedByUser && value.EndsWith('\\') && after < token.Length && token[after] == '"')
                value += new string('\\', value.Length - value.TrimEnd('\\').Length);
            return value;
        });
        if (quotedByUser || hasPaths) return expanded;
        bool placeholderAlone = Placeholder.Match(token) is { Success: true } only && only.Length == token.Length;
        return placeholderAlone || expanded.Any(char.IsWhiteSpace) ? Quote(expanded) : expanded;
    }

    /// <summary>Splits a command line at spaces, keeping anything in quotes together.</summary>
    private static List<string> SplitArguments(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        foreach (char c in text)
        {
            if (c == '"') inQuotes = !inQuotes;
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    // ---- running ----

    /// <summary>Starts a program. Returns null when it started, <see cref="Cancelled"/> when the administrator prompt was turned down, otherwise what went wrong.</summary>
    public static string? Start(Launch launch)
    {
        try
        {
            // Through the shell, so a program can be named the way it is in the Run box (pwsh.exe, code, a shortcut, a script).
            var info = new ProcessStartInfo(launch.Program, launch.Arguments) { UseShellExecute = true };
            if (Directory.Exists(launch.WorkingDirectory)) info.WorkingDirectory = launch.WorkingDirectory;
            if (launch.Elevated) info.Verb = "runas";
            using var process = Process.Start(info);
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return Cancelled; }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3) { return $"{launch.Program} was not found."; }
        catch (Exception ex)
        {
            Logger.Warn($"Custom action failed to start {launch.Program}: {ex.Message}");
            return ex.Message;
        }
    }

    // ---- the editor's example ----

    /// <summary>A made-up item of a kind the action applies to, for showing what its command line comes to.</summary>
    public static Target ExampleFor(CustomAction action)
    {
        // The profile folder rather than Documents, which OneDrive can turn into a path too long to read at a glance.
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var filter = Parse(action.AppliesTo);
        if (filter.Folders && !filter.AnyFile && filter.Extensions.Count == 0) return new Target(System.IO.Path.Combine(documents, "My folder"), true);
        // The first extension as the user wrote it, so the example looks like their own files.
        string first = action.AppliesTo.Split(new[] { ';', ',', ' ', '\t', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Select(t => FileKinds.TryGet(t, out var kind) || FileKinds.TryGet(t + "s", out kind) ? kind[0] : t.TrimStart('*').TrimStart('.'))
            .FirstOrDefault(t => t.Length > 0 && !t.Equals("folder", StringComparison.OrdinalIgnoreCase) && !t.Equals("folders", StringComparison.OrdinalIgnoreCase)) ?? "txt";
        return new Target(System.IO.Path.Combine(documents, "My file." + first), false);
    }
}
