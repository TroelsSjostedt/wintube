using WinTube.Core.SponsorBlock;

namespace WinTube.Core.Tests;

public class SponsorSegmentTests
{
    private static SponsorSegment Seg(string id, double start, double end,
        SponsorCategory category = SponsorCategory.Sponsor) => new(id, category, start, end);

    [Fact]
    public void ApiNames_RoundTrip()
    {
        Assert.Equal("selfpromo", SponsorCategory.SelfPromo.ApiName());
        Assert.Equal("music_offtopic", SponsorCategory.MusicOffTopic.ApiName());
        Assert.Equal(SponsorCategory.Interaction, SponsorCategories.FromApiName("interaction"));
        Assert.Null(SponsorCategories.FromApiName("exclusive_access"));   // unknown → ignored
    }

    [Fact]
    public void DisplayNames_MatchTheTvOsToastCopy()
    {
        Assert.Equal("Sponsor", SponsorCategory.Sponsor.DisplayName());
        Assert.Equal("Subscribe reminder", SponsorCategory.Interaction.DisplayName());
        Assert.Equal("Non-music section", SponsorCategory.MusicOffTopic.DisplayName());
    }

    [Fact]
    public void DefaultSkipped_IsTheFourNonEditorialCategories()
    {
        Assert.Equal(
            new[] { SponsorCategory.Sponsor, SponsorCategory.SelfPromo,
                    SponsorCategory.Interaction, SponsorCategory.MusicOffTopic }.ToHashSet(),
            SponsorCategories.DefaultSkipped);
    }

    [Fact]
    public void Contains_IsHalfOpen()
    {
        var segment = Seg("a", 10, 20);
        Assert.True(segment.Contains(10));
        Assert.True(segment.Contains(19.99));
        Assert.False(segment.Contains(20));
        Assert.False(segment.Contains(9.99));
    }

    [Fact]
    public void Merge_FoldsOverlappingAndTouching_KeepingEarlierIdentity()
    {
        var merged = SponsorSegment.Merge(
        [
            Seg("b", 15, 25, SponsorCategory.SelfPromo),
            Seg("a", 10, 16),
            Seg("c", 25.4, 30, SponsorCategory.Interaction),   // touching (gap 0.4 ≤ 0.5)
            Seg("d", 50, 60),
        ]);
        Assert.Equal(2, merged.Count);
        Assert.Equal(("a", SponsorCategory.Sponsor, 10.0, 30.0),
            (merged[0].Id, merged[0].Category, merged[0].Start, merged[0].End));
        Assert.Equal("d", merged[1].Id);
    }

    [Fact]
    public void Merge_DropsFullyContainedSegments()
    {
        var merged = SponsorSegment.Merge([Seg("a", 10, 30), Seg("b", 15, 20)]);
        Assert.Equal(("a", 30.0), (Assert.Single(merged).Id, merged[0].End));
    }

    [Fact]
    public void NextToSkip_SkipsTheSkippedSet()
    {
        var segments = new[] { Seg("a", 10, 20), Seg("b", 15, 25) };
        Assert.Equal("a", SponsorSegment.NextToSkip(segments, 12, new HashSet<string>())!.Id);
        Assert.Equal("b", SponsorSegment.NextToSkip(segments, 16, new HashSet<string> { "a" })!.Id);
        Assert.Null(SponsorSegment.NextToSkip(segments, 30, new HashSet<string>()));
        Assert.Null(SponsorSegment.NextToSkip(segments, 12, new HashSet<string> { "a", "b" }));
    }
}
