namespace WinTube.Core.Sync;

/// Where the watch-progress backend lives, and the protocol constants every call shares.
/// Null when secrets.json names no Appwrite — sync is then fully off (fresh-clone default).
public sealed record AppwriteConfig(string Endpoint, string ProjectId)
{
    public const string DatabaseId = "metube";
    public const string TableId = "watchProgress";
    public const string AuthFunctionId = "metube-auth";
    /// Appwrite matches this against the platforms registered on the project. The tvOS
    /// platform registration is reused deliberately — zero server changes; a dedicated
    /// Windows platform can be registered later without touching this app's rows.
    public const string Origin = "appwrite-tvos://dk.delectosoft.metube";
    public const string ResponseFormat = "1.9.5";

    public static AppwriteConfig? FromSecrets(Secrets secrets) =>
        string.IsNullOrEmpty(secrets.AppwriteHost) || string.IsNullOrEmpty(secrets.AppwriteProjectId)
            ? null
            : new AppwriteConfig($"https://{secrets.AppwriteHost}/v1", secrets.AppwriteProjectId);
}
