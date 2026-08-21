using WinTube.Core.Feed;

namespace WinTube.Core.Tests;

public class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("3 days ago", -3 * 24 * 60 * 60)]
    [InlineData("1 hour ago", -60 * 60)]
    [InlineData("2 weeks ago", -2 * 7 * 24 * 60 * 60)]
    [InlineData("12K views • 3 days ago", -3 * 24 * 60 * 60)]   // age mid-sentence
    [InlineData("Streamed 5 minutes ago", -5 * 60)]
    public void Parse_FindsAgeFragment(string text, int expectedSecondsFromNow)
    {
        Assert.Equal(Now.AddSeconds(expectedSecondsFromNow), RelativeTime.Parse(text, Now));
    }

    [Theory]
    [InlineData("12K views")]
    [InlineData("LIVE")]
    [InlineData("ago")]
    public void Parse_NoAge_ReturnsNull(string text)
    {
        Assert.Null(RelativeTime.Parse(text, Now));
    }

    [Theory]
    [InlineData(-30, "Just now")]
    [InlineData(-90, "1 minute ago")]
    [InlineData(-3 * 60 * 60, "3 hours ago")]
    [InlineData(-9 * 24 * 60 * 60, "1 week ago")]
    [InlineData(-800 * 24 * 60 * 60, "2 years ago")]
    public void Format_RendersBuckets(int secondsFromNow, string expected)
    {
        Assert.Equal(expected, RelativeTime.Format(Now.AddSeconds(secondsFromNow), Now));
    }

    [Fact]
    public void Format_FutureDate_ReturnsNull()
    {
        Assert.Null(RelativeTime.Format(Now.AddMinutes(5), Now));
    }
}
