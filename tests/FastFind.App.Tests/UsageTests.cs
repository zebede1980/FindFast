using FastFind.App.Treemap;
using FastFind.App.ViewModels;
using FastFind.Core.Usage;
using Xunit;

namespace FastFind.App.Tests;

public class TreemapTests
{
    private static SizeTree Flat(params long[] sizes)
    {
        var b = new SizeTree.Builder(@"C:\");
        for (int i = 0; i < sizes.Length; i++) b.AddFile(SizeTree.Root, $"file{i}.bin", sizes[i]);
        return b.Build();
    }

    /// <summary>C:\ with two folders (one nested two deep) and a loose file: 10,000 bytes in all.</summary>
    private static SizeTree Nested()
    {
        var b = new SizeTree.Builder(@"C:\");
        int media = b.AddFolder(SizeTree.Root, "media");
        b.AddFile(media, "film.mp4", 4000);
        int albums = b.AddFolder(media, "albums");
        b.AddFile(albums, "one.jpg", 1000);
        b.AddFile(albums, "two.jpg", 500);
        int code = b.AddFolder(SizeTree.Root, "code");
        for (int i = 0; i < 50; i++) b.AddFile(code, $"source{i}.cs", 50);
        b.AddFile(SizeTree.Root, "backup.zip", 2000);
        b.AddFolder(SizeTree.Root, "empty");
        b.AddFile(SizeTree.Root, "zero.txt", 0);
        return b.Build();
    }

    private static int Count(TreemapImage image, Func<int, bool> node)
    {
        int n = 0;
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                if (node(image.NodeAt(x, y))) n++;
        return n;
    }

    [Fact]
    public void EveryPixelBelongsToAFileAndAreasFollowSizes()
    {
        var tree = Flat(600, 300, 100);
        var image = TreemapRenderer.Render(tree, SizeTree.Root, 200, 100);
        Assert.Equal(3, image.BlockCount);
        Assert.Equal(0, Count(image, n => n < 0));
        foreach (int child in tree.Children(SizeTree.Root))
        {
            double share = Count(image, n => n == child) / 20000.0;
            Assert.InRange(share, tree.Size(child) / 1000.0 - 0.03, tree.Size(child) / 1000.0 + 0.03);
        }
        // Nothing is left the background colour.
        Assert.DoesNotContain(TreemapRenderer.Background, image.Pixels);
    }

    [Fact]
    public void BlocksNestInsideTheirFoldersAndNothingOverlaps()
    {
        var tree = Nested();
        var image = TreemapRenderer.Render(tree, SizeTree.Root, 400, 300);
        // Every pixel that is drawn belongs to a file (or an undivided folder) of the tree; hairlines between folders may stay empty.
        Assert.True(Count(image, n => n >= 0) >= 400 * 300 * 0.97);

        foreach (var (node, rect) in image.Rects)
        {
            Assert.True(rect.W > 0 && rect.H > 0);
            Assert.True(rect.X >= 0 && rect.Y >= 0 && rect.Right <= 400 && rect.Bottom <= 300);
            if (node == SizeTree.Root) continue;
            var parent = image.Rects[tree.Parent(node)];
            Assert.True(rect.X >= parent.X && rect.Y >= parent.Y && rect.Right <= parent.Right && rect.Bottom <= parent.Bottom, $"{tree.GetPath(node)} is not inside its folder");
        }

        // A pixel inside a file's rectangle reports that file; things with no size get no block at all.
        int film = tree.Find(@"C:\media\film.mp4");
        var at = image.Rects[film];
        Assert.Equal(film, image.NodeAt(at.X + at.W / 2, at.Y + at.H / 2));
        Assert.False(image.Rects.ContainsKey(tree.Find(@"C:\empty")));
        Assert.False(image.Rects.ContainsKey(tree.Find(@"C:\zero.txt")));

        // The media folder is 55% of the total and gets about that much of the picture.
        int media = tree.Find(@"C:\media");
        double share = Count(image, n => n >= 0 && tree.IsSameOrInside(n, media)) / (400.0 * 300);
        Assert.InRange(share, 0.52, 0.58);
        Assert.Equal(-1, image.NodeAt(-1, 0));
        Assert.Equal(-1, image.NodeAt(400, 0));
    }

    [Fact]
    public void ZoomingShowsOnlyTheFolderZoomedInto()
    {
        var tree = Nested();
        int media = tree.Find(@"C:\media");
        var image = TreemapRenderer.Render(tree, media, 300, 200);
        Assert.Equal(media, image.Root);
        Assert.Equal(0, Count(image, n => n >= 0 && !tree.IsSameOrInside(n, media)));
        Assert.True(Count(image, n => n == tree.Find(@"C:\media\film.mp4")) > 300 * 200 * 0.6);

        // Something outside the folder on show has no place on the map; something inside always resolves, if only to its folder.
        Assert.False(image.TryGetRect(tree, tree.Find(@"C:\backup.zip"), out _));
        Assert.True(image.TryGetRect(tree, tree.Find(@"C:\media\albums\two.jpg"), out var two));
        Assert.True(two.W > 0);
    }

    [Fact]
    public void AThousandTinyFilesStillFillTheirFolder()
    {
        var b = new SizeTree.Builder(@"C:\");
        int folder = b.AddFolder(SizeTree.Root, "many");
        for (int i = 0; i < 5000; i++) b.AddFile(folder, $"f{i}.dat", 1 + i % 7);
        b.AddFile(SizeTree.Root, "big.iso", 20000);
        var tree = b.Build();
        var image = TreemapRenderer.Render(tree, SizeTree.Root, 120, 80);
        // Far more files than pixels: the layout must stop cleanly and leave no holes beyond the folder's hairline.
        Assert.True(Count(image, n => n < 0) <= 120 + 80);
        Assert.True(image.BlockCount <= 120 * 80);
    }

    [Fact]
    public void AnEmptyFolderDrawsNothing()
    {
        var tree = new SizeTree.Builder(@"C:\nothing").Build();
        var image = TreemapRenderer.Render(tree, SizeTree.Root, 50, 40);
        Assert.Equal(0, image.BlockCount);
        Assert.Equal(-1, image.NodeAt(10, 10));
        Assert.All(image.Pixels, p => Assert.Equal(TreemapRenderer.Background, p));
    }

    private static int Brightness(int pixel) => ((pixel >> 16) & 0xFF) + ((pixel >> 8) & 0xFF) + (pixel & 0xFF);

    [Fact]
    public void PickingOutAKindDimsEverythingElse()
    {
        var tree = Nested();
        var plain = TreemapRenderer.Render(tree, SizeTree.Root, 400, 300);
        var picked = TreemapRenderer.Render(tree, SizeTree.Root, 400, 300, new TreemapHighlight(-1, FileCategory.Archives));
        var zip = plain.Rects[tree.Find(@"C:\backup.zip")];
        var film = plain.Rects[tree.Find(@"C:\media\film.mp4")];
        int zipAt = (zip.Y + zip.H / 2) * 400 + zip.X + zip.W / 2, filmAt = (film.Y + film.H / 2) * 400 + film.X + film.W / 2;
        Assert.Equal(plain.Pixels[zipAt], picked.Pixels[zipAt]);
        Assert.True(Brightness(picked.Pixels[filmAt]) < Brightness(plain.Pixels[filmAt]) / 2);

        // By extension: only the jpgs stay lit.
        int jpg = tree.ExtensionId(tree.Find(@"C:\media\albums\one.jpg"));
        var byType = TreemapRenderer.Render(tree, SizeTree.Root, 400, 300, new TreemapHighlight(jpg, null));
        var one = plain.Rects[tree.Find(@"C:\media\albums\one.jpg")];
        int oneAt = (one.Y + one.H / 2) * 400 + one.X + one.W / 2;
        Assert.Equal(plain.Pixels[oneAt], byType.Pixels[oneAt]);
        Assert.True(Brightness(byType.Pixels[zipAt]) < Brightness(plain.Pixels[zipAt]) / 2);
    }

    [Fact]
    public void AnExtensionKeepsItsColourFromOneMapToTheNext()
    {
        var first = Flat(10);
        var b = new SizeTree.Builder(@"D:\other");
        b.AddFile(SizeTree.Root, "a.txt", 5);
        b.AddFile(SizeTree.Root, "b.BIN", 5);
        var second = b.Build();
        var colour = TreemapPalette.ExtensionColour(first, first.ExtensionId(first.Find(@"C:\file0.bin")));
        Assert.Equal(colour, TreemapPalette.ExtensionColour(second, second.ExtensionId(second.Find(@"D:\other\b.BIN"))));
        Assert.NotEqual(colour, TreemapPalette.ExtensionColour(second, second.ExtensionId(second.Find(@"D:\other\a.txt"))));
    }
}

public class UsageRowListTests
{
    private static SizeTree Sample()
    {
        var b = new SizeTree.Builder(@"C:\");
        int users = b.AddFolder(SizeTree.Root, "Users");
        int joe = b.AddFolder(users, "joe");
        int docs = b.AddFolder(joe, "Documents");
        b.AddFile(docs, "a.txt", 100);
        b.AddFile(docs, "b.txt", 250);
        b.AddFile(joe, "photo.jpg", 1000);
        int windows = b.AddFolder(SizeTree.Root, "Windows");
        b.AddFile(windows, "notepad.exe", 5000);
        return b.Build();
    }

    private static string[] Names(UsageRowList rows) => rows.Select(r => new string(' ', r.Depth) + r.Name).ToArray();

    [Fact]
    public void StartsWithTheTopLevelOpenAndFoldersOpenAndClose()
    {
        var tree = Sample();
        var rows = new UsageRowList(tree);
        int resets = 0;
        rows.CollectionChanged += (_, _) => resets++;
        Assert.Equal(new[] { @"C:\", " Windows", " Users" }, Names(rows));

        int users = tree.Find(@"C:\Users"), joe = tree.Find(@"C:\Users\joe");
        rows.Expand(users);
        rows.Expand(joe);
        Assert.Equal(new[] { @"C:\", " Windows", " Users", "  joe", "   photo.jpg", "   Documents" }, Names(rows));
        Assert.True(rows[2].IsExpanded);
        Assert.True(rows[3].CanExpand);
        Assert.False(rows[4].CanExpand);
        Assert.Equal(2, resets);

        // Closing a folder closes what was open inside it, so it reopens one level deep.
        rows.Collapse(users);
        Assert.Equal(new[] { @"C:\", " Windows", " Users" }, Names(rows));
        Assert.False(rows.IsExpanded(joe));
        rows.Toggle(users);
        Assert.Equal(new[] { @"C:\", " Windows", " Users", "  joe" }, Names(rows));
        Assert.False(rows[3].IsExpanded);
    }

    [Fact]
    public void RevealOpensEverythingAboveAnItem()
    {
        var tree = Sample();
        var rows = new UsageRowList(tree);
        int a = tree.Find(@"C:\Users\joe\Documents\a.txt");
        Assert.Equal(-1, rows.IndexOf(a));
        int at = rows.Reveal(a);
        Assert.Equal(7, at);
        Assert.Equal("a.txt", rows[at].Name);
        Assert.Equal(4, rows[at].Depth);
        Assert.Equal(new[] { @"C:\", " Windows", " Users", "  joe", "   photo.jpg", "   Documents", "    b.txt", "    a.txt" }, Names(rows));
        // Already on show: nothing changes.
        int resets = 0;
        rows.CollectionChanged += (_, _) => resets++;
        Assert.Equal(at, rows.Reveal(a));
        Assert.Equal(0, resets);
    }

    [Fact]
    public void RowsDescribeSizeAndShare()
    {
        var tree = Sample();
        var rows = new UsageRowList(tree);
        rows.Reveal(tree.Find(@"C:\Users\joe\photo.jpg"));
        var windows = rows[1];
        Assert.Equal("4.9 KB", windows.SizeText);
        Assert.Equal("1", windows.FilesText);
        Assert.Equal(5000.0 / 6350, windows.Fraction, 6);
        var photo = rows.First(r => r.Name == "photo.jpg");
        Assert.Equal(1000.0 / 1350, photo.Fraction, 6);
        Assert.Equal("", photo.FilesText);
        Assert.NotNull(photo.Swatch);
        Assert.Null(windows.Swatch);
        Assert.Equal(1, rows[0].Fraction);
    }

    [Fact]
    public void RebuildAfterADeleteDropsTheRowsAndKeepsWhatWasOpen()
    {
        var tree = Sample();
        var rows = new UsageRowList(tree);
        rows.Reveal(tree.Find(@"C:\Users\joe\Documents\a.txt"));
        tree.Remove(tree.Find(@"C:\Users\joe\Documents"));
        rows.Rebuild();
        Assert.Equal(new[] { @"C:\", " Windows", " Users", "  joe", "   photo.jpg" }, Names(rows));
        Assert.Equal("1000 B", rows[2].SizeText);
    }
}
