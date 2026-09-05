namespace WinTube.Core.InnerTube;

/// Which InnerTube client identity a call presents as. Values verified against live requests
/// in the tvOS repo (reference/INNERTUBE.md, AppConfig.swift).
public enum ClientKind
{
    /// TVHTML5 — personalized feeds, search, accounts_list. Needs a Bearer token.
    Tv,
    /// VISIONOS — playback stream extraction. Full HLS ladder; needs scraped visitorData.
    VisionOs,
    /// ANDROID — playback fallback, muxed itag 18 only (360p). Needs nothing.
    Android,
    /// WEB — comments via /next, the one client verified to return the comment section.
    /// Unauthenticated.
    Web,
}

public sealed record ClientInfo(
    string Name,
    string Version,
    string NameId,
    string UserAgent,
    string? Referer,
    string Host,
    bool RequiresVisitorData,
    IReadOnlyDictionary<string, object> ExtraContext);

public static class Clients
{
    // These UA strings must be reproduced verbatim.
    private const string TvUserAgent =
        "Mozilla/5.0 (Linux armeabi-v7a; Android 7.1.2; Fire OS 6.0) Cobalt/22.lts.3.306369-gold (unlike Gecko) v8/8.8.278.8-jit gles Starboard/13, Amazon_ATV_mediatek8695_2019/NS6294 (Amazon, AFTMM, Wireless) com.amazon.firetv.youtube/22.3.r2.v66.0";
    private const string VisionOsUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15";
    private const string AndroidUserAgent =
        "com.google.android.youtube/21.26.364 (Linux; U; Android 11) gzip";
    private const string WebUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private static readonly Dictionary<string, object> NoExtra = [];

    private static readonly ClientInfo Tv = new(
        Name: "TVHTML5", Version: "7.20260707.07.00", NameId: "7",
        UserAgent: TvUserAgent, Referer: "https://www.youtube.com/tv",
        Host: "https://www.youtube.com", RequiresVisitorData: false, ExtraContext: NoExtra);

    private static readonly ClientInfo VisionOs = new(
        Name: "VISIONOS", Version: "1.02", NameId: "101",
        UserAgent: VisionOsUserAgent, Referer: "https://www.youtube.com/tv",
        Host: "https://www.youtube.com", RequiresVisitorData: true,
        ExtraContext: new Dictionary<string, object>
        {
            ["clientScreen"] = "WATCH",
            ["userAgent"] = VisionOsUserAgent,
            ["deviceMake"] = "Apple",
            ["deviceModel"] = "RealityDevice17,1",
            ["osName"] = "visionOS",
            ["osVersion"] = "26.5.23O471",
        });

    private static readonly ClientInfo Android = new(
        Name: "ANDROID", Version: "21.26.364", NameId: "3",
        UserAgent: AndroidUserAgent, Referer: null,
        Host: "https://youtubei.googleapis.com", RequiresVisitorData: false,
        ExtraContext: new Dictionary<string, object>
        {
            ["androidSdkVersion"] = 30,
            ["osName"] = "Android",
            ["osVersion"] = "11",
        });

    private static readonly ClientInfo Web = new(
        Name: "WEB", Version: "2.20260726.00.00", NameId: "1",
        UserAgent: WebUserAgent, Referer: "https://www.youtube.com",
        Host: "https://www.youtube.com", RequiresVisitorData: false, ExtraContext: NoExtra);

    public static ClientInfo Get(ClientKind kind) => kind switch
    {
        ClientKind.Tv => Tv,
        ClientKind.VisionOs => VisionOs,
        ClientKind.Android => Android,
        ClientKind.Web => Web,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
