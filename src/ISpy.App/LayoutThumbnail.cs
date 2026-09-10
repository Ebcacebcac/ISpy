using System.Windows;
using System.Windows.Media;
using ISpy.Core.Media;

namespace ISpy.App;

/// <summary>
/// A miniature preview of a layout: its tiles drawn to scale, the way the vendor clients show
/// their window divisions. Reuses the same geometry the live grid renders with, so the preview is
/// the layout, not an approximation of it.
/// </summary>
public sealed class LayoutThumbnail : FrameworkElement
{
    private static readonly Brush TileFill = new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x3A));
    private static readonly Brush HeroFill = new SolidColorBrush(Color.FromRgb(0x33, 0x46, 0x6E));
    private static readonly Pen TileStroke = new(new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x1A)), 1);

    static LayoutThumbnail()
    {
        TileFill.Freeze();
        HeroFill.Freeze();
        TileStroke.Freeze();
    }

    public static readonly DependencyProperty SpecProperty = DependencyProperty.Register(
        nameof(Spec), typeof(LayoutSpec), typeof(LayoutThumbnail),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public LayoutSpec? Spec
    {
        get => (LayoutSpec?)GetValue(SpecProperty);
        set => SetValue(SpecProperty, value);
    }

    protected override void OnRender(DrawingContext context)
    {
        if (Spec is null || ActualWidth <= 0 || ActualHeight <= 0) return;

        var tiles = TileLayout.Compute(Spec, (int)ActualWidth, (int)ActualHeight);

        foreach (var tile in tiles)
        {
            // Spanning tiles get the accent-leaning fill so hero layouts read at a glance.
            var isHero = tiles.Count > 1 &&
                         tile.Width * tile.Height > 1.5 * tiles.Min(t => t.Width * t.Height);

            context.DrawRoundedRectangle(
                isHero ? HeroFill : TileFill, TileStroke,
                new Rect(tile.X, tile.Y, Math.Max(1, tile.Width), Math.Max(1, tile.Height)),
                1.5, 1.5);
        }
    }
}
