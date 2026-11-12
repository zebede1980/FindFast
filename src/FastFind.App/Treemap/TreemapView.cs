using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FastFind.App.Services;
using FastFind.Core.Search;
using FastFind.Core.Usage;

namespace FastFind.App.Treemap;

/// <summary>
/// The disk usage map on screen: draws a <see cref="TreemapImage"/>, reports what the mouse is over, outlines the
/// selection and animates zooming into and out of folders. Rendering happens on a background thread at the exact
/// pixel size of the control, so it stays sharp at any display scaling and never blocks the window.
/// </summary>
public sealed class TreemapView : FrameworkElement
{
    private static readonly Duration ZoomTime = new(TimeSpan.FromMilliseconds(260));
    private static readonly Duration FadeTime = new(TimeSpan.FromMilliseconds(140));
    private static readonly Brush DimBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x48, 0, 0, 0)));
    private static readonly Brush ChipBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x8C, 0x0B, 0x0C, 0x0F)));
    private static readonly Brush LabelBrush = Brushes.White;
    private static readonly Brush LabelDetailBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen SelectionPen = Frozen(new Pen(Brushes.White, 2));
    private static readonly Pen SelectionShadowPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)), 4));
    private static readonly Pen HoverPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface DetailFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private readonly DrawingVisual _picture = new();
    private readonly DrawingVisual _labels = new();
    private readonly DrawingVisual _overlay = new();
    private readonly DispatcherTimer _resizeTimer;

    private SizeTree? _tree;
    private int _root;
    private TreemapImage? _image;
    private BitmapSource? _bitmap;
    private int _selected = -1;
    private int _hover = -1;
    private TreemapHighlight? _highlight;
    private bool _showLabels = true;
    private int _generation;
    private bool _animating;
    private CancellationTokenSource? _renderCancel;

    /// <summary>How the next picture should arrive: a zoom from or to this folder, a cross-fade, or at once.</summary>
    private enum Arrival { Instant, Fade, ZoomIn, ZoomOut }

    public TreemapView()
    {
        AddVisualChild(_picture);
        AddVisualChild(_labels);
        AddVisualChild(_overlay);
        ClipToBounds = true;
        Focusable = false;
        _resizeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(70) };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); Draw(Arrival.Instant, -1); };
        Unloaded += (_, _) => { _resizeTimer.Stop(); _renderCancel?.Cancel(); };
    }

    protected override int VisualChildrenCount => 3;
    protected override Visual GetVisualChild(int index) => index switch { 0 => _picture, 1 => _labels, _ => _overlay };
    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters) => new PointHitTestResult(this, hitTestParameters.HitPoint);

    // ---- what is shown ----

    /// <summary>Raised with the node under the mouse (-1 when it leaves) and where the mouse is.</summary>
    public event Action<int, Point>? HoverChanged;
    /// <summary>Raised when the folder on show changes.</summary>
    public event Action? RootChanged;
    /// <summary>Raised when a new picture has been drawn.</summary>
    public event Action? Drawn;

    public SizeTree? Tree => _tree;

    /// <summary>The folder the map shows: the tree's root, or a folder zoomed into.</summary>
    public int Root => _root;

    public bool HasPicture => _image != null;

    /// <summary>Shows a tree from its root.</summary>
    public void Show(SizeTree? tree)
    {
        _tree = tree;
        _root = SizeTree.Root;
        _selected = _hover = -1;
        _highlight = null;
        _image = null;
        _bitmap = null;
        using (_picture.RenderOpen()) { }
        RedrawLabels();
        RedrawOverlay();
        RootChanged?.Invoke();
        Draw(Arrival.Fade, -1);
    }

    /// <summary>Shows one folder of the tree, animating towards or away from it.</summary>
    public void ZoomTo(int node)
    {
        if (_tree == null || node < 0 || node == _root || !_tree.IsDirectory(node) || _tree.IsRemoved(node) || _tree.Size(node) <= 0) return;
        int previous = _root;
        bool inwards = _tree.IsSameOrInside(node, previous);
        _root = node;
        _hover = -1;
        RootChanged?.Invoke();
        Draw(inwards ? Arrival.ZoomIn : _tree.IsSameOrInside(previous, node) ? Arrival.ZoomOut : Arrival.Fade, previous);
    }

    /// <summary>Redraws after the tree itself changed (something was deleted).</summary>
    public void Refresh()
    {
        if (_tree == null) return;
        // The folder on show, or the selection, may have gone, or be inside something that has.
        if (!Attached(_root)) _root = SizeTree.Root;
        if (_selected >= 0 && !Attached(_selected)) _selected = -1;
        _hover = -1;
        Draw(Arrival.Fade, -1);
    }

    /// <summary>True while a node and every folder above it are still in the tree.</summary>
    private bool Attached(int node)
    {
        for (int n = node; n > SizeTree.Root; n = _tree!.Parent(n))
            if (_tree.IsRemoved(n)) return false;
        return true;
    }

    /// <summary>The file or folder outlined on the map, or -1.</summary>
    public int Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            RedrawOverlay();
        }
    }

    /// <summary>Blocks to pick out; everything else is dimmed. Null shows everything normally.</summary>
    public TreemapHighlight? Highlight
    {
        get => _highlight;
        set
        {
            if (_highlight == value) return;
            _highlight = value;
            Draw(Arrival.Fade, -1);
        }
    }

    /// <summary>Whether the biggest folders carry their names.</summary>
    public bool ShowLabels
    {
        get => _showLabels;
        set
        {
            if (_showLabels == value) return;
            _showLabels = value;
            RedrawLabels();
        }
    }

    /// <summary>The file (or undivided folder) drawn at a point of the control, or -1.</summary>
    public int NodeAt(Point point)
    {
        var image = _image;
        if (image == null || ActualWidth <= 0 || ActualHeight <= 0) return -1;
        int x = (int)(point.X * image.Width / ActualWidth), y = (int)(point.Y * image.Height / ActualHeight);
        return image.NodeAt(x, y);
    }

    /// <summary>The child of the folder on show that contains <paramref name="node"/>: one step into the map from where it is now.</summary>
    public int TopLevelOf(int node)
    {
        if (_tree == null || node < 0) return -1;
        for (int n = node; n > SizeTree.Root; n = _tree.Parent(n))
            if (_tree.Parent(n) == _root) return n;
        return -1;
    }

    // ---- drawing ----

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_tree == null) return;
        if (_image == null) { Draw(Arrival.Fade, -1); return; }
        // Stretch what is there while the window is being dragged; draw it properly once the size settles.
        ShowPicture(_bitmap, null, Arrival.Instant, default);
        RedrawLabels();
        RedrawOverlay();
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void Draw(Arrival arrival, int previousRoot)
    {
        var tree = _tree;
        if (tree == null || ActualWidth < 1 || ActualHeight < 1) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int width = Math.Max(1, (int)Math.Round(ActualWidth * scale)), height = Math.Max(1, (int)Math.Round(ActualHeight * scale));
        int root = _root, generation = ++_generation;
        var highlight = _highlight;
        _renderCancel?.Cancel();
        var cancel = _renderCancel = new CancellationTokenSource();
        Task.Run(() => TreemapRenderer.Render(tree, root, width, height, highlight, (float)scale, cancel.Token), cancel.Token).ContinueWith(task =>
        {
            if (generation != _generation || !ReferenceEquals(tree, _tree)) return;
            if (task.IsFaulted) { Logger.Warn($"Disk usage map could not be drawn: {task.Exception?.GetBaseException().Message}"); return; }
            if (task.IsCanceled) return;
            Present(task.Result, scale, arrival, previousRoot);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Present(TreemapImage image, double scale, Arrival arrival, int previousRoot)
    {
        var previousImage = _image;
        var previousBitmap = _bitmap;
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96 * scale, 96 * scale, PixelFormats.Bgr32, null, image.Pixels, image.Width * 4);
        bitmap.Freeze();
        _image = image;
        _bitmap = bitmap;

        // A zoom needs to know where the folder zoomed into (or out of) sat in the wider of the two pictures.
        Rect anchor = default;
        if (arrival == Arrival.ZoomIn && previousImage != null && previousImage.TryGetRect(_tree!, image.Root, out var into)) anchor = ToDips(into, previousImage);
        else if (arrival == Arrival.ZoomOut && image.TryGetRect(_tree!, previousRoot, out var outOf)) anchor = ToDips(outOf, image);
        if (arrival is Arrival.ZoomIn or Arrival.ZoomOut && (anchor.Width < 2 || anchor.Height < 2)) arrival = Arrival.Fade;
        if (previousBitmap == null && arrival != Arrival.Instant) arrival = Arrival.Fade;

        ShowPicture(bitmap, previousBitmap, arrival, anchor);
        Drawn?.Invoke();
    }

    private Rect ToDips(RectI rect, TreemapImage image)
    {
        double sx = ActualWidth / image.Width, sy = ActualHeight / image.Height;
        return new Rect(rect.X * sx, rect.Y * sy, rect.W * sx, rect.H * sy);
    }

    /// <summary>The rectangle a picture must be drawn into so that <paramref name="part"/> of it exactly fills the control.</summary>
    private Rect Magnified(Rect part)
    {
        double sx = ActualWidth / part.Width, sy = ActualHeight / part.Height;
        return new Rect(-part.X * sx, -part.Y * sy, ActualWidth * sx, ActualHeight * sy);
    }

    private void ShowPicture(BitmapSource? bitmap, BitmapSource? previous, Arrival arrival, Rect anchor)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        using var dc = _picture.RenderOpen();
        if (bitmap == null) return;
        if (arrival == Arrival.Instant || previous == null)
        {
            if (arrival == Arrival.Fade)
            {
                var appear = Clock(new DoubleAnimation(0, 1, FadeTime));
                dc.PushOpacity(0, appear);
                dc.DrawImage(bitmap, bounds);
                dc.Pop();
            }
            else dc.DrawImage(bitmap, bounds);
            FinishAnimation();
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        switch (arrival)
        {
            case Arrival.ZoomIn:
            {
                // The old picture swells until the folder fills the view; the new one grows out of the folder's place.
                dc.DrawImage(previous, bounds, Clock(new RectAnimation(bounds, Magnified(anchor), ZoomTime) { EasingFunction = ease }));
                dc.PushOpacity(0, Clock(new DoubleAnimation(0, 1, new Duration(ZoomTime.TimeSpan * 0.7))));
                dc.DrawImage(bitmap, anchor, Clock(new RectAnimation(anchor, bounds, ZoomTime) { EasingFunction = ease }, finishes: true));
                dc.Pop();
                break;
            }
            case Arrival.ZoomOut:
            {
                // The reverse: the new picture settles back from close up while the old one shrinks into its place in it.
                dc.DrawImage(bitmap, Magnified(anchor), Clock(new RectAnimation(Magnified(anchor), bounds, ZoomTime) { EasingFunction = ease }, finishes: true));
                dc.PushOpacity(1, Clock(new DoubleAnimation(1, 0, ZoomTime) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } }));
                dc.DrawImage(previous, bounds, Clock(new RectAnimation(bounds, anchor, ZoomTime) { EasingFunction = ease }));
                dc.Pop();
                break;
            }
            default:
                dc.DrawImage(previous, bounds);
                dc.PushOpacity(0, Clock(new DoubleAnimation(0, 1, FadeTime), finishes: true));
                dc.DrawImage(bitmap, bounds);
                dc.Pop();
                break;
        }
        _animating = true;
        RedrawLabels();
        RedrawOverlay();
    }

    private AnimationClock Clock(AnimationTimeline animation, bool finishes = false)
    {
        animation.FillBehavior = FillBehavior.HoldEnd;
        var clock = animation.CreateClock();
        if (finishes)
        {
            int generation = _generation;
            clock.Completed += (_, _) =>
            {
                if (generation != _generation) return;
                // Settle on a plain, unanimated drawing of the new picture, and bring the names and outlines back.
                ShowPicture(_bitmap, null, Arrival.Instant, default);
            };
        }
        return clock;
    }

    private void FinishAnimation()
    {
        _animating = false;
        RedrawLabels();
        RedrawOverlay();
    }

    private void RedrawLabels()
    {
        using var dc = _labels.RenderOpen();
        var image = _image;
        var tree = _tree;
        if (!_showLabels || _animating || image == null || tree == null || image.Root != _root) return;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (int child in tree.Children(_root))
        {
            if (!image.Rects.TryGetValue(child, out var placed)) continue;
            var rect = ToDips(placed, image);
            if (rect.Width < 62 || rect.Height < 26) continue;
            var name = new FormattedText(tree.Name(child), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 12, LabelBrush, pixelsPerDip)
            {
                MaxTextWidth = rect.Width - 22, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
            };
            FormattedText? detail = null;
            if (rect.Height >= 50 && rect.Width >= 84)
            {
                detail = new FormattedText(SizeParser.Format(tree.Size(child)), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, DetailFace, 11, LabelDetailBrush, pixelsPerDip)
                {
                    MaxTextWidth = rect.Width - 22, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
                };
            }
            double width = Math.Max(name.Width, detail?.Width ?? 0) + 12, height = name.Height + (detail?.Height ?? 0) + 6;
            var chip = new Rect(Math.Round(rect.X + 5), Math.Round(rect.Y + 5), Math.Ceiling(width), Math.Ceiling(height));
            dc.DrawRoundedRectangle(ChipBrush, null, chip, 5, 5);
            dc.DrawText(name, new Point(chip.X + 6, chip.Y + 3));
            if (detail != null) dc.DrawText(detail, new Point(chip.X + 6, chip.Y + 3 + name.Height));
        }
    }

    private void RedrawOverlay()
    {
        using var dc = _overlay.RenderOpen();
        var image = _image;
        var tree = _tree;
        if (_animating || image == null || tree == null || image.Root != _root) return;
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);

        if (_selected >= 0 && _selected != _root && !tree.IsRemoved(_selected) && image.TryGetRect(tree, _selected, out var placed))
        {
            // Spotlight: everything outside the selection is pushed back, and the selection gets a clean outline.
            var rect = Snap(ToDips(placed, image));
            var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(bounds), new RectangleGeometry(rect));
            outside.Freeze();
            dc.DrawGeometry(DimBrush, null, outside);
            dc.DrawRectangle(null, SelectionShadowPen, rect);
            dc.DrawRectangle(null, SelectionPen, rect);
            if (rect.Width < 16 && rect.Height < 16)
            {
                // Too small to notice on its own: ring it.
                var centre = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
                dc.DrawEllipse(null, SelectionShadowPen, centre, 17, 17);
                dc.DrawEllipse(null, SelectionPen, centre, 17, 17);
            }
        }
        if (_hover >= 0 && _hover != _selected && image.Rects.TryGetValue(_hover, out var hovered))
        {
            var rect = ToDips(hovered, image);
            if (rect.Width >= 3 && rect.Height >= 3)
                dc.DrawRectangle(null, HoverPen, new Rect(rect.X + 0.5, rect.Y + 0.5, Math.Max(0, rect.Width - 1), Math.Max(0, rect.Height - 1)));
        }
    }

    /// <summary>Keeps the outline at least a few pixels across, so a selected file too small to see can still be found.</summary>
    private static Rect Snap(Rect rect)
    {
        double w = Math.Max(rect.Width, 6), h = Math.Max(rect.Height, 6);
        return new Rect(rect.X - (w - rect.Width) / 2, rect.Y - (h - rect.Height) / 2, w, h);
    }

    // ---- mouse ----

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        int node = NodeAt(point);
        if (node != _hover)
        {
            _hover = node;
            RedrawOverlay();
        }
        HoverChanged?.Invoke(node, point);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0) return;
        _hover = -1;
        RedrawOverlay();
        HoverChanged?.Invoke(-1, default);
    }
}
