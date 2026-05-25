using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fluentia.Services;

public static class DesktopSessionProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Fluentia.DesktopSession.v1");

    public static string Protect(PersistedDesktopSession session)
    {
        var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(session));
        var protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public static PersistedDesktopSession? Unprotect(string protectedSession)
    {
        if (string.IsNullOrWhiteSpace(protectedSession))
        {
            return null;
        }

        try
        {
            var cipherBytes = Convert.FromBase64String(protectedSession);
            var plaintext = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<PersistedDesktopSession>(plaintext);
        }
        catch
        {
            return null;
        }
    }
}
