using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ISpy.App;

/// <summary>
/// Makes the native window chrome match the app.
/// </summary>
/// <remarks>
/// WPF windows keep the OS-default light title bar, which on a dark application reads as a white
/// stripe bolted onto the top. DWM has painted dark title bars on request since Windows 10 1809;
/// asking costs one call per window and degrades to the light bar silently anywhere it is not
/// supported - never an error the user sees.
/// </remarks>
public static class WindowStyling
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;

    public static void ApplyDarkChrome(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var enabled = 1;

        // Attribute id changed in 20H1; try the current one first, then the old one.
        if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(handle, UseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);
}
