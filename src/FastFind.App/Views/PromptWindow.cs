using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FastFind.App.Views;

/// <summary>A one-line question ("New folder name"). The validator returns an error to show, or null to accept.</summary>
public sealed class PromptWindow : Window
{
    private readonly TextBox _box;
    private readonly TextBlock _error;
    private readonly Func<string, string?> _accept;

    public string Value => _box.Text.Trim();

    /// <param name="accept">Called with the trimmed text when the user confirms. Return an error message to keep the dialog open.</param>
    /// <param name="selectLength">How much of the initial text to select (-1 for all), e.g. a file name without its extension.</param>
    public PromptWindow(string title, string label, string initial, string button, Func<string, string?> accept, int selectLength = -1)
    {
        _accept = accept;
        Title = title;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];

        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        var caption = new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 0, 0, 8), TextTrimming = TextTrimming.CharacterEllipsis };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        panel.Children.Add(caption);

        _box = new TextBox { Text = initial, FontSize = 14 };
        _box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Confirm(); e.Handled = true; } };
        panel.Children.Add(_box);

        _error = new TextBlock { FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        _error.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        panel.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = button, MinWidth = 90, IsDefault = true };
        ok.Click += (_, _) => Confirm();
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true });
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) =>
        {
            _box.Focus();
            if (selectLength < 0 || selectLength > initial.Length) _box.SelectAll();
            else _box.Select(0, selectLength);
        };
    }

    private void Confirm()
    {
        string? error = _accept(Value);
        if (error != null) { _error.Text = error; return; }
        DialogResult = true;
        Close();
    }
}
