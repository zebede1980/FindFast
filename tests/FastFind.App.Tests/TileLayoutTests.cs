using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FastFind.App.Controls;
using Xunit;

namespace FastFind.App.Tests;

public class TileLayoutTests
{
    [Fact]
    public void Tiles_fill_the_width_with_no_ragged_gap()
    {
        var layout = TileLayout.For(width: 700, minTileWidth: 132, tileHeight: 160, count: 23);

        Assert.Equal(5, layout.Columns);
        Assert.Equal(140, layout.TileWidth);
        Assert.Equal(5, layout.Rows);
        Assert.Equal(800, layout.ExtentHeight);
        Assert.Equal(new System.Windows.Rect(280, 160, 140, 160), layout.Bounds(7));
    }

    [Fact]
    public void A_pane_narrower_than_one_tile_still_has_one_column()
    {
        Assert.Equal(1, TileLayout.For(60, 132, 160, 10).Columns);
        Assert.Equal(1, TileLayout.For(double.PositiveInfinity, 132, 160, 10).Columns);
    }

    [Fact]
    public void Only_the_rows_on_screen_and_one_either_side_are_created()
    {
        var layout = TileLayout.For(700, 132, 160, 1_000_000);

        // Scrolled to row 100, with a little over three rows showing.
        var (first, last) = layout.Range(offset: 100 * 160, viewportHeight: 500);

        Assert.Equal(99 * 5, first);
        Assert.Equal(105 * 5 - 1, last);
        Assert.Equal((0, -1), TileLayout.For(700, 132, 160, 0).Range(0, 500));
        Assert.Equal((0, 2), TileLayout.For(700, 132, 160, 3).Range(0, 500));
    }

    [Theory]
    [InlineData(7, Key.Left, 6)]
    [InlineData(0, Key.Left, 0)]
    [InlineData(7, Key.Right, 8)]
    [InlineData(22, Key.Right, 22)] // the last tile
    [InlineData(7, Key.Up, 2)]
    [InlineData(2, Key.Up, 2)] // top row
    [InlineData(7, Key.Down, 12)]
    [InlineData(19, Key.Down, 22)] // into the short last row: lands on its last tile
    [InlineData(21, Key.Down, 21)] // already on the last row
    [InlineData(12, Key.PageUp, 2)] // two rows a page, but no higher than its own column's top
    [InlineData(2, Key.PageDown, 12)]
    [InlineData(20, Key.PageDown, 22)]
    [InlineData(12, Key.Home, 0)]
    [InlineData(3, Key.End, 22)]
    [InlineData(-1, Key.Down, 0)] // nothing focused yet
    public void Arrow_keys_move_through_the_grid_as_in_Explorer(int from, Key key, int expected)
    {
        var layout = TileLayout.For(700, 132, 160, 23); // 5 columns, the last row holding 3
        Assert.Equal(expected, layout.Step(from, key, pageRows: 2));
    }

    [Fact]
    public void Other_keys_do_not_move()
        => Assert.Null(TileLayout.For(700, 132, 160, 23).Step(4, Key.Enter, 2));

    [Fact]
    public void A_long_list_creates_only_the_tiles_on_screen_and_can_scroll_to_any() => Ui.Run(() =>
    {
        var list = new TileListBox { SelectionMode = SelectionMode.Extended, ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingTilePanel))) };
        ScrollViewer.SetCanContentScroll(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.ItemsSource = Enumerable.Range(0, 10_000).ToList();
        var window = new Window { Content = list, Width = 720, Height = 540, Left = -20_000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        window.Show();
        try
        {
            list.UpdateLayout();
            var panel = FindPanel(list);
            Assert.NotNull(panel);
            Assert.InRange(panel!.Children.Count, 5, 60);
            Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(0));

            list.FocusIndex(7_777, select: true);

            Assert.Equal(7_777, list.SelectedItem);
            Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(7_777));
            Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(100)); // the first screenful has been let go
            Assert.InRange(panel.Children.Count, 5, 60);
            Assert.True(panel.VerticalOffset > 0);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    private static VirtualizingTilePanel? FindPanel(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is VirtualizingTilePanel panel) return panel;
            if (FindPanel(child) is { } found) return found;
        }
        return null;
    }
}
