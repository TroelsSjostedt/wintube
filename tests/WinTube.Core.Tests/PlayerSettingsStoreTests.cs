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

    [Fact]
    public void SaveOver_CorruptFile_Succeeds()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{not json");
        var store = new PlayerSettingsStore(dir);
        // Save should not throw despite corrupt file
        store.SaveVolume(0.7);
        // Subsequent load should work
        Assert.Equal(0.7, store.LoadVolume(), 3);
    }

    [Fact]
    public void SaveOver_NonObjectRoot_Succeeds()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "settings.json"), """[1,2,3]""");
        var store = new PlayerSettingsStore(dir);
        // Save should not throw despite non-object root
        store.SaveSubtitleLanguage("en");
        // Subsequent load should work
        Assert.Equal("en", store.LoadSubtitleLanguage());
    }

    [Fact]
    public void UnknownKeys_SurviveASave()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{"volume":0.5,"unknown":"value"}""");
        var store = new PlayerSettingsStore(dir);
        store.SaveVolume(0.8);
        // Re-read file to check unknown key's value and new volume are preserved
        var content = File.ReadAllText(Path.Combine(dir, "settings.json"));
        Assert.Contains("\"unknown\"", content);
        Assert.Contains("\"value\"", content);
        Assert.Contains("0.8", content);
    }

    [Fact]
    public void WrongTypedVolume_LoadsAsDefault()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{"volume":{}}""");
        var store = new PlayerSettingsStore(dir);
        Assert.Equal(1.0, store.LoadVolume());
    }

    [Fact]
    public void WrongTypedSubtitleLanguage_LoadsAsNull()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{"subtitleLanguage":{}}""");
        var store = new PlayerSettingsStore(dir);
        Assert.Null(store.LoadSubtitleLanguage());
    }

    [Fact]
    public void EmptyString_SubtitleLanguage_TreatsAsNull()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var store = new PlayerSettingsStore(dir);
        store.SaveSubtitleLanguage("");
        Assert.Null(store.LoadSubtitleLanguage());
    }

    [Fact]
    public void DuplicateKeyFile_OperationsSucceed()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        // Duplicate keys in JSON trigger error during materialization
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{"volume":0.5,"volume":0.6}""");
        var store = new PlayerSettingsStore(dir);
        // All operations should succeed with defaults (not throw)
        Assert.Equal(1.0, store.LoadVolume());
        Assert.Null(store.LoadSubtitleLanguage());
        store.SaveVolume(0.7);
        Assert.Equal(0.7, store.LoadVolume(), 3);
        store.SaveSubtitleLanguage("en");
        Assert.Equal("en", store.LoadSubtitleLanguage());
    }

    [Fact]
    public void LoneSurrogateInVolume_LoadsAsDefault()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        // Lone surrogate string like \ud800 causes InvalidOperationException on ToJsonString
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"subtitleLanguage\":\"\\ud800\"}");
        var store = new PlayerSettingsStore(dir);
        // Load should not throw, should return null (invalid surrogate)
        Assert.Null(store.LoadSubtitleLanguage());
        // Save should not throw
        store.SaveVolume(0.6);
        Assert.Equal(0.6, store.LoadVolume(), 3);
    }

    [Fact]
    public void LoneSurrogateInUnknownKey_OperationsSucceed()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        // Lone surrogate in unknown key causes InvalidOperationException on ToJsonString
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"x\":\"\\ud800\"}");
        var store = new PlayerSettingsStore(dir);
        // Load should not throw
        Assert.Equal(1.0, store.LoadVolume());
        // Save should not throw
        store.SaveSubtitleLanguage("de");
        Assert.Equal("de", store.LoadSubtitleLanguage());
    }

    // Cache tuning keys are hand-edited only (no savers), so every case writes the file directly.
    private static string WriteSettings(string json)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "settings.json"), json);
        return dir;
    }

    [Fact]
    public void CacheTuning_DefaultsWhenAbsent()
    {
        var store = new PlayerSettingsStore(directory); // no file at all
        Assert.Equal(600, store.CacheReadaheadSeconds());
        Assert.Equal(700, store.CacheForwardMegabytes());
        Assert.Equal(300, store.CacheBackMegabytes());
    }

    [Fact]
    public void CacheTuning_ReadsHandWrittenValues()
    {
        var store = new PlayerSettingsStore(WriteSettings(
            """{"cacheReadaheadSecs":1200,"cacheForwardMb":1024,"cacheBackMb":0}"""));
        Assert.Equal(1200, store.CacheReadaheadSeconds());
        Assert.Equal(1024, store.CacheForwardMegabytes());
        Assert.Equal(0, store.CacheBackMegabytes());
    }

    [Fact]
    public void CacheTuning_WrongTypedValues_FallBackToDefaults()
    {
        var store = new PlayerSettingsStore(WriteSettings(
            """{"cacheReadaheadSecs":"lots","cacheForwardMb":{},"cacheBackMb":[1]}"""));
        Assert.Equal(600, store.CacheReadaheadSeconds());
        Assert.Equal(700, store.CacheForwardMegabytes());
        Assert.Equal(300, store.CacheBackMegabytes());
    }

    [Fact]
    public void CacheTuning_FractionalValue_FallsBackToDefault()
    {
        // 1.5 is not an int; guessing a rounding would hide a typo.
        Assert.Equal(600, new PlayerSettingsStore(WriteSettings("""{"cacheReadaheadSecs":1.5}""")).CacheReadaheadSeconds());
    }

    [Fact]
    public void CacheTuning_TooSmallValues_ClampToFloors()
    {
        var store = new PlayerSettingsStore(WriteSettings(
            """{"cacheReadaheadSecs":1,"cacheForwardMb":-5,"cacheBackMb":-1}"""));
        Assert.Equal(10, store.CacheReadaheadSeconds());
        Assert.Equal(50, store.CacheForwardMegabytes());
        Assert.Equal(0, store.CacheBackMegabytes());
    }

    [Fact]
    public void CacheTuning_AbsurdlyLargeValues_ClampToCeilings()
    {
        // A hand-edit like 99999999 must not reach mpv, which would reject it and silently keep its default.
        var store = new PlayerSettingsStore(WriteSettings(
            """{"cacheReadaheadSecs":99999999,"cacheForwardMb":99999999,"cacheBackMb":99999999}"""));
        Assert.Equal(86400, store.CacheReadaheadSeconds());
        Assert.Equal(8192, store.CacheForwardMegabytes());
        Assert.Equal(8192, store.CacheBackMegabytes());
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"cacheForwardMb":1,"cacheForwardMb":2}""")]
    [InlineData("""{"cacheBackMb":"\ud800"}""")] // JSON-escaped lone surrogate, as in the existing suites
    public void CacheTuning_HostileFiles_DoNotThrow_AndGiveDefaults(string json)
    {
        var store = new PlayerSettingsStore(WriteSettings(json));
        Assert.Equal(600, store.CacheReadaheadSeconds());
        Assert.Equal(700, store.CacheForwardMegabytes());
        Assert.Equal(300, store.CacheBackMegabytes());
    }

    [Fact]
    public void CacheTuning_SurvivesAVolumeSave()
    {
        var store = new PlayerSettingsStore(WriteSettings("""{"cacheForwardMb":900}"""));
        store.SaveVolume(0.5);
        Assert.Equal(900, store.CacheForwardMegabytes());
    }

    [Fact]
    public void Store_MeasuredAndSimulatedBandwidthRoundTrip()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var store = new PlayerSettingsStore(dir);
        Assert.Null(store.LoadMeasuredBandwidthBps());
        store.SaveMeasuredBandwidthBps(5_000_000);
        Assert.Equal(5_000_000, store.LoadMeasuredBandwidthBps()!.Value, 0);
        Assert.Null(store.LoadSimulatedBandwidthMbps());
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            """{"measuredBandwidthBps":1,"simulatedBandwidthMbps":25}""");
        Assert.Equal(25, store.LoadSimulatedBandwidthMbps()!.Value, 0);
    }

    [Fact]
    public void Store_WrongTypedBandwidthKeys_AreNull()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            """{"measuredBandwidthBps":{},"simulatedBandwidthMbps":"fast"}""");
        var store = new PlayerSettingsStore(dir);
        Assert.Null(store.LoadMeasuredBandwidthBps());
        Assert.Null(store.LoadSimulatedBandwidthMbps());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    public void Store_NonPositiveOrNaNMeasuredBandwidth_IsNotSaved(double bps)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var store = new PlayerSettingsStore(dir);
        store.SaveMeasuredBandwidthBps(5_000_000);
        store.SaveMeasuredBandwidthBps(bps);
        Assert.Equal(5_000_000, store.LoadMeasuredBandwidthBps()!.Value, 0);
    }

    [Theory]
    [InlineData("""{"simulatedBandwidthMbps":0}""")]
    [InlineData("""{"simulatedBandwidthMbps":-4}""")]
    public void Store_NonPositiveSimulatedBandwidth_IsNull(string json)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "settings.json"), json);
        Assert.Null(new PlayerSettingsStore(dir).LoadSimulatedBandwidthMbps());
    }

    [Fact]
    public void BandwidthKeys_AndVolume_PreserveEachOther()
    {
        var dir = WriteSettings("""{"simulatedBandwidthMbps":25,"volume":0.4}""");
        var store = new PlayerSettingsStore(dir);
        store.SaveMeasuredBandwidthBps(5_000_000);
        Assert.Equal(5_000_000, store.LoadMeasuredBandwidthBps()!.Value, 0);
        Assert.Equal(25, store.LoadSimulatedBandwidthMbps()!.Value, 0);
        Assert.Equal(0.4, store.LoadVolume(), 3);
        // The reverse direction: a volume save must keep the measured rate and the override.
        store.SaveVolume(0.9);
        Assert.Equal(5_000_000, store.LoadMeasuredBandwidthBps()!.Value, 0);
        Assert.Equal(25, store.LoadSimulatedBandwidthMbps()!.Value, 0);
        Assert.Equal(0.9, store.LoadVolume(), 3);
    }
}
