namespace WinTube.Core.Models;

/// One channel the account follows, as the FEchannels grid lists it. Only Id is guaranteed —
/// a cell that parses no further still lists the channel and opens its page.
public sealed record SubscribedChannel(
    string Id, string Title = "", string? AvatarUrl = null, string Detail = "");

/// Both readings of the one FEchannels response: the id set (membership tests) and the
/// named, pictured channels in YouTube's own order (the Subscriptions screen).
public sealed record SubscriptionListing(
    IReadOnlySet<string> ChannelIds, IReadOnlyList<SubscribedChannel> Channels);
