using WinTube.Core.InnerTube;
using WinTube.Core.Player;

namespace WinTube.Core.Tests;

public class ChapterServiceTests
{
    private static string ChapterJson(string title, long startMillis) =>
        "{\"chapterRenderer\":{\"title\":{\"simpleText\":\"" + title + "\"}," +
        "\"timeRangeStartMillis\":" + startMillis + "}}";

    /// A watch-page response whose player bar carries the given markersMap entries.
    private static string WatchPage(string markersMapEntries) =>
        "{\"playerOverlays\":{\"playerOverlayRenderer\":{" +
        "\"decoratedPlayerBarRenderer\":{\"decoratedPlayerBarRenderer\":{" +
        "\"playerBar\":{\"multiMarkersPlayerBarRenderer\":{" +
        "\"markersMap\":[" + markersMapEntries + "]}}}}}}}";

    private static string MarkersEntry(string key, params string[] chapters) =>
        "{\"key\":\"" + key + "\",\"value\":{\"chapters\":[" +
        string.Join(",", chapters) + "]}}";

    private static ChapterService Make(string response)
    {
        var handler = new StubHttpHandler((_, _) => StubHttpHandler.JsonResponse(response));
        var client = new InnerTubeClient(new HttpClient(handler), new Secrets("K", "", ""));
        return new ChapterService(client);
    }

    private static readonly IReadOnlyList<Chapter> Three =
    [
        new("Intro", TimeSpan.Zero),
        new("Middle", TimeSpan.FromSeconds(60)),
        new("Outro", TimeSpan.FromSeconds(120)),
    ];

    [Fact]
    public async Task Get_ParsesChaptersInOrder()
    {
        var service = Make(WatchPage(MarkersEntry("AUTO_CHAPTERS",
            ChapterJson("Intro", 0),
            ChapterJson("Locate the video", 48000),
            ChapterJson("Outro", 247000))));

        var chapters = await service.GetAsync("vid");

        Assert.Equal(3, chapters.Count);
        Assert.Equal(new Chapter("Intro", TimeSpan.Zero), chapters[0]);
        Assert.Equal(new Chapter("Locate the video", TimeSpan.FromSeconds(48)), chapters[1]);
        Assert.Equal(new Chapter("Outro", TimeSpan.FromSeconds(247)), chapters[2]);
    }

    [Fact]
    public async Task Get_ReturnsEmpty_WhenThePageHasNoMarkers()
    {
        var service = Make("{\"contents\":{}}");

        Assert.Empty(await service.GetAsync("vid"));
    }

    [Fact]
    public async Task Get_SkipsMarkersEntriesWithoutChapters()
    {
        var service = Make(WatchPage(
            "{\"key\":\"HEATSEEKER\",\"value\":{\"heatmap\":{}}}," +
            MarkersEntry("DESCRIPTION_CHAPTERS", ChapterJson("Only", 5000))));

        var chapters = await service.GetAsync("vid");

        Assert.Equal([new Chapter("Only", TimeSpan.FromSeconds(5))], chapters);
    }

    [Fact]
    public void NextStart_ReturnsTheFollowingChapterStart()
    {
        Assert.Equal(TimeSpan.FromSeconds(60),
            ChapterService.NextStart(Three, TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(120),
            ChapterService.NextStart(Three, TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void NextStart_ReturnsNull_InTheLastChapter()
    {
        Assert.Null(ChapterService.NextStart(Three, TimeSpan.FromSeconds(120)));
        Assert.Null(ChapterService.NextStart(Three, TimeSpan.FromSeconds(500)));
    }

    [Fact]
    public void PreviousStart_GoesToTheCurrentChapterStart_WhenWellIntoIt()
    {
        Assert.Equal(TimeSpan.FromSeconds(60),
            ChapterService.PreviousStart(Three, TimeSpan.FromSeconds(90)));
    }

    [Fact]
    public void PreviousStart_GoesToThePreviousChapter_NearTheCurrentStart()
    {
        Assert.Equal(TimeSpan.Zero,
            ChapterService.PreviousStart(Three, TimeSpan.FromSeconds(61)));
        Assert.Equal(TimeSpan.FromSeconds(60),
            ChapterService.PreviousStart(Three, TimeSpan.FromSeconds(121)));
    }

    [Fact]
    public void PreviousStart_ReturnsNull_AtTheVeryStart()
    {
        Assert.Null(ChapterService.PreviousStart(Three, TimeSpan.Zero));
        Assert.Null(ChapterService.PreviousStart(Three, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void At_ReturnsTheCoveringChapter()
    {
        Assert.Equal(Three[0], ChapterService.At(Three, TimeSpan.FromSeconds(59)));
        Assert.Equal(Three[1], ChapterService.At(Three, TimeSpan.FromSeconds(60)));
        Assert.Equal(Three[2], ChapterService.At(Three, TimeSpan.FromSeconds(999)));
    }

    [Fact]
    public void At_ReturnsNull_WithoutChapters()
    {
        Assert.Null(ChapterService.At([], TimeSpan.FromSeconds(10)));
    }
}
