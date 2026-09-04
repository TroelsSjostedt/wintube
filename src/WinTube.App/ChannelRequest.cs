namespace WinTube.App;

/// Navigation parameter for ChannelPage: the channel to open and the name from the card the
/// user came from, shown until the channel's own header arrives.
public sealed record ChannelRequest(string ChannelId, string FallbackTitle);
