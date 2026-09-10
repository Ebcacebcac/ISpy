using System.Text;
using ISpy.Core.Security;

namespace ISpy.Tests;

/// <summary>
/// Reversible stand-in for DPAPI so storage tests run on any OS. Deliberately not real encryption -
/// it exists only to prove the store round-trips through the protector rather than storing plaintext.
/// </summary>
internal sealed class FakeSecretProtector : ISecretProtector
{
    public byte[] Protect(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        for (var i = 0; i < bytes.Length; i++) bytes[i] ^= 0x5A;
        return bytes;
    }

    public string Unprotect(byte[] ciphertext)
    {
        var bytes = (byte[])ciphertext.Clone();
        for (var i = 0; i < bytes.Length; i++) bytes[i] ^= 0x5A;
        return Encoding.UTF8.GetString(bytes);
    }
}
