using System.Text.Json;
using WinTube.Core.InnerTube;
using WinTube.Core.Models;

namespace WinTube.Core.Player;

public sealed class CommentsUnavailableException()
    : Exception("Comments aren't available for this video.");

/// Fetches a video's comments and replies via InnerTube /next (WEB client, unauthenticated).
/// Two-step flow: /next {"videoId"} only says WHERE the comments are (a continuation token on
/// the watch page's comment section); /next {"continuation"} returns the comments. Bodies live
/// in frameworkUpdates as commentEntityPayload entities keyed by commentId; ordering and reply
/// tokens come from the renderer list — a page is parsed by joining the two on commentId.
public sealed class CommentService(InnerTubeClient innerTube)
{
    public async Task<CommentPage> TopLevelAsync(string videoId, CancellationToken ct = default)
    {
        using var doc = await innerTube.PostAsync("next", ClientKind.Web,
            new Dictionary<string, object?> { ["videoId"] = videoId }, ct: ct);
        // The watch page carries the comment section twice (inline + engagement panel), both
        // with the same sectionIdentifier; their tokens differ but resolve to the same list.
        var token = Json.FindAllRenderers(doc.RootElement, "itemSectionRenderer")
            .Where(s => Json.StringAt(s, "sectionIdentifier") == "comment-item-section")
            .Select(FirstContinuationToken)
            .FirstOrDefault(t => t is not null)
            ?? throw new CommentsUnavailableException();
        return await PageAsync(token, ct);
    }

    public async Task<CommentPage> PageAsync(string continuation, CancellationToken ct = default)
    {
        using var doc = await innerTube.PostAsync("next", ClientKind.Web,
            new Dictionary<string, object?> { ["continuation"] = continuation }, ct: ct);
        var root = doc.RootElement;

        var payloads = new Dictionary<string, JsonElement>();
        foreach (var payload in Json.FindAllRenderers(root, "commentEntityPayload"))
            if (Json.StringAt(payload, "properties/commentId") is { } id)
                payloads[id] = payload;

        var comments = new List<CommentItem>();
        var threads = Json.FindAllRenderers(root, "commentThreadRenderer").ToList();
        if (threads.Count == 0)
        {
            foreach (var viewModel in Json.FindAllRenderers(root, "commentViewModel"))
                if (Json.StringAt(viewModel, "commentId") is { } id
                    && payloads.TryGetValue(id, out var payload))
                    comments.Add(Item(payload, id, repliesToken: null));
        }
        else
        {
            foreach (var thread in threads)
            {
                var id = Json.FindAllRenderers(thread, "commentViewModel")
                    .Select(vm => Json.StringAt(vm, "commentId"))
                    .FirstOrDefault(x => x is not null);
                if (id is null || !payloads.TryGetValue(id, out var payload)) continue;
                var repliesToken = thread.TryGetProperty("replies", out var replies)
                    ? FirstContinuationToken(replies) : null;
                comments.Add(Item(payload, id, repliesToken));
            }
        }

        return new CommentPage(comments, NextPageToken(root));
    }

    /// Joins a renderer's id with its entity payload: author, avatar, text, relative time,
    /// and the two toolbar counts verbatim. An empty like count means "no likes yet" and is
    /// normalized to "0"; an empty reply count (replies never carry one) is left as-is.
    private static CommentItem Item(JsonElement payload, string id, string? repliesToken) =>
        new(
            Id: id,
            Author: Json.StringAt(payload, "author/displayName") ?? "",
            AvatarUrl: Json.StringAt(payload, "author/avatarThumbnailUrl"),
            Text: Json.StringAt(payload, "properties/content/content") ?? "",
            PublishedTime: Json.StringAt(payload, "properties/publishedTime") ?? "",
            LikeCount: Json.StringAt(payload, "toolbar/likeCountNotliked") ?? "0",
            ReplyCount: Json.StringAt(payload, "toolbar/replyCount") ?? "",
            RepliesToken: repliesToken);

    /// The first continuationCommand token anywhere in the subtree.
    private static string? FirstContinuationToken(JsonElement element) =>
        Json.FindAllRenderers(element, "continuationCommand")
            .Select(command => Json.StringAt(command, "token"))
            .FirstOrDefault(token => token is not null);

    /// The token for the next page of comments. Each onResponseReceivedEndpoints entry wraps
    /// its continuationItems array under one command name (e.g. reloadContinuationItemsCommand);
    /// only the LAST item in that array can be a trailing continuationItemRenderer for the next
    /// page — every earlier item is a thread whose own nested token is a reply token, not this one.
    private static string? NextPageToken(JsonElement root)
    {
        if (Json.ValueAt(root, "onResponseReceivedEndpoints") is not
            { ValueKind: JsonValueKind.Array } endpoints)
            return null;

        foreach (var endpoint in endpoints.EnumerateArray())
        {
            if (endpoint.ValueKind != JsonValueKind.Object) continue;
            foreach (var property in endpoint.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object) continue;
                if (!property.Value.TryGetProperty("continuationItems", out var items) ||
                    items.ValueKind != JsonValueKind.Array) continue;

                JsonElement? last = null;
                foreach (var item in items.EnumerateArray()) last = item;
                if (last is { } lastItem
                    && lastItem.TryGetProperty("continuationItemRenderer", out var renderer)
                    && FirstContinuationToken(renderer) is { } token)
                    return token;
            }
        }
        return null;
    }
}
