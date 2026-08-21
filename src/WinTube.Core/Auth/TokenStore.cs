using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinTube.Core.Auth;

public sealed record StoredProfile(
    string ProfileId, string Name, string? AvatarUrl, string AccessToken, string RefreshToken);

/// The one secret the app holds: the OAuth token pair, DPAPI-encrypted per user — the Windows
/// stand-in for the tvOS Keychain. Everything else (watch progress, history) is plain JSON.
[SupportedOSPlatform("windows")]
public sealed class TokenStore(string directory)
{
    private string FilePath => Path.Combine(directory, "tokens.bin");

    public void Save(StoredProfile profile)
    {
        Directory.CreateDirectory(directory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(profile);
        File.WriteAllBytes(FilePath,
            ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
    }

    /// Null on missing, corrupt, or undecryptable (e.g. copied from another user) file —
    /// all of which mean "sign in again", never a crash.
    public StoredProfile? Load()
    {
        try
        {
            var plain = ProtectedData.Unprotect(
                File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredProfile>(plain);
        }
        catch (Exception e) when (e is IOException or CryptographicException or JsonException)
        {
            return null;
        }
    }

    public void Delete() => File.Delete(FilePath);
}
