using System.Text.Json;

namespace WinTube.Core.Player;

/// Reads mpv's `demuxer-cache-state` property. Read as a string, a node-valued property comes back
/// as its full JSON form: an object holding "seekable-ranges": [{"start": s, "end": e}, ...] among
/// other fields. (The indexed sub-property route, demuxer-cache-state/seekable-ranges/N/start, does
/// not answer for it.) This is the pure string-to-data half, kept out of the mpv host so it is testable.
public static class DemuxerCacheState
{
    /// The cached spans, in seconds from the start of the file, in the order mpv listed them.
    /// Anything unusable — null/empty, bad JSON, a missing or wrong-typed key, a malformed entry —
    /// yields nothing for that part rather than throwing: it runs on mpv's event thread, where an
    /// exception would end the loop, and a missing buffered-range overlay costs far less than that.
    /// Empty and inverted spans (end <= start) are dropped.
    public static IReadOnlyList<(double Start, double End)> ParseRanges(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("seekable-ranges", out var array) ||
                array.ValueKind != JsonValueKind.Array) return [];

            var ranges = new List<(double, double)>();
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
        catch (JsonException)
        {
            return [];
        }
    }
}
