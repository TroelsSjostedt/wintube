namespace WinTube.Core.Player;

/// Exponential moving average of the download rate, fed by the periodic cache polls. Each accepted
/// sample moves the average toward it by alpha = 1 − 2^(−elapsed / half-life), where `elapsedSeconds`
/// is the time since the previous accepted sample (the caller supplies it), so an older sample loses
/// half its weight every RateSmoothingHalfLifeSeconds whatever the poll interval. The first accepted
/// sample seeds the average directly. A rejected sample leaves the value untouched: a bytes value that
/// is not a positive finite number is rejected at any time, and once seeded, so is an elapsed value
/// that is not a positive finite number. Not thread-safe — drive it from the UI thread.
public sealed class BandwidthSmoother
{
    private double? rate;

    /// The smoothed rate in bytes per second, or null until the first accepted sample.
    public double? BytesPerSecond => rate;

    /// Folds one sample into the average. `elapsedSeconds` is the time since the previous accepted sample.
    public void Sample(double bytesPerSecond, double elapsedSeconds)
    {
        if (!double.IsFinite(bytesPerSecond) || bytesPerSecond <= 0) return;

        if (rate is not double current)
        {
            rate = bytesPerSecond;
            return;
        }

        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return;

        var alpha = 1 - Math.Pow(2, -elapsedSeconds / AdaptiveAutoTuning.RateSmoothingHalfLifeSeconds);
        rate = current + alpha * (bytesPerSecond - current);
    }
}
