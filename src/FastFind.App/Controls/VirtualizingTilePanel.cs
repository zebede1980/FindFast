using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FastFind.App.Controls;

/// <summary>
/// Where each tile goes: as many equal columns as fit the width (stretched to fill it, so there is no ragged gap on the
/// right), and rows of a fixed height. Kept apart from the panel so the arithmetic can be tested on its own.
/// </summary>
public readonly record struct TileLayout(int Columns, double TileWidth, double TileHeight, int Count)
{
    public static TileLayout For(double width, double minTileWidth, double tileHeight, int count)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0) return new(1, minTileWidth, tileHeight, count);
        int columns = Math.Max(1, (int)(width / minTileWidth));
        return new(columns, Math.Floor(width / columns), tileHeight, count);
    }

    public int Rows => (Count + Columns - 1) / Columns;
    public double ExtentHeight => Rows * TileHeight;
    public int RowOf(int index) => index / Columns;
    public Rect Bounds(int index) => new(index % Columns * TileWidth, RowOf(index) * TileHeight, TileWidth, TileHeight);

    /// <summary>The first and last items worth creating for this scroll position, with a spare row either side. Last is -1 when there are none.</summary>
    public (int First, int Last) Range(double offset, double viewportHeight, int spareRows = 1)
    {
        if (Count == 0) return (0, -1);
        int firstRow = Math.Max(0, (int)(offset / TileHeight) - spareRows);
        int lastRow = Math.Min(Rows - 1, (int)Math.Ceiling((offset + viewportHeight) / TileHeight) - 1 + spareRows);
        return (Math.Min(Count - 1, firstRow * Columns), Math.Min(Count - 1, (lastRow + 1) * Columns - 1));
    }

    /// <summary>
    /// Where an arrow (or Home, End, Page Up, Page Down) key moves to from <paramref name="index"/>, as in Explorer: up
    /// from the top row and right from the last tile go nowhere, and down into a short last row lands on its last tile.
    /// Null for keys that do not move.
    /// </summary>
    public int? Step(int index, Key key, int pageRows)
    {
        if (Count == 0) return null;
        if (index < 0 || index >= Count) return key is Key.End ? Count - 1 : 0;
        int page = Math.Max(1, pageRows) * Columns;
        return key switch
        {
            Key.Left => Math.Max(0, index - 1),
            Key.Right => Math.Min(Count - 1, index + 1),
            Key.Up => index >= Columns ? index - Columns : index,
            Key.Down => index + Columns < Count ? index + Columns : RowOf(Count - 1) > RowOf(index) ? Count - 1 : index,
            Key.PageUp => Math.Max(index % Columns, index - page),
            Key.PageDown => Math.Min(Count - 1, index + page),
            Key.Home => 0,
            Key.End => Count - 1,
            _ => null,
        };
    }
}

/// <summary>
/// Lays tiles out in rows that wrap to the width of the pane, creating only the tiles on screen. WPF's own WrapPanel
/// would create one for every item, which a folder of thousands of pictures (or a search of millions of files) cannot
/// afford. It does its own scrolling, a pixel at a time, so the list scrolls smoothly rather than a row at a time.
/// </summary>
public sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty TileWidthProperty = DependencyProperty.Register(nameof(TileWidth), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(132.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty TileHeightProperty = DependencyProperty.Register(nameof(TileHeight), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(160.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The narrowest a tile may be; tiles widen to share out any room left over.</summary>
    public double TileWidth { get => (double)GetValue(TileWidthProperty); set => SetValue(TileWidthProperty, value); }
    public double TileHeight { get => (double)GetValue(TileHeightProperty); set => SetValue(TileHeightProperty, value); }

    private TileLayout _layout = new(1, 132, 160, 0);
    private Size _viewport;
    private double _offset;

    public TileLayout Layout => _layout;

    /// <summary>How many whole rows fit on screen, which is how far Page Up and Page Down go.</summary>
    public int RowsPerPage => Math.Max(1, (int)(_viewport.Height / _layout.TileHeight));

    protected override Size MeasureOverride(Size availableSize)
    {
        if (ItemsControl.GetItemsOwner(this) is TileListBox owner) owner.Panel = this;
        var children = InternalChildren; // the generator is not usable until the children have been asked for
        var generator = ItemContainerGenerator;
        int count = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;

        var previous = _layout;
        _layout = TileLayout.For(availableSize.Width, TileWidth, TileHeight, count);
        _viewport = new Size(double.IsInfinity(availableSize.Width) ? _layout.Columns * _layout.TileWidth : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? _layout.ExtentHeight : availableSize.Height);
        // When the pane is resized the columns change; keep the tile that was at the top in view.
        if (previous.Columns != _layout.Columns && previous.Count > 0 && _offset > 0)
            _offset = _layout.RowOf((int)(_offset / previous.TileHeight) * previous.Columns) * _layout.TileHeight;
        _offset = Math.Max(0, Math.Min(_offset, _layout.ExtentHeight - _viewport.Height));
        ScrollOwner?.InvalidateScrollInfo();

        var (first, last) = _layout.Range(_offset, _viewport.Height);
        var tileSize = new Size(_layout.TileWidth, _layout.TileHeight);
        if (last >= first)
        {
            var start = generator.GeneratorPositionFromIndex(first);
            int childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, true))
            {
                for (int i = first; i <= last; i++, childIndex++)
                {
                    if (generator.GenerateNext(out bool isNew) is not UIElement child) break;
                    if (isNew)
                    {
                        if (childIndex >= children.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(tileSize);
                }
            }
        }
        RemoveTilesOutside(first, last);
        return _viewport;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            var bounds = _layout.Bounds(index);
            bounds.Y -= _offset;
            InternalChildren[i].Arrange(bounds);
        }
        return finalSize;
    }

    private void RemoveTilesOutside(int first, int last)
    {
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        for (int i = children.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            int index = generator.IndexFromGeneratorPosition(position);
            if (index >= first && index <= last) continue;
            // The tile with the keyboard focus stays, or the focus would fall out of the list as it scrolls away.
            if (children[i].IsKeyboardFocusWithin) continue;
            generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
        }
    }

    /// <summary>Scrolls just far enough to show the tile: what ScrollIntoView and keyboard movement come to.</summary>
    protected override void BringIndexIntoView(int index)
    {
        if (index < 0 || index >= _layout.Count) return;
        var bounds = _layout.Bounds(index);
        if (bounds.Top < _offset) SetVerticalOffset(bounds.Top);
        else if (bounds.Bottom > _offset + _viewport.Height) SetVerticalOffset(bounds.Bottom - _viewport.Height);
    }

    // ---- IScrollInfo ----

    public ScrollViewer? ScrollOwner { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => _viewport.Width;
    public double ExtentHeight => _layout.ExtentHeight;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset;

    private double LineStep => _layout.TileHeight / 4;
    private double WheelStep => Math.Max(LineStep, _layout.TileHeight * SystemParameters.WheelScrollLines / 4);

    public void LineUp() => SetVerticalOffset(_offset - LineStep);
    public void LineDown() => SetVerticalOffset(_offset + LineStep);
    public void PageUp() => SetVerticalOffset(_offset - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offset - WheelStep);
    public void MouseWheelDown() => SetVerticalOffset(_offset + WheelStep);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Max(0, Math.Min(offset, _layout.ExtentHeight - _viewport.Height));
        if (Math.Abs(offset - _offset) < 0.5) return;
        _offset = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            if (InternalChildren[i] is not { } child || !(ReferenceEquals(child, visual) || child.IsAncestorOf(visual))) continue;
            BringIndexIntoView(ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0)));
            break;
        }
        return rectangle;
    }
}

/// <summary>
/// The list behind the tile view. It moves the keyboard cursor through the grid of tiles itself (WPF's own arrow-key
/// handling only knows about tiles that have been created, so it stops at the edge of the screen), keeping Shift and
/// Ctrl working as they do in the detailed list.
/// </summary>
public sealed class TileListBox : ListBox
{
    internal VirtualizingTilePanel? Panel { get; set; }

    /// <summary>The item whose tile has the keyboard focus, if any.</summary>
    public object? FocusedItem
        => Keyboard.FocusedElement is ListBoxItem tile && ReferenceEquals(ItemsControlFromItemContainer(tile), this) ? ItemContainerGenerator.ItemFromContainer(tile) : null;

    /// <summary>
    /// The item Shift+arrow stretches from. The list keeps its own anchor for Shift+click, but only accepts one whose
    /// tile is on screen, so this copy carries on once that tile has scrolled away.
    /// </summary>
    private object? _anchor;

    /// <summary>Makes the item the one a Shift+click or Shift+arrow selection stretches from.</summary>
    public void AnchorTo(object? item)
    {
        _anchor = item;
        if (item != null && ItemContainerGenerator.ContainerFromItem(item) != null) AnchorItem = item;
    }

    /// <summary>Scrolls to an item, gives its tile the keyboard focus and, if asked, makes it the only one selected.</summary>
    public void FocusIndex(int index, bool select)
    {
        if (index < 0 || index >= Items.Count) { Focus(); return; }
        var item = Items[index];
        if (select)
        {
            UnselectAll();
            SelectedItem = item;
        }
        ScrollIntoView(item);
        UpdateLayout();
        if (select) AnchorTo(item);
        if (ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem tile) tile.Focus();
        else Focus();
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // A click without Shift moves the list's own anchor; from here on that one is right.
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) _anchor = null;
        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers & ~ModifierKeys.Alt;
        if (Panel is { } panel && (Keyboard.Modifiers & ModifierKeys.Alt) == 0 && Items.Count > 0)
        {
            var focused = FocusedItem;
            int from = focused != null ? Items.IndexOf(focused) : SelectedIndex;
            if (panel.Layout.Step(from, e.Key, panel.RowsPerPage) is int to)
            {
                e.Handled = true;
                if (mods.HasFlag(ModifierKeys.Shift))
                {
                    // Extends from the item the selection started at, as Shift+click does.
                    var start = _anchor ?? AnchorItem;
                    int anchor = start != null ? Items.IndexOf(start) : -1;
                    if (anchor < 0) anchor = Math.Max(0, from);
                    int low = Math.Min(anchor, to), high = Math.Max(anchor, to);
                    SetSelectedItems(Enumerable.Range(low, high - low + 1).Select(i => Items[i]));
                    _anchor = Items[anchor];
                    FocusIndex(to, select: false);
                }
                else FocusIndex(to, select: !mods.HasFlag(ModifierKeys.Control)); // Ctrl moves the cursor and leaves the selection alone
                return;
            }
        }
        base.OnKeyDown(e);
    }
}
