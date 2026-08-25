using System.Text.RegularExpressions;

namespace WinTube.Core.Links;

/// Builds and parses YouTube video links — the one grammar the copy button, the search box,
/// the command line and the wintube:// protocol all share.
public static partial class YouTubeLink
{
    /// The share form: youtu.be, with the position as whole seconds when given.
    public static string For(string videoId, TimeSpan? at = null) =>
        at is { } position
            ? $"https://youtu.be/{videoId}?t={(int)position.TotalSeconds}"
            : $"https://youtu.be/{videoId}";

    /// The parsed target of a link, or null when `text` isn't a YouTube link. Bare text is
    /// never treated as a video id — an 11-letter search term must stay a search.
    public static (string VideoId, TimeSpan? StartAt)? TryParse(string text)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0 || !Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;

        string? id = null;
        if (uri.Scheme is "http" or "https")
        {
            var host = uri.Host.ToLowerInvariant();
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (host == "youtu.be")
            {
                id = segments.Length >= 1 ? segments[0] : null;
            }
            else if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com"
                or "music.youtube.com")
            {
                if (segments.Length >= 1 && segments[0] == "watch")
                    id = QueryValue(uri, "v");
                else if (segments.Length >= 2 && segments[0] is "shorts" or "embed" or "live")
                    id = segments[1];
            }
            else
            {
                return null;
            }
        }
        else if (uri.Scheme == "wintube")
        {
            // wintube://watch?v={id}&t=… — the same watch grammar, for tools targeting us.
            if (uri.Host == "watch") id = QueryValue(uri, "v");
        }
        else
        {
            return null;
        }

        if (id is null || !VideoIdPattern().IsMatch(id)) return null;
        return (id, ParseStart(QueryValue(uri, "t") ?? QueryValue(uri, "start")));
    }

    /// The position as the copy menu shows it: 12:34, or 1:02:34 past an hour.
    public static string Format(TimeSpan position) =>
        position.TotalHours >= 1
            ? $"{(int)position.TotalHours}:{position.Minutes:D2}:{position.Seconds:D2}"
            : $"{position.Minutes}:{position.Seconds:D2}";

    private static string? QueryValue(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?')
                     .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (!pair[..eq].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }

    /// t=754, t=754s, t=1h2m3s, start=30 — anything else contributes no start time (a broken
    /// timestamp must not reject the video itself). Components are parsed as long and the
    /// running total is checked against int.MaxValue seconds throughout, so a huge value like
    /// t=2147483648 yields null rather than overflowing int.Parse or wrapping into a bogus seek.
    private static TimeSpan? ParseStart(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var match = StartPattern().Match(value);
        if (!match.Success) return null;

        long total = 0;
        if (!TryAddComponent(match.Groups[1], 3600, ref total)) return null;
        if (!TryAddComponent(match.Groups[2], 60, ref total)) return null;
        if (!TryAddComponent(match.Groups[3], 1, ref total)) return null;
        return TimeSpan.FromSeconds(total);
    }

    private static bool TryAddComponent(Group group, long multiplier, ref long total)
    {
        if (!group.Success) return true;
        if (!long.TryParse(group.Value, out var value)) return false;
        total += value * multiplier;
        return total <= int.MaxValue;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdPattern();

    [GeneratedRegex(@"^(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s?)?$")]
    private static partial Regex StartPattern();
}
