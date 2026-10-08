using WinTube.Core.Player;

namespace WinTube.Core.Tests;

public class BandwidthSmootherTests
{
    [Fact]
    public void Smoother_HalfLifeWeightsANewSampleByHalf()
    {
        // One half-life after a seed, a new sample carries exactly half the weight: (1e6 + 3e6) / 2.
        var s = new BandwidthSmoother();
        s.Sample(1_000_000, 1.0);
        s.Sample(3_000_000, AdaptiveAutoTuning.RateSmoothingHalfLifeSeconds);
        Assert.True(Math.Abs(s.BytesPerSecond!.Value - 2_000_000) <= 1,
            $"expected 2,000,000 +/- 1, got {s.BytesPerSecond}");

        // Two half-lives after a fresh seed, alpha is 0.75: (1e6 + 3 * 3e6) / 4 = 2.5e6. A single
        // pin point is matched by many curves; the second one separates them.
        var t = new BandwidthSmoother();
        t.Sample(1_000_000, 1.0);
        t.Sample(3_000_000, 2 * AdaptiveAutoTuning.RateSmoothingHalfLifeSeconds);
        Assert.True(Math.Abs(t.BytesPerSecond!.Value - 2_500_000) <= 1,
            $"expected 2,500,000 +/- 1, got {t.BytesPerSecond}");
    }

    [Fact]
    public void Smoother_TracksAStepThenDampsIt()
    {
        var s = new BandwidthSmoother();
        s.Sample(1_000_000, 1.0);
        s.Sample(2_000_000, 1.0);
        var first = s.BytesPerSecond!.Value;
        Assert.True(first > 1_000_000 && first < 2_000_000, $"first step landed at {first}");
        s.Sample(2_000_000, 1.0);
        var second = s.BytesPerSecond!.Value;
        Assert.True(second > first && second < 2_000_000, $"second step landed at {second}");
    }

    [Fact]
    public void Smoother_FirstSampleSeedsEvenWithoutElapsedTime_ThenRejectsBadElapsed()
    {
        var s = new BandwidthSmoother();
        s.Sample(1_000_000, 0);
        Assert.Equal(1_000_000, s.BytesPerSecond!.Value, 0);
        // Once seeded, a sample with no usable elapsed time must not move the value.
        s.Sample(2_000_000, 0);
        s.Sample(2_000_000, double.NaN);
        s.Sample(2_000_000, -1);
        s.Sample(2_000_000, double.PositiveInfinity);
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
