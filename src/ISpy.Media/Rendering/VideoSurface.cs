using FFmpeg.AutoGen;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
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

    public VideoSurface(GpuDevice gpu) => _gpu = gpu;

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>True once a frame has been received, so the renderer knows there is something to draw.</summary>
    public bool HasContent { get; private set; }

    public ID3D11ShaderResourceView? Luma => _luma;
    public ID3D11ShaderResourceView? Chroma => _chroma;

    /// <summary>Copies a decoded frame in. Called on the decode thread while the frame is valid.</summary>
    public void Update(AVFrame* frame, VideoFrameInfo info)
    {
        if (info.Width <= 0 || info.Height <= 0) return;

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

        _gpu.Context.CopySubresourceRegion(
            _texture!, 0, 0, 0, 0,
            decoded, arraySlice);
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
        ReleaseTextures();
        ReleaseScaler();
    }
}
