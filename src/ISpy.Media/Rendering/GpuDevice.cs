using FFmpeg.AutoGen;
using Vortice.Direct3D;
using Vortice.Direct3D11;

// FFmpeg.AutoGen also generates ID3D11Device/ID3D11DeviceContext vtable stubs; alias the real
// Vortice interfaces so the ambiguity cannot be resolved the wrong way by accident.
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;

namespace ISpy.Media.Rendering;

/// <summary>
/// The Direct3D 11 device everything draws on.
/// </summary>
/// <remarks>
/// When hardware decoding is in use this wraps <em>FFmpeg's own</em> device rather than creating a
/// second one. Decoded surfaces live on whichever device produced them, and textures from one
/// device cannot be sampled by another without going through shared handles - which FFmpeg's
/// decoder textures are not created for. Adopting its device means a decoded frame reaches the
/// screen with a single GPU-side copy and never touches system memory.
/// </remarks>
public sealed unsafe class GpuDevice : IDisposable
{
    private readonly bool _ownsDevice;

    private GpuDevice(ID3D11Device device, ID3D11DeviceContext context, bool ownsDevice)
    {
        Device = device;
        Context = context;
        _ownsDevice = ownsDevice;
    }

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }

    /// <summary>
    /// Adopts the D3D11 device behind an FFmpeg hardware device reference.
    /// </summary>
    /// <remarks>
    /// AVD3D11VADeviceContext begins with ID3D11Device* followed by ID3D11DeviceContext*. That
    /// layout is public FFmpeg ABI, which is what makes reading it by offset safe; the binding
    /// generator simply does not surface the struct.
    /// </remarks>
    public static GpuDevice? FromFFmpeg(AVBufferRef* hardwareDeviceRef)
    {
        if (hardwareDeviceRef is null) return null;

        var deviceContext = (AVHWDeviceContext*)hardwareDeviceRef->data;
        if (deviceContext is null || deviceContext->hwctx is null) return null;

        var fields = (IntPtr*)deviceContext->hwctx;
        var devicePointer = fields[0];
        var contextPointer = fields[1];

        if (devicePointer == IntPtr.Zero || contextPointer == IntPtr.Zero) return null;

        // AddRef both, since the wrappers release on dispose and FFmpeg still owns its references.
        var device = new ID3D11Device(devicePointer);
        var context = new ID3D11DeviceContext(contextPointer);
        device.AddRef();
        context.AddRef();

        return new GpuDevice(device, context, ownsDevice: false);
    }

    /// <summary>Creates a device of our own, for software decoding or when no GPU decoder exists.</summary>
    public static GpuDevice Create()
    {
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;

        var result = D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, flags,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
            out var device, out _, out var context);

        if (result.Failure)
        {
            // A machine with no usable GPU (or an RDP session) still has to show a picture.
            result = D3D11.D3D11CreateDevice(
                null, DriverType.Warp, flags,
                [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
                out device, out _, out context);

            result.CheckError();
        }

        // Decode threads and the render thread both drive this one immediate context, so the
        // device must serialise its own access. This is set here rather than alongside the
        // hardware-decode setup because the software decode path shares the context too - and that
        // is precisely the path taken when hardware decoding was unavailable.
        using (var multithread = device!.QueryInterfaceOrNull<ID3D11Multithread>())
        {
            multithread?.SetMultithreadProtected(true);
        }

        return new GpuDevice(device, context!, ownsDevice: true);
    }

    public void Dispose()
    {
        Context.Dispose();
        Device.Dispose();

        // When we adopted FFmpeg's device the Dispose calls above only drop the references we
        // added; FFmpeg tears the device down when its own context is freed.
        _ = _ownsDevice;
    }
}
