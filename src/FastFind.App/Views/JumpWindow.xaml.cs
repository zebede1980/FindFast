using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FastFind.App.ViewModels;
using FastFind.Core.Api;
using FastFind.Core.Browse;
using FastFind.Core.Search;

namespace FastFind.App.Views;

/// <summary>
/// Ctrl+P: type a few letters of any folder on any indexed drive and go straight to it. Pinned folders and the
/// folders already open in tabs are offered first; the rest comes from the index, best matches at the top.
/// </summary>
public partial class JumpWindow : Window
{
    public sealed record Target(string Glyph, string Name, string Path);

    private const int MaxResults = 60;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _cts;
    private bool _closing;

    public JumpWindow(MainViewModel main)
    {
        _main = main;
        InitializeComponent();
        _debounce = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(60) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Search(); };
        Loaded += (_, _) => { Box.Focus(); Search(); };
        // Closing by any route (Alt+F4, the owner closing) deactivates the window; that must not try to close it a second time.
        Closing += (_, _) => { _closing = true; _cts?.Cancel(); };
    }

    private void OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Folders the user is likely to want, before anything is typed or when they match what is typed.</summary>
    private IEnumerable<Target> KnownPlaces()
    {
        foreach (var f in App.Settings.Favourites) yield return new Target("\uE734", PathUtil.LeafName(f), f);
        foreach (var tab in _main.AllTabs.Where(t => !t.IsEverywhere)) yield return new Target("\uE8B7", PathUtil.LeafName(tab.Location), tab.Location);
        foreach (var group in _main.Sidebar)
            foreach (var item in group.Items.Where(i => i.Kind is SidebarKind.Folder or SidebarKind.Drive or SidebarKind.Network))
                yield return new Target(item.Glyph, item.Title, item.Path);
    }

    private void Search()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        string text = Box.Text.Trim();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var known = KnownPlaces()
            .Where(t => words.All(w => t.Path.Contains(w, StringComparison.OrdinalIgnoreCase) || t.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(t => t.Path.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

        if (text.Length == 0)
        {
            Show(known);
            return;
        }

        // A typed path that exists is always the first suggestion.
        string asPath = PathUtil.Normalize(text);
        var request = new SearchRequest { Text = text, Kind = EntryKind.Folder, MatchPath = text.Contains('\\'), Sort = SortField.Relevance };
        _main.Index.SearchAsync(request, cts.Token).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested || _closing) { if (t.IsCompletedSuccessfully) t.Result.Dispose(); return; }
            var results = new List<Target>();
            if (asPath.Length > 0 && Directory.Exists(asPath)) results.Add(new Target("\uE8DE", PathUtil.LeafName(asPath), asPath));
            results.AddRange(known);
            if (t.IsCompletedSuccessfully)
            {
                using var set = t.Result;
                foreach (var e in set.GetRows(0, MaxResults))
                    if (e.Name != null) results.Add(new Target("\uE8B7", e.Name, e.FullPath));
            }
            Show(results.GroupBy(r => r.Path.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).Take(MaxResults).ToList());
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Show(List<Target> targets)
    {
        List.ItemsSource = targets;
        if (targets.Count > 0) List.SelectedIndex = 0;
        List.Visibility = targets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Finish();
                e.Handled = true;
                break;
            case Key.Down:
            case Key.Up:
                if (List.Items.Count > 0)
                {
                    int i = List.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                    List.SelectedIndex = (i + List.Items.Count) % List.Items.Count;
                    List.ScrollIntoView(List.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Enter:
                Go();
                e.Handled = true;
                break;
        }
    }

    private void OnListClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem != null) Go();
    }

    private void Go()
    {
        string? path = (List.SelectedItem as Target)?.Path;
        if (path == null)
        {
            // Nothing matched: accept a path typed in full.
            path = PathUtil.Normalize(Box.Text);
            if (path.Length == 0 || !Directory.Exists(path)) return;
        }
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (shift && _main.OtherPane(_main.ActivePane) is { } other) _main.ActivePane = other;
        _main.Open(path, newTab: ctrl);
        Finish();
    }

    private void OnDeactivated(object? sender, EventArgs e) => Finish();

    private void Finish()
    {
        if (_closing) return;
        _closing = true;
        _cts?.Cancel();
        Close();
    }
}
