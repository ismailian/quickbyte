using System.Security.Cryptography;
using System.Text;

namespace QuickByte.Core.Helpers;

/// <summary>
/// Encrypts a single secret string for storage in <c>downloads.json</c>.
///
/// The file sits in <c>%AppData%</c>, which is readable by anything running as
/// the user and by anyone who walks off with a copy of the profile, so an FTP
/// or HTTP password cannot simply be written into it as text. DPAPI's
/// <see cref="DataProtectionScope.CurrentUser"/> scope ties the ciphertext to
/// the Windows account that produced it: another account on the same machine —
/// and any other machine — gets nothing back.
///
/// That last part is the reason both directions are best-effort rather than
/// throwing. A profile copied to a new PC still has to load: the credential
/// simply comes back empty and the download asks for it again, which is a far
/// better failure than a startup crash over one unreadable field.
/// </summary>
public static class SecretProtector
{
    /// <summary>
    /// Mixed into the key so a blob lifted out of QuickByte's file can't be
    /// handed to some other CurrentUser-scope DPAPI consumer to decrypt.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QuickByte.Credential.v1");

    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;

        try
        {
            byte[] cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(cipher);
        }
        catch
        {
            // No usable DPAPI store (an unusual service account, a broken
            // profile). Dropping the secret is the only safe answer — writing
            // it in the clear instead would defeat the point of the class.
            return null;
        }
    }

    public static string Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return string.Empty;

        try
        {
            byte[] plain = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            // Wrong user, wrong machine, or a truncated file — see the summary.
            return string.Empty;
        }
    }
}
