using System.Text.RegularExpressions;

namespace WinTube.Core.Player;

public enum DescriptionRunKind { Text, Timestamp, Url }

/// One piece of a parsed description. Seconds is set only for Timestamp, Url only for Url —
/// Text carries neither. Text is always the literal substring shown for that run (the
/// timestamp's own "1:02:03", the URL with any trimmed trailing punctuation put back into the
/// surrounding Text run instead).
public sealed record DescriptionRun(string Text, DescriptionRunKind Kind,
    double? Seconds = null, string? Url = null);

/// Splits a YouTube video description into runs for display: plain text, YouTube-style
/// timestamps (m:ss, mm:ss, h:mm:ss — converted to total seconds), and http(s) URLs. Consumed
/// by PlayerPage's description panel, which turns Timestamp/Url runs into Hyperlink runs.
public static partial class DescriptionText
{
    public static IReadOnlyList<DescriptionRun> Parse(string? description)
    {
        if (string.IsNullOrEmpty(description)) return [];

        var runs = new List<DescriptionRun>();
        var pos = 0;
        foreach (Match match in TokenPattern().Matches(description))
        {
            if (match.Groups["url"].Success)
            {
                var url = TrimTrailingUrlPunctuation(match.Value);
                if (url.Length == 0) continue;   // pathological: the whole match was punctuation
                AddPlain(runs, description, pos, match.Index);
                runs.Add(new DescriptionRun(url, DescriptionRunKind.Url, Url: url));
                pos = match.Index + url.Length;
            }
            else
            {
                AddPlain(runs, description, pos, match.Index);
                runs.Add(new DescriptionRun(match.Value, DescriptionRunKind.Timestamp, Seconds(match)));
                pos = match.Index + match.Length;
            }
        }
        AddPlain(runs, description, pos, description.Length);
        return runs;
    }

    private static void AddPlain(List<DescriptionRun> runs, string source, int start, int end)
    {
        if (end > start) runs.Add(new DescriptionRun(source[start..end], DescriptionRunKind.Text));
    }

    private static double Seconds(Match match)
    {
        var hours = match.Groups["h"].Success ? int.Parse(match.Groups["h"].Value) : 0;
        var minutes = int.Parse(match.Groups["m"].Value);
        var seconds = int.Parse(match.Groups["s"].Value);
        return hours * 3600 + minutes * 60 + seconds;
    }

    /// Strips trailing sentence punctuation a description writer almost certainly didn't mean
    /// as part of the link, plus an unbalanced closing bracket (more `)`/`]` than `(`/`[` inside
    /// the match) — a URL that legitimately ends in one, like a Wikipedia "Foo_(bar)" link,
    /// keeps it, since there the counts come out even.
    private static string TrimTrailingUrlPunctuation(string url)
    {
        while (url.Length > 0)
        {
            var last = url[^1];
            if (last is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"')
            {
                url = url[..^1];
                continue;
            }
            if (last == ')' && Unbalanced(url, '(', ')')) { url = url[..^1]; continue; }
            if (last == ']' && Unbalanced(url, '[', ']')) { url = url[..^1]; continue; }
            break;
        }
        return url;
    }

    private static bool Unbalanced(string s, char open, char close) =>
        s.Count(c => c == close) > s.Count(c => c == open);

    /// Named-group alternation: a URL (greedy up to whitespace) or a timestamp. The timestamp
    /// branch is boundary-guarded on both sides ((?<!\d) / (?!\d)) so it never fires inside a
    /// longer run of digits, and its minutes/seconds groups are capped at 0-59 so something like
    /// "123:456:789" is correctly left alone. Matches never overlap — .NET resumes scanning from
    /// the end of each match — so a URL containing what looks like a timestamp (an odd port-like
    /// suffix, say) is never re-split.
    [GeneratedRegex(
        @"(?<url>https?://\S+)|(?<ts>(?<!\d)(?:(?<h>\d{1,2}):)?(?<m>[0-5]?\d):(?<s>[0-5]\d)(?!\d))")]
    private static partial Regex TokenPattern();
}
