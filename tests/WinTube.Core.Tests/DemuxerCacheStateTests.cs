using WinTube.Core.Player;

namespace WinTube.Core.Tests;

public class DemuxerCacheStateTests
{
    [Fact]
    public void ParsesSeekableRanges_FromTheFullStateObject()
    {
        // Shape of mpv's demuxer-cache-state string form: ranges plus unrelated fields around them.
        const string json = """
            {"cache-end":123.5,"reader-pts":10.0,"eof":false,"underrun":false,"idle":false,
             "total-bytes":1048576,"fw-bytes":524288,"raw-input-rate":250000.0,
             "seekable-ranges":[{"start":0.0,"end":42.5},{"start":100,"end":180.25}],
             "bof-cached":true}
            """;
        Assert.Equal([(0.0, 42.5), (100.0, 180.25)], DemuxerCacheState.ParseRanges(json));
    }

    [Fact]
    public void EmptyRanges_GiveEmptyList()
    {
        Assert.Empty(DemuxerCacheState.ParseRanges("""{"seekable-ranges":[],"eof":false}"""));
    }

    [Fact]
    public void MissingKey_GivesEmptyList()
    {
        Assert.Empty(DemuxerCacheState.ParseRanges("""{"cache-end":5.0}"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    [InlineData("""{"seekable-ranges":"nope"}""")]
    [InlineData("""{"seekable-ranges":{"start":1,"end":2}}""")]
    [InlineData("""{"seekable-ranges":[1,"x",null]}""")]
    public void GarbageInput_GivesEmptyList_AndNeverThrows(string? json)
    {
        Assert.Empty(DemuxerCacheState.ParseRanges(json));
    }

    [Fact]
    public void BadEntries_AreSkipped_GoodOnesKept()
    {
        const string json = """
            {"seekable-ranges":[{"start":"a","end":2},{"start":5},{"start":10,"end":20},{"start":30,"end":30},{"start":9,"end":4}]}
            """;
        // Wrong-typed, incomplete, empty (end == start) and inverted spans are dropped.
        Assert.Equal([(10.0, 20.0)], DemuxerCacheState.ParseRanges(json));
    }
}
