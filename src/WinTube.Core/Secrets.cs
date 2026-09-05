using System.Text.Json;

namespace WinTube.Core;

/// Google's own public YouTube-on-TV app constants — the same values baked into every
/// TV firmware and printed in yt-dlp's source. They identify the app, not a user; the
/// per-user secrets (OAuth tokens) are DPAPI-stored and never ship. constants ship embedded
/// as defaults; secrets.json is optional overrides only.
public sealed record Secrets(string InnerTubeApiKey, string OAuthClientId, string OAuthClientSecret)
{
    /// Optional: the personal Appwrite behind watch-progress sync. Host only — the scheme
    /// and Appwrite's fixed /v1 are appended in code. Empty (the fresh-clone default) keeps
    /// sync off and the app fully local, same policy as the tvOS Secrets.xcconfig.
    public string AppwriteHost { get; init; } = "";
    public string AppwriteProjectId { get; init; } = "";

    /// Google's own public YouTube-on-TV app constants — the same values baked into every
    /// TV firmware and printed in yt-dlp's source. They identify the app, not a user; the
    /// per-user secrets (OAuth tokens) are DPAPI-stored and never ship. secrets.json can
    /// still override any field, and the Appwrite fields deliberately have no defaults:
    /// sync is a personal opt-in.
    public static Secrets Defaults { get; } = new(
        InnerTubeApiKey: "AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8",
        OAuthClientId: "861556708454-d6dlm3lh05idd8npek18k6be8ba3oc68.apps.googleusercontent.com",
        OAuthClientSecret: "SboVhoG9s0rNafixCSGGKXAT");

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinTube", "secrets.json");

    public static Secrets Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            string Get(string name) =>
                doc.RootElement.TryGetProperty(name, out var el) ? el.GetString() ?? "" : "";
            string Or(string value, string fallback) => value.Length > 0 ? value : fallback;
            return new Secrets(
                Or(Get("innerTubeApiKey"), Defaults.InnerTubeApiKey),
                Or(Get("oauthClientId"), Defaults.OAuthClientId),
                Or(Get("oauthClientSecret"), Defaults.OAuthClientSecret))
            {
                AppwriteHost = Get("appwriteHost"),
                AppwriteProjectId = Get("appwriteProjectId")
            };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return Defaults;
        }
    }
}
