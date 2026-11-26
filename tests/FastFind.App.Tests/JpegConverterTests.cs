using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FastFind.App.Services;
using Xunit;

namespace FastFind.App.Tests;

public sealed class JpegConverterTests : IDisposable
{
    private static readonly Guid HeifContainer = new("E1E62521-6787-405B-A339-500715B5763F");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "FastFindJpg_" + Guid.NewGuid().ToString("N"));

    public JpegConverterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static BitmapSource Solid(int width, int height, uint bgra, PixelFormat format)
    {
        var pixels = new int[width * height];
        Array.Fill(pixels, unchecked((int)bgra));
        return BitmapSource.Create(width, height, 96, 96, format, null, pixels, width * 4);
    }

    private static BitmapFrame Load(string path)
    {
        using var stream = File.OpenRead(path);
        return BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
    }

    private static int PixelAt(BitmapSource image, int x, int y)
    {
        var pixel = new int[1];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[0];
    }

    [Theory]
    [InlineData("IMG_0511.HEIC", true)]
    [InlineData("photo.heif", true)]
    [InlineData("sticker.webp", true)]
    [InlineData("photo.avif", true)]
    [InlineData("photo.jpg", false)]
    [InlineData("photo.png", false)]
    [InlineData("heic", false)]
    public void Offered_only_for_formats_other_programs_refuse(string name, bool expected)
        => Assert.Equal(expected, JpegConverter.CanConvert(Path.Combine(@"C:\Pictures", name)));

    [Fact]
    public void A_heic_photo_keeps_its_details_and_is_not_turned_twice()
    {
        string heic = Path.Combine(_root, "IMG_0001.HEIC");
        BitmapEncoder encoder;
        try { encoder = BitmapEncoder.Create(HeifContainer); }
        catch (NotSupportedException) { return; } // this PC has no HEIF extension, so there is nothing to test against

        var details = new BitmapMetadata("heif");
        details.SetQuery("/ifd/{ushort=271}", "Apple");
        details.SetQuery("/ifd/{ushort=274}", (ushort)6); // "turn this a quarter" mark, which must not survive
        details.SetQuery("/ifd/exif", new BitmapMetadata("exif"));
        details.SetQuery("/ifd/exif/{ushort=36867}", "2026:03:01 17:29:07");
        encoder.Frames.Add(BitmapFrame.Create(Solid(64, 32, 0xFFD02020, PixelFormats.Bgr32), null, details, null));
        using (var stream = File.Create(heic)) encoder.Save(stream);

        string jpg = JpegConverter.ToJpeg(heic);

        Assert.Equal(Path.Combine(_root, "IMG_0001.jpg"), jpg);
        Assert.True(File.Exists(heic));
        var frame = Load(jpg);
        Assert.Equal("jpg", frame.Metadata is BitmapMetadata m ? m.Format : null);
        var metadata = (BitmapMetadata)frame.Metadata;
        Assert.Equal("Apple", metadata.GetQuery("/app1/ifd/{ushort=271}"));
        Assert.Equal("2026:03:01 17:29:07", metadata.GetQuery("/app1/ifd/exif/{ushort=36867}"));
        Assert.Equal((ushort)1, metadata.GetQuery("/app1/ifd/{ushort=274}"));
        Assert.Equal((uint)frame.PixelWidth, Convert.ToUInt32(metadata.GetQuery("/app1/ifd/exif/{ushort=40962}")));
        Assert.Equal(64, frame.PixelWidth);
    }

    [Fact]
    public void Transparency_becomes_white_and_an_existing_jpg_is_not_overwritten()
    {
        // Windows reads a picture by its content rather than its name, so a PNG stands in for a transparent WebP.
        string webp = Path.Combine(_root, "sticker.webp");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Solid(40, 20, 0x00000000, PixelFormats.Bgra32)));
        using (var stream = File.Create(webp)) encoder.Save(stream);
        File.WriteAllText(Path.Combine(_root, "sticker.jpg"), "already here");

        string jpg = JpegConverter.ToJpeg(webp);

        Assert.Equal(Path.Combine(_root, "sticker (2).jpg"), jpg);
        Assert.Equal("already here", File.ReadAllText(Path.Combine(_root, "sticker.jpg")));
        int pixel = PixelAt(Load(jpg), 20, 10);
        Assert.True((pixel & 0xFF) > 245 && ((pixel >> 8) & 0xFF) > 245 && ((pixel >> 16) & 0xFF) > 245, $"expected white, got {pixel:X8}");
    }

    [Fact]
    public void A_picture_windows_cannot_read_leaves_nothing_behind()
    {
        string broken = Path.Combine(_root, "broken.heic");
        File.WriteAllBytes(broken, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        var error = Assert.ThrowsAny<Exception>(() => JpegConverter.ToJpeg(broken));

        Assert.Contains("can't read .heic pictures", error.Message);
        Assert.Equal(new[] { broken }, Directory.GetFiles(_root));
    }
}
