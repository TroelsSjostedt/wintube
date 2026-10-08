using WinTube.Core.Player;

namespace WinTube.Core.Tests;

// Scenario offsets below are in seconds from t0 and assume the shipped tuning
// (window 60 s, 2 stalls, cooldown 90 s, cap 2). Boundary tests use the constants directly.
public class DownshiftPolicyTests
{
    [Fact]
    public void SecondStallInsideWindow_Downshifts()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var p = new DownshiftPolicy();
        Assert.Equal(DownshiftDecision.None, p.RecordStall(t0));
        Assert.Equal(DownshiftDecision.Downshift, p.RecordStall(t0.AddSeconds(10)));
    }

    [Fact]
    public void TwoStallsSeventySecondsApart_DoNotDownshift()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var p = new DownshiftPolicy();
        Assert.Equal(DownshiftDecision.None, p.RecordStall(t0));
        Assert.Equal(DownshiftDecision.None, p.RecordStall(t0.AddSeconds(70)));
    }

    [Fact]
    public void StallsExactlyWindowApart_AreOutOfWindow()
    {
        // Semantic: a stall counts toward a window ending at `now` only while now - stall < StallWindowSeconds.
        // Exactly StallWindowSeconds apart, the earlier stall is out, so the pair does not downshift.
        var t0 = DateTimeOffset.UnixEpoch;
        var p = new DownshiftPolicy();
        Assert.Equal(DownshiftDecision.None, p.RecordStall(t0));
        Assert.Equal(DownshiftDecision.None, p.RecordStall(t0.AddSeconds(AdaptiveAutoTuning.StallWindowSeconds)));
    }

    [Fact]
    public void StallsOneSecondInsideWindow_Downshift()
    {
        // The window boundary's inside neighbour: one second less than the window keeps the first stall in.
        var t0 = DateTimeOffset.UnixEpoch;
        var p = new DownshiftPolicy();
        Assert.Equal(DownshiftDecision.None, p.RecordStall(t0));
        Assert.Equal(DownshiftDecision.Downshift, p.RecordStall(t0.AddSeconds(AdaptiveAutoTuning.StallWindowSeconds - 1)));
    }

    [Fact]
    public void StallThirtySecondsAfterDownshift_IsSuppressedByCooldown()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var p = new DownshiftPolicy();
        Assert.Equal(DownshiftDecision.None, p.RecordStall(t0));
        Assert.Equal(DownshiftDecision.Downshift, p.RecordStall(t0.AddSeconds(10)));
        Assert.Equal(DownshiftDecision.SuppressedCooldown, p.RecordStall(t0.AddSeconds(40)));
    }

    [Fact]
    public void CooldownIsExclusiveAtItsBoundary()
    {
        // Downshift at D = t0+10. A stall one second short of the cooldown is still suppressed;
        // a stall exactly CooldownSeconds after D is outside the cooldown and downshifts.
        var t0 = DateTimeOffset.UnixEpoch;
        var d = t0.AddSeconds(10);
        var p = new DownshiftPolicy();
        Assert.Equal(DownshiftDecision.None, p.RecordStall(t0));
        Assert.Equal(DownshiftDecision.Downshift, p.RecordStall(d));
        Assert.Equal(DownshiftDecision.SuppressedCooldown, p.RecordStall(t0.AddSeconds(50)));
        Assert.Equal(DownshiftDecision.SuppressedCooldown, p.RecordStall(d.AddSeconds(AdaptiveAutoTuning.CooldownSeconds - 1)));
        Assert.Equal(DownshiftDecision.Downshift, p.RecordStall(d.AddSeconds(AdaptiveAutoTuning.CooldownSeconds)));
    }

    [Fact]
    public void ThirdDownshiftAfterTwoGranted_IsSuppressedByCap()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var p = new DownshiftPolicy();
        // First downshift at t0+10.
        Assert.Equal(DownshiftDecision.None, p.RecordStall(t0));
        Assert.Equal(DownshiftDecision.Downshift, p.RecordStall(t0.AddSeconds(10)));
        // Second downshift at t0+110, 100 s after the first, so outside the cooldown.
        Assert.Equal(DownshiftDecision.None, p.RecordStall(t0.AddSeconds(100)));
        Assert.Equal(DownshiftDecision.Downshift, p.RecordStall(t0.AddSeconds(110)));
        // Third attempt at t0+210, 100 s after the second, outside the cooldown: the cap refuses it.
        Assert.Equal(DownshiftDecision.None, p.RecordStall(t0.AddSeconds(200)));
        Assert.Equal(DownshiftDecision.SuppressedCap, p.RecordStall(t0.AddSeconds(210)));
    }

    [Fact]
    public void CapTakesPrecedenceOverCooldown()
    {
        // With the cap reached, a stall inside the cooldown is reported as the cap, the permanent reason.
        var t0 = DateTimeOffset.UnixEpoch;
        var p = new DownshiftPolicy();
        Assert.Equal(DownshiftDecision.None, p.RecordStall(t0));
        Assert.Equal(DownshiftDecision.Downshift, p.RecordStall(t0.AddSeconds(10)));
        Assert.Equal(DownshiftDecision.None, p.RecordStall(t0.AddSeconds(100)));
        Assert.Equal(DownshiftDecision.Downshift, p.RecordStall(t0.AddSeconds(110)));
        // Five seconds after the second downshift: inside the cooldown and past the cap.
        Assert.Equal(DownshiftDecision.SuppressedCap, p.RecordStall(t0.AddSeconds(115)));
    }

    [Fact]
    public void SingleStallAlone_IsNone()
    {
        var p = new DownshiftPolicy();
        Assert.Equal(DownshiftDecision.None, p.RecordStall(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Reset_ClearsStallHistory()
    {
        // The stall before Reset must not pair with the one after it.
        var t0 = DateTimeOffset.UnixEpoch;
        var p = new DownshiftPolicy();
        Assert.Equal(DownshiftDecision.None, p.RecordStall(t0));
        p.Reset();
        Assert.Equal(DownshiftDecision.None, p.RecordStall(t0.AddSeconds(10)));
    }

    [Fact]
    public void Reset_ClearsDownshiftCount()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var p = new DownshiftPolicy();
        // Two downshifts reach the cap.
        Assert.Equal(DownshiftDecision.None, p.RecordStall(t0));
        Assert.Equal(DownshiftDecision.Downshift, p.RecordStall(t0.AddSeconds(10)));
        Assert.Equal(DownshiftDecision.None, p.RecordStall(t0.AddSeconds(100)));
        Assert.Equal(DownshiftDecision.Downshift, p.RecordStall(t0.AddSeconds(110)));

        p.Reset();

        // New video: the cap is gone, so the pattern downshifts again (200/210 are outside any cooldown).
        Assert.Equal(DownshiftDecision.None, p.RecordStall(t0.AddSeconds(200)));
        Assert.Equal(DownshiftDecision.Downshift, p.RecordStall(t0.AddSeconds(210)));
    }

    [Fact]
    public void Reset_ClearsCooldown()
    {
        // Downshift at t0+10 starts a cooldown. After Reset, a stall pair at t0+20 and t0+25 sits
        // inside that old cooldown, so a stale lastDownshiftAt would report SuppressedCooldown.
        var t0 = DateTimeOffset.UnixEpoch;
        var p = new DownshiftPolicy();
        Assert.Equal(DownshiftDecision.None, p.RecordStall(t0));
        Assert.Equal(DownshiftDecision.Downshift, p.RecordStall(t0.AddSeconds(10)));

        p.Reset();

        Assert.Equal(DownshiftDecision.None, p.RecordStall(t0.AddSeconds(20)));
        Assert.Equal(DownshiftDecision.Downshift, p.RecordStall(t0.AddSeconds(25)));
    }

    [Fact]
    public void StallsSuppressedByCooldown_StillCount_SoAStallRunDownshiftsWhenCooldownEnds()
    {
        // The stall at t0+50 is inside the cooldown. It still counts toward the window, so when the
        // cooldown ends, the stalls at t0+99 and t0+100 pair with it and the policy downshifts.
        var t0 = DateTimeOffset.UnixEpoch;
        var p = new DownshiftPolicy();
        Assert.Equal(DownshiftDecision.None, p.RecordStall(t0));
        Assert.Equal(DownshiftDecision.Downshift, p.RecordStall(t0.AddSeconds(10)));
        Assert.Equal(DownshiftDecision.SuppressedCooldown, p.RecordStall(t0.AddSeconds(50)));
        Assert.Equal(DownshiftDecision.SuppressedCooldown, p.RecordStall(t0.AddSeconds(99)));
        Assert.Equal(DownshiftDecision.Downshift, p.RecordStall(t0.AddSeconds(100)));
    }
}
