namespace WinTube.Core.Feed;

/// Formats and parses "how long ago" strings for feed cards. InnerTube hands the age over as
/// already-rendered text, never a timestamp; parsing it back at fetch time means every card
/// reads the same regardless of renderer, and the age keeps ticking on screen.
public static class RelativeTime
{
    private const double Minute = 60;
    private const double Hour = 60 * Minute;
    private const double Day = 24 * Hour;
    private const double Week = 7 * Day;
    /// Average month — the source text never had more precision than this anyway.
    private const double Month = 30.44 * Day;
    private const double Year = 365.25 * Day;

    private static readonly Dictionary<string, double> Units = new()
    {
        ["second"] = 1, ["minute"] = Minute, ["hour"] = Hour,
        ["day"] = Day, ["week"] = Week, ["month"] = Month, ["year"] = Year,
    };

    /// Scans for the `<number> <unit> ago` fragment of a subtitle — the same line usually
    /// carries a view count too. Anchors to the older end of the window "3 days ago" spans.
    /// Walks word triples so a stray number ("12K views") can't be read as a count.
    public static DateTimeOffset? Parse(string text, DateTimeOffset now)
    {
        var words = System.Text.RegularExpressions.Regex
            .Split(text.ToLowerInvariant(), "[^a-z0-9]+")
            .Where(w => w.Length > 0).ToArray();
        for (var i = 0; i + 2 < words.Length; i++)
        {
            if (words[i + 2] != "ago") continue;
            if (!int.TryParse(words[i], out var count)) continue;
            var name = words[i + 1].TrimEnd('s');
            if (Units.TryGetValue(name, out var unit))
                return now.AddSeconds(-unit * count);
        }
        return null;
    }

    public static string? Format(DateTimeOffset date, DateTimeOffset now)
    {
        var elapsed = (now - date).TotalSeconds;
        if (elapsed < -Minute) return null;   // clock skew tolerance
        return elapsed switch
        {
            < Minute => "Just now",
            < Hour => Unit(elapsed / Minute, "minute"),
            < Day => Unit(elapsed / Hour, "hour"),
            < Week => Unit(elapsed / Day, "day"),
            < Month => Unit(elapsed / Week, "week"),
            < Year => Unit(elapsed / Month, "month"),
            _ => Unit(elapsed / Year, "year"),
        };
    }

    private static string Unit(double value, string name)
    {
        var count = Math.Max(1, (int)value);
        return $"{count} {name}{(count == 1 ? "" : "s")} ago";
    }
}
