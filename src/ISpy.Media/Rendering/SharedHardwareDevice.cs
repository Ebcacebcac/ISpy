using FFmpeg.AutoGen;
using Vortice.Direct3D11;

namespace ISpy.Media.Rendering;

/// <summary>
/// Wraps our Direct3D device in an FFmpeg hardware-device reference that every decoder shares.
/// </summary>
/// <remarks>
/// Letting each decoder call av_hwdevice_ctx_create would give every camera its own D3D11 device,
/// and a texture produced on one device cannot be sampled on another - sixteen cameras would mean
/// sixteen devices and no way to draw any of them together. Handing FFmpeg the device the presenter
/// already owns keeps decode and display on one device, which is what makes the copy-to-screen a
/// GPU-side operation instead of a round trip through system memory.
///
/// AVD3D11VADeviceContext's layout is public FFmpeg ABI - ID3D11Device*, then ID3D11DeviceContext*,
/// then the video device/context and lock callbacks - so it can be filled in by offset even though
/// the binding generator does not surface the struct.
/// </remarks>
public sealed unsafe class SharedHardwareDevice : IDisposable
{
    private AVBufferRef* _reference;

    private SharedHardwareDevice(AVBufferRef* reference) => _reference = reference;

    /// <summary>The reference to hand to each decoder. Null once disposed.</summary>
    public AVBufferRef* Reference => _reference;

    /// <summary>
    /// Builds the hardware-device reference, or returns null when D3D11VA is unavailable - in which
    /// case decoding falls back to software and the picture still appears.
    /// </summary>
    public static SharedHardwareDevice? Create(GpuDevice gpu)
    {
        if (!FFmpegRuntime.EnsureInitialised()) return null;

        // Several decode threads drive one immediate context, so the device must serialise its own
        // access. Without this the driver corrupts state under load in ways that look like random
        // decode failures.
        using (var multithread = gpu.Device.QueryInterfaceOrNull<ID3D11Multithread>())
        {
            multithread?.SetMultithreadProtected(true);
        }

        var reference = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (reference is null) return null;

        var deviceContext = (AVHWDeviceContext*)reference->data;
        if (deviceContext is null || deviceContext->hwctx is null)
        {
            ffmpeg.av_buffer_unref(&reference);
            return null;
        }

        // FFmpeg releases these when the context is freed, so hand it references of its own.
        gpu.Device.AddRef();
        gpu.Context.AddRef();

        var fields = (IntPtr*)deviceContext->hwctx;
        fields[0] = gpu.Device.NativePointer;
        fields[1] = gpu.Context.NativePointer;

        var initialised = ffmpeg.av_hwdevice_ctx_init(reference);
        if (initialised < 0)
        {
            ffmpeg.av_buffer_unref(&reference);
            return null;
        }

        return new SharedHardwareDevice(reference);
    }

    public void Dispose()
    {
        if (_reference is null) return;

        var reference = _reference;
        _reference = null;
        ffmpeg.av_buffer_unref(&reference);
    }
}
