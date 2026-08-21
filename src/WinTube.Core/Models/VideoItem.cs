namespace WinTube.Core.Models;

/// A single video shown in the feed and passed to the player. Field semantics follow the
/// tvOS VideoItem: ViewCount and Duration are InnerTube's display text shown verbatim;
/// PublishedAt is approximated from "3 days ago" text at parse time.
public sealed record VideoItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Author { get; init; } = "";
    /// The channel's UC… id, when the cell carried one.
    public string? ChannelId { get; init; }
    public string? ThumbnailUrl { get; init; }
    /// The channel's round profile picture; most TV-feed tiles don't carry one.
    public string? ChannelAvatarUrl { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string ViewCount { get; init; } = "";
    public string Duration { get; init; } = "";
    public bool IsShort { get; init; }

    /// Every video has this file — the fallback while nothing better is known.
    public static string FallbackThumbnail(string videoId) =>
        $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
}
