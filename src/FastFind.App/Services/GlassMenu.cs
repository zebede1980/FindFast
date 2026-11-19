using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace FastFind.App.Services;

/// <summary>
/// Gives every right-click menu the glass look: whatever is behind the menu shows through, blurred, under a tint.
/// A menu is a little window of its own, of a kind the ordinary backdrop request does not work on, so this asks
/// Windows for blur behind it a different way and for rounded corners. If Windows will not do it (or a high-contrast
/// theme is on) the menu falls back to a solid background, so it is always readable.
/// </summary>
public static class GlassMenu
{
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int State, Flags; public uint Colour; public int Animation; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionData { public int Attribute; public IntPtr Data; public int Size; }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static bool _registered;

    /// <summary>Call once at start-up; from then on every menu that opens is given glass.</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnOpened));
    }

    private static void OnOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        bool glass = false;
        try { glass = GlassChrome.Available && Blur(menu); }
        catch (Exception ex) { Logger.Warn($"Menu glass unavailable: {ex.Message}"); }
        // The menu's own background is a faint sheen meant to sit on the blur; without the blur it must hide what is behind it.
        if (glass) menu.ClearValue(Control.BackgroundProperty);
        else menu.SetResourceReference(Control.BackgroundProperty, "FrostMenuSolidBrush");
    }

    private static bool Blur(ContextMenu menu)
    {
        if (PresentationSource.FromVisual(menu) is not HwndSource source || source.Handle == IntPtr.Zero) return false;
        // The tint laid over the blur, in the order Windows wants it: alpha, blue, green, red.
        Color tint = menu.TryFindResource("FrostMenuTintColor") is Color colour ? colour : Color.FromArgb(0x99, 0x0E, 0x10, 0x1A);
        var policy = new AccentPolicy { State = ACCENT_ENABLE_ACRYLICBLURBEHIND, Colour = (uint)tint.A << 24 | (uint)tint.B << 16 | (uint)tint.G << 8 | tint.R };
        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, memory, false);
            var data = new CompositionData { Attribute = WCA_ACCENT_POLICY, Data = memory, Size = size };
            if (SetWindowCompositionAttribute(source.Handle, ref data) == 0) return false;
        }
        finally { Marshal.FreeHGlobal(memory); }
        // The blur fills the menu's whole rectangle; rounding the window itself trims it to the menu's rounded shape.
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(source.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        return true;
    }
}
