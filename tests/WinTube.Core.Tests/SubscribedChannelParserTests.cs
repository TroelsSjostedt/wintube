using System.Text.Json;
using WinTube.Core.Channel;

namespace WinTube.Core.Tests;

public class SubscribedChannelParserTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Channels_ParsesTvTiles_InDocumentOrder_DedupedById()
    {
        var json = Parse("{\"items\":[" +
            "{\"tileRenderer\":{" +
            "\"onSelectCommand\":{\"browseEndpoint\":{\"browseId\":\"UCaaa\"}}," +
            "\"metadata\":{\"tileMetadataRenderer\":{\"title\":{\"simpleText\":\"Vsauce\"}," +
            "\"lines\":[{\"lineRenderer\":{\"items\":[{\"lineItemRenderer\":{" +
            "\"text\":{\"simpleText\":\"1.2M subscribers\"}}}]}}]}}," +
            "\"thumbnail\":{\"thumbnails\":[{\"url\":\"https://yt3.ggpht.com/x=s176\",\"width\":176}]}}}," +
            "{\"tileRenderer\":{" +
            "\"onSelectCommand\":{\"browseEndpoint\":{\"browseId\":\"UCaaa\"}}," +
            "\"metadata\":{\"tileMetadataRenderer\":{\"title\":{\"simpleText\":\"Vsauce again\"}}}}}]}");
        var channel = Assert.Single(SubscribedChannelParser.Channels(json));
        Assert.Equal("UCaaa", channel.Id);
        Assert.Equal("Vsauce", channel.Title);
        Assert.Equal("https://yt3.ggpht.com/x=s176", channel.AvatarUrl);
        Assert.Equal("1.2M subscribers", channel.Detail);
    }

    [Fact]
    public void Channels_RejectsVideoShapedCells()
    {
        var json = Parse("{\"items\":[{\"tileRenderer\":{" +
            "\"contentType\":\"TILE_CONTENT_TYPE_VIDEO\"," +
            "\"onSelectCommand\":{\"browseEndpoint\":{\"browseId\":\"UCbbb\"}}}}]}");
        Assert.Empty(SubscribedChannelParser.Channels(json));
    }

    [Fact]
    public void ChannelIdsInOrder_CollectsEveryUcId_FirstOccurrenceFirst()
    {
        var json = Parse("{\"a\":[{\"browseEndpoint\":{\"browseId\":\"UCzz\"}}," +
            "{\"browseEndpoint\":{\"browseId\":\"FEhistory\"}}," +
            "{\"browseEndpoint\":{\"browseId\":\"UCaa\"}}," +
            "{\"browseEndpoint\":{\"browseId\":\"UCzz\"}}]}");
        Assert.Equal(["UCzz", "UCaa"], SubscribedChannelParser.ChannelIdsInOrder(json));
    }
}
