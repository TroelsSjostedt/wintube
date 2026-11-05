using WinTube.Core.Player;

namespace WinTube.Core.Tests;

public class VttParserTests
{
    [Fact]
    public void Parse_ReadsCues_JoiningMultiLineTextWithSpaces()
    {
        var cues = VttParser.Parse("WEBVTT\n\n" +
            "00:00:48.000 --> 00:00:52.000\nLocate the video\nin Studio\n\n" +
            "00:01:10.500 --> 00:01:14.000\nAdd chapters\n");

        Assert.Equal(2, cues.Count);
        Assert.Equal(new TranscriptCue(TimeSpan.FromSeconds(48), "Locate the video in Studio"), cues[0]);
        Assert.Equal(new TranscriptCue(TimeSpan.FromSeconds(70.5), "Add chapters"), cues[1]);
    }

    [Fact]
    public void Parse_ReadsMinuteOnlyTimestamps()
    {
        var cues = VttParser.Parse("WEBVTT\n\n01:05.250 --> 01:08.000\nhello\n");

        Assert.Equal([new TranscriptCue(TimeSpan.FromSeconds(65.25), "hello")], cues);
    }

    [Fact]
    public void Parse_IgnoresCueSettingsAfterTheArrow()
    {
        var cues = VttParser.Parse("WEBVTT\n\n" +
            "00:00:01.000 --> 00:00:03.000 align:start position:0%\nhello\n");

        Assert.Equal([new TranscriptCue(TimeSpan.FromSeconds(1), "hello")], cues);
    }

    [Fact]
    public void Parse_StripsInlineTags_AndDecodesEntities()
    {
        var cues = VttParser.Parse("WEBVTT\n\n" +
            "00:00:01.000 --> 00:00:03.000\n<c>you<00:00:01.500><c> &amp;</c></c> me\n");

        Assert.Equal([new TranscriptCue(TimeSpan.FromSeconds(1), "you & me")], cues);
    }

    /// ASR tracks roll: each cue repeats the previous cue's text before adding the new line.
    /// The repeated prefix is stripped so the transcript reads each line once.
    [Fact]
    public void Parse_StripsARollingCuesRepeatedPrefix()
    {
        var cues = VttParser.Parse("WEBVTT\n\n" +
            "00:00:00.000 --> 00:00:02.000\nhello world\n\n" +
            "00:00:02.000 --> 00:00:04.000\nhello world\nnext line\n\n" +
            "00:00:04.000 --> 00:00:06.000\nnext line\n");

        Assert.Equal(
        [
            new TranscriptCue(TimeSpan.Zero, "hello world"),
            new TranscriptCue(TimeSpan.FromSeconds(2), "next line"),
        ], cues);
    }

    [Fact]
    public void Parse_SkipsCuesWithNoTextLeft()
    {
        var cues = VttParser.Parse("WEBVTT\n\n" +
            "00:00:01.000 --> 00:00:02.000\n<c></c>\n\n" +
            "00:00:02.000 --> 00:00:03.000\nreal text\n");

        Assert.Equal([new TranscriptCue(TimeSpan.FromSeconds(2), "real text")], cues);
    }

    [Fact]
    public void Parse_EmptyOrGarbage_ReturnsEmpty()
    {
        Assert.Empty(VttParser.Parse(""));
        Assert.Empty(VttParser.Parse("WEBVTT\n"));
        Assert.Empty(VttParser.Parse("not a vtt file at all"));
    }
}
