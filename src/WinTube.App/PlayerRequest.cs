namespace WinTube.App;

/// The navigation parameter PlayerPage accepts. StartAt, when given, overrides the stored
/// resume position for this playback — a timestamped link's whole point.
public sealed record PlayerRequest(WinTube.Core.Models.VideoItem Video, TimeSpan? StartAt = null);
