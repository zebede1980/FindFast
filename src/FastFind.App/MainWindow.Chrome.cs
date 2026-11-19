using System.Windows;
using System.Windows.Threading;
using FastFind.App.Services;

namespace FastFind.App;

/// <summary>
/// The main window's side of the glass look (see <see cref="GlassChrome"/>): its title bar holds the jump box and the
/// window's own buttons, and it is the window that hears from Windows when the theme changes.
/// </summary>
public partial class MainWindow
{
    private const int WM_SYSCOLORCHANGE = 0x0015, WM_SETTINGCHANGE = 0x001A, WM_THEMECHANGED = 0x031A, WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
    private const double TitleBarHeight = 34; // the title bar row in MainWindow.xaml, its margin included

    private GlassChrome? _chrome;
    private DispatcherTimer? _themeTimer;

    private void InitializeChrome()
    {
        _chrome = GlassChrome.Attach(this, RootGrid, Aurora, CaptionSpacer, TitleBarHeight);
        _chrome.Changed += OnChromeChanged;
        TitleRight.SizeChanged += (_, _) => CentreJumpBox();
        OnChromeChanged();
    }

    private void OnChromeChanged()
    {
        // The ordinary title bar, when that is what we have, already shows the icon and the name.
        TitleBrand.Visibility = _chrome is { IsGlass: true } ? Visibility.Visible : Visibility.Collapsed;
        CentreJumpBox();
    }

    /// <summary>
    /// Puts the jump box in the middle of the window rather than the middle of what is left over, by giving the left of
    /// the title bar as much room as the right takes. When the window is too narrow for that, the box just takes what there is.
    /// </summary>
    private void CentreJumpBox()
    {
        double right = TitleRight.ActualWidth;
        TitleLeftColumn.MinWidth = ActualWidth - 2 * right >= 260 ? right : 0;
    }

    /// <summary>Windows tells every window when the theme, the accent colour or the contrast setting changes.</summary>
    private static bool IsThemeMessage(int msg) => msg is WM_SETTINGCHANGE or WM_SYSCOLORCHANGE or WM_THEMECHANGED or WM_DWMCOLORIZATIONCOLORCHANGED;

    /// <summary>These messages arrive in bursts, and WPF needs a moment to switch its own colours first; refresh ours once things settle.</summary>
    private void QueueThemeRefresh()
    {
        if (_themeTimer == null)
        {
            _themeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _themeTimer.Tick += (_, _) =>
            {
                _themeTimer.Stop();
                App.ApplyTheme(App.Settings.Theme);
            };
        }
        _themeTimer.Stop();
        _themeTimer.Start();
    }
}
