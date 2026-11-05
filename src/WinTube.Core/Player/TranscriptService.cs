namespace WinTube.Core.Player;

/// Downloads a caption track's VTT (CaptionTrack.VttUrl) and parses it into transcript cues.
/// The URL is pre-signed and IP-bound like the subtitle path mpv consumes — fetch at use time.
public sealed class TranscriptService(HttpClient http)
{
    public async Task<IReadOnlyList<TranscriptCue>> GetAsync(
        string vttUrl, CancellationToken ct = default) =>
        VttParser.Parse(await http.GetStringAsync(vttUrl, ct));
}
