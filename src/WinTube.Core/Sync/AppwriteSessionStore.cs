using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace WinTube.Core.Sync;

/// The Appwrite session cookie, DPAPI-protected per profile — it authenticates requests,
/// so it gets the same treatment as the OAuth tokens. Deleting it merely forces a fresh
/// sign-in through the auth function; the backend rows are untouched.
[SupportedOSPlatform("windows")]
public sealed class AppwriteSessionStore(string rootDirectory)
{
    private string FilePath(string profileId) =>
        Path.Combine(rootDirectory, "profiles", profileId, "appwrite-session.bin");

    public void Save(string profileId, AppwriteSession session)
    {
        var path = FilePath(profileId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(session);
        File.WriteAllBytes(path,
            ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
    }

    public AppwriteSession? Load(string profileId)
    {
        try
        {
            var plain = ProtectedData.Unprotect(
                File.ReadAllBytes(FilePath(profileId)), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<AppwriteSession>(plain);
        }
        catch (Exception e) when (e is IOException or CryptographicException or JsonException)
        {
            return null;
        }
    }

    public void Delete(string profileId)
    {
        try { File.Delete(FilePath(profileId)); }
        catch (IOException) { /* a locked or missing file is not worth failing sign-out over */ }
    }
}
