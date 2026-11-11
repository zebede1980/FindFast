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
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfields0, dsBitfields1, dsBitfields2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    [return: MarshalAs(UnmanagedType.Interface)]
    private static extern IShellItemImageFactory SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr pbc, ref Guid riid);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int cb, out DIBSECTION dib);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    private static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    /// <summary>Fetches a thumbnail on a background STA thread (thumbnail handlers can be slow and expect one). Null if none.</summary>
    public static Task<ImageSource?> GetAsync(string path, int size)
    {
        var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(Get(path, size)); }
            catch (Exception) { tcs.TrySetResult(null); }
        })
        { IsBackground = true, Name = "FastFind thumbnail" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static ImageSource? Get(string path, int size)
    {
        var iid = IID_IShellItemImageFactory;
        var factory = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid);
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            if (factory.GetImage(new SIZE { cx = size, cy = size }, 0, out hbitmap) != 0 || hbitmap == IntPtr.Zero) return null;
            BitmapSource source;
            // Read the pixels directly so transparency survives (the stock HBITMAP conversion flattens it to black).
            if (GetObject(hbitmap, Marshal.SizeOf<DIBSECTION>(), out var dib) >= Marshal.SizeOf<BITMAP>() && dib.dsBm.bmBitsPixel == 32 && dib.dsBm.bmBits != IntPtr.Zero)
            {
                int width = dib.dsBm.bmWidth, height = Math.Abs(dib.dsBm.bmHeight), stride = dib.dsBm.bmWidthBytes;
                source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, dib.dsBm.bmBits, stride * height, stride);
                // A DIB section with a positive header height stores its rows bottom-up.
                if (dib.dsBmih.biHeight > 0) source = new TransformedBitmap(source, new ScaleTransform(1, -1));
                source = new WriteableBitmap(source); // copy out of the GDI memory before the bitmap is deleted
            }
            else
            {
                source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            source.Freeze();
            return source;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            Marshal.FinalReleaseComObject(factory);
        }
    }
}
