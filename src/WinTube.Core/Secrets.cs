using System.Text.Json;

namespace WinTube.Core;

/// The three public values every InnerTube/OAuth call needs. Not per-user confidential
/// secrets, but kept out of the repo (gitignored secrets.json) so nothing credential-shaped
/// lands in it — same policy as the tvOS app's Secrets.xcconfig.
public sealed record Secrets(string InnerTubeApiKey, string OAuthClientId, string OAuthClientSecret)
{
    /// Optional: the personal Appwrite behind watch-progress sync. Host only — the scheme
    /// and Appwrite's fixed /v1 are appended in code. Empty (the fresh-clone default) keeps
    /// sync off and the app fully local, same policy as the tvOS Secrets.xcconfig.
    public string AppwriteHost { get; init; } = "";
    public string AppwriteProjectId { get; init; } = "";

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinTube", "secrets.json");

    /// Missing or unreadable file yields empty strings: the app still starts, and API calls
    /// fail at runtime with an HTTP error rather than the app refusing to launch.
    public static Secrets Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            string Get(string name) =>
                doc.RootElement.TryGetProperty(name, out var el) ? el.GetString() ?? "" : "";
            return new Secrets(Get("innerTubeApiKey"), Get("oauthClientId"), Get("oauthClientSecret"))
            {
                AppwriteHost = Get("appwriteHost"),
                AppwriteProjectId = Get("appwriteProjectId")
            };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Secrets("", "", "");
        }
    }
}
