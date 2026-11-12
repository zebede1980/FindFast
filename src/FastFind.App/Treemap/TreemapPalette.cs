using System.Runtime.CompilerServices;
using System.Windows.Media;
using FastFind.Core.Usage;

namespace FastFind.App.Treemap;

/// <summary>
/// Colours of the disk usage map. Hue says what kind of file a block is, and each kind always has the same hue, so
/// media is red in every map. Within a kind, each extension gets its own shade (dll and sys are both
/// "system" blue, but not the same blue), which keeps a drive full of one kind from turning into a single slab.
/// The eight hues are a set chosen to stay distinguishable from one another, including with colour-blindness, on the
/// dark background the map is drawn on.
/// </summary>
public static class TreemapPalette
{
    // Indexed by FileCategory. "Other" is deliberately colourless.
    private static readonly int[] CategoryRgb =
    {
        0x7C828C, // Other
        0x3987E5, // System: blue
        0xD95926, // Archives: orange
        0x199E70, // Applications: aqua
        0xC98500, // Data: yellow
        0xD55181, // Images: magenta
        0x008300, // Code: green
        0x9085E9, // Documents: violet
        0xE66767, // Media: red
    };

    /// <summary>A folder with nothing in it that has a size.</summary>
    public const int EmptyFolder = 0x5A5F68;

    private static readonly ConditionalWeakTable<SizeTree, int[]> Cache = new();

    public static Color CategoryColour(FileCategory category)
    {
        int rgb = CategoryRgb[(int)category];
        return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    public static Color ExtensionColour(SizeTree tree, int extensionId)
    {
        int rgb = ExtensionColours(tree)[extensionId];
        return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    /// <summary>The colour (0xRRGGBB) of every extension in a tree, by extension id.</summary>
    public static int[] ExtensionColours(SizeTree tree)
        => Cache.GetValue(tree, static t =>
        {
            var colours = new int[t.ExtensionCount];
            for (int i = 0; i < colours.Length; i++) colours[i] = Vary(CategoryRgb[(int)t.CategoryOfExtension(i)], t.ExtensionName(i));
            return colours;
        });

    /// <summary>
    /// Nudges a category colour by an amount fixed by the extension's name: the same extension is the same shade in
    /// every map and every session.
    /// </summary>
    private static int Vary(int rgb, string extension)
    {
        if (extension.Length == 0) return rgb;
        uint hash = 2166136261;
        foreach (char c in extension) hash = (hash ^ c) * 16777619;
        // Two independent numbers in -1..1 from the hash: one turns the hue a little, one lightens or darkens.
        double a = (hash & 0xFFFF) / 32767.5 - 1, b = ((hash >> 16) & 0xFFFF) / 32767.5 - 1;
        ToHsl(rgb, out double h, out double s, out double l);
        h = (h + a * 9 + 360) % 360;
        l = Math.Clamp(l * (1 + b * 0.30), 0.24, 0.62);
        return FromHsl(h, s, l);
    }

    private static void ToHsl(int rgb, out double h, out double s, out double l)
    {
        double r = ((rgb >> 16) & 0xFF) / 255.0, g = ((rgb >> 8) & 0xFF) / 255.0, b = (rgb & 0xFF) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2;
        if (max == min) { h = s = 0; return; }
        double d = max - min;
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h *= 60;
    }

    private static int FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        int R = (int)Math.Round((r + m) * 255), G = (int)Math.Round((g + m) * 255), B = (int)Math.Round((b + m) * 255);
        return (Math.Clamp(R, 0, 255) << 16) | (Math.Clamp(G, 0, 255) << 8) | Math.Clamp(B, 0, 255);
    }
}
