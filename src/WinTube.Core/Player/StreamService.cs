using System.Text.Json;
using WinTube.Core.InnerTube;

namespace WinTube.Core.Player;

/// A stream ready to hand to the media element.
/// UserAgent is the resolving client's, sent on media requests so playback presents as the
/// same client that minted the URL. OriginalAudioLanguage marks the video's own audio track
/// for dubbed videos — the HLS manifest declares no default, so the player must select it.
public sealed record ResolvedStream(
    Uri Url, ClientKind Client, string UserAgent, bool IsAdaptive, string? OriginalAudioLanguage);

/// The message is user-showable: YouTube's own playability reason when there was one.
public sealed class StreamException(string message) : Exception(message);

/// Resolves a playable stream URL for a videoId. Port of the tvOS StreamService:
///  1. VISIONOS (+ scraped visitorData) → streamingData.hlsManifestUrl, the full HLS ladder.
///  2. ANDROID → muxed progressive itag 18 (360p MP4). No ABR, but needs no session token.
/// streamingData.adaptiveFormats is deliberately not used for playback (signature ciphers,
/// SABR); only the audioTrack metadata is read off it.
public sealed class StreamService(InnerTubeClient innerTube, VisitorDataStore visitorData)
{
    private static readonly ClientKind[] Ladder = [ClientKind.VisionOs, ClientKind.Android];

    private const string GenericFailure = "No playable stream was found for this video.";

    /// `after` resumes the ladder past a client whose URL resolved but would not play —
    /// YouTube occasionally serves an HLS manifest whose audio renditions all 404.
    public async Task<ResolvedStream> ResolveAsync(
        string videoId, ClientKind? after = null, CancellationToken ct = default)
    {
        string? firstReason = null;

        var remaining = Ladder.AsEnumerable();
        if (after is { } failed)
            remaining = Ladder.SkipWhile(c => c != failed).Skip(1);

        foreach (var kind in remaining)
        {
            try
            {
                var (stream, skipReason) = await ResolveWithAsync(videoId, kind, ct);
                if (stream is not null) return stream;
                // A gated client is a reason to try the next one — but keep the first
                // human-readable reason, since a genuinely unavailable video returns the same
                // statuses from every client and this is the path carrying the real message.
                firstReason ??= skipReason;
            }
            catch (OperationCanceledException) { throw; }
            catch (StreamException) { throw; }
            catch (Exception e) when (e is InnerTubeException or HttpRequestException or VisitorDataException)
            {
                firstReason ??= e.Message;
            }
        }

        throw new StreamException(firstReason ?? GenericFailure);
    }

    /// One rung of the ladder: (stream, null) on success, (null, reason?) to try the next.
    private async Task<(ResolvedStream? Stream, string? SkipReason)> ResolveWithAsync(
        string videoId, ClientKind kind, CancellationToken ct)
    {
        var client = Clients.Get(kind);
        string? vd = client.RequiresVisitorData ? await visitorData.GetTokenAsync(ct) : null;

        using var first = await PostPlayerAsync(videoId, kind, vd, ct);
        var json = first.RootElement;
        JsonDocument? retry = null;
        try
        {
            // An expired visitorData looks exactly like never having sent one: retry once
            // with a fresh token before believing the rejection.
            if (client.RequiresVisitorData &&
                Json.StringAt(json, "playabilityStatus/status") == "LOGIN_REQUIRED")
            {
                visitorData.Invalidate();
                vd = await visitorData.GetTokenAsync(ct);
                retry = await PostPlayerAsync(videoId, kind, vd, ct);
                json = retry.RootElement;
            }

            var status = Json.StringAt(json, "playabilityStatus/status");
            if (status != "OK")
            {
                var reason = Json.StringAt(json, "playabilityStatus/reason")
                    ?? Json.StringAt(json,
                        "playabilityStatus/errorScreen/playerErrorMessageRenderer/reason/simpleText");
                // LOGIN_REQUIRED and ERROR are ambiguous: what a gated client returns, and
                // also what an unavailable video returns from every client.
                if (status is "LOGIN_REQUIRED" or "ERROR") return (null, reason);
                throw new StreamException(reason ?? GenericFailure);
            }

            return (Pick(json, kind, client.UserAgent), null);
        }
        finally { retry?.Dispose(); }
    }

    private Task<JsonDocument> PostPlayerAsync(
        string videoId, ClientKind kind, string? vd, CancellationToken ct) =>
        innerTube.PostAsync("player", kind, new Dictionary<string, object?>
        {
            ["videoId"] = videoId,
            ["contentCheckOk"] = true,
            ["racyCheckOk"] = true,
        }, bearer: null, visitorData: vd, ct: ct);

    /// The stream pick order: HLS manifest, then progressive itag 18, then any progressive
    /// MP4 with a plain url. Null means playable-but-nothing-usable (typically SABR-only).
    private static ResolvedStream? Pick(JsonElement json, ClientKind kind, string userAgent)
    {
        if (Json.StringAt(json, "streamingData/hlsManifestUrl") is { } hls &&
            Uri.TryCreate(hls, UriKind.Absolute, out var hlsUrl))
            return new ResolvedStream(hlsUrl, kind, userAgent, IsAdaptive: true,
                OriginalAudioLanguage: OriginalAudioLanguage(json));

        var formats = Json.ValueAt(json, "streamingData/formats") is
            { ValueKind: JsonValueKind.Array } arr
            ? arr.EnumerateArray().ToList() : [];

        // itag 18 is the one muxed format that reliably carries a plain `url` — no
        // signatureCipher, no `n` throttle param, so no JS deciphering.
        var pick = formats.FirstOrDefault(f =>
                f.TryGetProperty("itag", out var itag) && Json.IntValue(itag) == 18 &&
                UsableUrl(f) is not null)
            is { ValueKind: JsonValueKind.Object } exact
            ? UsableUrl(exact)
            : null;

        pick ??= formats
            .Where(f => (Json.StringAt(f, "mimeType") ?? "")
                .StartsWith("video/mp4", StringComparison.OrdinalIgnoreCase))
            .Select(UsableUrl)
            .FirstOrDefault(u => u is not null);

        return pick is null
            ? null
            : new ResolvedStream(pick, kind, userAgent, IsAdaptive: false,
                OriginalAudioLanguage: null);
    }

    private static Uri? UsableUrl(JsonElement format) =>
        Json.StringAt(format, "url") is { } s &&
        Uri.TryCreate(s, UriKind.Absolute, out var url) ? url : null;

    /// The language tag of the track YouTube marks as the video's own —
    /// adaptiveFormats[].audioTrack with audioIsDefault, id "en-US.4" → "en-US".
    /// Null for undubbed videos (no audioTrack anywhere), which is correct: one track,
    /// nothing to select.
    private static string? OriginalAudioLanguage(JsonElement json)
    {
        if (Json.ValueAt(json, "streamingData/adaptiveFormats") is not
            { ValueKind: JsonValueKind.Array } formats) return null;
        foreach (var format in formats.EnumerateArray())
        {
            if (Json.ValueAt(format, "audioTrack") is not { ValueKind: JsonValueKind.Object } track)
                continue;
            if (track.TryGetProperty("audioIsDefault", out var def) &&
                def.ValueKind == JsonValueKind.True &&
                Json.StringAt(track, "id") is { } id)
                return id.Split('.')[0];
        }
        return null;
    }
}
