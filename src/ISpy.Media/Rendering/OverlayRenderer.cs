using ISpy.Core.Media;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ISpy.Media.Rendering;

/// <summary>
/// Draws the tile furniture - camera name, connection state - straight onto the swapchain.
/// </summary>
/// <remarks>
/// The video canvas is a child window, so WPF elements cannot be layered over it (the classic
/// "airspace" limitation). Rather than fight that, the labels are drawn with Direct2D onto the same
/// back buffer as the video, which also means they cannot lag or tear relative to the picture.
/// </remarks>
public sealed class OverlayRenderer : IDisposable
{
    private const float LabelHeight = 22f;
    private const float LabelPadding = 8f;

    private readonly ID2D1DeviceContext _context;
    private readonly IDWriteTextFormat _labelFormat;
    private readonly ID2D1SolidColorBrush _labelBackground;
    private readonly ID2D1SolidColorBrush _labelText;
    private readonly ID2D1SolidColorBrush _mutedText;
    private readonly ID2D1SolidColorBrush _selectionStroke;

    private ID2D1Bitmap1? _target;

    public OverlayRenderer(GpuDevice gpu)
    {
        using var dxgiDevice = gpu.Device.QueryInterface<IDXGIDevice>();
        using var factory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        using var d2dDevice = factory.CreateDevice(dxgiDevice);

        _context = d2dDevice.CreateDeviceContext(DeviceContextOptions.None);

        using var writeFactory = DWrite.DWriteCreateFactory<IDWriteFactory>();
        _labelFormat = writeFactory.CreateTextFormat(
            "Segoe UI", FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal, 12.5f);
        _labelFormat.ParagraphAlignment = ParagraphAlignment.Center;

        _labelBackground = _context.CreateSolidColorBrush(new Color4(0f, 0f, 0f, 0.55f));
        _labelText = _context.CreateSolidColorBrush(new Color4(0.94f, 0.95f, 0.97f, 1f));
        _mutedText = _context.CreateSolidColorBrush(new Color4(0.62f, 0.66f, 0.72f, 1f));
        _selectionStroke = _context.CreateSolidColorBrush(new Color4(0.30f, 0.55f, 1f, 1f));
    }

    /// <summary>Rebinds to the swapchain's back buffer. Called on creation and after every resize.</summary>
    public void BindTarget(IDXGISwapChain1 swapChain)
    {
        _target?.Dispose();
        _target = null;

        using var surface = swapChain.GetBuffer<IDXGISurface>(0);

        _target = _context.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1
        {
            BitmapOptions = BitmapOptions.Target | BitmapOptions.CannotDraw,
            PixelFormat = new Vortice.DCommon.PixelFormat(
                Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
        });

        _context.Target = _target;
    }

    /// <summary>
    /// Drops the back-buffer binding. ResizeBuffers fails while any view onto the back buffer is
    /// still outstanding, and the Direct2D target counts.
    /// </summary>
    public void BindTargetRelease()
    {
        _context.Target = null;
        _target?.Dispose();
        _target = null;
    }

    public void Begin() => _context.BeginDraw();

    public void End() => _context.EndDraw();

    /// <summary>Draws one tile's label strip and, when a stream is not playing, why.</summary>
    public void DrawTile(TileRect tile, string label, StreamState state, string? message, bool isSelected)
    {
        if (tile.Width <= 0 || tile.Height <= 0) return;

        var strip = new Rect(tile.X, tile.Bottom - LabelHeight, tile.Width, LabelHeight);

        _context.FillRectangle(strip, _labelBackground);

        var textRect = new Rect(
            strip.X + LabelPadding, strip.Y, strip.Width - LabelPadding * 2, strip.Height);

        _context.DrawText(label, _labelFormat, textRect, _labelText);

        // Anything other than a playing stream gets its status spelled out in the middle of the
        // tile, so a black rectangle is never left unexplained.
        if (state != StreamState.Playing)
        {
            var status = state switch
            {
                StreamState.Connecting => "Connecting…",
                StreamState.Reconnecting => message is null ? "Reconnecting…" : $"Reconnecting… {message}",
                StreamState.Failed => message ?? "Unavailable",
                _ => "Idle",
            };

            var centre = new Rect(tile.X + LabelPadding, tile.Y, tile.Width - LabelPadding * 2, tile.Height);
            _context.DrawText(status, _labelFormat, centre, _mutedText);
        }

        if (isSelected)
        {
            _context.DrawRectangle(
                new Rect(tile.X + 1, tile.Y + 1, tile.Width - 2, tile.Height - 2),
                _selectionStroke, 2f);
        }
    }

    public void Dispose()
    {
        _target?.Dispose();
        _selectionStroke.Dispose();
        _mutedText.Dispose();
        _labelText.Dispose();
        _labelBackground.Dispose();
        _labelFormat.Dispose();
        _context.Dispose();
    }
}
