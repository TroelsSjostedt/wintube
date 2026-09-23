using System.Text.RegularExpressions;

namespace WinTube.Core.Player;

/// One video rung of an HLS master playlist.
public sealed record HlsVariant(int Height, uint Bandwidth, string Codecs);

/// One entry of the quality picker: a height and the exact BANDWIDTH of the variant that
/// represents it, which is what AdaptiveMediaSource's Desired bitrates are pinned to.
public sealed record HlsQuality(int Height, uint Bandwidth)
{
    public string Label => $"{Height}p";
}

/// Reads the quality rungs out of an HLS master playlist. YouTube's TV manifest carries each
/// height in up to three codec flavors — H.264 (which tops out at 1080p), SDR VP9 (which goes
/// to 2160p) and 10-bit HDR VP9 — plus audio-only entries, which have no RESOLUTION and are
/// skipped.
public static partial class HlsVariantParser
{
    public static IReadOnlyList<HlsVariant> Parse(string masterPlaylist)
    {
        var variants = new List<HlsVariant>();
        foreach (Match line in StreamInfPattern().Matches(masterPlaylist))
        {
            var attributes = line.Groups[1].Value;
            var resolution = ResolutionPattern().Match(attributes);
            var bandwidth = BandwidthPattern().Match(attributes);
            if (!resolution.Success || !bandwidth.Success) continue;
            if (!int.TryParse(resolution.Groups[2].Value, out var height)) continue;
            if (!uint.TryParse(bandwidth.Groups[1].Value, out var bits)) continue;
            var codecs = CodecsPattern().Match(attributes);
            variants.Add(new HlsVariant(height, bits, codecs.Success ? codecs.Groups[1].Value : ""));
        }
        return variants;
    }

    /// One picker entry per height, tallest first. Among a height's codec flavors, H.264
    /// ("avc1") is preferred — Windows always decodes it, so a pinned rung can never fail on
    /// a missing codec — then SDR VP9 ("vp09.00…", the only family above 1080p), then
    /// whatever remains; ties go to the highest bandwidth (the better audio pairing). When
    /// the machine has no VP9 decoder, pass includeVp9Only: false and the VP9-only heights
    /// are dropped instead of being offered as guaranteed failures.
    public static IReadOnlyList<HlsQuality> QualityLevels(
        IReadOnlyList<HlsVariant> variants, bool includeVp9Only = true) =>
        variants
            .GroupBy(v => v.Height)
            .Select(group => group
                .OrderByDescending(v => v.Codecs.StartsWith("avc1") ? 2
                    : v.Codecs.StartsWith("vp09.00") ? 1 : 0)
                .ThenByDescending(v => v.Bandwidth)
                .First())
            .Where(v => includeVp9Only || v.Codecs.StartsWith("avc1"))
            .OrderByDescending(v => v.Height)
            .Select(v => new HlsQuality(v.Height, v.Bandwidth))
            .ToList();

    /// Rewrites a master playlist to carry only its H.264 rungs. Media Foundation's HLS
    /// pipeline refuses the VP9 variants YouTube now includes (SourceNotSupported the moment
    /// the source opens, even with the VP9 decoder installed), so the player feeds
    /// AdaptiveMediaSource this filtered text instead of the raw manifest. A STREAM-INF line
    /// and the URI line after it travel as a pair; every other line passes through.
    public static string FilterToAvc(string masterPlaylist)
    {
        var lines = masterPlaylist.Split('\n');
        var kept = new List<string>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (!line.StartsWith("#EXT-X-STREAM-INF:"))
            {
                kept.Add(line);
                continue;
            }
            var codecs = CodecsPattern().Match(line);
            var keep = codecs.Success && codecs.Groups[1].Value.Contains("avc1");
            if (keep) kept.Add(line);
            if (i + 1 < lines.Length)
            {
                if (keep) kept.Add(lines[i + 1].TrimEnd('\r'));
                i++;
            }
        }
        return string.Join('\n', kept);
    }

    /// Rewrites a master playlist to carry only the single STREAM-INF/URI pair whose
    /// BANDWIDTH= attribute matches the target. Every non-STREAM-INF, non-EXT-X-MEDIA line
    /// passes through; audio-only STREAM-INF entries (which have no RESOLUTION) are filtered
    /// by the bandwidth check same as any other rung. EXT-X-MEDIA lines belonging to an audio
    /// group other than the kept variant's own AUDIO="..." group are dropped too — mpv (ffmpeg)
    /// otherwise probes every rendition in the master, including audio tracks nothing will ever
    /// play, which is pure open-latency. A variant with no AUDIO attribute leaves every
    /// EXT-X-MEDIA line untouched, since there's then nothing to match against.
    public static string FilterToBandwidth(string masterPlaylist, uint bandwidth)
    {
        var lines = masterPlaylist.Split('\n');

        string? audioGroup = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("#EXT-X-STREAM-INF:")) continue;
            var bandwidthMatch = BandwidthPattern().Match(line);
            if (!bandwidthMatch.Success || !uint.TryParse(bandwidthMatch.Groups[1].Value, out var bw) || bw != bandwidth) continue;
            var audioMatch = AudioGroupPattern().Match(line);
            if (audioMatch.Success) audioGroup = audioMatch.Groups[1].Value;
            break;
        }

        var kept = new List<string>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith("#EXT-X-MEDIA:"))
            {
                var groupMatch = MediaGroupIdPattern().Match(line);
                if (audioGroup is not null && groupMatch.Success && groupMatch.Groups[1].Value != audioGroup)
                    continue;
                kept.Add(line);
                continue;
            }
            if (!line.StartsWith("#EXT-X-STREAM-INF:"))
            {
                kept.Add(line);
                continue;
            }
            var bandwidthMatch2 = BandwidthPattern().Match(line);
            var keep = bandwidthMatch2.Success && uint.TryParse(bandwidthMatch2.Groups[1].Value, out var bw2) && bw2 == bandwidth;
            if (keep) kept.Add(line);
            if (i + 1 < lines.Length)
            {
                if (keep) kept.Add(lines[i + 1].TrimEnd('\r'));
                i++;
            }
        }
        return string.Join('\n', kept);
    }

    /// Selects the highest quality whose height does not exceed maxHeight, or the lowest
    /// available quality if all exceed it. Returns null only for an empty quality list.
    /// Expects the list to be ordered tallest-first, as QualityLevels returns it.
    public static HlsQuality? AutoQuality(IReadOnlyList<HlsQuality> qualities, int maxHeight) =>
        qualities.FirstOrDefault(q => q.Height <= maxHeight) ?? qualities.LastOrDefault();

    [GeneratedRegex(@"#EXT-X-STREAM-INF:([^\r\n]*)")]
    private static partial Regex StreamInfPattern();

    [GeneratedRegex(@"RESOLUTION=(\d+)x(\d+)")]
    private static partial Regex ResolutionPattern();

    [GeneratedRegex(@"BANDWIDTH=(\d+)")]
    private static partial Regex BandwidthPattern();

    [GeneratedRegex("AUDIO=\"([^\"]*)\"")]
    private static partial Regex AudioGroupPattern();

    [GeneratedRegex("GROUP-ID=\"([^\"]*)\"")]
    private static partial Regex MediaGroupIdPattern();

    [GeneratedRegex("CODECS=\"([^\"]*)\"")]
    private static partial Regex CodecsPattern();
}
