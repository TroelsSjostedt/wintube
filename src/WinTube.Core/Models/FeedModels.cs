namespace WinTube.Core.Models;

/// One horizontal row of the feed — a YouTube "shelf". Id is assigned at parse time and
/// stays stable for the section's lifetime (pages are only appended).
public sealed record FeedSection(
    string Id, string Title, IReadOnlyList<VideoItem> Items, string? Continuation, bool IsShorts)
{
    /// Items from this row's continuation, shaped to what this row shows: a Shorts row
    /// vouches for everything it pages in, and every other row drops the Shorts YouTube
    /// mixes into it. Applied where a page is appended rather than where it's fetched,
    /// because a continuation reply is a bare cell list with nothing naming its shelf.
    public IReadOnlyList<VideoItem> Admitting(IReadOnlyList<VideoItem> items) =>
        IsShorts
            ? items.Select(item => item with { IsShort = true }).ToList()
            : items.Where(item => !item.IsShort).ToList();
}

/// One page of the feed: shelves plus the token for the next page of shelves, if any.
public sealed record FeedPage(IReadOnlyList<FeedSection> Sections, string? Continuation);

/// One page of a single row: the videos it added plus the token for the page after it.
public sealed record FeedRowPage(IReadOnlyList<VideoItem> Items, string? Continuation);

/// A channel's browse page: who it is, and its shelves in the same shape as any feed's.
/// IsSubscribed is null when the page carried no subscribe button — "unknown", never "no".
public sealed record ChannelPage(
    string Title, string? AvatarUrl, string? BannerUrl, bool? IsSubscribed, FeedPage Feed);

/// The stitched Home screen: Home's own shelves with the subscriptions feed's rows woven in
/// after the first non-Shorts row and the history rows at the end. HistoryRowCount says how
/// many trailing sections are history, so paging can insert new Home shelves above them.
public sealed record CompositeHomePage(
    IReadOnlyList<FeedSection> Sections, string? Continuation, int HistoryRowCount);
