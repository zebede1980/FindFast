using System.Collections.Concurrent;
using FastFind.Core.Usage;

namespace FastFind.App.Treemap;

/// <summary>A rectangle in whole pixels.</summary>
public readonly record struct RectI(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;
}

/// <summary>
/// One drawn disk usage map: where every file and folder landed, and the shaded picture. Produced by
/// <see cref="TreemapRenderer"/> on a background thread and then only read.
/// </summary>
public sealed class TreemapImage
{
    internal TreemapImage(int width, int height, int root, float scale)
    {
        Width = width;
        Height = height;
        Root = root;
        Scale = scale;
        Pixels = new int[width * height];
        LeafAt = new int[width * height];
        Array.Fill(LeafAt, -1);
    }

    public int Width { get; }
    public int Height { get; }
    /// <summary>Display scaling the image was drawn for (1 = 100%); fine detail is sized by it.</summary>
    public float Scale { get; }
    /// <summary>The folder the map shows (the tree's root, or a folder zoomed into).</summary>
    public int Root { get; }
    /// <summary>The picture, one opaque BGRA value per pixel, row by row.</summary>
    public int[] Pixels { get; }
    /// <summary>For each pixel, the index of the block drawn there, or -1 for background.</summary>
    internal int[] LeafAt { get; }
    /// <summary>Blocks drawn: a file, or a folder too small to be divided further.</summary>
    internal List<int> LeafNode { get; } = new();
    internal List<RectI> LeafRect { get; } = new();
    /// <summary>The cushion surface of each block: four coefficients per block.</summary>
    internal List<float> LeafSurface { get; } = new();
    /// <summary>Where every file and folder that got at least a pixel was placed.</summary>
    public Dictionary<int, RectI> Rects { get; } = new();

    public int BlockCount => LeafNode.Count;

    /// <summary>The file (or undivided folder) drawn at a pixel, or -1.</summary>
    public int NodeAt(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return -1;
        int leaf = LeafAt[y * Width + x];
        return leaf < 0 ? -1 : LeafNode[leaf];
    }

    /// <summary>
    /// Where a node was drawn. A node too small to have been given its own block reports the block of the nearest
    /// folder above it that was. False when the node is not inside the folder on show.
    /// </summary>
    public bool TryGetRect(SizeTree tree, int node, out RectI rect)
    {
        for (int n = node; n >= 0; n = tree.Parent(n))
        {
            if (Rects.TryGetValue(n, out rect)) return true;
            if (n == Root) break;
        }
        rect = default;
        return false;
    }
}

/// <summary>Which blocks stay bright when part of the map is being picked out; everything else is dimmed.</summary>
/// <param name="ExtensionId">Files of this extension, or -1.</param>
/// <param name="Category">Files of this kind, or null.</param>
/// <param name="Folders">Everything inside any of these folders (or these files themselves): an application's folders, or what a clean-up would remove.</param>
public readonly record struct TreemapHighlight(int ExtensionId, FileCategory? Category, IReadOnlySet<int>? Folders = null)
{
    public static readonly TreemapHighlight None = new(-1, null);
    public bool IsActive => ExtensionId >= 0 || Category != null || Folders != null;

    /// <summary>True when a block for <paramref name="node"/> (showing a file of the given extension) is one of those picked out.</summary>
    internal bool Includes(SizeTree tree, int node, int file, int extension, int root)
    {
        if (Folders != null)
        {
            for (int n = node; n >= 0; n = tree.Parent(n))
            {
                if (Folders.Contains(n)) return true;
                if (n == root) break;
            }
            return false;
        }
        if (file < 0) return false;
        return ExtensionId >= 0 ? extension == ExtensionId : tree.CategoryOfExtension(extension) == Category;
    }
}

/// <summary>
/// Lays a folder out as a squarified treemap (every file a rectangle whose area is its size, nested inside its
/// folder's rectangle) and shades it as "cushions": each folder and file bulges slightly, lit from the top left,
/// so the nesting reads as depth. The method is van Wijk and van de Wetering's cushion treemap, as used by
/// SequoiaView and WinDirStat; the lighting adds a soft highlight for a glossier, more solid look.
/// </summary>
public static class TreemapRenderer
{
    /// <summary>How much each level bulges, and how quickly that falls off with depth.</summary>
    private const float RidgeHeight = 0.16f, RidgeFalloff = 0.90f;
    /// <summary>Each block is a tile whose edges roll off over this many pixels (at 100% display scaling), this steeply.</summary>
    private const float BevelWidth = 4.5f, BevelSlope = 0.85f;
    private const float Ambient = 0.40f, Diffuse = 0.74f, Specular = 0.12f;
    /// <summary>Background showing through the hairline gaps between top-level folders.</summary>
    public const int Background = unchecked((int)0xFF0F1114);

    // Light from the upper left, a little in front of the surface.
    private static readonly float Lx, Ly, Lz, Hx, Hy, Hz;

    static TreemapRenderer()
    {
        float lx = -0.9f, ly = -1.1f, lz = 2.6f;
        float len = MathF.Sqrt(lx * lx + ly * ly + lz * lz);
        Lx = lx / len; Ly = ly / len; Lz = lz / len;
        // Halfway between the light and the viewer (who looks straight down the z axis): where highlights appear.
        float hx = Lx, hy = Ly, hz = Lz + 1f;
        len = MathF.Sqrt(hx * hx + hy * hy + hz * hz);
        Hx = hx / len; Hy = hy / len; Hz = hz / len;
    }

    /// <summary>Lays out and shades the folder <paramref name="root"/> of <paramref name="tree"/> at the given pixel size.</summary>
    public static TreemapImage Render(SizeTree tree, int root, int width, int height, TreemapHighlight? highlight = null, float scale = 1, CancellationToken ct = default)
    {
        width = Math.Clamp(width, 1, 16384);
        height = Math.Clamp(height, 1, 16384);
        var image = new TreemapImage(width, height, root, scale);
        Array.Fill(image.Pixels, Background);
        if (tree.Size(root) > 0)
        {
            var layout = new Layout(tree, image, ct);
            layout.Place(root, 0, 0, width, height, 0, 0, 0, 0, RidgeHeight, 0);
        }
        Shade(tree, image, highlight, ct);
        return image;
    }

    // ---- layout ----

    private sealed class Layout
    {
        private readonly SizeTree _tree;
        private readonly TreemapImage _image;
        private readonly CancellationToken _ct;

        public Layout(SizeTree tree, TreemapImage image, CancellationToken ct)
        {
            _tree = tree;
            _image = image;
            _ct = ct;
        }

        public void Place(int node, int x, int y, int w, int h, float s0, float s1, float s2, float s3, float ridge, int depth)
        {
            _image.Rects[node] = new RectI(x, y, w, h);
            bool folder = _tree.IsDirectory(node);
            // A hairline of background between the folders at the top two levels, so the big groupings read at a glance.
            if (folder && depth is 1 or 2 && w > 12 && h > 12) { w--; h--; }
            if (depth > 0)
            {
                // Add this node's bulge to the surface it inherited: a parabola across the rectangle in each direction.
                float wf = 4 * ridge / w, hf = 4 * ridge / h;
                s2 += wf * (x + x + w);
                s0 -= wf;
                s3 += hf * (y + y + h);
                s1 -= hf;
            }
            if (!folder || _tree.Size(node) <= 0 || Math.Min(w, h) < 2 || (long)w * h < 8)
            {
                int leaf = _image.LeafNode.Count;
                _image.LeafNode.Add(node);
                _image.LeafRect.Add(new RectI(x, y, w, h));
                _image.LeafSurface.Add(s0);
                _image.LeafSurface.Add(s1);
                _image.LeafSurface.Add(s2);
                _image.LeafSurface.Add(s3);
                for (int row = y; row < y + h; row++) _image.LeafAt.AsSpan(row * _image.Width + x, w).Fill(leaf);
                return;
            }
            _ct.ThrowIfCancellationRequested();
            Squarify(node, x, y, w, h, s0, s1, s2, s3, ridge * RidgeFalloff, depth + 1);
        }

        /// <summary>
        /// Squarified layout (Bruls, Huizing and van Wijk): children, largest first, are placed in strips along the
        /// shorter side of the space left, each strip closed as soon as adding another child would make its
        /// rectangles less square. Edges are snapped to whole pixels as they are placed, so neighbours always meet.
        /// </summary>
        private void Squarify(int node, int x, int y, int w, int h, float s0, float s1, float s2, float s3, float ridge, int depth)
        {
            var children = _tree.Children(node);
            int n = children.Length;
            double total = _tree.Size(node);
            double scale = (double)w * h / total; // pixels per byte
            double fx = x, fy = y, right = x + w, bottom = y + h;
            double remaining = total;
            int i = 0;
            while (i < n)
            {
                double first = _tree.Size(children[i]);
                if (first <= 0 || remaining * scale < 0.5) break; // what is left would not fill half a pixel
                double fw = right - fx, fh = bottom - fy;
                if (fw <= 0 || fh <= 0) break;
                bool alongLeft = fw >= fh; // the strip runs down the left edge when the space is wide, across the top when it is tall
                double shortSide = alongLeft ? fh : fw;
                double side2 = shortSide * shortSide;

                double rowSum = 0, worst = double.MaxValue;
                int j = i;
                while (j < n)
                {
                    double size = _tree.Size(children[j]);
                    if (size <= 0) break;
                    double area = (rowSum + size) * scale;
                    double ratio = Math.Max(side2 * first * scale / (area * area), area * area / (side2 * size * scale));
                    if (j > i && ratio > worst) break;
                    worst = ratio;
                    rowSum += size;
                    j++;
                }

                double thickness = rowSum * scale / shortSide;
                remaining -= rowSum;
                // The final strip takes whatever is left, so rounding never leaves a sliver uncovered.
                bool last = j >= n || _tree.Size(children[j]) <= 0 || remaining * scale < 0.5;
                if (alongLeft)
                {
                    double x1 = last ? right : Math.Min(right, fx + thickness);
                    int px0 = (int)Math.Round(fx), px1 = (int)Math.Round(x1);
                    double cy = fy;
                    for (int k = i; k < j; k++)
                    {
                        double cy1 = k == j - 1 ? bottom : Math.Min(bottom, cy + _tree.Size(children[k]) * scale / thickness);
                        int py0 = (int)Math.Round(cy), py1 = (int)Math.Round(cy1);
                        if (px1 > px0 && py1 > py0) Place(children[k], px0, py0, px1 - px0, py1 - py0, s0, s1, s2, s3, ridge, depth);
                        cy = cy1;
                    }
                    fx = x1;
                }
                else
                {
                    double y1 = last ? bottom : Math.Min(bottom, fy + thickness);
                    int py0 = (int)Math.Round(fy), py1 = (int)Math.Round(y1);
                    double cx = fx;
                    for (int k = i; k < j; k++)
                    {
                        double cx1 = k == j - 1 ? right : Math.Min(right, cx + _tree.Size(children[k]) * scale / thickness);
                        int px0 = (int)Math.Round(cx), px1 = (int)Math.Round(cx1);
                        if (px1 > px0 && py1 > py0) Place(children[k], px0, py0, px1 - px0, py1 - py0, s0, s1, s2, s3, ridge, depth);
                        cx = cx1;
                    }
                    fy = y1;
                }
                i = j;
            }
        }
    }

    // ---- shading ----

    /// <summary>
    /// Colours every block of an image that has been laid out. Called again, without a new layout, when only the
    /// highlight changes.
    /// </summary>
    public static void Shade(SizeTree tree, TreemapImage image, TreemapHighlight? picked, CancellationToken ct = default)
    {
        int count = image.LeafNode.Count;
        if (count == 0) return;
        var colours = TreemapPalette.ExtensionColours(tree);
        var pixels = image.Pixels;
        int stride = image.Width;
        float bevel = BevelWidth * image.Scale;
        var highlight = picked ?? TreemapHighlight.None;
        bool highlighting = highlight.IsActive;
        Parallel.ForEach(Partitioner.Create(0, count, Math.Max(64, count / (Environment.ProcessorCount * 8))), new ParallelOptions { CancellationToken = ct }, range =>
        {
            for (int leaf = range.Item1; leaf < range.Item2; leaf++)
            {
                int node = image.LeafNode[leaf];
                // A folder too small to divide takes the colour of the biggest file inside it.
                int file = tree.IsDirectory(node) ? tree.LargestFile(node) : node;
                int extension = file < 0 ? 0 : tree.ExtensionId(file);
                int colour = file < 0 ? TreemapPalette.EmptyFolder : colours[extension];
                float r = (colour >> 16) & 0xFF, g = (colour >> 8) & 0xFF, b = colour & 0xFF;
                float specular = Specular;
                if (highlighting && !highlight.Includes(tree, node, file, extension, image.Root))
                {
                    // Not part of what is being picked out: drain the colour and push it back.
                    float grey = (r * 0.3f + g * 0.59f + b * 0.11f) * 0.30f;
                    r = r * 0.08f + grey; g = g * 0.08f + grey; b = b * 0.08f + grey;
                    specular = 0.03f;
                }

                var rect = image.LeafRect[leaf];
                float s0 = image.LeafSurface[leaf * 4], s1 = image.LeafSurface[leaf * 4 + 1], s2 = image.LeafSurface[leaf * 4 + 2], s3 = image.LeafSurface[leaf * 4 + 3];
                // Each block is a tile with rolled-off edges: within this many pixels of an edge the surface falls away.
                float edgeX = MathF.Min(bevel, rect.W * 0.5f), edgeY = MathF.Min(bevel, rect.H * 0.5f);
                for (int y = rect.Y; y < rect.Bottom; y++)
                {
                    float ny = -(2 * s1 * (y + 0.5f) + s3);
                    float top = y + 0.5f - rect.Y, below = rect.Bottom - (y + 0.5f);
                    if (top < edgeY) ny -= BevelSlope * (1 - top / edgeY);
                    else if (below < edgeY) ny += BevelSlope * (1 - below / edgeY);
                    int at = y * stride + rect.X;
                    for (int x = rect.X; x < rect.Right; x++, at++)
                    {
                        // The surface's slope here gives its normal; the brightness is how squarely that faces the light.
                        float nx = -(2 * s0 * (x + 0.5f) + s2);
                        float left = x + 0.5f - rect.X, beside = rect.Right - (x + 0.5f);
                        if (left < edgeX) nx -= BevelSlope * (1 - left / edgeX);
                        else if (beside < edgeX) nx += BevelSlope * (1 - beside / edgeX);
                        float inv = 1f / MathF.Sqrt(nx * nx + ny * ny + 1f);
                        float cos = (nx * Lx + ny * Ly + Lz) * inv;
                        float light = Ambient + (cos > 0 ? Diffuse * cos : 0);
                        float nh = (nx * Hx + ny * Hy + Hz) * inv;
                        float gloss = 0;
                        if (nh > 0)
                        {
                            float p = nh * nh; p *= p; p *= p; p *= p; p *= p; // nh^32
                            gloss = p * specular * 255f;
                        }
                        int pr = (int)(r * light + gloss), pg = (int)(g * light + gloss), pb = (int)(b * light + gloss);
                        if (pr > 255) pr = 255;
                        if (pg > 255) pg = 255;
                        if (pb > 255) pb = 255;
                        pixels[at] = unchecked((int)0xFF000000) | (pr << 16) | (pg << 8) | pb;
                    }
                }
            }
        });
    }
}
