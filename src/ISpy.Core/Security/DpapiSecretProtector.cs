using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace ISpy.Core.Security;

/// <summary>
/// Windows DPAPI protection scoped to the current user. The ciphertext is bound to the Windows
/// account, so copying inventory.db to another machine or user does not leak camera passwords.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    /// <summary>Extra entropy, so another app's DPAPI blob can never be decrypted through ours.</summary>
    private static readonly byte[] Entropy = "ISpy.DeviceCredential.v1"u8.ToArray();

    public byte[] Protect(string plaintext) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);

    public string Unprotect(byte[] ciphertext) =>
        Encoding.UTF8.GetString(
            ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser));
}
