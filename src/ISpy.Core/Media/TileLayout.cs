namespace ISpy.Core.Media;

/// <summary>A tile's position within the video canvas, in device pixels.</summary>
public readonly record struct TileRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>The grid shapes offered in the UI.</summary>
public enum GridLayout
{
    Single = 1,
    TwoByTwo = 4,
    ThreeByThree = 9,
    FourByFour = 16,
}

/// <summary>
/// Works out where each tile goes. Pure geometry, so it is unit tested rather than eyeballed.
/// </summary>
public static class TileLayout
{
    /// <summary>Gap between tiles, in device pixels.</summary>
    public const int Gutter = 2;

    public static (int Columns, int Rows) Dimensions(GridLayout layout) => layout switch
    {
        GridLayout.Single => (1, 1),
        GridLayout.TwoByTwo => (2, 2),
        GridLayout.ThreeByThree => (3, 3),
        GridLayout.FourByFour => (4, 4),
        _ => (1, 1),
    };

    /// <summary>Smallest standard layout that fits <paramref name="cameraCount"/> cameras.</summary>
    public static GridLayout FitFor(int cameraCount) => cameraCount switch
    {
        <= 1 => GridLayout.Single,
        <= 4 => GridLayout.TwoByTwo,
        <= 9 => GridLayout.ThreeByThree,
        _ => GridLayout.FourByFour,
    };

    /// <summary>
    /// Divides the canvas into tiles. Remainder pixels from integer division are handed to the
    /// leading tiles one each, so the grid always fills the canvas exactly with no seam on the
    /// right or bottom edge.
    /// </summary>
    public static IReadOnlyList<TileRect> Compute(GridLayout layout, int canvasWidth, int canvasHeight)
    {
        var (side, _) = Dimensions(layout);
        return Compute(LayoutSpec.Uniform(layout.ToString(), side), canvasWidth, canvasHeight);
    }

    /// <summary>
    /// Lays a spec's tiles onto the canvas. A spanning tile's edges land exactly on the cell
    /// boundaries of the tiles around it, so a hero tile lines up with its neighbours to the pixel.
    /// </summary>
    public static IReadOnlyList<TileRect> Compute(LayoutSpec spec, int canvasWidth, int canvasHeight)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0) return [];

        var columnStarts = CellBoundaries(spec.Columns, canvasWidth);
        var rowStarts = CellBoundaries(spec.Rows, canvasHeight);

        return spec.Cells
            .Select(cell => new TileRect(
                columnStarts[cell.Column],
                rowStarts[cell.Row],
                columnStarts[cell.Right] - columnStarts[cell.Column] - Gutter,
                rowStarts[cell.Bottom] - rowStarts[cell.Row] - Gutter))
            .ToArray();
    }

    /// <summary>
    /// Start position of each of <paramref name="count"/> cells across <paramref name="total"/>
    /// pixels, plus a final sentinel one gutter past the end - which is what lets a span's width be
    /// computed as a simple difference of boundaries.
    /// </summary>
    private static int[] CellBoundaries(int count, int total)
    {
        var usable = Math.Max(0, total - Gutter * (count - 1));
        var baseSize = usable / count;
        var extra = usable % count;

        var starts = new int[count + 1];
        var position = 0;

        for (var i = 0; i < count; i++)
        {
            starts[i] = position;
            position += baseSize + (i < extra ? 1 : 0) + Gutter;
        }

        starts[count] = position;
        return starts;
    }

    /// <summary>
    /// Fits a video of <paramref name="sourceWidth"/>x<paramref name="sourceHeight"/> inside a tile
    /// without distorting it, centred, letterboxing whatever is left over.
    /// </summary>
    public static TileRect Letterbox(TileRect tile, int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || tile.Width <= 0 || tile.Height <= 0)
            return tile with { Width = 0, Height = 0 };

        var scale = Math.Min((double)tile.Width / sourceWidth, (double)tile.Height / sourceHeight);
        var width = (int)Math.Round(sourceWidth * scale);
        var height = (int)Math.Round(sourceHeight * scale);

        return new TileRect(
            tile.X + (tile.Width - width) / 2,
            tile.Y + (tile.Height - height) / 2,
            width,
            height);
    }

    /// <summary>Index of the tile containing a point, or null when the point is in a gutter.</summary>
    public static int? HitTest(IReadOnlyList<TileRect> tiles, int x, int y)
    {
        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            if (x >= tile.X && x < tile.Right && y >= tile.Y && y < tile.Bottom) return i;
        }

        return null;
    }
}
