namespace WinTube.Core.Player;

/// The outcome of one recorded stall.
public enum DownshiftDecision
{
    /// Not a downshift: fewer than StallsToDownshift stalls fall inside the window.
    None,
    /// Step down one rung now.
    Downshift,
    /// Enough stalls, but the last downshift was less than CooldownSeconds ago.
    SuppressedCooldown,
    /// Enough stalls, but MaxDownshiftsPerVideo downshifts were already granted (reported even during a cooldown).
    SuppressedCap,
}

/// Decides when Auto quality should step down one rung after genuine stalls. Pure: every decision takes
/// the caller's `now` and nothing reads the wall clock.
///
/// The CALLER filters reload-induced stalls: a stall that follows our own quality switch or reload is
/// not evidence of a bandwidth shortage and must not be passed to RecordStall. This policy counts every
/// stall it is given.
///
/// Rules:
/// - Window: a stall counts toward the window ending at `now` only while it is less than
///   StallWindowSeconds old. A stall exactly StallWindowSeconds old is outside the window.
/// - Downshift: when the stalls in the window, including this one, reach StallsToDownshift.
/// - Cooldown: a downshift is blocked while less than CooldownSeconds have passed since the last
///   downshift. Exactly CooldownSeconds after it is outside the cooldown.
/// - Cap: no more than MaxDownshiftsPerVideo downshifts are granted until Reset().
/// - Precedence when several apply: the cap is reported before the cooldown, since it is permanent for the video.
/// - Stalls suppressed by cooldown or cap still count toward the window and are not discarded, so a stall
///   run that continues past the cooldown downshifts as soon as the cooldown ends.
/// - Reset() starts a new video: it clears the stall history, the last downshift and the downshift count.
///
/// Not thread-safe; drive it from the UI thread.
public sealed class DownshiftPolicy
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(AdaptiveAutoTuning.StallWindowSeconds);
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(AdaptiveAutoTuning.CooldownSeconds);

    private readonly List<DateTimeOffset> stalls = new();
    private DateTimeOffset? lastDownshiftAt;
    private int downshifts;

    /// Records one genuine stall at `now` and returns what it means for quality. `now` must be
    /// non-decreasing across calls; the caller owns clock monotonicity.
    public DownshiftDecision RecordStall(DateTimeOffset now)
    {
        stalls.RemoveAll(s => now - s >= Window);
        stalls.Add(now);

        if (stalls.Count < AdaptiveAutoTuning.StallsToDownshift) return DownshiftDecision.None;
        if (downshifts >= AdaptiveAutoTuning.MaxDownshiftsPerVideo) return DownshiftDecision.SuppressedCap;
        if (lastDownshiftAt is { } last && now - last < Cooldown) return DownshiftDecision.SuppressedCooldown;

        downshifts++;
        lastDownshiftAt = now;
        return DownshiftDecision.Downshift;
    }

    /// Starts a new video: clears the stall history, the cooldown and the downshift count.
    public void Reset()
    {
        stalls.Clear();
        lastDownshiftAt = null;
        downshifts = 0;
    }
}
