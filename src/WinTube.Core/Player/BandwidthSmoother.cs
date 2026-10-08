namespace WinTube.Core.Player;

/// Exponential moving average of the download rate, fed by the periodic cache polls. Each accepted
/// sample moves the average toward it by alpha = 1 − 2^(−elapsed / half-life), so the influence of
/// older samples halves every RateSmoothingHalfLifeSeconds regardless of the poll interval.
/// Samples that are not positive finite numbers, or that carry no elapsed time, are ignored
/// entirely: they neither move the average nor advance its clock, so a stall or a pause in
/// polling cannot decay the rate. Not thread-safe — drive it from the UI thread.
public sealed class BandwidthSmoother
{
    private double? rate;

    /// The smoothed rate in bytes per second, or null until the first accepted sample.
    public double? BytesPerSecond => rate;

    /// Folds one sample into the average. `elapsedSeconds` is the time since the previous accepted sample.
    public void Sample(double bytesPerSecond, double elapsedSeconds)
    {
        if (!double.IsFinite(bytesPerSecond) || bytesPerSecond <= 0) return;
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return;

        if (rate is not double current)
        {
            rate = bytesPerSecond;
            return;
        }

        var alpha = 1 - Math.Pow(2, -elapsedSeconds / AdaptiveAutoTuning.RateSmoothingHalfLifeSeconds);
        rate = current + alpha * (bytesPerSecond - current);
    }
}
