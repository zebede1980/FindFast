using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace FastFind.App.Services;

/// <summary>
/// The colours of the glass look. The main window lets the blurred desktop show through, and everything on it (the
/// panes, the boxes you type in, a row under the mouse) is a translucent tint laid over that. Behind the panes the
/// window also glows with pools of coloured light taken from the Windows accent colour, so it looks lively whatever
/// the wallpaper. Each colour has a dark and a light value and most follow the accent, so they are made here and
/// handed to the window as resources rather than written out in XAML.
/// </summary>
public static class FrostTheme
{
    /// <summary>True when the chosen theme comes out dark: "Dark", or "System" while Windows is set to dark apps.</summary>
    public static bool IsDark(string theme) => theme switch
    {
        "Dark" => true,
        "Light" => false,
        _ => !WindowsUsesLightApps(),
    };

    private static bool WindowsUsesLightApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception) { return true; }
    }

    /// <summary>Puts the colours for a theme into <paramref name="resources"/>, replacing the ones already there.</summary>
    public static void Apply(ResourceDictionary resources, string theme)
    {
        if (SystemParameters.HighContrast) { ApplyHighContrast(resources); return; }

        bool dark = IsDark(theme);
        // Windows keeps lighter and darker steps of the accent colour; the lighter one reads better on dark glass.
        Color accent = dark ? SystemColors.AccentColorLight2 : SystemColors.AccentColorDark1;
        Color ink = Color.FromRgb(0x18, 0x22, 0x38); // the dark blue-grey that light-theme lines and hovers are tints of

        void Set(string key, Color colour, byte alpha) => resources[key] = Tint(colour, alpha);

        // The light behind the glass: the accent colour and three companions found by turning its hue, so a blue accent
        // glows blue, violet, teal and ember, and any other accent gets a set that suits it as well.
        Color source = SystemColors.AccentColor;
        double light = dark ? 0.55 : 0.68;
        // The light sits on top of the window tint, so the thinner that tint, the louder the light; keep the two in step.
        // In dark the window is deliberately dark and the light a quiet glow at its corners, not a wash of colour.
        double strength = dark ? 0.5 : 0.5;
        Color violet = Turn(source, 72, 0.85, light);
        resources["FrostGlowABrush"] = Glow(Turn(source, 0, 0.95, light), 0x8C * strength, 0.08, 0.02, 0.55, 0.80);
        resources["FrostGlowBBrush"] = Glow(violet, 0x80 * strength, 0.97, 0.00, 0.50, 0.85);
        resources["FrostGlowCBrush"] = Glow(Turn(source, -38, 0.90, light - 0.07), 0x73 * strength, 0.78, 1.05, 0.50, 0.72);
        resources["FrostGlowDBrush"] = Glow(Turn(source, 168, 0.95, light), 0x66 * strength, 0.26, 1.12, 0.36, 0.58);
        resources["FrostAccentColor"] = accent;
        // Behind Windows' own minimise, maximise and close: darkens (or, in light mode, lightens) whatever shows through
        // there, fading in from the left so it reads as shadow rather than a box.
        resources["FrostCaptionShadeBrush"] = Shade(dark ? Colors.Black : Colors.White, dark ? 0x80 : 0x99);
        resources["FrostBrandBrush"] = Fade(accent, 0xFF, Turn(source, 72, 0.9, dark ? 0.7 : 0.5), 0xFF, new Point(0, 0), new Point(1, 1));

        if (dark)
        {
            // Windows' own dark glass is a mid grey, which leaves white text looking washed out; a deep blue-black laid
            // over it gives the contrast back and still lets the colours of the desktop come through.
            Set("FrostWindowTintBrush", Color.FromRgb(0x08, 0x0A, 0x16), 0xC2);
            Set("FrostWindowTintInactiveBrush", Color.FromRgb(0x0A, 0x0C, 0x16), 0xE6);
            // Panes are smoked glass: darker than the window around them, so the lists and buttons inside read clearly
            // while the coloured light stays bright in the gaps and the sidebar. A bright upper edge catches the light.
            resources["FrostPaneBrush"] = Fade(Color.FromRgb(0x08, 0x0A, 0x18), 0x70, Color.FromRgb(0x08, 0x0A, 0x18), 0x8F, new Point(0, 0), new Point(0, 1));
            // Buttons and chips are the opposite: lighter than what they sit on, so they stand out from the pane.
            resources["FrostCardBrush"] = Fade(Colors.White, 0x2E, Colors.White, 0x1A, new Point(0, 0), new Point(0, 1));
            resources["FrostCardStrokeBrush"] = Fade(Colors.White, 0x66, Colors.White, 0x1F, new Point(0, 0), new Point(0, 1), middle: 0.35);
            Set("FrostFieldBrush", Colors.Black, 0x70);
            Set("FrostFieldStrokeBrush", Colors.White, 0x38);
            Set("FrostHoverBrush", Colors.White, 0x1F);
            Set("FrostPressedBrush", Colors.White, 0x12);
            resources["FrostTabSelectedBrush"] = Fade(accent, 0x66, accent, 0x2B, new Point(0, 0), new Point(0, 1));
            Set("FrostDividerBrush", Colors.White, 0x26);
            // Sizes, dates, icons and labels: quieter than names, but brighter than the theme's own secondary text,
            // which was chosen for a flat dark background rather than coloured glass.
            Set("FrostTextSecondaryBrush", Colors.White, 0xE0);
            Set("FrostTextTertiaryBrush", Colors.White, 0xA6);
            // Right-click menus (see GlassMenu): the tint over the blur behind a menu, the faint sheen the menu adds
            // on top of that, and the solid colour it uses instead when there is no blur to sit on.
            resources["FrostMenuTintColor"] = Color.FromArgb(0xA6, 0x0C, 0x0E, 0x1C);
            resources["FrostMenuBrush"] = Fade(Colors.White, 0x1A, Colors.White, 0x08, new Point(0, 0), new Point(0, 1));
            Set("FrostMenuSolidBrush", Color.FromRgb(0x1A, 0x1C, 0x2A), 0xFF);
            resources["RowSelectedBrush"] = Fade(accent, 0x85, accent, 0x38, new Point(0, 0), new Point(1, 0));
            Set("FrostActiveStrokeBrush", accent, 0xF2);
        }
        else
        {
            Set("FrostWindowTintBrush", Color.FromRgb(0xF3, 0xF5, 0xFA), 0x99);
            Set("FrostWindowTintInactiveBrush", Color.FromRgb(0xEE, 0xF0, 0xF5), 0xD9);
            resources["FrostPaneBrush"] = Fade(Colors.White, 0xB3, Colors.White, 0x8F, new Point(0, 0), new Point(0, 1));
            Set("FrostTextSecondaryBrush", ink, 0xD1);
            Set("FrostTextTertiaryBrush", ink, 0x99);
            resources["FrostMenuTintColor"] = Color.FromArgb(0xB8, 0xF6, 0xF7, 0xFB);
            resources["FrostMenuBrush"] = Fade(Colors.White, 0x66, Colors.White, 0x33, new Point(0, 0), new Point(0, 1));
            Set("FrostMenuSolidBrush", Color.FromRgb(0xF6, 0xF7, 0xFB), 0xFF);
            resources["FrostCardBrush"] = Fade(Colors.White, 0xB8, Colors.White, 0x94, new Point(0, 0), new Point(0, 1));
            resources["FrostCardStrokeBrush"] = Fade(Colors.White, 0xFF, Colors.White, 0xCC, new Point(0, 0), new Point(0, 1), middle: 0.35);
            Set("FrostFieldBrush", Colors.White, 0xB3);
            Set("FrostFieldStrokeBrush", ink, 0x26);
            Set("FrostHoverBrush", ink, 0x0F);
            Set("FrostPressedBrush", ink, 0x0A);
            resources["FrostTabSelectedBrush"] = Fade(accent, 0x38, accent, 0x1F, new Point(0, 0), new Point(0, 1));
            Set("FrostDividerBrush", ink, 0x1A);
            resources["RowSelectedBrush"] = Fade(accent, 0x4D, accent, 0x24, new Point(0, 0), new Point(1, 0));
            Set("FrostActiveStrokeBrush", accent, 0xD9);
        }
    }

    /// <summary>
    /// With a high-contrast theme Windows draws no glass, and the user has asked for their own colours: panes become
    /// plain outlines in the text colour, highlights use the system highlight colour, and there is no coloured light.
    /// </summary>
    private static void ApplyHighContrast(ResourceDictionary resources)
    {
        Color text = SystemColors.WindowTextColor, highlight = SystemColors.HighlightColor;
        void Set(string key, Color colour, byte alpha) => resources[key] = Tint(colour, alpha);
        Set("FrostWindowTintBrush", SystemColors.WindowColor, 0xFF);
        Set("FrostWindowTintInactiveBrush", SystemColors.WindowColor, 0xFF);
        Set("FrostCardBrush", SystemColors.WindowColor, 0x00);
        Set("FrostPaneBrush", SystemColors.WindowColor, 0x00);
        Set("FrostTextSecondaryBrush", text, 0xFF);
        Set("FrostTextTertiaryBrush", text, 0xFF);
        resources["FrostMenuTintColor"] = SystemColors.WindowColor;
        Set("FrostMenuBrush", SystemColors.WindowColor, 0xFF);
        Set("FrostMenuSolidBrush", SystemColors.WindowColor, 0xFF);
        Set("FrostCardStrokeBrush", text, 0xFF);
        Set("FrostFieldBrush", SystemColors.WindowColor, 0xFF);
        Set("FrostFieldStrokeBrush", text, 0xFF);
        Set("FrostHoverBrush", highlight, 0x40);
        Set("FrostPressedBrush", highlight, 0x59);
        Set("FrostTabSelectedBrush", highlight, 0x59);
        Set("FrostDividerBrush", text, 0x66);
        Set("RowSelectedBrush", highlight, 0x66);
        Set("FrostActiveStrokeBrush", highlight, 0xFF);
        Set("FrostBrandBrush", highlight, 0xFF);
        Set("FrostCaptionShadeBrush", highlight, 0x00);
        resources["FrostAccentColor"] = highlight;
        foreach (string glow in new[] { "FrostGlowABrush", "FrostGlowBBrush", "FrostGlowCBrush", "FrostGlowDBrush" }) Set(glow, highlight, 0x00);
    }

    /// <summary>Frozen, because a brush used in a style trigger must be.</summary>
    private static SolidColorBrush Tint(Color colour, byte alpha)
    {
        var brush = new SolidColorBrush(WithAlpha(colour, alpha));
        brush.Freeze();
        return brush;
    }

    private static Color WithAlpha(Color colour, double alpha) => Color.FromArgb((byte)Math.Clamp(Math.Round(alpha), 0, 255), colour.R, colour.G, colour.B);

    /// <summary>A brush that runs from one tint to another; with <paramref name="middle"/>, most of the change happens before that point.</summary>
    private static LinearGradientBrush Fade(Color from, byte fromAlpha, Color to, byte toAlpha, Point start, Point end, double middle = -1)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        brush.GradientStops.Add(new GradientStop(WithAlpha(from, fromAlpha), 0));
        if (middle > 0) brush.GradientStops.Add(new GradientStop(WithAlpha(to, toAlpha + (fromAlpha - toAlpha) * 0.15), middle));
        brush.GradientStops.Add(new GradientStop(WithAlpha(to, toAlpha), 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>Clear at the left, reaching full strength a quarter of the way across and staying there.</summary>
    private static LinearGradientBrush Shade(Color colour, double alpha)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        brush.GradientStops.Add(new GradientStop(WithAlpha(colour, 0), 0));
        brush.GradientStops.Add(new GradientStop(WithAlpha(colour, alpha), 0.25));
        brush.GradientStops.Add(new GradientStop(WithAlpha(colour, alpha), 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// A pool of coloured light: brightest at its centre and gone at its edge. The centre and size are fractions of
    /// whatever it is painted on, so the light keeps its place as the window is resized.
    /// </summary>
    private static RadialGradientBrush Glow(Color colour, double alpha, double centreX, double centreY, double radiusX, double radiusY)
    {
        var centre = new Point(centreX, centreY);
        var brush = new RadialGradientBrush { Center = centre, GradientOrigin = centre, RadiusX = radiusX, RadiusY = radiusY };
        brush.GradientStops.Add(new GradientStop(WithAlpha(colour, alpha), 0));
        brush.GradientStops.Add(new GradientStop(WithAlpha(colour, alpha * 0.45), 0.45));
        brush.GradientStops.Add(new GradientStop(WithAlpha(colour, 0), 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>The colour whose hue is <paramref name="degrees"/> round the colour wheel from another's, at a given saturation and lightness.</summary>
    private static Color Turn(Color colour, double degrees, double saturation, double lightness)
    {
        double r = colour.R / 255.0, g = colour.G / 255.0, b = colour.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), spread = max - min;
        double hue = 0;
        if (spread > 0)
        {
            if (max == r) hue = (g - b) / spread % 6;
            else if (max == g) hue = (b - r) / spread + 2;
            else hue = (r - g) / spread + 4;
            hue *= 60;
        }
        hue = ((hue + degrees) % 360 + 360) % 360;

        double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        double m = lightness - chroma / 2;
        (double r1, double g1, double b1) = hue < 60 ? (chroma, x, 0.0) : hue < 120 ? (x, chroma, 0.0) : hue < 180 ? (0.0, chroma, x)
            : hue < 240 ? (0.0, x, chroma) : hue < 300 ? (x, 0.0, chroma) : (chroma, 0.0, x);
        return Color.FromRgb(Channel(r1 + m), Channel(g1 + m), Channel(b1 + m));
    }

    private static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
}
