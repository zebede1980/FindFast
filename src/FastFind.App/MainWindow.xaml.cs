using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using FastFind.App.Services;
using FastFind.App.ViewModels;
using FastFind.App.Views;
using FastFind.Core.Browse;

namespace FastFind.App;

public partial class MainWindow : Window
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeFilterStruct);

    public MainViewModel ViewModel { get; }

    private TrayIcon? _tray;
    private GlobalHotkey? _hotkey;
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel(App.Index, Dispatcher);
        ViewModel.IsWindowVisible = () => IsVisible && WindowState != WindowState.Minimized;
        ViewModel.ShowSettings = ShowSettings;
        ViewModel.ShowHelp = ShowHelp;
        ViewModel.ShowJump = ShowJump;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = ViewModel;

        var s = App.Settings;
        Width = s.WindowWidth;
        Height = s.WindowHeight;
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
        ApplyLayout();

        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        Closed += (_, _) => { _hotkey?.Dispose(); _tray?.Dispose(); };
        Loaded += (_, _) => ActiveView?.FocusGrid();
    }

    // ---- layout: sidebar, one or two panes, preview ----

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsDualPane):
            case nameof(MainViewModel.SidebarVisible):
            case nameof(MainViewModel.PreviewVisible):
                ApplyLayout();
                if (e.PropertyName == nameof(MainViewModel.PreviewVisible)) UpdatePreview();
                break;
            case nameof(MainViewModel.ActivePane):
            case nameof(MainViewModel.ActiveTab):
                _previewView = null; // follow the pane the user moved to
                Dispatcher.BeginInvoke(UpdatePreview);
                break;
        }
    }

    private void ApplyLayout()
    {
        var s = App.Settings;
        bool sidebar = ViewModel.SidebarVisible, dual = ViewModel.IsDualPane, preview = ViewModel.PreviewVisible;

        SidebarHost.Visibility = SidebarSplitter.Visibility = sidebar ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = new GridLength(sidebar ? Math.Clamp(s.SidebarWidth, 140, 480) : 0);

        // With one pane, the left pane simply spans the space the right one would have taken.
        RightPane.Visibility = PaneSplitter.Visibility = dual ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(LeftPane, dual ? 1 : 3);
        RightColumn.MinWidth = dual ? 260 : 0;

        Preview.Visibility = PreviewSplitter.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        PreviewColumn.Width = new GridLength(preview ? Math.Clamp(s.PreviewWidth, 200, 800) : 0);
        MinWidth = 520 + (sidebar ? 160 : 0) + (dual ? 260 : 0) + (preview ? 200 : 0);
    }

    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (ViewModel.SidebarVisible) App.Settings.SidebarWidth = SidebarColumn.ActualWidth;
        if (ViewModel.PreviewVisible) App.Settings.PreviewWidth = PreviewColumn.ActualWidth;
    }

    private void OnPaneSplitterDoubleClick(object sender, MouseButtonEventArgs e)
    {
        LeftColumn.Width = RightColumn.Width = new GridLength(1, GridUnitType.Star);
        e.Handled = true;
    }

    // ---- panes ----

    private PaneView ActivePaneView => ReferenceEquals(ViewModel.ActivePane, ViewModel.Right) ? RightPane : LeftPane;

    /// <summary>The file view that keyboard commands act on.</summary>
    public FilePaneView? ActiveView => ActivePaneView.CurrentView;

    /// <summary>Tab key: move to the other pane. Returns false when only one pane is showing.</summary>
    public bool FocusOtherPane(FilePaneView from)
    {
        if (!ViewModel.IsDualPane) return false;
        var target = ReferenceEquals(LeftPane.CurrentView, from) ? RightPane : LeftPane;
        if (target.DataContext is PaneViewModel pane) ViewModel.ActivePane = pane;
        target.CurrentView?.FocusGrid();
        return true;
    }

    public void OnPaneSelectionChanged(FilePaneView view)
    {
        // Preview whatever was selected most recently, whichever pane it is in.
        _previewView = view;
        UpdatePreview();
    }

    private FilePaneView? _previewView;

    private void UpdatePreview()
    {
        if (!ViewModel.PreviewVisible) return;
        var view = _previewView is { IsVisible: true } recent ? recent : ActiveView;
        var tab = view?.DataContext as FileTabViewModel;
        if (view == null || tab == null) { Preview.Show(null, 0, ""); return; }
        Preview.Show(view.PreviewRow, view.SelectedRows().Count, tab.SelectionText);
    }

    /// <summary>A column was shown or hidden in one pane: the layout is shared, so the other pane follows.</summary>
    public void ApplyColumnLayoutToAllPanes(FilePaneView changed)
    {
        foreach (var view in new[] { LeftPane.CurrentView, RightPane.CurrentView })
            if (view != null && !ReferenceEquals(view, changed)) view.ApplyColumnLayout();
    }

    // ---- window plumbing: tray, hotkey, single instance, device changes ----

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource?)PresentationSource.FromVisual(this);
        source?.AddHook(WndProc);

        var hwnd = new WindowInteropHelper(this).Handle;
        _hotkey = new GlobalHotkey(hwnd);
        ApplyHotkey(App.Settings.Hotkey);
        // Let a normal second launch reach this window even if it was started as administrator (UIPI blocks the message otherwise).
        ChangeWindowMessageFilterEx(hwnd, App.ShowMessage, 1 /* MSGFLT_ALLOW */, IntPtr.Zero);

        _tray = new TrayIcon(_hotkey.Error == null ? _hotkey.Text : "");
        _tray.ShowRequested += () => Dispatcher.BeginInvoke(() => ShowFromTray(search: false));
        _tray.NewSearchRequested += () => Dispatcher.BeginInvoke(() => ShowFromTray(search: true));
        _tray.RebuildRequested += () => App.Index.Rebuild(null);
        _tray.ExitRequested += () => Dispatcher.BeginInvoke(ExitApplication);
    }

    /// <summary>Re-registers the global hotkey; returns an error message if the combination is invalid or taken.</summary>
    public string? ApplyHotkey(string text)
    {
        if (_hotkey == null) return null;
        bool ok = _hotkey.Register(text);
        if (!ok) Logger.Warn($"Hotkey not registered: {_hotkey.Error}");
        _tray?.UpdateHotkeyText(ok ? _hotkey.Text : "");
        return ok ? null : _hotkey.Error;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var s = App.Settings;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal) { s.WindowWidth = Width; s.WindowHeight = Height; }
        ViewModel.SaveSession();
        s.Save();
        if (s.MinimizeToTray && !_exiting && _tray != null)
        {
            // Keep running in the tray so the hotkey keeps working and the window comes back instantly.
            e.Cancel = true;
            Hide();
            _tray.ShowHiddenTip(_hotkey?.Error == null ? _hotkey?.Text ?? "" : "");
            TrimMemory();
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    /// <summary>While hidden in the tray, give unused heap and working set back to Windows.</summary>
    private static void TrimMemory()
    {
        Task.Run(() =>
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1));
        });
    }

    /// <summary>Brings the window forward. With <paramref name="search"/>, lands in the search-everywhere tab ready to type.</summary>
    public void ShowFromTray(bool search)
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = App.Settings.WindowMaximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
        Topmost = true;   // brief topmost toggle is the reliable way to steal focus from another app
        Topmost = false;
        if (search) SearchEverywhere();
        else Dispatcher.BeginInvoke(() => ActiveView?.FocusGrid());
    }

    private void ToggleFromHotkey()
    {
        if (IsVisible && IsActive && WindowState != WindowState.Minimized && App.Settings.MinimizeToTray) Hide();
        else ShowFromTray(search: true);
    }

    /// <summary>Ctrl+Shift+F and the global hotkey: switch to the tab that searches every indexed drive and put the cursor in its box.</summary>
    public void SearchEverywhere()
    {
        ViewModel.ActivePane.ShowEverywhere();
        // The tab control swaps the view's data when the tab changes; focus once that has happened.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => ActiveView?.FocusSearch());
    }

    public void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_hotkey != null && _hotkey.IsMessage(msg, wParam))
        {
            ToggleFromHotkey();
            handled = true;
            return IntPtr.Zero;
        }
        if (App.ShowMessage != 0 && (uint)msg == App.ShowMessage)
        {
            // A second launch asks the running window to come forward, optionally opening the folder it was given.
            string? path = App.TakeOpenRequest();
            ShowFromTray(search: false);
            if (path != null) ViewModel.OpenPath(path, newTab: true);
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == WM_DEVICECHANGE)
        {
            // Volume arrivals (USB, BitLocker unlock, mounted VHD): look for new drives to index and list.
            int evt = wParam.ToInt32();
            if (evt == DBT_DEVICEARRIVAL || evt == DBT_DEVICEREMOVECOMPLETE)
            {
                Logger.Info($"Device change 0x{evt:X}; refreshing volumes");
                _ = Task.Delay(1500).ContinueWith(_ =>
                {
                    App.Index.RefreshVolumes();
                    Dispatcher.BeginInvoke(ViewModel.RefreshSidebar);
                });
            }
        }
        return IntPtr.Zero;
    }

    // ---- keyboard ----

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift), alt = mods.HasFlag(ModifierKeys.Alt);
        var pane = ViewModel.ActivePane;
        bool handled = true;

        if (key == Key.F1 && mods == ModifierKeys.None) ShowHelp();
        else if (ctrl && !alt && key == Key.Q) ExitApplication();
        else if (ctrl && !alt && key == Key.Tab) pane.SelectRelative(shift ? -1 : 1);
        else if (ctrl && !alt && !shift && key >= Key.D1 && key <= Key.D9)
        {
            int i = key - Key.D1;
            if (i < pane.Tabs.Count) pane.SelectedTab = pane.Tabs[i];
        }
        else if (ctrl && !alt && shift && key == Key.T) pane.ReopenClosedTab();
        else if (ctrl && !alt && key == Key.T) pane.NewTabCommand.Execute(null);
        else if (ctrl && !alt && (key == Key.W || key == Key.F4)) pane.CloseTab(pane.SelectedTab);
        else if (ctrl && !alt && !shift && key == Key.P) ShowJump();
        else if (ctrl && !alt && shift && key == Key.F) SearchEverywhere();
        else if (ctrl && !alt && !shift && key == Key.B) ViewModel.SidebarVisible = !ViewModel.SidebarVisible;
        else if (ctrl && !alt && !shift && key is Key.OemBackslash or Key.Oem5) ViewModel.IsDualPane = !ViewModel.IsDualPane;
        else if (alt && !ctrl && !shift && key == Key.P) ViewModel.PreviewVisible = !ViewModel.PreviewVisible;
        else
        {
            handled = false;
            // Focus outside both panes (sidebar, nothing at all): pane shortcuts still go to the active pane.
            bool inPane = Keyboard.FocusedElement is DependencyObject d && FindVisualParent<FilePaneView>(d) != null;
            if (!inPane && ActiveView is { } view) handled = view.HandleKey(e);
        }
        if (handled) e.Handled = true;
    }

    // ---- dialogs ----

    private void ShowHelp()
    {
        var w = new SyntaxHelpWindow { Owner = this };
        w.Show();
    }

    private void ShowSettings()
    {
        var w = new SettingsWindow(ViewModel) { Owner = this };
        w.ShowDialog();
    }

    private void ShowJump()
    {
        var jump = new JumpWindow(ViewModel) { Owner = this };
        // Centre it near the top of this window, in device-independent units whatever the monitor's scaling.
        var origin = PointToScreen(new Point(0, 0));
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null) origin = source.CompositionTarget.TransformFromDevice.Transform(origin);
        jump.Left = origin.X + Math.Max(0, (ActualWidth - jump.Width) / 2);
        jump.Top = origin.Y + 70;
        jump.Show();
    }

    private void OnEnableServiceClick(object sender, RoutedEventArgs e) => EnableService(this);

    /// <summary>Installs the index service (one UAC prompt). Shared by the status bar link and the Settings window.</summary>
    public static void EnableService(Window owner, Action? done = null)
    {
        if (!App.Index.ServiceAvailable)
        {
            MessageBox.Show(owner, "FastFind.Service.exe was not found next to FastFind.exe, so the index service cannot be installed from this copy.",
                "Index service", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Task.Run(App.Index.InstallService).ContinueWith(t =>
        {
            string? error = t.IsFaulted ? t.Exception?.GetBaseException().Message : t.Result;
            if (error != null && error != "Cancelled.")
                MessageBox.Show(owner, error, "Index service", MessageBoxButton.OK, MessageBoxImage.Warning);
            done?.Invoke();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---- sidebar ----

    private static SidebarItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as SidebarItem;

    private void GoToSidebarItem(SidebarItem item, bool newTab)
    {
        switch (item.Kind)
        {
            case SidebarKind.Search:
                SearchEverywhere();
                break;
            case SidebarKind.PinAction:
                PinCurrentFolder();
                break;
            case SidebarKind.Shell:
                ShellOps.OpenInExplorer(item.Path);
                break;
            default:
                ViewModel.Open(item.Path, newTab);
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => ActiveView?.FocusGrid());
                break;
        }
    }

    private void OnSidebarClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) GoToSidebarItem(item, newTab: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    private void OnSidebarMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Middle-click opens the place in a new tab.
        if (e.ChangedButton == MouseButton.Middle && ItemOf(sender) is { } item)
        {
            GoToSidebarItem(item, newTab: true);
            e.Handled = true;
        }
    }

    private void OnSidebarRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is not { } item || item.Kind is SidebarKind.Search or SidebarKind.Shell or SidebarKind.PinAction) return;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        void Add(string text, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = text, IsEnabled = enabled };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        Add("Open", () => GoToSidebarItem(item, false));
        Add("Open in new tab", () => GoToSidebarItem(item, true));
        Add("Open in other pane", () => ViewModel.OpenInOtherPane(ViewModel.ActivePane, item.Path));
        menu.Items.Add(new Separator());
        if (item.Kind == SidebarKind.Favourite)
        {
            int at = App.Settings.Favourites.FindIndex(f => string.Equals(f, item.Path, StringComparison.OrdinalIgnoreCase));
            Add("Move up", () => ViewModel.MovePin(item.Path, -1), at > 0);
            Add("Move down", () => ViewModel.MovePin(item.Path, 1), at >= 0 && at < App.Settings.Favourites.Count - 1);
            Add("Unpin from sidebar", () => ViewModel.Unpin(item.Path));
            menu.Items.Add(new Separator());
        }
        else if (item.Kind is SidebarKind.Folder or SidebarKind.Drive or SidebarKind.Network && !ViewModel.IsPinned(PathUtil.Normalize(item.Path)))
        {
            Add("Pin to sidebar", () => ViewModel.Pin(PathUtil.Normalize(item.Path)));
        }
        Add("Copy path", () => ShellOps.CopyText(item.Path));
        Add("Open in Terminal", () => ShellOps.OpenTerminal(item.Path));
        Add("Open in Windows Explorer", () => ShellOps.OpenFolder(item.Path));
        Add("Properties", () => ShellOps.ShowProperties(item.Path));
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static bool AcceptsFiles(SidebarItem? item) => item is { Kind: SidebarKind.Folder or SidebarKind.Favourite or SidebarKind.Drive or SidebarKind.Network };

    /// <summary>Folders among the things being dragged (only folders can be pinned).</summary>
    private static List<string> DraggedFolders(DragEventArgs e)
        => e.Data.GetData(DataFormats.FileDrop) is string[] paths ? paths.Where(Directory.Exists).ToList() : new List<string>();

    /// <summary>Pins the folder on show in the active pane (the "Pin current folder" entry).</summary>
    private void PinCurrentFolder()
    {
        var tab = ViewModel.ActiveTab;
        if (tab == null || tab.IsEverywhere) { tab?.SetTransientStatus("Open a folder first, then pin it."); return; }
        if (ViewModel.IsPinned(tab.Location)) { tab.SetTransientStatus($"{PathUtil.LeafName(tab.Location)} is already pinned."); return; }
        ViewModel.Pin(tab.Location);
        tab.SetTransientStatus($"Pinned {PathUtil.LeafName(tab.Location)} to the sidebar.");
    }

    private void OnSidebarDragOver(object sender, DragEventArgs e)
    {
        var item = ItemOf(sender);
        if (item?.Kind == SidebarKind.PinAction)
            e.Effects = DraggedFolders(e).Count > 0 ? DragDropEffects.Link : DragDropEffects.None; // dropping a folder here pins it
        else
            e.Effects = AcceptsFiles(item) && ActiveView is { } view ? view.PlanDropOnFolder(e, item!.Path) : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnSidebarDrop(object sender, DragEventArgs e)
    {
        var item = ItemOf(sender);
        if (item?.Kind == SidebarKind.PinAction)
        {
            foreach (var folder in DraggedFolders(e)) ViewModel.Pin(PathUtil.Normalize(folder));
        }
        else if (AcceptsFiles(item) && ActiveView is { } view) view.DropOnFolder(e, item!.Path);
        e.Handled = true;
    }

    // ---- visual tree helpers ----

    internal static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var inner = FindVisualChild<T>(child);
            if (inner != null) return inner;
        }
        return null;
    }

    internal static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var current = child;
        while (current != null)
        {
            if (current is T t) return t;
            current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
