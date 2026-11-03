using System.Windows;
using System.Windows.Controls;
using FastFind.App.Services;
using FastFind.App.ViewModels;
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
        CreatedColumnBox.IsChecked = s.ShowCreatedColumn;
        RemovableBox.IsChecked = s.IncludeRemovable;
        NetworkBox.IsChecked = s.IncludeNetwork;
        TrayBox.IsChecked = s.MinimizeToTray;
        HotkeyBox.Text = s.Hotkey;
        SingleInstanceBox.IsChecked = s.SingleInstance;
        RescanHoursBox.Text = s.NetworkRescanHours.ToString("0.##");
        foreach (var p in s.NetworkLocations) NetworkList.Items.Add(p);
        VolumeGrid.ItemsSource = _vm.Volumes;
        ElevationText.Text = vm.IsElevated
            ? "Running as administrator: NTFS volumes are read straight from the Master File Table and kept live via the USN change journal."
            : "Running without administrator rights: volumes are scanned folder by folder and watched with FileSystemWatcher. The scan result is cached so later launches start instantly.";
        ElevateButton.Visibility = vm.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        LogPathText.Text = Logger.FilePath;
        _loading = false;
        _vm.Manager.VolumesChanged += OnVolumesChanged;
        Closed += (_, _) => _vm.Manager.VolumesChanged -= OnVolumesChanged;
    }

    private void OnVolumesChanged() => Dispatcher.BeginInvoke(() => VolumeGrid.Items.Refresh());

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
        s.ShowCreatedColumn = CreatedColumnBox.IsChecked == true;
        s.IncludeRemovable = RemovableBox.IsChecked == true;
        s.IncludeNetwork = NetworkBox.IsChecked == true;
        s.MinimizeToTray = TrayBox.IsChecked == true;
        s.SingleInstance = SingleInstanceBox.IsChecked == true;
        s.Save();
        _vm.Manager.Options.IncludeRemovable = s.IncludeRemovable;
        _vm.Manager.Options.IncludeNetwork = s.IncludeNetwork;
        _ = _vm.Manager.RefreshVolumesAsync();
        if (Owner is MainWindow main && MainWindow.FindVisualChild<SearchTabView>(main) is { } view)
            view.CreatedColumn.Visibility = s.ShowCreatedColumn ? Visibility.Visible : Visibility.Collapsed;
        if (s.Columns.Count > 0)
        {
            var created = s.Columns.FirstOrDefault(c => c.Key == "Date created");
            if (created != null) { created.Visible = s.ShowCreatedColumn; s.Save(); }
        }
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
        _ = _vm.Manager.RefreshVolumesAsync(); // drops the volume on the next discovery pass
    }

    private void SaveNetworkLocations()
    {
        var s = App.Settings;
        s.NetworkLocations = NetworkList.Items.Cast<string>().ToList();
        s.Save();
        _vm.Manager.Options.NetworkLocations.Clear();
        _vm.Manager.Options.NetworkLocations.AddRange(s.NetworkLocations);
        _ = _vm.Manager.RefreshVolumesAsync();
    }

    private void OnRescanHoursChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (!double.TryParse(RescanHoursBox.Text, out double hours) || hours <= 0) { RescanHoursBox.Text = App.Settings.NetworkRescanHours.ToString("0.##"); return; }
        App.Settings.NetworkRescanHours = hours;
        App.Settings.Save();
        _vm.Manager.Options.NetworkRescanInterval = TimeSpan.FromHours(Math.Clamp(hours, 0.25, 24 * 30));
    }

    private void OnRescanNetworkClick(object sender, RoutedEventArgs e)
    {
        foreach (var v in _vm.Manager.Volumes.Where(v => v.IsNetwork)) _ = _vm.Manager.RebuildAsync(v.Root);
    }

    private void OnRebuildClick(object sender, RoutedEventArgs e) => _ = _vm.Manager.RebuildAllAsync();

    private void OnRebuildSelectedClick(object sender, RoutedEventArgs e)
    {
        if (VolumeGrid.SelectedItem is VolumeState v) _ = _vm.Manager.RebuildAsync(v.Root);
    }

    private void OnElevateClick(object sender, RoutedEventArgs e) => ShellOps.RestartElevated();
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
