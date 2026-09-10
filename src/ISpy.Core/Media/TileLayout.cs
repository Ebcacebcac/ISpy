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
        var (columns, rows) = Dimensions(layout);
        if (canvasWidth <= 0 || canvasHeight <= 0) return [];

        var usableWidth = Math.Max(0, canvasWidth - Gutter * (columns - 1));
        var usableHeight = Math.Max(0, canvasHeight - Gutter * (rows - 1));

        var baseWidth = usableWidth / columns;
        var baseHeight = usableHeight / rows;
        var extraColumns = usableWidth % columns;
        var extraRows = usableHeight % rows;

        var tiles = new List<TileRect>(columns * rows);
        var y = 0;

        for (var row = 0; row < rows; row++)
        {
            var height = baseHeight + (row < extraRows ? 1 : 0);
            var x = 0;

            for (var column = 0; column < columns; column++)
            {
                var width = baseWidth + (column < extraColumns ? 1 : 0);
                tiles.Add(new TileRect(x, y, width, height));
                x += width + Gutter;
            }

            y += height + Gutter;
        }

        return tiles;
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
