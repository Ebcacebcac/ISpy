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
/// buffer to the desktop compositor directly. The costs: WPF elements cannot be drawn over it
/// (labels are drawn with Direct2D inside the swapchain instead), and mouse input arrives as Win32
/// messages on the child window rather than as WPF events - so the window is subclassed and the
/// messages surfaced as the <see cref="PointerPressed"/> family of events, in device pixels, which
/// is the coordinate space the tile layout already lives in.
/// </remarks>
public sealed class VideoCanvas : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;

    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;

    private const int GwlpWndProc = -4;

    private IntPtr _hwnd;
    private IntPtr _originalWndProc;

    // Held for the window's lifetime: Win32 keeps only the raw function pointer, and if the
    // delegate were collected the next mouse message would jump into freed memory.
    private WndProcDelegate? _wndProc;

    /// <summary>Raised once the child window exists and a swapchain can be attached to it.</summary>
    public event Action<IntPtr>? SurfaceReady;

    /// <summary>Raised when the canvas changes size, in device pixels.</summary>
    public event Action<int, int>? SurfaceResized;

    /// <summary>Mouse events over the video, in device pixels relative to the canvas.</summary>
    public event Action<int, int>? PointerPressed;
    public event Action<int, int>? PointerMoved;
    public event Action<int, int>? PointerReleased;
    public event Action<int, int>? PointerDoubleClicked;

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

        _wndProc = HandleMessage;
        _originalWndProc = SetWindowLongPtr(
            _hwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_wndProc));

        SurfaceReady?.Invoke(_hwnd);
        return new HandleRef(this, _hwnd);
    }

    private IntPtr HandleMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmLButtonDown:
                // Capture so a drag that ends outside the canvas still delivers its release,
                // otherwise a swap-drag released over the sidebar would leave the drag stuck.
                SetCapture(hwnd);
                Raise(PointerPressed, lParam);
                break;

            case WmMouseMove:
                Raise(PointerMoved, lParam);
                break;

            case WmLButtonUp:
                ReleaseCapture();
                Raise(PointerReleased, lParam);
                break;

            case WmLButtonDblClk:
                Raise(PointerDoubleClicked, lParam);
                break;
        }

        return CallWindowProc(_originalWndProc, hwnd, message, wParam, lParam);
    }

    private static void Raise(Action<int, int>? handler, IntPtr lParam)
    {
        // Client coordinates: x in the low word, y in the high word, both signed.
        var x = (short)((long)lParam & 0xFFFF);
        var y = (short)(((long)lParam >> 16) & 0xFFFF);
        handler?.Invoke(x, y);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (_hwnd == IntPtr.Zero) return;

        if (_originalWndProc != IntPtr.Zero)
            SetWindowLongPtr(_hwnd, GwlpWndProc, _originalWndProc);

        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        _wndProc = null;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);

        var (width, height) = PixelSize;
        if (width > 0 && height > 0) SurfaceResized?.Invoke(width, height);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string? windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(
        IntPtr previous, IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();
}
