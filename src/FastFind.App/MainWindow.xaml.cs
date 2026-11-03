using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using FastFind.App.Services;
using FastFind.App.ViewModels;
using FastFind.App.Views;

namespace FastFind.App;

public partial class MainWindow : Window
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

    public MainViewModel ViewModel { get; }

    private TrayIcon? _tray;
    private GlobalHotkey? _hotkey;
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel(App.IndexManager, Dispatcher);
        ViewModel.ShowSettings = () => OnSettingsClick(this, new RoutedEventArgs());
        ViewModel.ShowHelp = () => OnHelpClick(this, new RoutedEventArgs());
        DataContext = ViewModel;

        var s = App.Settings;
        Width = s.WindowWidth;
        Height = s.WindowHeight;
        if (s.WindowMaximized) WindowState = WindowState.Maximized;

        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        Closed += (_, _) => { _hotkey?.Dispose(); _tray?.Dispose(); };
        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource?)PresentationSource.FromVisual(this);
        source?.AddHook(WndProc);

        _hotkey = new GlobalHotkey(new WindowInteropHelper(this).Handle);
        ApplyHotkey(App.Settings.Hotkey);

        _tray = new TrayIcon(_hotkey.Error == null ? _hotkey.Text : "");
        _tray.ShowRequested += () => Dispatcher.BeginInvoke(ShowFromTray);
        _tray.NewSearchRequested += () => Dispatcher.BeginInvoke(() => { ShowFromTray(); ViewModel.AddTab(); });
        _tray.RebuildRequested += () => _ = App.IndexManager.RebuildAllAsync();
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

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var s = App.Settings;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal) { s.WindowWidth = Width; s.WindowHeight = Height; }
        if (s.MinimizeToTray && !_exiting && _tray != null)
        {
            // Keep running in the tray so the index stays in memory and the hotkey keeps working.
            e.Cancel = true;
            Hide();
            _tray.ShowHiddenTip(_hotkey?.Error == null ? _hotkey?.Text ?? "" : "");
        }
    }

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = App.Settings.WindowMaximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
        Topmost = true;   // brief topmost toggle is the reliable way to steal focus from another app
        Topmost = false;
        FocusSearchBox();
    }

    private void ToggleFromHotkey()
    {
        if (IsVisible && IsActive && WindowState != WindowState.Minimized && App.Settings.MinimizeToTray) Hide();
        else ShowFromTray();
    }

    public void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    /// <summary>Volume arrivals (USB, BitLocker unlock, mounted VHD) trigger an immediate rescan of drives; WM_HOTKEY toggles the window.</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_hotkey != null && _hotkey.IsMessage(msg, wParam))
        {
            ToggleFromHotkey();
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == WM_DEVICECHANGE)
        {
            int evt = wParam.ToInt32();
            if (evt == DBT_DEVICEARRIVAL || evt == DBT_DEVICEREMOVECOMPLETE)
            {
                Logger.Info($"Device change 0x{evt:X}; refreshing volumes");
                _ = Task.Delay(1500).ContinueWith(_ => App.IndexManager.RefreshVolumesAsync());
            }
        }
        return IntPtr.Zero;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.F1) { OnHelpClick(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && e.Key == Key.S)
        {
            FindVisualChild<SearchTabView>(Tabs)?.ExportResults(selectedOnly: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Q) { ExitApplication(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Tab)
        {
            int n = ViewModel.Tabs.Count;
            if (n > 1)
            {
                int i = ViewModel.Tabs.IndexOf(ViewModel.SelectedTab!);
                int next = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? (i - 1 + n) % n : (i + 1) % n;
                ViewModel.SelectedTab = ViewModel.Tabs[next];
            }
            e.Handled = true;
        }
        else if (ctrl && e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            int i = e.Key - Key.D1;
            if (i < ViewModel.Tabs.Count) { ViewModel.SelectedTab = ViewModel.Tabs[i]; e.Handled = true; }
        }
        else if (ctrl && (e.Key == Key.F || e.Key == Key.L || e.Key == Key.E && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
        {
            FocusSearchBox();
            e.Handled = true;
        }
    }

    private void FocusSearchBox()
    {
        if (Tabs.SelectedContent is not null && FindVisualChild<SearchTabView>(Tabs) is { } view) view.FocusSearch();
    }

    private void OnTabsPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Middle-click a tab header to close it.
        if (e.ChangedButton != MouseButton.Middle) return;
        if (e.OriginalSource is DependencyObject d && FindVisualParent<TabItem>(d) is { DataContext: SearchTabViewModel tab })
        {
            ViewModel.CloseTab(tab);
            e.Handled = true;
        }
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        var w = new SyntaxHelpWindow { Owner = this };
        w.Show();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var w = new SettingsWindow(ViewModel) { Owner = this };
        w.ShowDialog();
    }

    private void OnElevateClick(object sender, RoutedEventArgs e) => ShellOps.RestartElevated();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Dropping a folder anywhere on the window opens a tab scoped to it.</summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        string folder = Directory.Exists(paths[0]) ? paths[0] : Path.GetDirectoryName(paths[0]) ?? paths[0];
        ViewModel.AddTab(scope: folder);
        e.Handled = true;
    }

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
