using System.Text.Json;

namespace WinTube.Core.Player;

/// Reads mpv's `demuxer-cache-state` property. Read as a string, a node-valued property comes back
/// as its full JSON form: an object holding "seekable-ranges": [{"start": s, "end": e}, ...] and
/// "raw-input-rate" (bytes per second) among other fields. (The indexed sub-property route,
/// demuxer-cache-state/seekable-ranges/N/start, does not answer for it.) This is the pure
/// string-to-data half, kept out of the mpv host so it is testable.
public static class DemuxerCacheState
{
    /// The cached spans, in seconds from the start of the file, in the order mpv listed them.
    /// Anything unusable — null/empty, bad JSON, a missing or wrong-typed key, a malformed entry —
    /// yields nothing for that part rather than throwing: it runs on mpv's event thread, where an
    /// exception would end the loop, and a missing buffered-range overlay costs far less than that.
    /// Empty and inverted spans (end <= start) are dropped.
    public static IReadOnlyList<(double Start, double End)> ParseRanges(string? json) => ParseState(json).Ranges;

    /// The state's parts: the cached spans (as <see cref="ParseRanges"/>), mpv's current download rate
    /// in bytes per second ("raw-input-rate") and the bytes cached ahead of the playhead ("fw-bytes").
    /// Each number is null when its key is absent, not a number, negative or not finite. Same tolerance
    /// as <see cref="ParseRanges"/>: never throws.
    public static (IReadOnlyList<(double Start, double End)> Ranges, double? RawInputBytesPerSecond, double? ForwardBytes) ParseState(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ([], null, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return ([], null, null);
            return (ReadRanges(doc.RootElement),
                ReadNonNegative(doc.RootElement, "raw-input-rate"),
                ReadNonNegative(doc.RootElement, "fw-bytes"));
        }
        catch (JsonException)
        {
            return ([], null, null);
        }
    }

    /// True when the forward cache holds at least CacheFullFraction of its configured cap. Once full,
    /// mpv stops reading ahead and raw-input-rate falls to what playback consumes (the playing rung's
    /// bitrate), not what the line can carry, so a reading taken then must not feed the bandwidth
    /// estimate. An unknown fill (null) is never "full".
    public static bool IsForwardCacheFull(double? forwardBytes, int forwardCacheMegabytes) =>
        forwardBytes is { } fw &&
        fw >= AdaptiveAutoTuning.CacheFullFraction * forwardCacheMegabytes * 1024.0 * 1024.0;

    private static List<(double Start, double End)> ReadRanges(JsonElement root)
    {
        var ranges = new List<(double Start, double End)>();
        if (!root.TryGetProperty("seekable-ranges", out var array) ||
            array.ValueKind != JsonValueKind.Array) return ranges;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("start", out var s) || s.ValueKind != JsonValueKind.Number ||
                !item.TryGetProperty("end", out var e) || e.ValueKind != JsonValueKind.Number) continue;
            var (start, end) = (s.GetDouble(), e.GetDouble());
            if (end > start) ranges.Add((start, end));
        }
        return ranges;
    }

    private static double? ReadNonNegative(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var r) || r.ValueKind != JsonValueKind.Number ||
            !r.TryGetDouble(out var value) || !double.IsFinite(value) || value < 0) return null;
        return value;
    }
}
