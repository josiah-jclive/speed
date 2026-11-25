using System.Security.Cryptography;
using System.Text;

namespace Speed.Core;

/// <summary>
/// Encrypts stream keys in settings.json. On Windows this uses DPAPI, so only the same
/// Windows account on the same PC can read them back. On macOS keys are stored as typed.
/// </summary>
public static class KeyProtection
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = "JSPEED stream keys"u8.ToArray();

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
                return Prefix + Convert.ToBase64String(bytes);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"A stream key couldn't be encrypted, so it's stored as typed: {ex.Message}");
            }
        }
        return plain;
    }

    public static string Unprotect(string stored)
    {
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;
        if (!OperatingSystem.IsWindows()) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // Settings copied from another PC or account: the key can't be read here.
            return "";
        }
    }
}
