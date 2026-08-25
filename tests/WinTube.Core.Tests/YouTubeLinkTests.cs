using WinTube.Core.Links;

namespace WinTube.Core.Tests;

public class YouTubeLinkTests
{
    private const string Id = "dQw4w9WgXcQ";

    [Fact]
    public void For_PlainAndTimestamped()
    {
        Assert.Equal($"https://youtu.be/{Id}", YouTubeLink.For(Id));
        Assert.Equal($"https://youtu.be/{Id}?t=754",
            YouTubeLink.For(Id, TimeSpan.FromSeconds(754.9)));   // floored
    }

    [Theory]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", null)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=754", 754)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", null)]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=90s", 90)]
    [InlineData("https://youtube.com/watch?list=PL123&v=dQw4w9WgXcQ&start=30", 30)]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ", null)]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ", null)]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", null)]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ?t=5", 5)]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ", null)]
    [InlineData("http://youtu.be/dQw4w9WgXcQ", null)]
    [InlineData("  https://youtu.be/dQw4w9WgXcQ  ", null)]          // trimmed
    [InlineData("wintube://watch?v=dQw4w9WgXcQ&t=1h2m3s", 3723)]
    public void TryParse_AcceptedShapes(string url, int? startSeconds)
    {
        var parsed = YouTubeLink.TryParse(url);
        Assert.NotNull(parsed);
        Assert.Equal(Id, parsed!.Value.VideoId);
        Assert.Equal(startSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
            parsed.Value.StartAt);
    }

    [Theory]
    [InlineData("dQw4w9WgXcQ")]                                     // bare id is a search
    [InlineData("6502 computer")]
    [InlineData("https://vimeo.com/12345")]
    [InlineData("https://www.youtube.com/watch?v=short")]           // malformed id
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQtoolong")]
    [InlineData("https://www.youtube.com/feed/subscriptions")]
    [InlineData("https://youtu.be/")]
    [InlineData("wintube://sync")]
    [InlineData("")]
    public void TryParse_Rejections(string text)
    {
        Assert.Null(YouTubeLink.TryParse(text));
    }

    [Fact]
    public void TryParse_BadStartTime_StillParsesTheVideo()
    {
        var parsed = YouTubeLink.TryParse($"https://youtu.be/{Id}?t=abc");
        Assert.Equal((Id, (TimeSpan?)null), parsed);
    }

    [Fact]
    public void TryParse_OverflowingStartTime_StillParsesTheVideo()
    {
        var parsed = YouTubeLink.TryParse($"https://youtu.be/{Id}?t=2147483648");
        Assert.Equal((Id, (TimeSpan?)null), parsed);
    }

    [Fact]
    public void TryParse_MultiplyOverflowingStartTime_StillParsesTheVideo()
    {
        var parsed = YouTubeLink.TryParse($"https://youtu.be/{Id}?t=9223372036854775807h");
        Assert.Equal((Id, (TimeSpan?)null), parsed);
    }

    [Theory]
    [InlineData(754, "12:34")]
    [InlineData(3754, "1:02:34")]
    [InlineData(34, "0:34")]
    public void Format_MatchesTheMenuShapes(int seconds, string expected)
    {
        Assert.Equal(expected, YouTubeLink.Format(TimeSpan.FromSeconds(seconds)));
    }
}
