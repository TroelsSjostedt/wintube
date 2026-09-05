using WinTube.Core;
using WinTube.Core.InnerTube;
using WinTube.Core.Player;

namespace WinTube.Core.Tests;

public class CommentServiceTests
{
    private static string EntityPayload(string id, string text, string author = "@a",
        string likes = "12K", string replies = "") =>
        "{\"commentEntityPayload\":{" +
        "\"properties\":{\"commentId\":\"" + id + "\"," +
        "\"content\":{\"content\":\"" + text + "\"},\"publishedTime\":\"2 years ago\"}," +
        "\"author\":{\"displayName\":\"" + author + "\"," +
        "\"avatarThumbnailUrl\":\"https://yt3.ggpht.com/" + id + "\"}," +
        "\"toolbar\":{\"likeCountNotliked\":\"" + likes + "\",\"replyCount\":\"" + replies + "\"}}}";

    private static string Thread(string id, string? repliesToken = null) =>
        "{\"commentThreadRenderer\":{" +
        "\"commentViewModel\":{\"commentViewModel\":{\"commentId\":\"" + id + "\"}}" +
        (repliesToken is null ? "" :
            ",\"replies\":{\"commentRepliesRenderer\":{\"contents\":[{\"continuationItemRenderer\":{" +
            "\"button\":{\"buttonRenderer\":{\"command\":{\"continuationCommand\":{\"token\":\"" +
            repliesToken + "\"}}}}}}]}}") +
        "}}";

    /// A page response: threads (or bare view models), the entity payloads, and optionally a
    /// trailing continuation item for the next page.
    private static string PageResponse(string items, string payloads, string? nextToken) =>
        "{\"onResponseReceivedEndpoints\":[{\"reloadContinuationItemsCommand\":{" +
        "\"continuationItems\":[" + items +
        (nextToken is null ? "" :
            ",{\"continuationItemRenderer\":{\"continuationEndpoint\":{" +
            "\"continuationCommand\":{\"token\":\"" + nextToken + "\"}}}}") +
        "]}}]," +
        "\"frameworkUpdates\":{\"entityBatchUpdate\":{\"mutations\":[" + payloads + "]}}}";

    private static (CommentService Service, StubHttpHandler Handler) Make(
        Func<string, string> respondByBody)
    {
        var handler = new StubHttpHandler((_, body) => StubHttpHandler.JsonResponse(respondByBody(body)));
        var client = new InnerTubeClient(new HttpClient(handler), new Secrets("K", "", ""));
        return (new CommentService(client), handler);
    }

    [Fact]
    public async Task TopLevel_FindsTheCommentSectionToken_ThenFetchesThePage()
    {
        var watchPage = "{\"contents\":{\"sections\":[" +
            "{\"itemSectionRenderer\":{\"sectionIdentifier\":\"other-section\"," +
            "\"contents\":[{\"continuationItemRenderer\":{\"continuationEndpoint\":{" +
            "\"continuationCommand\":{\"token\":\"WRONG\"}}}}]}}," +
            "{\"itemSectionRenderer\":{\"sectionIdentifier\":\"comment-item-section\"," +
            "\"contents\":[{\"continuationItemRenderer\":{\"continuationEndpoint\":{" +
            "\"continuationCommand\":{\"token\":\"COMMENTS_TOKEN\"}}}}]}}]}}";
        var page = PageResponse(Thread("c1"), EntityPayload("c1", "hello"), nextToken: null);
        var (service, handler) = Make(body => body.Contains("\"videoId\"") ? watchPage : page);

        var result = await service.TopLevelAsync("vid");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("\"continuation\":\"COMMENTS_TOKEN\"", handler.Requests[1].Body);
        Assert.Equal("hello", Assert.Single(result.Comments).Text);
        // WEB client, unauthenticated:
        Assert.Null(handler.Requests[0].Message.Headers.Authorization);
        Assert.Equal("1", handler.Requests[0].Message.Headers.GetValues("X-Youtube-Client-Name").Single());
        Assert.Contains("\"clientName\":\"WEB\"", handler.Requests[0].Body);
        Assert.Contains("\"clientVersion\":\"2.20260726.00.00\"", handler.Requests[0].Body);
    }

    [Fact]
    public async Task TopLevel_NoCommentSection_IsTyped()
    {
        var (service, _) = Make(_ => "{\"contents\":{}}");
        await Assert.ThrowsAsync<CommentsUnavailableException>(() => service.TopLevelAsync("vid"));
    }

    [Fact]
    public async Task Page_JoinsThreadsWithPayloads_InThreadOrder_WithReplyTokens()
    {
        var response = PageResponse(
            Thread("c2", repliesToken: "REPLIES_C2") + "," + Thread("c1"),
            EntityPayload("c1", "first", author: "@one", likes: "", replies: "") + "," +
            EntityPayload("c2", "second", author: "@two", likes: "3.4K", replies: "214"),
            nextToken: "PAGE2");
        var (service, _) = Make(_ => response);

        var page = await service.PageAsync("T");

        Assert.Equal(["c2", "c1"], page.Comments.Select(c => c.Id));   // renderer order wins
        var c2 = page.Comments[0];
        Assert.Equal("@two", c2.Author);
        Assert.Equal("https://yt3.ggpht.com/c2", c2.AvatarUrl);
        Assert.Equal("2 years ago", c2.PublishedTime);
        Assert.Equal("3.4K", c2.LikeCount);
        Assert.Equal("214", c2.ReplyCount);
        Assert.Equal("REPLIES_C2", c2.RepliesToken);
        Assert.True(c2.HasReplies);
        var c1 = page.Comments[1];
        Assert.Equal("0", c1.LikeCount);          // empty like count falls back to "0"
        Assert.Null(c1.RepliesToken);
        Assert.Equal("PAGE2", page.Continuation); // trailing item, not the nested reply token
    }

    [Fact]
    public async Task Page_RepliesShape_BareViewModelsInOrder_NoReplyTokens()
    {
        var response = PageResponse(
            "{\"commentViewModel\":{\"commentId\":\"r1\"}},{\"commentViewModel\":{\"commentId\":\"r2\"}}",
            EntityPayload("r1", "reply one") + "," + EntityPayload("r2", "reply two"),
            nextToken: null);
        var (service, _) = Make(_ => response);

        var page = await service.PageAsync("T");

        Assert.Equal(["r1", "r2"], page.Comments.Select(c => c.Id));
        Assert.All(page.Comments, c => Assert.Null(c.RepliesToken));
        Assert.Null(page.Continuation);
    }

    [Fact]
    public async Task Page_PayloadWithoutRenderer_AndRendererWithoutPayload_AreDropped()
    {
        var response = PageResponse(
            Thread("c1") + "," + Thread("ghost"),
            EntityPayload("c1", "kept") + "," + EntityPayload("orphan", "dropped"),
            nextToken: null);
        var (service, _) = Make(_ => response);
        var page = await service.PageAsync("T");
        Assert.Equal(["c1"], page.Comments.Select(c => c.Id));
    }

    [Fact]
    public async Task Page_Empty_IsAnEmptyPage()
    {
        var (service, _) = Make(_ => "{\"onResponseReceivedEndpoints\":[]}");
        var page = await service.PageAsync("T");
        Assert.Empty(page.Comments);
        Assert.Null(page.Continuation);
    }
}
