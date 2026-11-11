using System.Windows;
using System.Windows.Controls;
using FastFind.App.Services;
using FastFind.App.ViewModels;
using FastFind.Core.Api;
using FastFind.Core.Indexing;

namespace FastFind.App.Views;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _loading = true;

    public SettingsWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        var s = App.Settings;
        foreach (ComboBoxItem item in ThemeBox.Items)
            if ((string)item.Tag == s.Theme) ThemeBox.SelectedItem = item;
        if (ThemeBox.SelectedItem == null) ThemeBox.SelectedIndex = 0;
        FolderSizesBox.IsChecked = s.ShowFolderSizes;
        HideHiddenBox.IsChecked = s.HideHiddenSystemByDefault;
        ConfirmRecycleBox.IsChecked = s.ConfirmRecycle;
        RemovableBox.IsChecked = s.IncludeRemovable;
        NetworkBox.IsChecked = s.IncludeNetwork;
        TrayBox.IsChecked = s.MinimizeToTray;
        HotkeyBox.Text = s.Hotkey;
        SingleInstanceBox.IsChecked = s.SingleInstance;
        ExplorerMenuBox.IsChecked = ExplorerIntegration.IsRegistered;
        RescanHoursBox.Text = s.NetworkRescanHours.ToString("0.##");
        foreach (var p in s.NetworkLocations) NetworkList.Items.Add(p);
        VolumeGrid.ItemsSource = _vm.Volumes;
        LogPathText.Text = Logger.FilePath;
        UpdateServiceState();
        _loading = false;
        App.Index.SourceChanged += OnSourceChanged;
        Closed += (_, _) => App.Index.SourceChanged -= OnSourceChanged;
    }

    private void OnSourceChanged() => Dispatcher.BeginInvoke(UpdateServiceState);

    /// <summary>Explains where the index lives right now and offers the matching action.</summary>
    private void UpdateServiceState()
    {
        var index = App.Index;
        if (index.IsRemote)
        {
            ElevationText.Text = index.IsElevated
                ? "The index service is running. NTFS volumes are read straight from the Master File Table and kept live from the change journal, while this window runs without administrator rights."
                : "The index service is running, but without administrator rights, so it scans folder by folder.";
            ServiceButton.Content = "Remove index service";
            ServiceButton.Visibility = Visibility.Visible;
        }
        else if (index.IsElevated)
        {
            ElevationText.Text = "This window is running as administrator, so it reads the NTFS Master File Table itself. Files opened from here also run as administrator; "
                                 + "installing the index service lets you run FastFind normally and keep the fast index.";
            ServiceButton.Content = "Install index service";
            ServiceButton.Visibility = index.ServiceAvailable ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            ElevationText.Text = "The index is built by scanning folders, which takes minutes on a large drive (the result is cached, so later launches start instantly). "
                                 + "Installing the index service (one administrator prompt) reads the NTFS file table instead: seconds to build, and always up to date.";
            ServiceButton.Content = "Install index service";
            ServiceButton.Visibility = index.ServiceAvailable ? Visibility.Visible : Visibility.Collapsed;
        }
        ServiceButton.IsEnabled = true;
    }

    private void OnServiceClick(object sender, RoutedEventArgs e)
    {
        ServiceButton.IsEnabled = false;
        if (App.Index.IsRemote)
        {
            Task.Run(App.Index.UninstallService).ContinueWith(t =>
            {
                string? error = t.IsFaulted ? t.Exception?.GetBaseException().Message : t.Result;
                if (error != null && error != "Cancelled.") MessageBox.Show(this, error, "Index service", MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateServiceState();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        else
        {
            MainWindow.EnableService(this, UpdateServiceState);
        }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedItem is not ComboBoxItem item) return;
        App.Settings.Theme = (string)item.Tag;
        App.ApplyTheme(App.Settings.Theme);
        App.Settings.Save();
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = App.Settings;
        bool sizesChanged = s.ShowFolderSizes != (FolderSizesBox.IsChecked == true);
        s.ShowFolderSizes = FolderSizesBox.IsChecked == true;
        s.HideHiddenSystemByDefault = HideHiddenBox.IsChecked == true;
        s.ConfirmRecycle = ConfirmRecycleBox.IsChecked == true;
        s.IncludeRemovable = RemovableBox.IsChecked == true;
        s.IncludeNetwork = NetworkBox.IsChecked == true;
        s.MinimizeToTray = TrayBox.IsChecked == true;
        s.SingleInstance = SingleInstanceBox.IsChecked == true;
        s.Save();
        App.Index.Configure(App.IndexSettingsFrom(s));
        if (sizesChanged) _vm.RefreshFolderViews();
    }

    private void OnExplorerMenuChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        string? error = ExplorerMenuBox.IsChecked == true ? ExplorerIntegration.Register() : ExplorerIntegration.Unregister();
        if (error != null) MessageBox.Show(this, error, "Windows integration", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnHotkeyKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { OnHotkeyChanged(sender, e); e.Handled = true; }
    }

    private void OnHotkeyChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        string text = HotkeyBox.Text.Trim();
        string? error = Owner is MainWindow main ? main.ApplyHotkey(text) : null;
        HotkeyError.Text = error ?? (text.Length == 0 ? "Hotkey disabled" : "");
        if (error == null)
        {
            App.Settings.Hotkey = text;
            App.Settings.Save();
        }
    }

    // ---- network locations ----

    private void OnNetworkPathKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { OnAddNetworkClick(sender, e); e.Handled = true; }
    }

    private void OnAddNetworkClick(object sender, RoutedEventArgs e)
    {
        string root = IndexManager.NormalizeRoot(NetworkPathBox.Text);
        if (root.Length < 3) return;
        if (!root.StartsWith(@"\\") && !(root.Length == 3 && root[1] == ':'))
        {
            MessageBox.Show(this, @"Enter a UNC path such as \\server\share or a drive root such as Z:\", "Network location", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (NetworkList.Items.Cast<string>().Any(x => x.Equals(root, StringComparison.OrdinalIgnoreCase))) return;
        NetworkList.Items.Add(root);
        NetworkPathBox.Clear();
        SaveNetworkLocations();
    }

    private void OnRemoveNetworkClick(object sender, RoutedEventArgs e)
    {
        if (NetworkList.SelectedItem is not string root) return;
        NetworkList.Items.Remove(root);
        SaveNetworkLocations();
    }

    private void SaveNetworkLocations()
    {
        var s = App.Settings;
        s.NetworkLocations = NetworkList.Items.Cast<string>().ToList();
        s.Save();
        App.Index.Configure(App.IndexSettingsFrom(s)); // the index adds or drops the volume on its next discovery pass
        _vm.RefreshSidebar();
    }

    private void OnRescanHoursChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (!double.TryParse(RescanHoursBox.Text, out double hours) || hours <= 0) { RescanHoursBox.Text = App.Settings.NetworkRescanHours.ToString("0.##"); return; }
        App.Settings.NetworkRescanHours = hours;
        App.Settings.Save();
        App.Index.Configure(App.IndexSettingsFrom(App.Settings));
    }

    private void OnRescanNetworkClick(object sender, RoutedEventArgs e)
    {
        foreach (var v in App.Index.Volumes.Where(v => v.IsNetwork)) App.Index.Rebuild(v.Root);
    }

    private void OnRebuildClick(object sender, RoutedEventArgs e) => App.Index.Rebuild(null);

    private void OnRebuildSelectedClick(object sender, RoutedEventArgs e)
    {
        if (VolumeGrid.SelectedItem is VolumeInfo v) App.Index.Rebuild(v.Root);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
