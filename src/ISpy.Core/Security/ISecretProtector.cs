namespace ISpy.Core.Security;

/// <summary>
/// Encrypts device passwords at rest. Abstracted so the storage layer stays testable on any OS
/// while the shipped app always uses Windows DPAPI.
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(string plaintext);
    string Unprotect(byte[] ciphertext);
}
