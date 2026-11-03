using System.IO;
using System.Windows;
using System.Windows.Input;
using FastFind.App.Services;

namespace FastFind.App.Views;

public partial class RenameWindow : Window
{
    private readonly string _path;

    public RenameWindow(string path)
    {
        _path = path;
        InitializeComponent();
        PathText.Text = path;
        string name = Path.GetFileName(path);
        NameBox.Text = name;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            // Select the base name only, like Explorer.
            int dot = Directory.Exists(path) ? -1 : name.LastIndexOf('.');
            NameBox.Select(0, dot > 0 ? dot : name.Length);
        };
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnRenameClick(sender, e); e.Handled = true; }
    }

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        string? error = ShellFileOps.Rename(_path, NameBox.Text.Trim());
        if (error != null) { ErrorText.Text = error; return; }
        DialogResult = true;
        Close();
    }
}
