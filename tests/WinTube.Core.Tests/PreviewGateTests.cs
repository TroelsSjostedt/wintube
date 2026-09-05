using WinTube.Core.Player;

namespace WinTube.Core.Tests;

public class PreviewGateTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private static PreviewGate Gate() => new(TimeSpan.FromMilliseconds(700));

    [Fact]
    public void DwellElapsed_StartsTheWarmCard_AfterTheDwell()
    {
        var gate = Gate();
        Assert.Null(gate.Warm("a", T0));
        Assert.Equal("a", gate.DwellElapsed("a", T0.AddMilliseconds(700)));
        Assert.True(gate.IsActive("a"));
    }

    [Fact]
    public void DwellElapsed_DoesNothing_BeforeTheDwell_OrForACardNoLongerWarm()
    {
        var gate = Gate();
        gate.Warm("a", T0);
        Assert.Null(gate.DwellElapsed("a", T0.AddMilliseconds(500)));   // too early
        gate.Cold("a");
        Assert.Null(gate.DwellElapsed("a", T0.AddMilliseconds(700)));   // left already
        Assert.False(gate.IsActive("a"));
    }

    [Fact]
    public void Cold_StopsAnActivePreview_AndOnlyThat()
    {
        var gate = Gate();
        gate.Warm("a", T0);
        gate.DwellElapsed("a", T0.AddMilliseconds(700));
        Assert.Null(gate.Cold("b"));            // someone else's cold is not ours
        Assert.Equal("a", gate.Cold("a"));      // ours stops
        Assert.Null(gate.Cold("a"));            // idempotent
        Assert.False(gate.IsActive("a"));
    }

    [Fact]
    public void Warm_PreemptsTheActivePreview_Immediately()
    {
        var gate = Gate();
        gate.Warm("a", T0);
        gate.DwellElapsed("a", T0.AddMilliseconds(700));
        Assert.Equal("a", gate.Warm("b", T0.AddSeconds(5)));   // b warm -> stop a now
        Assert.False(gate.IsActive("a"));
        Assert.Equal("b", gate.DwellElapsed("b", T0.AddSeconds(5).AddMilliseconds(700)));
        Assert.True(gate.IsActive("b"));
    }

    [Fact]
    public void ReWarmingTheActiveCard_DoesNotStopIt()
    {
        var gate = Gate();
        gate.Warm("a", T0);
        gate.DwellElapsed("a", T0.AddMilliseconds(700));
        Assert.Null(gate.Warm("a", T0.AddSeconds(2)));   // pointer jiggle on the same card
        Assert.True(gate.IsActive("a"));
    }

    [Fact]
    public void StaleCompletion_IsNotActive()
    {
        var gate = Gate();
        gate.Warm("a", T0);
        gate.DwellElapsed("a", T0.AddMilliseconds(700));
        gate.Warm("b", T0.AddSeconds(1));      // preempts a
        Assert.False(gate.IsActive("a"));      // a's late resolution must be discarded
    }

    [Fact]
    public void AFreshWarm_RestartsTheDwellClock()
    {
        var gate = Gate();
        gate.Warm("a", T0);
        gate.Cold("a");
        gate.Warm("a", T0.AddMilliseconds(600));
        Assert.Null(gate.DwellElapsed("a", T0.AddMilliseconds(700)));   // only 100ms into the new dwell
        Assert.Equal("a", gate.DwellElapsed("a", T0.AddMilliseconds(1300)));
    }
}
