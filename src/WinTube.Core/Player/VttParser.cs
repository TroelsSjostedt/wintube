using System.Net;
using System.Text.RegularExpressions;

namespace WinTube.Core.Player;

/// One transcript line: where it starts and its plain text.
public sealed record TranscriptCue(TimeSpan Start, string Text);

/// Parses a WebVTT subtitle file into transcript cues — the same fmt=vtt payload the player
/// hands mpv for subtitles, re-read here as text for the transcript panel.
public static class VttParser
{
    private static readonly Regex TimingLine = new(
        @"^(?:(\d+):)?(\d{2}):(\d{2})\.(\d{3})\s+-->", RegexOptions.Compiled);

    private static readonly Regex InlineTags = new(@"<[^>]*>", RegexOptions.Compiled);

    public static IReadOnlyList<TranscriptCue> Parse(string vtt)
    {
        var cues = new List<TranscriptCue>();
        var lines = vtt.Split('\n');
        var previousText = "";
        for (var i = 0; i < lines.Length; i++)
        {
            var match = TimingLine.Match(lines[i].TrimEnd('\r'));
            if (!match.Success) continue;

            var start = new TimeSpan(0,
                match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : 0,
                int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value),
                int.Parse(match.Groups[4].Value));

            var text = new List<string>();
            for (i++; i < lines.Length; i++)
            {
                var line = WebUtility.HtmlDecode(
                    InlineTags.Replace(lines[i].TrimEnd('\r'), "")).Trim();
                if (line.Length == 0) break;
                text.Add(line);
            }
            var joined = string.Join(' ', text);

            // ASR tracks roll: a cue repeats the previous cue's text before adding its new
            // line(s). Strip the repeat so each line reads once; a cue that was ONLY the
            // repeat contributes nothing.
            if (previousText.Length > 0 && joined.StartsWith(previousText, StringComparison.Ordinal))
                joined = joined[previousText.Length..].TrimStart();
            if (joined.Length == 0) continue;

            cues.Add(new TranscriptCue(start, joined));
            previousText = joined;
        }
        return cues;
    }
}
