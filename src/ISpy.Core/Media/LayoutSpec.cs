using System.Text.Json;

namespace ISpy.Core.Media;

/// <summary>One tile's place on the layout grid, in cells. Spans let a tile cover several cells.</summary>
public sealed record LayoutCell(int Column, int Row, int ColumnSpan = 1, int RowSpan = 1)
{
    public int Right => Column + ColumnSpan;
    public int Bottom => Row + RowSpan;

    public bool Overlaps(LayoutCell other) =>
        Column < other.Right && other.Column < Right &&
        Row < other.Bottom && other.Row < Bottom;

    public bool Contains(int column, int row) =>
        column >= Column && column < Right && row >= Row && row < Bottom;
}

/// <summary>
/// A grid arrangement: an R-by-C cell grid and the tiles placed on it. Uniform grids are the
/// degenerate case where every tile is one cell; the classic CCTV "hero" layouts put one large
/// tile in the corner with small ones around it.
/// </summary>
/// <remarks>
/// Cameras are assigned to tiles in reading order of each tile's top-left corner - row first, then
/// column - so the first camera lands in the hero tile of a hero layout. Keeping that rule fixed
/// (rather than "largest first" or author order) means a user can predict where cameras go in a
/// layout they just designed.
/// </remarks>
public sealed record LayoutSpec
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public required string Name { get; init; }
    public required int Columns { get; init; }
    public required int Rows { get; init; }
    public required IReadOnlyList<LayoutCell> Cells { get; init; }

    /// <summary>True for the presets that ship with the app; those are never saved or deleted.</summary>
    public bool IsBuiltIn { get; init; }

    public int TileCount => Cells.Count;

    /// <summary>Cells in camera-assignment order: reading order of top-left corners.</summary>
    public LayoutSpec Sorted() => this with
    {
        Cells = Cells.OrderBy(cell => cell.Row).ThenBy(cell => cell.Column).ToArray(),
    };

    /// <summary>Problems that make the layout undrawable. Empty means valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (Columns is < 1 or > 12 || Rows is < 1 or > 12)
            problems.Add("The grid must be between 1x1 and 12x12.");

        if (Cells.Count == 0) problems.Add("The layout has no tiles.");

        foreach (var cell in Cells)
        {
            if (cell.Column < 0 || cell.Row < 0 || cell.ColumnSpan < 1 || cell.RowSpan < 1 ||
                cell.Right > Columns || cell.Bottom > Rows)
            {
                problems.Add($"A tile at column {cell.Column}, row {cell.Row} falls outside the grid.");
            }
        }

        for (var i = 0; i < Cells.Count; i++)
        {
            for (var j = i + 1; j < Cells.Count; j++)
            {
                if (Cells[i].Overlaps(Cells[j]))
                {
                    problems.Add("Two tiles overlap.");
                    return problems;
                }
            }
        }

        return problems;
    }

    // ---- presets ---------------------------------------------------------

    public static LayoutSpec Uniform(string name, int side) => new LayoutSpec
    {
        Name = name,
        Columns = side,
        Rows = side,
        IsBuiltIn = true,
        Cells = Enumerable.Range(0, side * side)
            .Select(i => new LayoutCell(i % side, i / side))
            .ToArray(),
    }.Sorted();

    /// <summary>A hero tile of (side-1) cells square in the top-left, small tiles down the right and along the bottom.</summary>
    public static LayoutSpec Hero(string name, int side)
    {
        var cells = new List<LayoutCell> { new(0, 0, side - 1, side - 1) };

        for (var row = 0; row < side - 1; row++) cells.Add(new LayoutCell(side - 1, row));
        for (var column = 0; column < side; column++) cells.Add(new LayoutCell(column, side - 1));

        return new LayoutSpec
        {
            Name = name,
            Columns = side,
            Rows = side,
            IsBuiltIn = true,
            Cells = cells,
        }.Sorted();
    }

    public static readonly LayoutSpec Single = Uniform("1", 1);
    public static readonly LayoutSpec TwoByTwo = Uniform("2 x 2", 2);
    public static readonly LayoutSpec ThreeByThree = Uniform("3 x 3", 3);
    public static readonly LayoutSpec FourByFour = Uniform("4 x 4", 4);
    public static readonly LayoutSpec OnePlusFive = Hero("1 + 5", 3);
    public static readonly LayoutSpec OnePlusSeven = Hero("1 + 7", 4);

    public static readonly IReadOnlyList<LayoutSpec> BuiltIn =
        [Single, TwoByTwo, OnePlusFive, ThreeByThree, OnePlusSeven, FourByFour];

    /// <summary>
    /// The preset that suits a camera count. Hero layouts are preferred where they fit exactly-ish,
    /// because a wall with a focus tile is what people actually watch.
    /// </summary>
    public static LayoutSpec FitFor(int cameraCount) => cameraCount switch
    {
        <= 1 => Single,
        <= 4 => TwoByTwo,
        <= 6 => OnePlusFive,
        <= 8 => OnePlusSeven,
        <= 9 => ThreeByThree,
        _ => FourByFour,
    };

    // ---- persistence -----------------------------------------------------

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static LayoutSpec? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            var spec = JsonSerializer.Deserialize<LayoutSpec>(json, JsonOptions);
            return spec is null || spec.Validate().Count > 0 ? null : spec.Sorted();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string ListToJson(IEnumerable<LayoutSpec> specs) =>
        JsonSerializer.Serialize(specs.ToArray(), JsonOptions);

    public static List<LayoutSpec> ListFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            var specs = JsonSerializer.Deserialize<LayoutSpec[]>(json, JsonOptions) ?? [];
            return specs.Where(spec => spec.Validate().Count == 0)
                .Select(spec => spec.Sorted())
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
