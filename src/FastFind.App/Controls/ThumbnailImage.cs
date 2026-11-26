using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FastFind.App.Services;
using FastFind.App.ViewModels;

namespace FastFind.App.Controls;

/// <summary>
/// The picture on a tile: the file's thumbnail (or, for anything without one, its large icon), fetched in the
/// background while the tile is on screen. The small icon from the list stands in until it arrives.
/// </summary>
public sealed class ThumbnailImage : Image
{
    public static readonly DependencyProperty FileProperty = DependencyProperty.Register(nameof(File), typeof(FileRow), typeof(ThumbnailImage),
        new PropertyMetadata(null, (d, _) => ((ThumbnailImage)d).Load()));

    /// <summary>The size the thumbnail is drawn at, in device-independent pixels; the request allows for the screen's scaling.</summary>
    public static readonly DependencyProperty ThumbnailSizeProperty = DependencyProperty.Register(nameof(ThumbnailSize), typeof(double), typeof(ThumbnailImage),
        new PropertyMetadata(112.0));

    public FileRow? File { get => (FileRow?)GetValue(FileProperty); set => SetValue(FileProperty, value); }
    public double ThumbnailSize { get => (double)GetValue(ThumbnailSizeProperty); set => SetValue(ThumbnailSizeProperty, value); }

    private CancellationTokenSource? _cancel;

    public ThumbnailImage()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        // Thumbnails shrink to fit the tile; an icon smaller than the tile is drawn at its own size rather than blurred.
        Stretch = Stretch.Uniform;
        StretchDirection = StretchDirection.DownOnly;
        Loaded += (_, _) => Load();
        Unloaded += (_, _) => Cancel();
    }

    private void Cancel()
    {
        // Not disposed: the loader may still be waiting on its token, and a source without a timer holds nothing that needs freeing.
        _cancel?.Cancel();
        _cancel = null;
    }

    private void Load()
    {
        Cancel();
        if (File is not { IsDeleted: false } row) { Source = null; return; }
        Source = row.Icon;
        if (!IsLoaded) return;
        var cancel = _cancel = new CancellationTokenSource();
        int pixels = (int)Math.Ceiling(ThumbnailSize * VisualTreeHelper.GetDpi(this).DpiScaleX);
        ThumbnailLoader.Request(row.FullPath, row.ModifiedFileTime, pixels, image => { if (!cancel.IsCancellationRequested) Source = image; }, cancel.Token);
    }
}
