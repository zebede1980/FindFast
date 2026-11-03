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
        s.Save();
        _vm.Manager.Options.IncludeRemovable = s.IncludeRemovable;
        _vm.Manager.Options.IncludeNetwork = s.IncludeNetwork;
        _ = _vm.Manager.RefreshVolumesAsync();
        if (Owner is MainWindow main && MainWindow.FindVisualChild<SearchTabView>(main) is { } view)
            view.CreatedColumn.Visibility = s.ShowCreatedColumn ? Visibility.Visible : Visibility.Collapsed;
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

    private void OnRebuildClick(object sender, RoutedEventArgs e) => _ = _vm.Manager.RebuildAllAsync();

    private void OnRebuildSelectedClick(object sender, RoutedEventArgs e)
    {
        if (VolumeGrid.SelectedItem is VolumeState v) _ = _vm.Manager.RebuildAsync(v.Root);
    }

    private void OnElevateClick(object sender, RoutedEventArgs e) => ShellOps.RestartElevated();
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
