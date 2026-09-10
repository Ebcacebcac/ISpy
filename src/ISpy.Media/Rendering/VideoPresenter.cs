using System.Numerics;
using System.Runtime.InteropServices;
using ISpy.Core.Media;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;

namespace ISpy.Media.Rendering;

/// <summary>What to draw in one tile this frame.</summary>
public readonly record struct TileVisual(
    VideoSurface Surface,
    TileRect Tile,
    string Label,
    StreamState State,
    string? Message,
    bool IsSelected);

/// <summary>Constant buffer laid out to match TileConstants in the vertex shader.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TileConstants
{
    public Vector4 Rect;
    public Vector4 Reserved;
}

/// <summary>
/// Owns the swapchain and draws every visible tile into it.
/// </summary>
/// <remarks>
/// One swapchain for the whole canvas, not one per tile. Sixteen swapchains means sixteen Present
/// calls fighting over vsync, sixteen sets of back buffers, and tearing between tiles that were
/// meant to be one picture; a single swapchain draws the grid as one frame and presents it once.
/// </remarks>
public sealed class VideoPresenter : IDisposable
{
    private readonly GpuDevice _gpu;

    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _backBufferView;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11Buffer? _constants;
    private ID3D11SamplerState? _sampler;
    private OverlayRenderer? _overlay;

    private int _width;
    private int _height;

    public VideoPresenter(GpuDevice gpu, IntPtr windowHandle, int width, int height)
    {
        _gpu = gpu;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);

        CreateSwapChain(windowHandle);
        CreatePipeline();

        _overlay = new OverlayRenderer(gpu);
        _overlay.BindTarget(_swapChain!);
    }

    private void CreateSwapChain(IntPtr windowHandle)
    {
        using var dxgiDevice = _gpu.Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        _swapChain = factory.CreateSwapChainForHwnd(_gpu.Device, windowHandle, new SwapChainDescription1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),

            // Flip discard is what lets Windows composite the video without an extra full-screen
            // copy every frame; the older blt models cost real GPU time on a 16-tile canvas.
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
        });

        // The app draws its own fullscreen handling; letting DXGI hijack Alt+Enter causes the
        // window to enter exclusive fullscreen unexpectedly.
        factory.MakeWindowAssociation(windowHandle, WindowAssociationFlags.IgnoreAltEnter);

        CreateBackBufferView();
    }

    private void CreateBackBufferView()
    {
        using var backBuffer = _swapChain!.GetBuffer<Vortice.Direct3D11.ID3D11Texture2D>(0);
        _backBufferView = _gpu.Device.CreateRenderTargetView(backBuffer);
    }

    /// <summary>Size of the canvas in device pixels.</summary>
    public (int Width, int Height) Size => (_width, _height);

    private void CreatePipeline()
    {
        var vertexBlob = Compiler.Compile(
            Shaders.VertexShader, "main", "ISpy.VertexShader", "vs_5_0");
        var pixelBlob = Compiler.Compile(
            Shaders.PixelShader, "main", "ISpy.PixelShader", "ps_5_0");

        _vertexShader = _gpu.Device.CreateVertexShader(vertexBlob.Span);
        _pixelShader = _gpu.Device.CreatePixelShader(pixelBlob.Span);

        _constants = _gpu.Device.CreateBuffer(
            (uint)Marshal.SizeOf<TileConstants>(),
            BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write);

        _sampler = _gpu.Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,

            // Clamp, so bilinear sampling at the frame edge cannot wrap a row of pixels around to
            // the opposite side - visible as a bright seam along tile edges.
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxLOD = float.MaxValue,
        });
    }

    /// <summary>Resizes the swapchain to match the canvas. Cheap to call with an unchanged size.</summary>
    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        if (_swapChain is null || (width == _width && height == _height)) return;

        _width = width;
        _height = height;

        // Every view onto the back buffer must be released before ResizeBuffers will succeed,
        // the Direct2D overlay target included.
        _overlay?.BindTargetRelease();
        _backBufferView?.Dispose();
        _backBufferView = null;

        _gpu.Context.UnsetRenderTargets();
        _swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);

        CreateBackBufferView();
        _overlay?.BindTarget(_swapChain);
    }

    /// <summary>Draws every tile - picture then label - and presents the result.</summary>
    public void Present(IReadOnlyList<TileVisual> tiles, bool waitForVSync = true)
    {
        if (_swapChain is null || _backBufferView is null) return;

        var context = _gpu.Context;

        context.OMSetRenderTargets(_backBufferView);
        context.RSSetViewport(0, 0, _width, _height);
        context.ClearRenderTargetView(_backBufferView, new Vortice.Mathematics.Color4(0.04f, 0.05f, 0.07f, 1f));

        context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleStrip);
        context.VSSetShader(_vertexShader);
        context.PSSetShader(_pixelShader);
        context.PSSetSampler(0, _sampler);
        context.VSSetConstantBuffer(0, _constants);

        foreach (var visual in tiles)
        {
            if (!visual.Surface.TryGetDrawState(out var luma, out var chroma, out var width, out var height))
                continue;

            // Preserve the camera's aspect ratio inside its tile rather than stretching it.
            var target = TileLayout.Letterbox(visual.Tile, width, height);
            if (target.Width <= 0 || target.Height <= 0) continue;

            WriteConstants(target);

            context.PSSetShaderResource(0, luma);
            context.PSSetShaderResource(1, chroma);
            context.Draw(4, 0);
        }

        if (_overlay is not null)
        {
            _overlay.Begin();

            foreach (var visual in tiles)
            {
                _overlay.DrawTile(
                    visual.Tile, visual.Label, visual.State, visual.Message, visual.IsSelected);
            }

            _overlay.End();
        }

        // Present with vsync so we never render faster than the display; a camera grid gains
        // nothing from tearing at 500fps and it costs real power on a laptop.
        _swapChain.Present(waitForVSync ? 1u : 0u, PresentFlags.None);
    }

    /// <summary>Maps the tile rectangle from pixels into clip space, whose origin is the centre.</summary>
    private void WriteConstants(TileRect target)
    {
        var constants = new TileConstants
        {
            Rect = new Vector4(
                target.X * 2f / _width - 1f,
                1f - target.Y * 2f / _height,
                target.Width * 2f / _width,
                target.Height * 2f / _height),
        };

        var mapped = _gpu.Context.Map(_constants!, 0, Vortice.Direct3D11.MapMode.WriteDiscard);

        try
        {
            Marshal.StructureToPtr(constants, mapped.DataPointer, false);
        }
        finally
        {
            _gpu.Context.Unmap(_constants!, 0);
        }
    }

    public void Dispose()
    {
        _overlay?.Dispose();
        _sampler?.Dispose();
        _constants?.Dispose();
        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
        _backBufferView?.Dispose();
        _swapChain?.Dispose();
    }
}
