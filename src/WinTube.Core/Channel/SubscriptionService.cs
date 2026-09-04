using WinTube.Core.InnerTube;
using WinTube.Core.Models;

namespace WinTube.Core.Channel;

/// Subscribing, unsubscribing, and reading back which channels the account follows.
/// Same TV client and Bearer token the feeds use; the mutations take a list of channel ids,
/// of which this app only ever sends one. Success is any 2xx — the response body carries
/// nothing the caller needs.
public sealed class SubscriptionService(InnerTubeClient innerTube)
{
    public async Task SubscribeAsync(string channelId, string accessToken, CancellationToken ct = default)
    {
        using var _ = await innerTube.PostAsync("subscription/subscribe", ClientKind.Tv,
            new Dictionary<string, object?> { ["channelIds"] = new[] { channelId } },
            bearer: accessToken, ct: ct);
    }

    public async Task UnsubscribeAsync(string channelId, string accessToken, CancellationToken ct = default)
    {
        using var _ = await innerTube.PostAsync("subscription/unsubscribe", ClientKind.Tv,
            new Dictionary<string, object?> { ["channelIds"] = new[] { channelId } },
            bearer: accessToken, ct: ct);
    }

    /// FEchannels — the "All subscriptions" grid. Cells that parse are the listing; when none
    /// do but UC ids exist, they are listed bare rather than showing an empty screen.
    public async Task<SubscriptionListing> LoadSubscriptionsAsync(
        string accessToken, CancellationToken ct = default)
    {
        using var doc = await innerTube.PostAsync("browse", ClientKind.Tv,
            new Dictionary<string, object?> { ["browseId"] = "FEchannels" },
            bearer: accessToken, ct: ct);
        var ordered = SubscribedChannelParser.ChannelIdsInOrder(doc.RootElement);
        var parsed = SubscribedChannelParser.Channels(doc.RootElement);
        var channels = parsed.Count > 0
            ? parsed
            : ordered.Select(id => new SubscribedChannel(id)).ToList();
        return new SubscriptionListing(ordered.ToHashSet(), channels);
    }
}
