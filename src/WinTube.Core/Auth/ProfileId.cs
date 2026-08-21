using System.Security.Cryptography;
using System.Text;

namespace WinTube.Core.Auth;

/// The app's stable identity for an account: sha256 of YouTube's own account key. The same
/// value on every device and after every reinstall — and the same derivation as the tvOS app,
/// so a future watch-progress sync stage shares profile ids with the Apple TV.
public static class ProfileId
{
    public static string From(string accountKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountKey)))
            .ToLowerInvariant();
}
