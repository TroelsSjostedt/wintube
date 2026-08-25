using WinTube.Core.Models;

namespace WinTube.Core.Tests;

public class FeedSectionTests
{
    private static VideoItem Video(string id, bool isShort = false) =>
        new() { Id = id, Title = "t", IsShort = isShort };

    [Fact]
    public void Admitting_ShortsRow_VouchesForEverything()
    {
        var row = new FeedSection("id", "Shorts", [], null, IsShorts: true);
        var admitted = row.Admitting([Video("a"), Video("b", isShort: true)]);
        Assert.Equal(2, admitted.Count);
        Assert.All(admitted, item => Assert.True(item.IsShort));
    }

    [Fact]
    public void Admitting_OrdinaryRow_DropsShorts()
    {
        var row = new FeedSection("id", "Recommended", [], null, IsShorts: false);
        var admitted = row.Admitting([Video("a"), Video("b", isShort: true)]);
        Assert.Equal("a", Assert.Single(admitted).Id);
    }
}
