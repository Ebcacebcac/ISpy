using System.Diagnostics.CodeAnalysis;
using FFmpeg.AutoGen;
using ISpy.Core.Media;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11ShaderResourceView = Vortice.Direct3D11.ID3D11ShaderResourceView;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace ISpy.Media.Rendering;

/// <summary>
/// One camera's picture on the GPU: an NV12 texture plus the two views the shader samples.
/// </summary>
/// <remarks>
/// Frames are copied into this rather than sampled from the decoder's own surface. Decoder textures
/// are allocated as a decode-only array and are reused for reference frames the moment they are
/// released, so sampling one directly races the decoder and tears. One GPU-side copy into a texture
/// we own is the cheap way to make the frame ours.
/// </remarks>
public sealed unsafe class VideoSurface : IDisposable
{
    /// <summary>swscale's SWS_BILINEAR. The binding generator does not surface the flag constants.</summary>
    private const int BilinearScaling = 2;


    private readonly GpuDevice _gpu;

    private ID3D11Texture2D? _texture;
    private ID3D11Texture2D? _staging;
    private ID3D11ShaderResourceView? _luma;
    private ID3D11ShaderResourceView? _chroma;
    private SwsContext* _scaler;
    private AVPixelFormat _scalerSource = AVPixelFormat.AV_PIX_FMT_NONE;

    /// <summary>
    /// Guards the textures and their views. D3D itself is made thread-safe by multithread
    /// protection, but that does not stop the renderer reading these fields while the decode thread
    /// is replacing them after a resolution change - which would draw from a disposed view.
    /// </summary>
    private readonly Lock _gate = new();

    public VideoSurface(GpuDevice gpu) => _gpu = gpu;

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>True once a frame has been received, so the renderer knows there is something to draw.</summary>
    public bool HasContent { get; private set; }

    public ID3D11ShaderResourceView? Luma => _luma;
    public ID3D11ShaderResourceView? Chroma => _chroma;

    /// <summary>
    /// Uploads a still image as the tile's contents, used to show the last known frame while the
    /// live stream is still connecting. Overwritten by the first real frame.
    /// </summary>
    public void SetPoster(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width < 2 || height < 2) return;

        lock (_gate)
        {
            SetPosterCore(bgra, width, height);
        }
    }

    private void SetPosterCore(ReadOnlySpan<byte> bgra, int width, int height)
    {
        EnsureTextures(width, height);
        if (_texture is null || _staging is null) return;

        var mapped = _gpu.Context.Map(_staging, 0, Vortice.Direct3D11.MapMode.Write, MapFlags.None);

        try
        {
            var destination = new Span<byte>(
                (void*)mapped.DataPointer, (int)mapped.RowPitch * height * 3 / 2);

            ColorConversion.BgraToNv12(bgra, destination, width, height, (int)mapped.RowPitch);
        }
        finally
        {
            _gpu.Context.Unmap(_staging, 0);
        }

        _gpu.Context.CopyResource(_texture, _staging);
        HasContent = true;
    }

    /// <summary>
    /// Reads the current picture back as BGRA so it can be saved as a thumbnail. A GPU readback
    /// stalls the pipeline, so this is only ever called on shutdown.
    /// </summary>
    public byte[]? ReadBack()
    {
        lock (_gate) return ReadBackCore();
    }

    private byte[]? ReadBackCore()
    {
        if (_texture is null || Width < 2 || Height < 2) return null;

        using var readable = _gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });

        _gpu.Context.CopyResource(readable, _texture);

        var mapped = _gpu.Context.Map(readable, 0, Vortice.Direct3D11.MapMode.Read, MapFlags.None);

        try
        {
            var source = new ReadOnlySpan<byte>(
                (void*)mapped.DataPointer, (int)mapped.RowPitch * Height * 3 / 2);

            return ColorConversion.Nv12ToBgra(source, Width, Height, (int)mapped.RowPitch);
        }
        finally
        {
            _gpu.Context.Unmap(readable, 0);
        }
    }

    /// <summary>Copies a decoded frame in. Called on the decode thread while the frame is valid.</summary>
    public void Update(AVFrame* frame, VideoFrameInfo info)
    {
        if (info.Width <= 0 || info.Height <= 0) return;

        lock (_gate)
        {
            EnsureTextures(info.Width, info.Height);
            if (_texture is null) return;

            if (info.IsHardware)
            {
                CopyFromDecoderTexture(frame);
            }
            else
            {
                CopyFromSystemMemory(frame, info);
            }

            HasContent = true;
        }
    }

    /// <summary>
    /// Hands the renderer everything it needs for one draw, taken atomically. Returns false when
    /// there is nothing to draw yet.
    /// </summary>
    public bool TryGetDrawState(
        [NotNullWhen(true)] out ID3D11ShaderResourceView? luma,
        [NotNullWhen(true)] out ID3D11ShaderResourceView? chroma,
        out int width,
        out int height)
    {
        lock (_gate)
        {
            luma = _luma;
            chroma = _chroma;
            width = Width;
            height = Height;

            return HasContent && luma is not null && chroma is not null;
        }
    }

    /// <summary>
    /// The GPU path: the decoded surface is a slice of the decoder's texture array, identified by
    /// data[0] (the texture) and data[1] (the array index).
    /// </summary>
    private void CopyFromDecoderTexture(AVFrame* frame)
    {
        var texturePointer = (IntPtr)frame->data[0];
        if (texturePointer == IntPtr.Zero) return;

        var arraySlice = (uint)(nint)frame->data[1];

        using var decoded = new ID3D11Texture2D(texturePointer);
        decoded.AddRef();

        // Decoder surfaces are allocated at coded size - the frame height rounded up for the
        // codec's macroblock alignment, e.g. 1088 rows for 1080p video. Copying the whole
        // subresource would write past the end of our display-sized texture, which is an invalid
        // copy the driver answers with a device removal, taking the app down with it.
        var region = new Vortice.Mathematics.Box(0, 0, 0, Width, Height, 1);

        _gpu.Context.CopySubresourceRegion(
            _texture!, 0, 0, 0, 0,
            decoded, arraySlice, region);
    }

    /// <summary>
    /// The fallback path. Software decoding hands back YUV420P (or whatever the codec produces), so
    /// it is converted to NV12 straight into a mapped staging texture and copied up.
    /// </summary>
    private void CopyFromSystemMemory(AVFrame* frame, VideoFrameInfo info)
    {
        if (_staging is null) return;

        EnsureScaler(info);
        if (_scaler is null) return;

        var mapped = _gpu.Context.Map(_staging, 0, Vortice.Direct3D11.MapMode.Write, MapFlags.None);

        try
        {
            // NV12 in one allocation: the chroma plane follows luma, both at the same row pitch.
            var destination = new byte_ptrArray4();
            destination[0] = (byte*)mapped.DataPointer;
            destination[1] = (byte*)mapped.DataPointer + mapped.RowPitch * info.Height;

            var strides = new int_array4();
            strides[0] = (int)mapped.RowPitch;
            strides[1] = (int)mapped.RowPitch;

            ffmpeg.sws_scale(
                _scaler,
                frame->data, frame->linesize, 0, info.Height,
                destination, strides);
        }
        finally
        {
            _gpu.Context.Unmap(_staging, 0);
        }

        _gpu.Context.CopyResource(_texture!, _staging);
    }

    private void EnsureScaler(VideoFrameInfo info)
    {
        if (_scaler is not null && _scalerSource == info.PixelFormat) return;

        ReleaseScaler();

        _scaler = ffmpeg.sws_getContext(
            info.Width, info.Height, info.PixelFormat,
            info.Width, info.Height, AVPixelFormat.AV_PIX_FMT_NV12,
            BilinearScaling, null, null, null);

        _scalerSource = info.PixelFormat;
    }

    private void EnsureTextures(int width, int height)
    {
        if (_texture is not null && Width == width && Height == height) return;

        ReleaseTextures();

        Width = width;
        Height = height;

        _texture = _gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        });

        _staging = _gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Write,
        });

        // NV12 is sampled as two views over one texture: luma as single-channel, chroma as the
        // interleaved half-resolution pair.
        _luma = _gpu.Device.CreateShaderResourceView(_texture, new ShaderResourceViewDescription
        {
            Format = Format.R8_UNorm,
            ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
        });

        _chroma = _gpu.Device.CreateShaderResourceView(_texture, new ShaderResourceViewDescription
        {
            Format = Format.R8G8_UNorm,
            ViewDimension = Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
        });
    }

    private void ReleaseTextures()
    {
        _luma?.Dispose();
        _chroma?.Dispose();
        _staging?.Dispose();
        _texture?.Dispose();

        _luma = null;
        _chroma = null;
        _staging = null;
        _texture = null;
        HasContent = false;
    }

    private void ReleaseScaler()
    {
        if (_scaler is null) return;

        ffmpeg.sws_freeContext(_scaler);
        _scaler = null;
        _scalerSource = AVPixelFormat.AV_PIX_FMT_NONE;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            ReleaseTextures();
            ReleaseScaler();
        }
    }
}
