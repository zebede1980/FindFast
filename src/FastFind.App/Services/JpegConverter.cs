using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FastFind.App.Services;

/// <summary>
/// Makes ordinary JPG copies of pictures that other programs often refuse, above all the HEIC photos iPhones take.
/// It uses the picture decoders built into Windows, so nothing extra ships with FastFind; Windows itself needs the
/// matching free extension from the Microsoft Store (HEIF Image Extensions for HEIC).
/// </summary>
public static class JpegConverter
{
    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".heic"] = "HEIF Image Extensions",
        [".heif"] = "HEIF Image Extensions",
        [".avif"] = "AV1 Video Extension",
        [".webp"] = "WebP Image Extensions",
        [".jxl"] = "JPEG XL Image Extension",
    };

    private const int Quality = 92;

    // EXIF tags, by number.
    private const ushort Orientation = 274, ExifBlock = 34665, GpsBlock = 34853, MakerNote = 37500, PixelWidth = 40962, PixelHeight = 40963;

    private static readonly HashSet<PixelFormat> WithAlpha = new()
    {
        PixelFormats.Bgra32, PixelFormats.Pbgra32, PixelFormats.Rgba64, PixelFormats.Prgba64, PixelFormats.Rgba128Float, PixelFormats.Prgba128Float,
    };

    /// <summary>True for the kinds of picture "Convert to JPG" is offered for.</summary>
    public static bool CanConvert(string path) => Extensions.ContainsKey(Path.GetExtension(path));

    /// <summary>
    /// Saves a JPG copy beside the picture, named after it ("IMG_0511.jpg", or "IMG_0511 (2).jpg" if that is taken),
    /// and returns its path. The original is left alone. The date taken, camera settings and location come across.
    /// Windows hands the picture over already turned the right way up, so the copy is marked as needing no turning;
    /// keeping the original's rotation mark would make viewers turn it a second time. Transparent areas become white.
    /// Runs on the calling thread; call from a background task.
    /// </summary>
    public static string ToJpeg(string path)
    {
        string folder = Path.GetDirectoryName(path) ?? throw new IOException("The picture has no containing folder.");
        string jpgPath = Path.Combine(folder, FileOperations.UniqueName(folder, Path.GetFileNameWithoutExtension(path), ".jpg"));
        string temp = jpgPath + ".partial";
        try
        {
            using (var input = File.OpenRead(path))
            {
                BitmapFrame frame;
                try { frame = BitmapDecoder.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0]; }
                catch (NotSupportedException e) { throw new IOException(NoDecoderMessage(path), e); }

                var pixels = Opaque(frame);
                var metadata = CopyMetadata(frame, pixels.PixelWidth, pixels.PixelHeight);
                Save(pixels, metadata, ColourProfile(frame), temp);
            }
            File.Move(temp, jpgPath);
            return jpgPath;
        }
        catch
        {
            try { File.Delete(temp); } catch (Exception) { }
            throw;
        }
    }

    private static string NoDecoderMessage(string path)
    {
        string ext = Path.GetExtension(path);
        return Extensions.TryGetValue(ext, out string? extension)
            ? $"Windows can't read {ext.ToLowerInvariant()} pictures on this PC. Installing \"{extension}\" from the Microsoft Store adds that."
            : $"Windows can't read {ext.ToLowerInvariant()} pictures on this PC.";
    }

    /// <summary>The picture as plain 8-bit colour, which is all a JPG holds, with any transparency laid over white.</summary>
    private static BitmapSource Opaque(BitmapSource source)
    {
        if (!WithAlpha.Contains(source.Format))
            return source.Format == PixelFormats.Bgr24 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);

        // Premultiplied, a pixel over white is just its colour plus whatever share of white shows through.
        var premultiplied = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        int width = premultiplied.PixelWidth, height = premultiplied.PixelHeight, stride = checked(width * 4);
        var bytes = new byte[checked(stride * height)];
        premultiplied.CopyPixels(bytes, stride, 0);
        for (int i = 0; i < bytes.Length; i += 4)
        {
            int clear = 255 - bytes[i + 3];
            bytes[i] += (byte)clear;
            bytes[i + 1] += (byte)clear;
            bytes[i + 2] += (byte)clear;
            bytes[i + 3] = 255;
        }
        return BitmapSource.Create(width, height, Dpi(source.DpiX), Dpi(source.DpiY), PixelFormats.Bgr32, null, bytes, stride);
    }

    private static double Dpi(double dpi) => dpi > 0 ? dpi : 96;

    /// <summary>
    /// The photo's EXIF details (date taken, camera, exposure, GPS), copied tag by tag: the formats store them in
    /// different containers, and Windows will not move a whole block from one to the other. Tags it refuses are skipped.
    /// </summary>
    private static BitmapMetadata CopyMetadata(BitmapFrame frame, int width, int height)
    {
        var jpeg = new BitmapMetadata("jpg");
        jpeg.SetQuery("/app1/ifd", new BitmapMetadata("ifd"));
        try
        {
            if (frame.Metadata is BitmapMetadata source && source.GetQuery("/ifd") is BitmapMetadata ifd)
                foreach (string tag in ifd)
                {
                    var value = ifd.GetQuery(tag);
                    if (value is BitmapMetadata block)
                    {
                        string? name = tag == Tag(ExifBlock) ? "exif" : tag == Tag(GpsBlock) ? "gps" : null;
                        if (name == null) continue;
                        jpeg.SetQuery($"/app1/ifd/{name}", new BitmapMetadata(name));
                        foreach (string inner in block)
                            if (inner != Tag(MakerNote) && block.GetQuery(inner) is { } innerValue and not BitmapMetadata)
                                TrySet(jpeg, $"/app1/ifd/{name}{inner}", innerValue);
                    }
                    else if (tag != Tag(Orientation) && value != null) TrySet(jpeg, "/app1/ifd" + tag, value);
                }
        }
        catch (NotSupportedException) { } // a decoder with no metadata to give

        jpeg.SetQuery("/app1/ifd" + Tag(Orientation), (ushort)1);
        if (jpeg.ContainsQuery("/app1/ifd/exif"))
        {
            // The original's figures describe it before it was turned the right way up.
            TrySet(jpeg, "/app1/ifd/exif" + Tag(PixelWidth), (uint)width);
            TrySet(jpeg, "/app1/ifd/exif" + Tag(PixelHeight), (uint)height);
        }
        return jpeg;
    }

    private static string Tag(ushort number) => $"/{{ushort={number}}}";

    private static void TrySet(BitmapMetadata metadata, string query, object value)
    {
        try { metadata.SetQuery(query, value); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    /// <summary>The colour profile, so iPhone photos (which use wider-than-usual colour) don't come out looking washed out.</summary>
    private static ReadOnlyCollection<ColorContext>? ColourProfile(BitmapFrame frame)
    {
        try { return frame.ColorContexts is { Count: > 0 } contexts ? contexts : null; }
        catch (NotSupportedException) { return null; }
    }

    /// <summary>Writes the JPG, dropping the colour profile and then the details if the encoder will not take them.</summary>
    private static void Save(BitmapSource pixels, BitmapMetadata metadata, ReadOnlyCollection<ColorContext>? colours, string path)
    {
        var attempts = new List<(BitmapMetadata?, ReadOnlyCollection<ColorContext>?)> { (metadata, colours) };
        if (colours != null) attempts.Add((metadata, null));
        attempts.Add((null, null));
        for (int i = 0; ; i++)
        {
            try
            {
                var encoder = new JpegBitmapEncoder { QualityLevel = Quality };
                var (details, profile) = attempts[i];
                encoder.Frames.Add(BitmapFrame.Create(pixels, null, details, profile));
                using var output = File.Create(path);
                encoder.Save(output);
                return;
            }
            catch (Exception e) when (i < attempts.Count - 1 && e is not IOException and not UnauthorizedAccessException) { }
        }
    }
}
