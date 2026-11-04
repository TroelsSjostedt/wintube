using System.Text.Json;
using WinTube.Core.InnerTube;

namespace WinTube.Core.Player;

/// One chapter of a video: its title and where it starts.
public sealed record Chapter(string Title, TimeSpan Start);

/// Fetches a video's chapters via InnerTube /next (WEB client, unauthenticated).
/// Chapters live in the watch page's player bar markers — NOT in the player response and
/// not reliably in the description (auto-generated chapters have no description timestamps):
/// playerOverlays…multiMarkersPlayerBarRenderer.markersMap, one entry per marker kind
/// (AUTO_CHAPTERS / DESCRIPTION_CHAPTERS); the first entry carrying chapters wins.
public sealed class ChapterService(InnerTubeClient innerTube)
{
    public async Task<IReadOnlyList<Chapter>> GetAsync(
        string videoId, CancellationToken ct = default)
    {
        using var doc = await innerTube.PostAsync("next", ClientKind.Web,
            new Dictionary<string, object?> { ["videoId"] = videoId }, ct: ct);

        foreach (var bar in Json.FindAllRenderers(doc.RootElement, "multiMarkersPlayerBarRenderer"))
        {
            if (Json.ValueAt(bar, "markersMap") is not
                { ValueKind: JsonValueKind.Array } markersMap)
                continue;
            foreach (var entry in markersMap.EnumerateArray())
            {
                var chapters = Parse(entry);
                if (chapters.Count > 0) return chapters;
            }
        }
        return [];
    }

    private static List<Chapter> Parse(JsonElement markersEntry)
    {
        var chapters = new List<Chapter>();
        if (Json.ValueAt(markersEntry, "value/chapters") is not
            { ValueKind: JsonValueKind.Array } array)
            return chapters;

        foreach (var item in array.EnumerateArray())
        {
            if (Json.ValueAt(item, "chapterRenderer") is not { } renderer) continue;
            var title = Json.InnerTubeText(Json.ValueAt(renderer, "title"));
            if (title is null ||
                Json.ValueAt(renderer, "timeRangeStartMillis") is not
                    { ValueKind: JsonValueKind.Number } millis)
                continue;
            chapters.Add(new Chapter(title, TimeSpan.FromMilliseconds(millis.GetInt64())));
        }
        chapters.Sort((a, b) => a.Start.CompareTo(b.Start));
        return chapters;
    }

    /// Within this span after a chapter start, a back-step crosses into the previous chapter.
    private static readonly TimeSpan BackStepTolerance = TimeSpan.FromSeconds(2);

    /// The start of the chapter after `position`, else null (in the last chapter already).
    public static TimeSpan? NextStart(IReadOnlyList<Chapter> chapters, TimeSpan position)
    {
        foreach (var chapter in chapters)
            if (chapter.Start > position) return chapter.Start;
        return null;
    }

    /// The start to jump to on a back-step: the current chapter's start when more than the
    /// tolerance into it, else the previous chapter's start — so repeated presses walk back
    /// chapter by chapter instead of pinning to the same start. Null at the first chapter's start.
    public static TimeSpan? PreviousStart(IReadOnlyList<Chapter> chapters, TimeSpan position)
    {
        TimeSpan? result = null;
        foreach (var chapter in chapters)
            if (chapter.Start < position - BackStepTolerance) result = chapter.Start;
            else break;
        return result;
    }

    /// The chapter covering `position`, else null (no chapters).
    public static Chapter? At(IReadOnlyList<Chapter> chapters, TimeSpan position)
    {
        Chapter? result = null;
        foreach (var chapter in chapters)
            if (chapter.Start <= position) result = chapter;
            else break;
        return result;
    }
}
