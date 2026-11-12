using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FastFind.App.Services;
using Xunit;

namespace FastFind.App.Tests;

public class ThumbnailTests
{
    /// <summary>A picture that is red across the top half and blue across the bottom, so "which way up" can be read from two pixels.</summary>
    private static void WriteRedOverBlue(string path, bool jpeg)
    {
        const int width = 600, height = 400;
        var pixels = new int[width * height];
        for (int y = 0; y < height; y++)
            Array.Fill(pixels, y < height / 2 ? unchecked((int)0xFFD02020) : unchecked((int)0xFF2030D0), y * width, width);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 90 } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static (int Red, int Blue) At(BitmapSource image, double down)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixel = new int[1];
        converted.CopyPixels(new System.Windows.Int32Rect(image.PixelWidth / 2, (int)(image.PixelHeight * down), 1, 1), pixel, 4, 0);
        return ((pixel[0] >> 16) & 0xFF, pixel[0] & 0xFF);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PictureThumbnailsAreTheRightWayUp(bool jpeg)
    {
        string path = Path.Combine(Path.GetTempPath(), $"FastFind.ThumbnailTests.{Guid.NewGuid():N}.{(jpeg ? "jpg" : "png")}");
        try
        {
            WriteRedOverBlue(path, jpeg);
            var image = await ShellThumbnails.GetAsync(path, 256) as BitmapSource;
            Assert.NotNull(image);
            // No thumbnail handler on this machine (a bare server, say): the icon came back instead and says nothing about orientation.
            if (image!.PixelWidth == image.PixelHeight) return;
            Assert.Equal(256, image.PixelWidth);
            var top = At(image, 0.25);
            var bottom = At(image, 0.75);
            Assert.True(top.Red > top.Blue + 60, $"expected red at the top, found r={top.Red} b={top.Blue}");
            Assert.True(bottom.Blue > bottom.Red + 60, $"expected blue at the bottom, found r={bottom.Red} b={bottom.Blue}");
        }
        finally
        {
            try { File.Delete(path); } catch (Exception) { }
        }
    }

    [Fact]
    public async Task AFileWithNoThumbnailGetsItsIconAndAMissingFileGetsNothing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"FastFind.ThumbnailTests.{Guid.NewGuid():N}.fastfindtest");
        try
        {
            File.WriteAllText(path, "not a picture");
            bool stoodIn = false;
            var image = await ShellThumbnails.GetAsync(path, 128, _ => stoodIn = true) as BitmapSource;
            Assert.NotNull(image);
            Assert.Equal(image!.PixelWidth, image.PixelHeight);
            // The stand-in is only for a thumbnail that is on its way; this file will never have one.
            Assert.False(stoodIn);
            Assert.True(image.IsFrozen);
        }
        finally
        {
            try { File.Delete(path); } catch (Exception) { }
        }
        Assert.Null(await ShellThumbnails.GetAsync(Path.Combine(Path.GetTempPath(), "FastFind.ThumbnailTests.missing." + Guid.NewGuid().ToString("N")), 128));
    }
}
