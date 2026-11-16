using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FastFind.App.Services;
using FastFind.App.ViewModels;

namespace FastFind.App.Views;

/// <summary>
/// Where the user makes their own right-click entries (Settings > Right-click actions): the list on the left in menu
/// order, the selected one's details on the right, and an example of the command it comes to.
/// </summary>
public partial class CustomActionsWindow : Window
{
    /// <summary>An action as the list shows it.</summary>
    public sealed class Row : ObservableObject
    {
        public CustomAction Action { get; }
        public Row(CustomAction action) => Action = action;

        public string Title => Action.Name.Trim().Length > 0 ? Action.Name.Trim() : "(no name yet)";
        public string Subtitle => !CustomActions.IsUsable(Action) ? "Needs a name and a program"
            : (Action.AppliesTo.Trim().Length > 0 ? Action.AppliesTo.Trim() : "Offered for nothing") + (Action.RunAsAdmin ? "  ·  as administrator" : "");

        public void Refresh()
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Subtitle));
        }
    }

    private readonly ObservableCollection<Row> _rows = new();
    /// <summary>Set while the boxes are being filled from an action, so that filling them is not taken for the user typing.</summary>
    private bool _loading;

    public CustomActionsWindow()
    {
        InitializeComponent();
        foreach (var action in App.Settings.CustomActions) _rows.Add(new Row(action));
        ActionList.ItemsSource = _rows;
        if (_rows.Count > 0) ActionList.SelectedIndex = 0;
        ShowSelected();
        Closed += (_, _) => Save();
    }

    private Row? Selected => ActionList.SelectedItem as Row;

    private void Save()
    {
        App.Settings.CustomActions = _rows.Select(r => r.Action).ToList();
        App.Settings.Save();
    }

    // ---- the list ----

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowSelected();
        if (IsLoaded) Save(); // what was typed into the action just left
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (AddButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = AddButton;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void OnAddEmptyClick(object sender, RoutedEventArgs e) => Add(new CustomAction { AppliesTo = "*" });

    private void OnAddPowerShellClick(object sender, RoutedEventArgs e) => Add(new CustomAction
    {
        Name = "Run in PowerShell",
        AppliesTo = "ps1",
        // PowerShell 7 where it is installed, otherwise the one that comes with Windows.
        Program = IsOnPath("pwsh.exe") ? "pwsh.exe" : "powershell.exe",
        Arguments = "-NoExit -File {path}",
    });

    private void OnAddProgramClick(object sender, RoutedEventArgs e)
    {
        if (BrowseForProgram() is not { } program) return;
        Add(new CustomAction { Name = "Open with " + ProgramName(program), AppliesTo = "*", Program = program });
    }

    private void Add(CustomAction action)
    {
        var row = new Row(action);
        _rows.Add(row);
        ActionList.SelectedItem = row;
        ActionList.ScrollIntoView(row);
        Save();
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        // Only something that took effort to set up is worth a question.
        if (CustomActions.IsUsable(row.Action)
            && MessageBox.Show(this, $"Remove \"{row.Title}\" from the right-click menu?", Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        int at = _rows.IndexOf(row);
        _rows.Remove(row);
        if (_rows.Count > 0) ActionList.SelectedIndex = Math.Min(at, _rows.Count - 1);
        ShowSelected();
        Save();
    }

    private void OnUpClick(object sender, RoutedEventArgs e) => Move(-1);
    private void OnDownClick(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int delta)
    {
        if (Selected is not { } row) return;
        int from = _rows.IndexOf(row), to = from + delta;
        if (to < 0 || to >= _rows.Count) return;
        _rows.Move(from, to);
        ActionList.SelectedItem = row;
        ShowSelected();
        Save();
    }

    // ---- the selected action ----

    private void ShowSelected()
    {
        var row = Selected;
        int at = row == null ? -1 : _rows.IndexOf(row);
        FormScroll.Visibility = row != null ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = row == null ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.IsEnabled = row != null;
        UpButton.IsEnabled = at > 0;
        DownButton.IsEnabled = at >= 0 && at < _rows.Count - 1;
        if (row == null) return;

        _loading = true;
        NameBox.Text = row.Action.Name;
        AppliesBox.Text = row.Action.AppliesTo;
        ProgramBox.Text = row.Action.Program;
        ArgumentsBox.Text = row.Action.Arguments;
        StartInBox.Text = row.Action.StartIn;
        AdminBox.IsChecked = row.Action.RunAsAdmin;
        _loading = false;
        ShowExample(row.Action);
    }

    private void OnFieldChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || Selected is not { } row) return;
        var action = row.Action;
        action.Name = NameBox.Text;
        action.AppliesTo = AppliesBox.Text;
        action.Program = ProgramBox.Text;
        action.Arguments = ArgumentsBox.Text;
        action.StartIn = StartInBox.Text;
        action.RunAsAdmin = AdminBox.IsChecked == true;
        row.Refresh();
        ShowExample(action);
    }

    /// <summary>Shows the command the action comes to for a made-up file, so placeholders and quoting can be checked before trying it for real.</summary>
    private void ShowExample(CustomAction action)
    {
        if (!CustomActions.IsUsable(action))
        {
            PreviewCaption.Text = "NOT IN THE MENU YET";
            PreviewText.Visibility = Visibility.Collapsed;
            PreviewNote.Text = "Give it a name and a program and it will appear in the right-click menu.";
            return;
        }
        var example = CustomActions.ExampleFor(action);
        var targets = new List<CustomActions.Target> { example };
        bool once = CustomActions.RunsOnce(action);
        // Two items when they are handed over together, to show how they are set out.
        if (once && !example.IsDirectory)
            targets.Add(new CustomActions.Target(Path.Combine(Path.GetDirectoryName(example.Path) ?? "", "Another file" + Path.GetExtension(example.Path)), false));
        else if (once)
            targets.Add(new CustomActions.Target(Path.Combine(Path.GetDirectoryName(example.Path) ?? "", "Another folder"), true));

        var launches = CustomActions.Plan(action, targets, out string? error);
        PreviewCaption.Text = "WHAT IT RUNS, FOR EXAMPLE";
        PreviewText.Visibility = Visibility.Visible;
        if (error != null || launches.Count == 0) PreviewText.Text = error ?? "";
        else
        {
            string program = launches[0].Program.Any(char.IsWhiteSpace) ? $"\"{launches[0].Program}\"" : launches[0].Program;
            PreviewText.Text = $"{program} {launches[0].Arguments}".TrimEnd();
        }
        PreviewNote.Text = (once ? "Started once, with everything you have selected." : "Started once for each item you have selected.")
                           + (action.RunAsAdmin ? " Windows asks for administrator permission first." : "");
    }

    // ---- choosing a program ----

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (BrowseForProgram() is not { } program) return;
        ProgramBox.Text = program;
        if (NameBox.Text.Trim().Length == 0) NameBox.Text = "Open with " + ProgramName(program);
    }

    private string? BrowseForProgram()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a program",
            Filter = "Programs and scripts|*.exe;*.cmd;*.bat;*.com;*.lnk|All files|*.*",
            DereferenceLinks = false, // a shortcut is run as a shortcut, with the options it carries
        };
        string current = Environment.ExpandEnvironmentVariables(ProgramBox.Text.Trim().Trim('"'));
        try
        {
            if (Selected != null && Path.IsPathRooted(current) && Directory.Exists(Path.GetDirectoryName(current))) dialog.InitialDirectory = Path.GetDirectoryName(current);
            else dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        }
        catch (ArgumentException) { }
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    /// <summary>What a program calls itself ("HandBrake"), falling back on its file name.</summary>
    private static string ProgramName(string path)
    {
        try
        {
            string? description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(description)) return description;
        }
        catch (Exception) { }
        return Path.GetFileNameWithoutExtension(path);
    }

    private static bool IsOnPath(string file)
    {
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (File.Exists(Path.Combine(folder.Trim(), file))) return true; }
            catch (ArgumentException) { }
        }
        return false;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
