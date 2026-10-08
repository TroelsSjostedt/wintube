namespace WinTube.Core.Player;

/// Every tunable of the adaptive Auto quality policy, in one place. Call sites use these names;
/// no magic numbers belong elsewhere.
public static class AdaptiveAutoTuning
{
    /// Fraction of the measured download rate a rung's BANDWIDTH may use (rate × 8 × SafetyFactor).
    public const double SafetyFactor = 0.7;

    /// Window in which genuine stalls are counted toward a downshift.
    public const int StallWindowSeconds = 60;

    /// Stalls inside the window that trigger a downshift.
    public const int StallsToDownshift = 2;

    /// Minimum time between two downshifts.
    public const int CooldownSeconds = 90;

    /// Downshifts allowed for one video.
    public const int MaxDownshiftsPerVideo = 2;

    /// Buffering edges are ignored for this long after a file opens (initial load, quality or
    /// subtitle reload) and after a user-initiated seek (scrub, +-10 s, timestamp link, sponsor skip):
    /// both routinely raise paused-for-cache without saying anything about the connection's rate.
    /// One constant serves both windows on purpose; a seek into an uncached range refills about as fast
    /// as an open does, so a second knob would only be a second number to tune.
    public const int PostOpenStallGraceSeconds = 5;

    /// Half-life of the download-rate exponential moving average.
    public const double RateSmoothingHalfLifeSeconds = 5.0;
}
