using ISpy.Core.Media;
using Xunit;

namespace ISpy.Tests;

public class LayoutSpecTests
{
    [Fact]
    public void The_one_plus_seven_layout_matches_the_classic_hero_shape()
    {
        var spec = LayoutSpec.OnePlusSeven;

        Assert.Equal(8, spec.TileCount);
        Assert.Empty(spec.Validate());

        // First tile in assignment order is the 3x3 hero at the top-left.
        var hero = spec.Cells[0];
        Assert.Equal((0, 0, 3, 3), (hero.Column, hero.Row, hero.ColumnSpan, hero.RowSpan));

        // The rest are single cells down the right edge and along the bottom.
        Assert.All(spec.Cells.Skip(1), cell => Assert.Equal((1, 1), (cell.ColumnSpan, cell.RowSpan)));
    }

    [Fact]
    public void The_one_plus_five_layout_has_six_tiles() =>
        Assert.Equal(6, LayoutSpec.OnePlusFive.TileCount);

    [Fact]
    public void Every_builtin_layout_is_valid_and_marked_builtin()
    {
        Assert.All(LayoutSpec.BuiltIn, spec =>
        {
            Assert.Empty(spec.Validate());
            Assert.True(spec.IsBuiltIn);
        });
    }

    [Fact]
    public void Cameras_are_assigned_in_reading_order_of_tile_corners()
    {
        var spec = new LayoutSpec
        {
            Name = "custom",
            Columns = 2,
            Rows = 2,
            Cells = new[]
            {
                new LayoutCell(1, 1),
                new LayoutCell(0, 0),
                new LayoutCell(1, 0),
                new LayoutCell(0, 1),
            },
        }.Sorted();

        Assert.Equal(
            [(0, 0), (1, 0), (0, 1), (1, 1)],
            spec.Cells.Select(cell => (cell.Column, cell.Row)));
    }

    [Fact]
    public void Overlapping_tiles_are_rejected()
    {
        var spec = new LayoutSpec
        {
            Name = "bad",
            Columns = 2,
            Rows = 2,
            Cells = [new LayoutCell(0, 0, 2, 2), new LayoutCell(1, 1)],
        };

        Assert.Contains(spec.Validate(), problem => problem.Contains("overlap"));
    }

    [Fact]
    public void Tiles_outside_the_grid_are_rejected()
    {
        var spec = new LayoutSpec
        {
            Name = "bad",
            Columns = 2,
            Rows = 2,
            Cells = [new LayoutCell(1, 1, 2, 1)],
        };

        Assert.Contains(spec.Validate(), problem => problem.Contains("outside"));
    }

    [Fact]
    public void A_layout_round_trips_through_json()
    {
        var spec = new LayoutSpec
        {
            Name = "Front of house",
            Columns = 3,
            Rows = 2,
            Cells = [new LayoutCell(0, 0, 2, 2), new LayoutCell(2, 0), new LayoutCell(2, 1)],
        };

        var restored = LayoutSpec.FromJson(spec.ToJson());

        Assert.NotNull(restored);
        Assert.Equal("Front of house", restored.Name);
        Assert.Equal(3, restored.TileCount);
        Assert.Equal(spec.Sorted().Cells, restored.Cells);
    }

    [Fact]
    public void Corrupt_json_reads_as_null_rather_than_throwing()
    {
        Assert.Null(LayoutSpec.FromJson("not json"));
        Assert.Null(LayoutSpec.FromJson(""));
        Assert.Null(LayoutSpec.FromJson(null));
        Assert.Null(LayoutSpec.FromJson("{\"Name\":\"x\",\"Columns\":2,\"Rows\":2,\"Cells\":[]}"));
    }

    [Fact]
    public void A_list_of_custom_layouts_round_trips_and_drops_invalid_entries()
    {
        var good = new LayoutSpec
        {
            Name = "good",
            Columns = 2,
            Rows = 1,
            Cells = [new LayoutCell(0, 0), new LayoutCell(1, 0)],
        };

        var restored = LayoutSpec.ListFromJson(LayoutSpec.ListToJson([good]));
        Assert.Single(restored);

        Assert.Empty(LayoutSpec.ListFromJson("broken"));
        Assert.Empty(LayoutSpec.ListFromJson(null));
    }

    [Theory]
    [InlineData(1, "1")]
    [InlineData(4, "2 x 2")]
    [InlineData(6, "1 + 5")]
    [InlineData(8, "1 + 7")]
    [InlineData(9, "3 x 3")]
    [InlineData(14, "4 x 4")]
    public void The_fitting_preset_prefers_hero_layouts(int cameras, string expected) =>
        Assert.Equal(expected, LayoutSpec.FitFor(cameras).Name);
}

public class SpecGeometryTests
{
    [Fact]
    public void A_hero_tile_lines_up_exactly_with_its_neighbours()
    {
        var tiles = TileLayout.Compute(LayoutSpec.OnePlusSeven, 1001, 803);

        var hero = tiles[0];
        var topRight = tiles[1];   // (3,0)
        var bottomFirst = tiles[4]; // (0,3)

        // The hero's right edge plus the gutter is the small column's left edge.
        Assert.Equal(topRight.X, hero.Right + TileLayout.Gutter);
        Assert.Equal(bottomFirst.Y, hero.Bottom + TileLayout.Gutter);

        // And the far edges still land exactly on the canvas boundary.
        Assert.Equal(1001, tiles.Max(t => t.Right));
        Assert.Equal(803, tiles.Max(t => t.Bottom));
    }

    [Fact]
    public void Spec_and_enum_uniform_grids_agree()
    {
        var viaSpec = TileLayout.Compute(LayoutSpec.ThreeByThree, 1000, 700);
        var viaEnum = TileLayout.Compute(GridLayout.ThreeByThree, 1000, 700);

        Assert.Equal(viaEnum, viaSpec);
    }

    [Fact]
    public void A_spanning_tile_is_wider_than_its_single_cell_neighbours()
    {
        var tiles = TileLayout.Compute(LayoutSpec.OnePlusFive, 900, 900);

        Assert.True(tiles[0].Width > tiles[1].Width * 2 - 2);
        Assert.True(tiles[0].Height > tiles[1].Height * 2 - 2);
    }

    [Fact]
    public void An_empty_canvas_yields_no_tiles() =>
        Assert.Empty(TileLayout.Compute(LayoutSpec.OnePlusSeven, 0, 500));

    [Fact]
    public void Hit_testing_finds_the_hero_across_its_whole_area()
    {
        var tiles = TileLayout.Compute(LayoutSpec.OnePlusSeven, 800, 800);

        Assert.Equal(0, TileLayout.HitTest(tiles, 10, 10));
        Assert.Equal(0, TileLayout.HitTest(tiles, 500, 500));
        Assert.NotEqual(0, TileLayout.HitTest(tiles, 790, 10));
    }
}

public class TileOrderTests
{
    [Fact]
    public void Saved_order_is_restored()
    {
        var items = new[] { "a", "b", "c" };

        var ordered = TileOrder.Apply(items, item => item, ["c", "a", "b"]);

        Assert.Equal(["c", "a", "b"], ordered);
    }

    [Fact]
    public void New_cameras_append_after_the_arranged_ones()
    {
        var items = new[] { "new1", "a", "new2", "b" };

        var ordered = TileOrder.Apply(items, item => item, ["b", "a"]);

        Assert.Equal(["b", "a", "new1", "new2"], ordered);
    }

    [Fact]
    public void Saved_keys_for_removed_cameras_are_ignored()
    {
        var items = new[] { "a", "b" };

        var ordered = TileOrder.Apply(items, item => item, ["gone", "b", "a"]);

        Assert.Equal(["b", "a"], ordered);
    }

    [Fact]
    public void No_saved_order_leaves_items_untouched()
    {
        var items = new[] { "a", "b" };

        Assert.Equal(items, TileOrder.Apply(items, item => item, null));
        Assert.Equal(items, TileOrder.Apply(items, item => item, []));
    }
}
