namespace WinTube.Core.Models;

/// One horizontal row of the feed — a YouTube "shelf". Id is assigned at parse time and
/// stays stable for the section's lifetime (pages are only appended).
public sealed record FeedSection(
    string Id, string Title, IReadOnlyList<VideoItem> Items, string? Continuation, bool IsShorts);

/// One page of the feed: shelves plus the token for the next page of shelves, if any.
public sealed record FeedPage(IReadOnlyList<FeedSection> Sections, string? Continuation);

/// One page of a single row: the videos it added plus the token for the page after it.
public sealed record FeedRowPage(IReadOnlyList<VideoItem> Items, string? Continuation);
