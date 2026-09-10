using System.Runtime.InteropServices;

namespace ISpy.Core.Security;

public static class SecretProtector
{
    /// <summary>
    /// The protector the app should use. Windows-only by design: there is no safe way to encrypt
    /// a password at rest on another OS without a key the user supplies, and ISpy is a Windows app.
    /// </summary>
    public static ISecretProtector CreateDefault()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("ISpy stores credentials using Windows DPAPI.");

        return new DpapiSecretProtector();
    }
}
