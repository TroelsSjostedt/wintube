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

    /// Both halves of the state: the cached spans (as <see cref="ParseRanges"/>) and mpv's current
    /// download rate in bytes per second. The rate is null when the key is absent, not a number,
    /// negative or not finite. Same tolerance as <see cref="ParseRanges"/>: never throws.
    public static (IReadOnlyList<(double Start, double End)> Ranges, double? RawInputBytesPerSecond) ParseState(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ([], null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return ([], null);
            return (ReadRanges(doc.RootElement), ReadRate(doc.RootElement));
        }
        catch (JsonException)
        {
            return ([], null);
        }
    }

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

    private static double? ReadRate(JsonElement root)
    {
        if (!root.TryGetProperty("raw-input-rate", out var r) || r.ValueKind != JsonValueKind.Number ||
            !r.TryGetDouble(out var rate) || !double.IsFinite(rate) || rate < 0) return null;
        return rate;
    }
}
