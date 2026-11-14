using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FastFind.App.ViewModels;

namespace FastFind.App.Views;

/// <summary>One pane of the window: its tab strip and the tab on show.</summary>
public partial class PaneView : UserControl
{
    private PaneViewModel? Pane => DataContext as PaneViewModel;

    public PaneView()
    {
        InitializeComponent();
        // Any click or keyboard focus inside a pane makes it the active one.
        PreviewMouseDown += (_, _) => Activate();
        GotKeyboardFocus += (_, _) => Activate();
        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    /// <summary>Tells the tab strip how much room to leave on its right for the buttons laid over it.</summary>
    private void OnStripButtonsSizeChanged(object sender, SizeChangedEventArgs e) => Tabs.Tag = StripButtons.ActualWidth + 4;

    /// <summary>The file view of the selected tab (the tab control shows one view and swaps its data).</summary>
    public FilePaneView? CurrentView => MainWindow.FindVisualChild<FilePaneView>(Tabs);

    private void Activate()
    {
        if (Pane is { } pane && !pane.IsActive) pane.Main.ActivePane = pane;
    }

    private void OnTabsPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d || MainWindow.FindVisualParent<TabItem>(d) is not { DataContext: FileTabViewModel tab }) return;
        var pane = Pane;
        if (pane == null) return;
        if (e.ChangedButton == MouseButton.Middle)
        {
            pane.CloseTab(tab);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            ShowTabMenu(pane, tab, MainWindow.FindVisualParent<TabItem>(d)!);
            e.Handled = true;
        }
    }

    private static void ShowTabMenu(PaneViewModel pane, FileTabViewModel tab, TabItem header)
    {
        var menu = new ContextMenu { PlacementTarget = header };
        void Add(string text, Action action, string? gesture = null, bool enabled = true)
        {
            var item = new MenuItem { Header = text, IsEnabled = enabled };
            if (gesture != null) item.InputGestureText = gesture;
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("Duplicate tab", () => pane.AddTab(tab.Location, tab.QueryText, tab.Recursive), "Ctrl+T");
        // A folder tab opens as a new tab on the other side (bringing up the second pane if need be); a search tab has no folder to send.
        Add("Open in other pane", () => pane.Main.OpenInOtherPane(pane, tab.Location, newTab: true), enabled: !tab.IsEverywhere);
        if (!tab.IsEverywhere)
        {
            bool pinned = pane.Main.IsPinned(tab.Location);
            Add(pinned ? "Unpin from sidebar" : "Pin to sidebar", () => { if (pinned) pane.Main.Unpin(tab.Location); else pane.Main.Pin(tab.Location); });
        }
        Add(tab.IsLocked ? "Unlock tab" : "Lock tab (folders open in the other pane)", () => tab.IsLocked = !tab.IsLocked, "Ctrl+Shift+L");
        menu.Items.Add(new Separator());
        Add("Close tab", () => pane.CloseTab(tab), "Ctrl+W");
        Add("Close other tabs", () => { foreach (var t in pane.Tabs.Where(t => !ReferenceEquals(t, tab)).ToList()) pane.CloseTab(t); }, enabled: pane.Tabs.Count > 1);
        Add("Reopen closed tab", pane.ReopenClosedTab, "Ctrl+Shift+T");
        menu.IsOpen = true;
    }

    // Dropping a folder on the tab strip opens it in a new tab. (Drops on the file list are handled by the list itself.)
    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Handled || Pane == null || e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        bool isFolder = Directory.Exists(paths[0]);
        string folder = isFolder ? paths[0] : Path.GetDirectoryName(paths[0]) ?? paths[0];
        Pane.AddTab(folder, selectName: isFolder ? null : Path.GetFileName(paths[0]));
        Pane.Main.ActivePane = Pane;
        e.Handled = true;
    }
}
