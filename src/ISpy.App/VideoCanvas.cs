using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ISpy.App;

/// <summary>
/// A bare child window for the Direct3D swapchain to present into.
/// </summary>
/// <remarks>
/// WPF's own surfaces (D3DImage) route every frame through the WPF compositor, which on a grid of
/// cameras costs a full-canvas copy per frame. Presenting to a real child HWND lets DXGI hand the
/// buffer to the desktop compositor directly. The cost is that WPF elements cannot be drawn over
/// it, which is why tile labels are drawn with Direct2D inside the swapchain instead.
/// </remarks>
public sealed class VideoCanvas : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;

    private IntPtr _hwnd;

    /// <summary>Raised once the child window exists and a swapchain can be attached to it.</summary>
    public event Action<IntPtr>? SurfaceReady;

    /// <summary>Raised when the canvas changes size, in device pixels.</summary>
    public event Action<int, int>? SurfaceResized;

    public IntPtr SurfaceHandle => _hwnd;

    /// <summary>Canvas size in device pixels, which is what the swapchain needs (not DIPs).</summary>
    public (int Width, int Height) PixelSize
    {
        get
        {
            var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            return ((int)Math.Round(ActualWidth * scale), (int)Math.Round(ActualHeight * scale));
        }
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = CreateWindowEx(
            0,
            "static",
            null,
            WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            0, 0, 1, 1,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("Could not create the video window.");

        SurfaceReady?.Invoke(_hwnd);
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (_hwnd == IntPtr.Zero) return;

        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);

        var (width, height) = PixelSize;
        if (width > 0 && height > 0) SurfaceResized?.Invoke(width, height);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string? windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
