using WinTube.Core.Player;

namespace WinTube.Core.Tests;

public class DescriptionTextTests
{
    private static DescriptionRun Plain(string text) => new(text, DescriptionRunKind.Text);

    [Fact]
    public void Parse_NullOrEmpty_ReturnsNoRuns()
    {
        Assert.Empty(DescriptionText.Parse(null));
        Assert.Empty(DescriptionText.Parse(""));
    }

    [Fact]
    public void Parse_PlainTextOnly_IsOneTextRun()
    {
        var runs = DescriptionText.Parse("just some words, nothing special");
        var run = Assert.Single(runs);
        Assert.Equal(DescriptionRunKind.Text, run.Kind);
        Assert.Equal("just some words, nothing special", run.Text);
    }

    [Fact]
    public void Parse_MixedTextAndTimestampAndUrl()
    {
        var runs = DescriptionText.Parse("Intro at 0:30 then check https://example.com/x for more.");

        Assert.Equal(
        [
            Plain("Intro at "),
            new DescriptionRun("0:30", DescriptionRunKind.Timestamp, 30),
            Plain(" then check "),
            new DescriptionRun("https://example.com/x", DescriptionRunKind.Url, Url: "https://example.com/x"),
            Plain(" for more."),
        ], runs);
    }

    [Fact]
    public void Parse_MultipleTimestamps_AllConverted()
    {
        var runs = DescriptionText.Parse("0:00 Intro\n1:23 Chapter two\n12:34 Chapter three");
        var timestamps = runs.Where(r => r.Kind == DescriptionRunKind.Timestamp).ToList();
        Assert.Equal(3, timestamps.Count);
        Assert.Equal([0.0, 83.0, 754.0], timestamps.Select(t => t.Seconds));
    }

    [Fact]
    public void Parse_HourMinuteSecond_ConvertsToTotalSeconds()
    {
        var runs = DescriptionText.Parse("Full track at 1:02:03 in the video");
        var ts = Assert.Single(runs, r => r.Kind == DescriptionRunKind.Timestamp);
        Assert.Equal("1:02:03", ts.Text);
        Assert.Equal(3600 + 120 + 3, ts.Seconds);
    }

    [Fact]
    public void Parse_Url_StopsAtWhitespace()
    {
        var runs = DescriptionText.Parse("See https://example.com/path?query=1 right there");
        var url = Assert.Single(runs, r => r.Kind == DescriptionRunKind.Url);
        Assert.Equal("https://example.com/path?query=1", url.Text);
        Assert.Equal("https://example.com/path?query=1", url.Url);
    }

    [Fact]
    public void Parse_TimestampInParens_ParensAreNotPartOfTheRun()
    {
        var runs = DescriptionText.Parse("Best bit (3:45) right there");

        Assert.Equal(
        [
            Plain("Best bit ("),
            new DescriptionRun("3:45", DescriptionRunKind.Timestamp, 225),
            Plain(") right there"),
        ], runs);
    }

    [Fact]
    public void Parse_TrailingPeriod_IsNotPartOfTheUrl()
    {
        var runs = DescriptionText.Parse("Source: https://example.com/page.");

        Assert.Equal(
        [
            Plain("Source: "),
            new DescriptionRun("https://example.com/page", DescriptionRunKind.Url, Url: "https://example.com/page"),
            Plain("."),
        ], runs);
    }

    [Fact]
    public void Parse_UrlInParens_ClosingParenIsNotPartOfTheUrlUnlessBalanced()
    {
        var unbalanced = DescriptionText.Parse("(see https://example.com/x)");
        Assert.Equal(
        [
            Plain("(see "),
            new DescriptionRun("https://example.com/x", DescriptionRunKind.Url, Url: "https://example.com/x"),
            Plain(")"),
        ], unbalanced);

        var balanced = DescriptionText.Parse("wiki https://en.wikipedia.org/wiki/Foo_(bar) page");
        Assert.Equal(
        [
            Plain("wiki "),
            new DescriptionRun("https://en.wikipedia.org/wiki/Foo_(bar)", DescriptionRunKind.Url,
                Url: "https://en.wikipedia.org/wiki/Foo_(bar)"),
            Plain(" page"),
        ], balanced);
    }

    [Fact]
    public void Parse_DoesNotMatchTimestampInsideALongerNumber()
    {
        var runs = DescriptionText.Parse("Call 123:456:789 for support");
        Assert.DoesNotContain(runs, r => r.Kind == DescriptionRunKind.Timestamp);
    }
}
