using WinTube.Core.Player;

namespace WinTube.Core.Tests;

public class HlsVariantParserTests
{
    private const string Manifest = """
        #EXTM3U
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="a",NAME="en"
        #EXT-X-STREAM-INF:BANDWIDTH=6321284,RESOLUTION=1920x1080,FRAME-RATE=60,CODECS="avc1.64002A,mp4a.40.2"
        v1080-avc.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=7689974,RESOLUTION=1920x1080,FRAME-RATE=60,CODECS="vp09.00.41.08,mp4a.40.2"
        v1080-vp9.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=8047925,RESOLUTION=1920x1080,FRAME-RATE=60,CODECS="vp09.02.41.10.01.09.16.09.00,mp4a.40.2"
        v1080-hdr.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=28033687,RESOLUTION=3840x2160,FRAME-RATE=60,CODECS="vp09.00.51.08,mp4a.40.2"
        v2160.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=801959,RESOLUTION=640x360,CODECS="avc1.4D401E,mp4a.40.2"
        v360-hi.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=619812,RESOLUTION=640x360,CODECS="vp09.00.21.08,mp4a.40.2"
        v360-lo.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=144000,CODECS="mp4a.40.2"
        audio-only.m3u8
        """;

    [Fact]
    public void Parse_ReadsEveryVariantWithAResolution()
    {
        var variants = HlsVariantParser.Parse(Manifest);
        Assert.Equal(6, variants.Count);
        Assert.Contains(variants, v => v.Height == 2160 && v.Bandwidth == 28033687);
        Assert.DoesNotContain(variants, v => v.Bandwidth == 144000);   // audio-only skipped
    }

    [Fact]
    public void QualityLevels_OnePerHeight_Descending_PreferringAvcOverSdrVp9OverHdr()
    {
        var levels = HlsVariantParser.QualityLevels(HlsVariantParser.Parse(Manifest));
        Assert.Equal([2160, 1080, 360], levels.Select(l => l.Height));
        Assert.Equal(6321284u, levels.Single(l => l.Height == 1080).Bandwidth);  // avc1 wins
        Assert.Equal(801959u, levels.Single(l => l.Height == 360).Bandwidth);    // avc1 over vp09
        Assert.Equal(28033687u, levels.Single(l => l.Height == 2160).Bandwidth); // vp09-only height kept
    }

    [Fact]
    public void QualityLevels_WithoutVp9Decoder_DropsVp9OnlyHeights()
    {
        var levels = HlsVariantParser.QualityLevels(HlsVariantParser.Parse(Manifest), includeVp9Only: false);
        Assert.Equal([1080, 360], levels.Select(l => l.Height));   // 2160p (VP9-only) gone
    }

    [Fact]
    public void QualityLevels_FallsBackToSdrVp9_WhenNoAvc()
    {
        var text = "#EXT-X-STREAM-INF:BANDWIDTH=200,RESOLUTION=1280x720,CODECS=\"vp09.00.40.08\"\nv1\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=300,RESOLUTION=1280x720,CODECS=\"vp09.02.40.10\"\nv2\n";
        var levels = HlsVariantParser.QualityLevels(HlsVariantParser.Parse(text));
        Assert.Equal(200u, Assert.Single(levels).Bandwidth);
    }

    [Fact]
    public void FilterToAvc_KeepsHeaderAndH264Pairs_DropsVp9Pairs()
    {
        var filtered = HlsVariantParser.FilterToAvc(Manifest);
        Assert.Contains("#EXT-X-MEDIA:TYPE=AUDIO", filtered);            // header passes through
        Assert.Contains("v1080-avc.m3u8", filtered);
        Assert.Contains("v360-hi.m3u8", filtered);
        Assert.DoesNotContain("v1080-vp9.m3u8", filtered);
        Assert.DoesNotContain("v1080-hdr.m3u8", filtered);
        Assert.DoesNotContain("v2160.m3u8", filtered);
        Assert.DoesNotContain("vp09", filtered);
        var variants = HlsVariantParser.Parse(filtered);
        Assert.Equal([1080, 360], HlsVariantParser.QualityLevels(variants).Select(q => q.Height));
    }

    [Fact]
    public void Parse_GarbageAndEmpty_YieldNothing()
    {
        Assert.Empty(HlsVariantParser.Parse(""));
        Assert.Empty(HlsVariantParser.Parse("#EXT-X-STREAM-INF:BANDWIDTH=notanumber,RESOLUTION=axb\nx\n"));
    }

    [Fact]
    public void FilterToBandwidth_KeepsOnlyThatPair_AndAllOtherLines()
    {
        var filtered = HlsVariantParser.FilterToBandwidth(Manifest, 6321284);
        Assert.Contains("#EXT-X-MEDIA:TYPE=AUDIO", filtered);
        Assert.Contains("v1080-avc.m3u8", filtered);
        Assert.DoesNotContain("v1080-vp9.m3u8", filtered);
        Assert.DoesNotContain("v2160.m3u8", filtered);
        Assert.DoesNotContain("v360-hi.m3u8", filtered);
        // The audio-only STREAM-INF pair is dropped too: a STREAM-INF pair is kept iff its
        // bandwidth matches. Real YouTube audio travels on #EXT-X-MEDIA lines, which pass through.
        Assert.DoesNotContain("audio-only.m3u8", filtered);
        Assert.Single(HlsVariantParser.Parse(filtered));
    }

    [Fact]
    public void AutoQuality_HighestAtOrBelowScreen()
    {
        var levels = HlsVariantParser.QualityLevels(HlsVariantParser.Parse(Manifest)); // 2160, 1080, 360
        Assert.Equal(1080, HlsVariantParser.AutoQuality(levels, 1440)!.Height);
        Assert.Equal(2160, HlsVariantParser.AutoQuality(levels, 2160)!.Height);
        Assert.Equal(360, HlsVariantParser.AutoQuality(levels, 240)!.Height);   // everything too tall -> lowest
        Assert.Null(HlsVariantParser.AutoQuality([], 1080));
    }
}
