using System.Text.Json;
using WinTube.Core.InnerTube;

namespace WinTube.Core.Tests;

public class JsonTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ValueAt_FollowsSlashPath()
    {
        var root = Parse("""{"a":{"b":{"c":42}}}""");
        Assert.Equal(42, Json.ValueAt(root, "a/b/c")!.Value.GetInt32());
        Assert.Null(Json.ValueAt(root, "a/x/c"));
    }

    [Fact]
    public void StringAt_ReturnsNullForNonStringOrEmpty()
    {
        var root = Parse("""{"s":"hi","n":7,"e":""}""");
        Assert.Equal("hi", Json.StringAt(root, "s"));
        Assert.Null(Json.StringAt(root, "n"));
        Assert.Null(Json.StringAt(root, "e"));
    }

    [Fact]
    public void FindAllRenderers_CollectsAtAnyDepthInDocumentOrder()
    {
        var root = Parse("""
            {"contents":[{"tileRenderer":{"id":1}},{"x":{"tileRenderer":{"id":2}}}]}
            """);
        var found = Json.FindAllRenderers(root, "tileRenderer");
        Assert.Equal(2, found.Count);
        Assert.Equal(1, found[0].GetProperty("id").GetInt32());
        Assert.Equal(2, found[1].GetProperty("id").GetInt32());
    }

    [Fact]
    public void InnerTubeText_ReadsSimpleTextAndRuns()
    {
        Assert.Equal("Hello", Json.InnerTubeText(Parse("""{"simpleText":"Hello"}""")));
        Assert.Equal("a b", Json.InnerTubeText(Parse("""{"runs":[{"text":"a "},{"text":"b"}]}""")));
        Assert.Null(Json.InnerTubeText(Parse("""{"runs":[]}""")));
        Assert.Null(Json.InnerTubeText(Parse("""{"other":1}""")));
    }

    [Fact]
    public void ContainsKey_FindsKeyAnywhere()
    {
        var root = Parse("""{"a":[{"b":{"reelWatchEndpoint":{}}}]}""");
        Assert.True(Json.ContainsKey(root, "reelWatchEndpoint"));
        Assert.False(Json.ContainsKey(root, "watchEndpoint"));
    }

    [Fact]
    public void IntValue_ReadsIntDoubleAndNumericString()
    {
        Assert.Equal(18, Json.IntValue(Parse("""{"v":18}""").GetProperty("v")));
        Assert.Equal(18, Json.IntValue(Parse("""{"v":18.0}""").GetProperty("v")));
        Assert.Equal(18, Json.IntValue(Parse("""{"v":"18"}""").GetProperty("v")));
        Assert.Null(Json.IntValue(Parse("""{"v":"x"}""").GetProperty("v")));
    }
}
