using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ISpy.Core.Media;
using ISpy.Core.Model;

namespace ISpy.App;

/// <summary>
/// The scrub bar: a day of footage drawn as coloured spans, with hour ticks and a playhead.
/// </summary>
/// <remarks>
/// Drawn directly rather than assembled from WPF shapes. A day can contain hundreds of segments and
/// giving each one an element makes the bar stutter while dragging; one render pass does not.
/// </remarks>
public sealed class TimelineBar : FrameworkElement
{
    private static readonly Brush Background = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1E));
    private static readonly Brush Continuous = new SolidColorBrush(Color.FromRgb(0x3B, 0x6B, 0xC4));
    private static readonly Brush Motion = new SolidColorBrush(Color.FromRgb(0xC9, 0x8A, 0x2B));
    private static readonly Brush Alarm = new SolidColorBrush(Color.FromRgb(0xC4, 0x4B, 0x4B));
    private static readonly Brush Manual = new SolidColorBrush(Color.FromRgb(0x4B, 0xA5, 0x7A));
    private static readonly Brush TickBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x4C));
    private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA3));
    private static readonly Pen PlayheadPen = new(new SolidColorBrush(Colors.White), 1.5);

    static TimelineBar()
    {
        foreach (var brush in new[] { Background, Continuous, Motion, Alarm, Manual, TickBrush, LabelBrush })
            brush.Freeze();

        PlayheadPen.Freeze();
    }

    private RecordingTimeline? _timeline;
    private DateTimeOffset? _playhead;

    public TimelineBar()
    {
        Height = 56;
        Cursor = Cursors.Hand;
    }

    /// <summary>Raised when the user clicks or drags to a moment.</summary>
    public event Action<DateTimeOffset>? Scrubbed;

    public void SetTimeline(RecordingTimeline? timeline)
    {
        _timeline = timeline;
        InvalidateVisual();
    }

    public void SetPlayhead(DateTimeOffset? moment)
    {
        _playhead = moment;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        context.DrawRectangle(Background, null, new Rect(0, 0, width, height));

        if (_timeline is null)
        {
            DrawCentredText(context, "Pick a camera and a day", width, height);
            return;
        }

        DrawHourTicks(context, width, height);
        DrawSegments(context, width, height);

        if (_timeline.IsEmpty) DrawCentredText(context, "No recordings on this day", width, height);

        DrawPlayhead(context, width, height);
    }

    private void DrawHourTicks(DrawingContext context, double width, double height)
    {
        var window = _timeline!.WindowDuration;

        // Aim for a tick roughly every 90 pixels, snapped to a sensible unit.
        var targetTicks = Math.Max(2, (int)(width / 90));
        var step = TimelineTicks.ChooseStep(window, targetTicks);

        for (var at = _timeline.WindowStart; at < _timeline.WindowEnd; at += step)
        {
            var x = _timeline.PositionOf(at) * width;
            context.DrawRectangle(TickBrush, null, new Rect(x, 0, 1, height));

            var label = new FormattedText(
                at.ToLocalTime().ToString(step < TimeSpan.FromHours(1) ? "HH:mm" : "HH:mm", CultureInfo.CurrentCulture),
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 10, LabelBrush, 1.0);

            context.DrawText(label, new Point(x + 3, height - 14));
        }
    }

    private void DrawSegments(DrawingContext context, double width, double height)
    {
        const double top = 8;
        var barHeight = height - 24;

        foreach (var segment in _timeline!.Segments)
        {
            var left = _timeline.PositionOf(segment.Start) * width;
            var right = _timeline.PositionOf(segment.End) * width;

            // A minute of footage on a day-wide bar is a fraction of a pixel; give every segment at
            // least one so short motion clips are still visible and clickable.
            var segmentWidth = Math.Max(1.5, right - left);

            context.DrawRectangle(
                BrushFor(segment.Trigger), null, new Rect(left, top, segmentWidth, barHeight));
        }
    }

    private void DrawPlayhead(DrawingContext context, double width, double height)
    {
        if (_playhead is not { } moment) return;

        var x = _timeline!.PositionOf(moment) * width;
        context.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, height));
    }

    private void DrawCentredText(DrawingContext context, string text, double width, double height)
    {
        var formatted = new FormattedText(
            text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 12, LabelBrush, 1.0);

        context.DrawText(formatted, new Point((width - formatted.Width) / 2, (height - formatted.Height) / 2));
    }

    private static Brush BrushFor(RecordingTrigger trigger) => trigger switch
    {
        RecordingTrigger.Motion => Motion,
        RecordingTrigger.Alarm => Alarm,
        RecordingTrigger.Manual => Manual,
        _ => Continuous,
    };

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CaptureMouse();
        ScrubTo(e.GetPosition(this).X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured) ScrubTo(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
    }

    private void ScrubTo(double x)
    {
        if (_timeline is null || ActualWidth <= 0) return;

        var moment = _timeline.MomentAt(x / ActualWidth);

        // Land on footage rather than in a gap, so a click always plays something.
        Scrubbed?.Invoke(_timeline.SnapToFootage(moment) ?? moment);
    }
}
