using System.Windows;
using System.Windows.Input;

namespace FastFind.App.Views;

public partial class SyntaxHelpWindow : Window
{
    public SyntaxHelpWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
}
