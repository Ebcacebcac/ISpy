using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ISpy.Core.Media;

namespace ISpy.App;

/// <summary>
/// The layout designer: a cell grid where dragging across cells merges them into one large tile,
/// and clicking a large tile splits it back. Everything else - which camera lands where - follows
/// from reading order, previewed as numbers on the tiles.
/// </summary>
public partial class LayoutEditorWindow : Window
{
    private static readonly Brush CellFill = new SolidColorBrush(Color.FromRgb(0x1B, 0x1E, 0x26));
    private static readonly Brush CellStroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x3A));
    private static readonly Brush MergedFill = new SolidColorBrush(Color.FromRgb(0x24, 0x33, 0x52));
    private static readonly Brush AccentStroke = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));
    private static readonly Brush SelectionFill = new SolidColorBrush(Color.FromArgb(0x50, 0x4C, 0x8D, 0xFF));

    /// <summary>Multi-cell tiles. Cells not covered by one of these are implicit 1x1 tiles.</summary>
    private readonly List<LayoutCell> _merged = [];

    private (int Column, int Row)? _dragAnchor;
    private Border? _selectionPreview;

    public LayoutEditorWindow(LayoutSpec? existing = null)
    {
        InitializeComponent();

        foreach (var size in Enumerable.Range(1, 6))
        {
            ColumnsBox.Items.Add(size);
            RowsBox.Items.Add(size);
        }

        if (existing is not null)
        {
            NameBox.Text = existing.Name;
            ColumnsBox.SelectedItem = existing.Columns;
            RowsBox.SelectedItem = existing.Rows;
            _merged.AddRange(existing.Cells.Where(c => c.ColumnSpan > 1 || c.RowSpan > 1));
        }
        else
        {
            ColumnsBox.SelectedItem = 4;
            RowsBox.SelectedItem = 4;
        }

        RebuildBoard();
    }

    /// <summary>The saved layout, set when the dialog closes with success.</summary>
    public LayoutSpec? Result { get; private set; }

    private int Columns => ColumnsBox.SelectedItem as int? ?? 4;
    private int Rows => RowsBox.SelectedItem as int? ?? 4;

    /// <summary>The spec as currently drawn: merged tiles plus every uncovered cell as a 1x1.</summary>
    private LayoutSpec CurrentSpec()
    {
        var cells = new List<LayoutCell>(_merged);

        for (var row = 0; row < Rows; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                if (!_merged.Any(m => m.Contains(column, row)))
                    cells.Add(new LayoutCell(column, row));
            }
        }

        return new LayoutSpec
        {
            Name = NameBox.Text.Trim(),
            Columns = Columns,
            Rows = Rows,
            Cells = cells,
        }.Sorted();
    }

    private void OnGridSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Board is null) return;

        // Merged tiles that no longer fit the resized grid are dropped rather than clipped.
        _merged.RemoveAll(m => m.Right > Columns || m.Bottom > Rows);
        RebuildBoard();
    }

    private void RebuildBoard()
    {
        Board.Children.Clear();
        Board.ColumnDefinitions.Clear();
        Board.RowDefinitions.Clear();

        for (var i = 0; i < Columns; i++)
            Board.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < Rows; i++)
            Board.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var spec = CurrentSpec();

        for (var index = 0; index < spec.Cells.Count; index++)
        {
            var cell = spec.Cells[index];
            var isMerged = cell.ColumnSpan > 1 || cell.RowSpan > 1;

            var tile = new Border
            {
                Background = isMerged ? MergedFill : CellFill,
                BorderBrush = isMerged ? AccentStroke : CellStroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(2),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = (index + 1).ToString(),
                    Foreground = Foreground,
                    Opacity = isMerged ? 1.0 : 0.55,
                    FontSize = isMerged ? 22 : 13,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            Grid.SetColumn(tile, cell.Column);
            Grid.SetRow(tile, cell.Row);
            Grid.SetColumnSpan(tile, cell.ColumnSpan);
            Grid.SetRowSpan(tile, cell.RowSpan);
            Board.Children.Add(tile);
        }

        StatusText.Text = $"{spec.TileCount} tiles";
    }

    // ---- drag to merge, click to split -----------------------------------

    private (int Column, int Row)? CellAt(Point position)
    {
        if (Board.ActualWidth <= 0 || Board.ActualHeight <= 0) return null;

        var column = (int)(position.X / (Board.ActualWidth / Columns));
        var row = (int)(position.Y / (Board.ActualHeight / Rows));

        if (column < 0 || row < 0 || column >= Columns || row >= Rows) return null;
        return (column, row);
    }

    private static LayoutCell SelectionRect((int Column, int Row) a, (int Column, int Row) b)
    {
        var column = Math.Min(a.Column, b.Column);
        var row = Math.Min(a.Row, b.Row);

        return new LayoutCell(
            column, row,
            Math.Max(a.Column, b.Column) - column + 1,
            Math.Max(a.Row, b.Row) - row + 1);
    }

    private void OnBoardMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragAnchor = CellAt(e.GetPosition(Board));
        if (_dragAnchor is null) return;

        Board.CaptureMouse();
        UpdateSelectionPreview(_dragAnchor.Value, _dragAnchor.Value);
    }

    private void OnBoardMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragAnchor is not { } anchor || !Board.IsMouseCaptured) return;
        if (CellAt(e.GetPosition(Board)) is not { } current) return;

        UpdateSelectionPreview(anchor, current);
    }

    private void OnBoardMouseUp(object sender, MouseButtonEventArgs e)
    {
        Board.ReleaseMouseCapture();
        ClearSelectionPreview();

        if (_dragAnchor is not { } anchor) return;
        _dragAnchor = null;

        if (CellAt(e.GetPosition(Board)) is not { } current) return;

        var selection = SelectionRect(anchor, current);

        if (selection.ColumnSpan == 1 && selection.RowSpan == 1)
        {
            // A plain click: splitting an existing large tile back into cells.
            _merged.RemoveAll(m => m.Contains(selection.Column, selection.Row));
        }
        else
        {
            // A drag: the new tile absorbs anything it overlaps.
            _merged.RemoveAll(m => m.Overlaps(selection));
            _merged.Add(selection);
        }

        RebuildBoard();
    }

    private void UpdateSelectionPreview((int Column, int Row) a, (int Column, int Row) b)
    {
        ClearSelectionPreview();

        var rect = SelectionRect(a, b);

        _selectionPreview = new Border
        {
            Background = SelectionFill,
            BorderBrush = AccentStroke,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(2),
            IsHitTestVisible = false,
        };

        Grid.SetColumn(_selectionPreview, rect.Column);
        Grid.SetRow(_selectionPreview, rect.Row);
        Grid.SetColumnSpan(_selectionPreview, rect.ColumnSpan);
        Grid.SetRowSpan(_selectionPreview, rect.RowSpan);
        Board.Children.Add(_selectionPreview);
    }

    private void ClearSelectionPreview()
    {
        if (_selectionPreview is null) return;

        Board.Children.Remove(_selectionPreview);
        _selectionPreview = null;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var spec = CurrentSpec();

        if (spec.Name.Length == 0)
        {
            StatusText.Text = "Give the layout a name first.";
            NameBox.Focus();
            return;
        }

        var problems = spec.Validate();
        if (problems.Count > 0)
        {
            StatusText.Text = problems[0];
            return;
        }

        Result = spec;
        DialogResult = true;
    }
}
