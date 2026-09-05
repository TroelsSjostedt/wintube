using WinTube.Core.Stores;

namespace WinTube.Core.Tests;

public class PlayerSettingsStoreTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "wintube-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void Volume_RoundTrips()
    {
        new PlayerSettingsStore(directory).SaveVolume(0.35);
        Assert.Equal(0.35, new PlayerSettingsStore(directory).LoadVolume());
    }

    [Fact]
    public void Volume_DefaultsToFull_WhenNothingStored()
    {
        Assert.Equal(1.0, new PlayerSettingsStore(directory).LoadVolume());
    }

    [Fact]
    public void Volume_DefaultsToFull_OnCorruptFile()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), "{not json");
        Assert.Equal(1.0, new PlayerSettingsStore(directory).LoadVolume());
    }

    [Theory]
    [InlineData(-0.5, 0.0)]
    [InlineData(1.5, 1.0)]
    [InlineData(double.NaN, 1.0)]
    public void Volume_OutOfRangeValues_AreClampedOnLoad(double stored, double expected)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"),
            $"{{\"volume\":{(double.IsNaN(stored) ? "\"NaN\"" : stored.ToString(System.Globalization.CultureInfo.InvariantCulture))}}}");
        Assert.Equal(expected, new PlayerSettingsStore(directory).LoadVolume());
    }
}
