namespace WinTube.Core.Models;

/// One comment or reply, as read off a commentEntityPayload joined with its renderer. Fields
/// carry YouTube's own verbatim strings (like counts such as "12K", relative times such as
/// "2 years ago") rather than parsed values — same policy as the tvOS CommentService. An
/// empty like count is normalized to "0"; a missing reply count stays empty (top-level
/// comments only — replies never carry one).
public sealed record CommentItem(
    string Id,
    string Author,
    string? AvatarUrl,
    string Text,
    string PublishedTime,
    string LikeCount,
    string ReplyCount,
    string? RepliesToken)
{
    public bool HasReplies => RepliesToken is not null;
}

/// One page of comments (top-level or replies), flattened from the renderer/payload join,
/// plus the continuation token for the next page if there is one.
public sealed record CommentPage(IReadOnlyList<CommentItem> Comments, string? Continuation);
