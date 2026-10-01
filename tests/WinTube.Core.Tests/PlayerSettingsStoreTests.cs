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

    [Fact]
    public void SavingSubtitle_PreservesVolume_AndViceVersa()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var store = new PlayerSettingsStore(dir);
        store.SaveVolume(0.42);
        store.SaveSubtitleLanguage("en");
        Assert.Equal(0.42, store.LoadVolume(), 3);
        Assert.Equal("en", store.LoadSubtitleLanguage());
        store.SaveVolume(0.9);
        Assert.Equal("en", store.LoadSubtitleLanguage());
        store.SaveSubtitleLanguage(null);
        Assert.Null(store.LoadSubtitleLanguage());
        Assert.Equal(0.9, store.LoadVolume(), 3);
    }

    [Fact]
    public void OldVolumeOnlyFile_StillLoads()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{"volume":0.5}""");
        var store = new PlayerSettingsStore(dir);
        Assert.Equal(0.5, store.LoadVolume(), 3);
        Assert.Null(store.LoadSubtitleLanguage());
    }
}
