using WinTube.Core.Player;

namespace WinTube.Core.Tests;

public class BandwidthSmootherTests
{
    [Fact]
    public void Smoother_ConvergesTowardSteadyRate()
    {
        var s = new BandwidthSmoother();
        for (var i = 0; i < 20; i++) s.Sample(1_000_000, 1.0);
        Assert.Equal(1_000_000, s.BytesPerSecond!.Value, 0);
    }

    [Fact]
    public void Smoother_IgnoresZeroAndNegativeAndNaN()
    {
        var s = new BandwidthSmoother();
        s.Sample(1_000_000, 1.0);
        var before = s.BytesPerSecond;
        s.Sample(0, 1.0); s.Sample(-5, 1.0); s.Sample(double.NaN, 1.0);
        Assert.Equal(before, s.BytesPerSecond);   // a long pause must not decay the rate
    }

    [Fact]
    public void Smoother_NullUntilFirstAcceptedSample()
    {
        var s = new BandwidthSmoother();
        s.Sample(0, 1.0);
        Assert.Null(s.BytesPerSecond);
    }
}
