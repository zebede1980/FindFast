using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace FastFind.App.Services;

/// <summary>
/// The glass look for one window: the blurred desktop behind it, the tint and coloured light laid over that, and a
/// title bar the window draws itself so its own controls can sit there. Windows still draws minimise, maximise and
/// close (so snapping and the maximise button's layout menu keep working); this only keeps their corner clear.
/// The main window, the disk usage window and the duplicates window each attach one.
/// </summary>
public sealed class GlassChrome
{
    private const int DWMWA_CAPTION_BUTTON_BOUNDS = 5, DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_TRANSIENTWINDOW = 3; // acrylic: the desktop behind the window, blurred

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

    /// <summary>Every window that has the glass look at the moment, so a change of theme can reach them all.</summary>
    private static readonly List<GlassChrome> Attached = new();

    private readonly Window _window;
    private readonly Panel _root;
    private readonly UIElement? _light;
    private readonly FrameworkElement? _captionSpacer;
    private readonly double _captionHeight;

    /// <summary>
    /// A soft shade behind minimise, maximise and close. Windows draws them in thin light glyphs, which vanish over a
    /// bright wallpaper or a pool of coloured light showing through the glass; the shade keeps them readable.
    /// </summary>
    private readonly Border _buttonShade = new()
    {
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top,
        IsHitTestVisible = false,
    };

    /// <summary>
    /// Glass needs Windows 11 22H2 or later, and is not used with a high-contrast theme (Windows draws no glass then,
    /// and the user has asked for plain colours). Without it a window keeps the ordinary Windows title bar.
    /// </summary>
    public static bool Available => Environment.OSVersion.Version.Build >= 22621 && !SystemParameters.HighContrast;

    /// <summary>True while this window is drawing its own title bar on glass.</summary>
    public bool IsGlass { get; private set; }

    /// <summary>Raised when glass has been switched on or off, or the room for Windows' own buttons has been measured again.</summary>
    public event Action? Changed;

    /// <summary>Gives a window the glass look.</summary>
    /// <param name="root">The panel that fills the window; it carries the tint, and is pulled in from the edges when maximised.</param>
    /// <param name="light">The coloured light behind the content, dimmed while the window is in the background.</param>
    /// <param name="captionSpacer">An element at the right of the title bar, kept as wide as Windows' own three buttons.</param>
    /// <param name="captionHeight">How much of the top of the window can be dragged like a title bar.</param>
    public static GlassChrome Attach(Window window, Panel root, UIElement? light, FrameworkElement? captionSpacer, double captionHeight)
        => new(window, root, light, captionSpacer, captionHeight);

    private GlassChrome(Window window, Panel root, UIElement? light, FrameworkElement? captionSpacer, double captionHeight)
    {
        _window = window;
        _root = root;
        _light = light;
        _captionSpacer = captionSpacer;
        _captionHeight = captionHeight;
        Attached.Add(this);

        _buttonShade.SetResourceReference(Border.BackgroundProperty, "FrostCaptionShadeBrush");
        Grid.SetRowSpan(_buttonShade, int.MaxValue);
        Grid.SetColumnSpan(_buttonShade, int.MaxValue);
        root.Children.Add(_buttonShade);

        Apply();
        window.SourceInitialized += (_, _) => Apply();
        window.StateChanged += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateEdges);
        window.SizeChanged += (_, _) => UpdateEdges();
        window.DpiChanged += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateEdges);
        // WPF puts its own backdrop back at moments of its choosing (a theme change, for one); asking again is cheap.
        window.Activated += (_, _) => { ApplyBackdrop(); ApplyTint(); };
        window.Deactivated += (_, _) => ApplyTint();
        window.Closed += (_, _) => Attached.Remove(this);
        ApplyTint();
    }

    /// <summary>After a change of theme: WPF has put its own backdrop back on every window, so ask for glass again.</summary>
    public static void ApplyAll()
    {
        foreach (var chrome in Attached.ToList()) chrome.Apply();
    }

    /// <summary>Switches between our title bar on glass and the ordinary Windows one, whichever this PC can show right now.</summary>
    public void Apply()
    {
        bool glass = Available;
        bool hasChrome = WindowChrome.GetWindowChrome(_window) != null;
        if (glass != IsGlass || hasChrome != glass)
        {
            IsGlass = glass;
            WindowChrome.SetWindowChrome(_window, glass
                ? new WindowChrome
                {
                    CaptionHeight = _captionHeight,
                    GlassFrameThickness = new Thickness(-1), // the backdrop runs under the whole window, title bar included
                    ResizeBorderThickness = new Thickness(6),
                    CornerRadius = new CornerRadius(0),
                    UseAeroCaptionButtons = true,
                }
                : null);
        }
        ApplyBackdrop();
        UpdateEdges();
    }

    private void ApplyBackdrop()
    {
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero || !IsGlass) return;
        int acrylic = DWMSBT_TRANSIENTWINDOW;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref acrylic, sizeof(int));
    }

    /// <summary>
    /// Windows only blurs the desktop behind the window you are working in; behind any other it paints a flat grey.
    /// A heavier tint while the window is in the background keeps it looking like itself rather than washed out, and
    /// the coloured light dims with it, so a window in the background does not shout for attention.
    /// </summary>
    private void ApplyTint()
    {
        _root.SetResourceReference(Panel.BackgroundProperty, _window.IsActive ? "FrostWindowTintBrush" : "FrostWindowTintInactiveBrush");
        if (_light != null) _light.Opacity = _window.IsActive ? 1 : 0.5;
    }

    /// <summary>
    /// Keeps the title bar out of the way of the buttons Windows draws, and keeps everything on screen when maximised
    /// (a maximised window without the ordinary frame hangs over the edges of the monitor by the width of that frame).
    /// </summary>
    private void UpdateEdges()
    {
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero) return;
        double scale = VisualTreeHelper.GetDpi(_window).DpiScaleX;
        if (!IsGlass || !GetClientRect(hwnd, out RECT client))
        {
            _root.Margin = default;
            if (_captionSpacer != null) _captionSpacer.Width = 0;
            _buttonShade.Visibility = Visibility.Collapsed;
            Changed?.Invoke();
            return;
        }

        int left = 0, top = 0, right = 0, bottom = 0;
        if (_window.WindowState == WindowState.Maximized)
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
        _root.Margin = new Thickness(left / scale, top / scale, right / scale, bottom / scale);
        if (WindowChrome.GetWindowChrome(_window) is { } chrome) chrome.CaptionHeight = _captionHeight + top / scale;

        // Where Windows has put minimise, maximise and close, measured from the window's left edge.
        double room = 150, buttonHeight = 32;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CAPTION_BUTTON_BOUNDS, out RECT buttons, Marshal.SizeOf<RECT>()) == 0 && buttons.Right > buttons.Left)
        {
            room = (client.Right - right - buttons.Left) / scale;
            buttonHeight = (buttons.Bottom - top) / scale;
        }
        if (_captionSpacer != null) _captionSpacer.Width = Math.Max(0, room + 4);
        // A little wider than the buttons, because the shade fades in from its left edge.
        _buttonShade.Width = Math.Max(0, room * 1.3);
        _buttonShade.Height = Math.Max(0, buttonHeight);
        _buttonShade.Visibility = Visibility.Visible;
        Changed?.Invoke();
    }
}
