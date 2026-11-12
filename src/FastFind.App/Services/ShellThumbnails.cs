using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FastFind.App.Services;

/// <summary>
/// Large thumbnails from the shell: pictures, videos, PDFs and Office documents get their real preview image where a
/// handler is installed, everything else gets its large icon. This is what Explorer's own preview uses.
/// </summary>
public static class ShellThumbnails
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        // Room for the colour masks some drivers write back even for plain 32-bit data.
        public uint mask0, mask1, mask2, mask3;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    [return: MarshalAs(UnmanagedType.Interface)]
    private static extern IShellItemImageFactory SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr pbc, ref Guid riid);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int cb, out BITMAP bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbitmap, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFO info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    private static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    private const int IconOnly = 0x4, ThumbnailOnly = 0x8;
    /// <summary>WTS_E_EXTRACTIONPENDING: Windows is still making the thumbnail and will have it shortly.</summary>
    private const int ExtractionPending = unchecked((int)0x8004B205);
    /// <summary>How long to wait between looks for a thumbnail that is still being made: about seven seconds in all.</summary>
    private static readonly int[] RetryDelaysMs = { 120, 250, 500, 1000, 2000, 3000 };

    /// <summary>
    /// Fetches a thumbnail on a background STA thread (thumbnail handlers can be slow and expect one). The result is
    /// the real thumbnail where there is one, otherwise the large icon; null if neither could be had.
    /// </summary>
    /// <param name="interim">
    /// Called (on the background thread) with the large icon when the thumbnail is not ready yet. Windows makes the
    /// thumbnails of files in OneDrive and other cloud folders in the background, so the first request for one comes
    /// back empty-handed; the icon stands in while this keeps asking.
    /// </param>
    public static Task<ImageSource?> GetAsync(string path, int size, Action<ImageSource>? interim = null, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(Get(path, size, interim, ct)); }
            catch (Exception) { tcs.TrySetResult(null); }
        })
        { IsBackground = true, Name = "FastFind thumbnail" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static ImageSource? Get(string path, int size, Action<ImageSource>? interim, CancellationToken ct)
    {
        // Asking for the thumbnail alone tells "there is none" apart from "not ready yet"; asking for either would
        // quietly hand back the icon in both cases.
        var image = Fetch(path, size, ThumbnailOnly, out int result);
        if (image != null) return image;
        var icon = Fetch(path, size, IconOnly, out _) ?? Fetch(path, size, 0, out _);
        if (result != ExtractionPending) return icon;

        if (icon != null) interim?.Invoke(icon);
        foreach (int delay in RetryDelaysMs)
        {
            if (ct.WaitHandle.WaitOne(delay)) return icon; // the caller has moved on to another file
            image = Fetch(path, size, ThumbnailOnly, out result);
            if (image != null) return image;
            if (result != ExtractionPending) break;
        }
        return icon;
    }

    private static BitmapSource? Fetch(string path, int size, int flags, out int result)
    {
        var iid = IID_IShellItemImageFactory;
        var factory = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid);
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            result = factory.GetImage(new SIZE { cx = size, cy = size }, flags, out hbitmap);
            if (result != 0 || hbitmap == IntPtr.Zero) return null;
            var source = FromHBitmap(hbitmap);
            source.Freeze();
            return source;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            Marshal.FinalReleaseComObject(factory);
        }
    }

    /// <summary>
    /// Copies a shell bitmap into a WPF image, keeping its transparency (the stock conversion flattens it to black).
    /// The pixels are fetched through GDI, top row first, rather than read from the bitmap's memory: the shell stores
    /// thumbnails top-down and icons bottom-up, and the bitmap's header does not say which, so reading the memory
    /// directly shows one or the other upside down.
    /// </summary>
    private static BitmapSource FromHBitmap(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), out var bitmap) != 0 && bitmap.bmBitsPixel == 32 && bitmap.bmWidth > 0 && bitmap.bmHeight != 0)
        {
            int width = bitmap.bmWidth, height = Math.Abs(bitmap.bmHeight);
            var info = new BITMAPINFO
            {
                biSize = 40, biWidth = width, biHeight = -height /* negative: top row first */, biPlanes = 1, biBitCount = 32, biCompression = 0 /* BI_RGB */,
            };
            var pixels = new byte[width * 4 * height];
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            try
            {
                if (dc != IntPtr.Zero && GetDIBits(dc, hbitmap, 0, (uint)height, pixels, ref info, 0 /* DIB_RGB_COLORS */) == height)
                {
                    // A bitmap with no transparency at all may leave the alpha bytes at zero; that means opaque, not invisible.
                    bool hasAlpha = false;
                    for (int i = 3; i < pixels.Length; i += 4)
                        if (pixels[i] != 0) { hasAlpha = true; break; }
                    return BitmapSource.Create(width, height, 96, 96, hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, pixels, width * 4);
                }
            }
            finally
            {
                if (dc != IntPtr.Zero) DeleteDC(dc);
            }
        }
        return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
    }
}
