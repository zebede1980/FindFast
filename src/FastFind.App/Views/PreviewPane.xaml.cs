using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FastFind.App.Services;
using FastFind.App.ViewModels;
using FastFind.Core.Search;

namespace FastFind.App.Views;

/// <summary>Shows the selected item: its thumbnail, key details, and the first lines of a text file.</summary>
public partial class PreviewPane : UserControl
{
    private const int MaxTextBytes = 64 * 1024;
    private const int MaxTextLines = 400;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "log", "md", "json", "xml", "yaml", "yml", "csv", "tsv", "ini", "cfg", "conf", "config", "toml", "env", "editorconfig", "gitignore", "gitattributes",
        "cs", "csproj", "sln", "slnx", "props", "targets", "xaml", "js", "ts", "tsx", "jsx", "py", "java", "cpp", "c", "h", "hpp", "go", "rs", "rb", "php",
        "html", "htm", "css", "scss", "sql", "sh", "ps1", "psm1", "bat", "cmd", "tf", "bicep", "razor", "cshtml", "vb", "fs", "kt", "swift", "lua", "r", "reg",
    };

    private readonly DispatcherTimer _delay;
    private FileRow? _pending;
    private int _pendingCount;
    private string _pendingSummary = "";
    private int _version;
    private CancellationTokenSource? _thumbnailCancel;

    public PreviewPane()
    {
        InitializeComponent();
        // Arrowing quickly down a list should not load a thumbnail for every row passed on the way.
        _delay = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _delay.Tick += (_, _) => { _delay.Stop(); Render(); };
    }

    /// <summary>Tells the pane what is selected: one row, or just a count and summary for several.</summary>
    public void Show(FileRow? row, int selectedCount, string summary)
    {
        _pending = row;
        _pendingCount = selectedCount;
        _pendingSummary = summary;
        _delay.Stop();
        if (IsVisible) _delay.Start();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsVisibleProperty && e.NewValue is true) Render();
    }

    private void Render()
    {
        int version = ++_version;
        // Stop waiting for the thumbnail of whatever was shown before.
        try { _thumbnailCancel?.Cancel(); } catch (ObjectDisposedException) { }
        _thumbnailCancel = null;
        var row = _pending;
        Labels.Children.Clear();
        Values.Children.Clear();
        TextHost.Visibility = Visibility.Collapsed;
        TextPreview.Text = "";

        if (row == null || row.IsDeleted)
        {
            ImageHost.Visibility = Visibility.Collapsed;
            Thumb.Source = null;
            NameText.Visibility = Details.Visibility = Visibility.Collapsed;
            Placeholder.Visibility = Visibility.Visible;
            Placeholder.Text = _pendingCount > 1 ? _pendingSummary : "Select a file to preview it";
            return;
        }

        Placeholder.Visibility = Visibility.Collapsed;
        NameText.Visibility = Details.Visibility = Visibility.Visible;
        NameText.Text = row.Name;
        // Show the small icon at once; the real thumbnail replaces it when it arrives.
        Thumb.Source = row.Icon;
        ImageHost.Visibility = Visibility.Visible;

        AddDetail("Type", row.IsDirectory ? "Folder" : row.Extension.Length == 0 ? "File" : row.Extension.ToUpperInvariant() + " file");
        if (!row.IsDirectory || row.Size > 0) AddDetail("Size", $"{SizeParser.Format(row.Size)}  ({row.Size:N0} bytes)");
        if (row.Modified is { } modified) AddDetail("Modified", modified.ToString("dd MMM yyyy HH:mm"));
        if (row.Created is { } created) AddDetail("Created", created.ToString("dd MMM yyyy HH:mm"));
        if (row.AttributesText.Length > 0) AddDetail("Attributes", row.AttributesText);
        AddDetail("Location", row.Directory);

        string path = row.FullPath;
        var cancel = _thumbnailCancel = new CancellationTokenSource();
        // A thumbnail Windows is still making (files in OneDrive, mostly) arrives a moment later: its large icon stands in until then.
        Action<System.Windows.Media.ImageSource> interim = icon => Dispatcher.BeginInvoke(() => { if (version == _version) Thumb.Source = icon; });
        ShellThumbnails.GetAsync(path, 512, interim, cancel.Token).ContinueWith(t =>
        {
            cancel.Dispose();
            if (version != _version || t.Result == null) return;
            Thumb.Source = t.Result;
        }, TaskScheduler.FromCurrentSynchronizationContext());

        if (!row.IsDirectory && (TextExtensions.Contains(row.Extension) || row.Extension.Length == 0 && row.Size < MaxTextBytes) && row.Size > 0)
        {
            Task.Run(() => ReadText(path)).ContinueWith(t =>
            {
                if (version != _version || t.IsFaulted || t.Result == null) return;
                TextPreview.Text = t.Result;
                TextHost.Visibility = Visibility.Visible;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    private void AddDetail(string label, string value)
    {
        var l = new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 2, 0, 2) };
        l.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Labels.Children.Add(l);
        var v = new TextBlock { Text = value, FontSize = 12, Margin = new Thickness(0, 2, 0, 2), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = value };
        v.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        Values.Children.Add(v);
    }

    /// <summary>The start of a text file, or null when the content looks binary.</summary>
    private static string? ReadText(string path)
    {
        var buffer = new byte[MaxTextBytes];
        int read;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (read == 0) return null;

        Encoding encoding = Encoding.UTF8;
        int skip = 0;
        if (read >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE) { encoding = Encoding.Unicode; skip = 2; }
        else if (read >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF) { encoding = Encoding.BigEndianUnicode; skip = 2; }
        else if (read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF) skip = 3;
        else if (Array.IndexOf(buffer, (byte)0, 0, read) >= 0) return null; // a NUL in an 8-bit file means it is not text

        string text = encoding.GetString(buffer, skip, read - skip);
        var lines = text.Split('\n');
        bool truncated = read == buffer.Length || lines.Length > MaxTextLines;
        text = string.Join("\n", lines.Take(MaxTextLines).Select(l => l.Length > 400 ? l[..400] + "…" : l.TrimEnd('\r')));
        return truncated ? text + "\n…" : text;
    }
}
