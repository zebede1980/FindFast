using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace FastFind.App;

/// <summary>
/// The glass look of the main window: the blurred desktop behind it, and a title bar we draw ourselves so it can hold
/// the jump box and the window's own buttons. Windows still draws minimise, maximise and close (so snapping and the
/// maximise button's layout menu keep working); we only keep their corner clear.
/// </summary>
public partial class MainWindow
{
    private const int WM_SYSCOLORCHANGE = 0x0015, WM_SETTINGCHANGE = 0x001A, WM_THEMECHANGED = 0x031A, WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
    private const int DWMWA_CAPTION_BUTTON_BOUNDS = 5, DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_TRANSIENTWINDOW = 3; // acrylic: the desktop behind the window, blurred
    private const double TitleBarHeight = 34; // the title bar row in MainWindow.xaml, its margin included

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    private bool _glass;
    private DispatcherTimer? _themeTimer;

    /// <summary>
    /// Glass needs Windows 11 22H2 or later, and is not used with a high-contrast theme (Windows draws no glass then,
    /// and the user has asked for plain colours). Without it the window keeps the ordinary Windows title bar.
    /// </summary>
    private static bool GlassAvailable => Environment.OSVersion.Version.Build >= 22621 && !SystemParameters.HighContrast;

    private void InitializeChrome()
    {
        ApplyChrome();
        StateChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateWindowEdges);
        SizeChanged += (_, _) => UpdateWindowEdges();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateWindowEdges);
        TitleRight.SizeChanged += (_, _) => CentreJumpBox();
        // WPF puts its own backdrop back at moments of its choosing (a theme change, for one); asking again is cheap.
        Activated += (_, _) => { ApplyBackdrop(); ApplyWindowTint(); };
        Deactivated += (_, _) => ApplyWindowTint();
        ApplyWindowTint();
    }

    /// <summary>
    /// Windows only blurs the desktop behind the window you are working in; behind any other it paints a flat grey.
    /// A heavier tint while FastFind is in the background keeps it looking like itself rather than washed out.
    /// </summary>
    private void ApplyWindowTint()
    {
        RootGrid.SetResourceReference(Panel.BackgroundProperty, IsActive ? "FrostWindowTintBrush" : "FrostWindowTintInactiveBrush");
        // The coloured light dims with it, so a window in the background does not shout for attention.
        Aurora.Opacity = IsActive ? 1 : 0.5;
    }

    /// <summary>Switches between our title bar on glass and the ordinary Windows one, whichever this PC can show right now.</summary>
    internal void ApplyChrome()
    {
        bool glass = GlassAvailable;
        bool hasChrome = WindowChrome.GetWindowChrome(this) != null;
        if (glass != _glass || hasChrome != glass)
        {
            _glass = glass;
            WindowChrome.SetWindowChrome(this, glass
                ? new WindowChrome
                {
                    CaptionHeight = TitleBarHeight,
                    GlassFrameThickness = new Thickness(-1), // the backdrop runs under the whole window, title bar included
                    ResizeBorderThickness = new Thickness(6),
                    CornerRadius = new CornerRadius(0),
                    UseAeroCaptionButtons = true,
                }
                : null);
            // The ordinary title bar already shows the icon and the name.
            TitleBrand.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
        }
        ApplyBackdrop();
        UpdateWindowEdges();
    }

    private void ApplyBackdrop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !_glass) return;
        int acrylic = DWMSBT_TRANSIENTWINDOW;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref acrylic, sizeof(int));
    }

    /// <summary>
    /// Keeps our title bar out of the way of the buttons Windows draws, and keeps everything on screen when maximised
    /// (a maximised window without the ordinary frame hangs over the edges of the monitor by the width of that frame).
    /// </summary>
    private void UpdateWindowEdges()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (!_glass || !GetClientRect(hwnd, out RECT client))
        {
            RootGrid.Margin = default;
            CaptionSpacer.Width = 0;
            CentreJumpBox();
            return;
        }

        int left = 0, top = 0, right = 0, bottom = 0;
        if (WindowState == WindowState.Maximized)
        {
            var origin = new POINT();
            var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            if (ClientToScreen(hwnd, ref origin) && GetMonitorInfoW(MonitorFromWindow(hwnd, 2 /* nearest */), ref info))
            {
                left = Math.Max(0, info.Work.Left - origin.X);
                top = Math.Max(0, info.Work.Top - origin.Y);
                right = Math.Max(0, origin.X + client.Right - info.Work.Right);
                bottom = Math.Max(0, origin.Y + client.Bottom - info.Work.Bottom);
            }
        }
        RootGrid.Margin = new Thickness(left / scale, top / scale, right / scale, bottom / scale);
        if (WindowChrome.GetWindowChrome(this) is { } chrome) chrome.CaptionHeight = TitleBarHeight + top / scale;

        // Where Windows has put minimise, maximise and close, measured from the window's left edge.
        double room = 150;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CAPTION_BUTTON_BOUNDS, out RECT buttons, Marshal.SizeOf<RECT>()) == 0 && buttons.Right > buttons.Left)
            room = (client.Right - right - buttons.Left) / scale;
        CaptionSpacer.Width = Math.Max(0, room + 4);
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
