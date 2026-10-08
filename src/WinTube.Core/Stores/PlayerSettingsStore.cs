using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinTube.Core.Stores;

/// Small plain-text player preferences — currently volume and subtitle language. Not per-profile
/// and not secret, so an ordinary JSON file rather than the DPAPI stores the tokens use. Anything
/// unreadable falls back to defaults; losing a setting must never cost a launch.
public sealed class PlayerSettingsStore(string dataDirectory)
{
    private string FilePath => Path.Combine(dataDirectory, "settings.json");

    /// Loads the settings from the JSON file as a JsonObject, tolerating missing file, bad JSON, non-object roots,
    /// duplicate keys, and lone surrogates. Returns an empty object on any error (defaults).
    private JsonObject Load()
    {
        try
        {
            var text = File.ReadAllText(FilePath);
            var obj = JsonNode.Parse(text) as JsonObject ?? new JsonObject();
            // Force materialization of lazy parsing to surface duplicate keys and lone surrogates
            _ = obj.Count;
            _ = obj.ToJsonString();
            return obj;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            return new JsonObject();
        }
    }

    /// Saves the settings back to the JSON file, preserving all keys.
    /// A failed save costs one setting, not the session.
    private void Save(JsonObject settings)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(FilePath, settings.ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A failed save costs one remembered setting, not the session.
        }
    }

    /// The stored volume, clamped to 0..1. Full volume when nothing usable is stored —
    /// MediaPlayer's own default, so a fresh install behaves exactly as before this store.
    public double LoadVolume()
    {
        var settings = Load();
        if (settings["volume"] is JsonValue v && v.TryGetValue(out double d))
        {
            if (double.IsNaN(d)) return 1.0;
            return Math.Clamp(d, 0.0, 1.0);
        }
        return 1.0;
    }

    /// Saves the volume, preserving any other settings in the file.
    public void SaveVolume(double volume)
    {
        if (double.IsNaN(volume)) return;
        var settings = Load();
        settings["volume"] = Math.Clamp(volume, 0.0, 1.0);
        Save(settings);
    }

    /// The stored subtitle language preference, or null if not set or unreadable.
    public string? LoadSubtitleLanguage()
    {
        var settings = Load();
        if (settings["subtitleLanguage"] is JsonValue v && v.TryGetValue(out string? s))
        {
            return s;
        }
        return null;
    }

    /// Saves the subtitle language preference. Pass null or empty string to clear the preference.
    public void SaveSubtitleLanguage(string? language)
    {
        var settings = Load();
        // Treat empty string as null (off)
        if (string.IsNullOrEmpty(language))
        {
            settings.Remove("subtitleLanguage");
        }
        else
        {
            settings["subtitleLanguage"] = language;
        }
        Save(settings);
    }

    /// The download rate (bytes/s) the last session measured at teardown, seeding Auto's first pick.
    /// Null if nothing usable is stored: absent, wrong-typed, or not a positive finite number.
    public double? LoadMeasuredBandwidthBps()
    {
        if (Load()["measuredBandwidthBps"] is JsonValue v && v.TryGetValue(out double d) && double.IsFinite(d) && d > 0)
        {
            return d;
        }
        return null;
    }

    /// Saves the measured download rate, preserving other settings. Only a positive finite rate is
    /// stored; anything else is dropped so the previous measurement stays in place.
    public void SaveMeasuredBandwidthBps(double bytesPerSecond)
    {
        if (!double.IsFinite(bytesPerSecond) || bytesPerSecond <= 0) return;
        var settings = Load();
        settings["measuredBandwidthBps"] = bytesPerSecond;
        Save(settings);
    }

    /// The hand-edited test override for Auto's bandwidth, in megabits per second. Read-only — nothing
    /// in the app writes it. Null if absent, wrong-typed, or not a positive finite number.
    public double? LoadSimulatedBandwidthMbps()
    {
        if (Load()["simulatedBandwidthMbps"] is JsonValue v && v.TryGetValue(out double d) && double.IsFinite(d) && d > 0)
        {
            return d;
        }
        return null;
    }

    // Stream-cache tuning. Read-only, edited by hand in settings.json (like the secrets overrides) —
    // no savers, so nothing in the app ever rewrites them. Defaults are sized for a long adaptive
    // video: ten minutes of lookahead, ~700 MB forward and ~300 MB of back-buffer for rewinds.

    /// Seconds of lookahead mpv's demuxer may buffer (`cacheReadaheadSecs`, default 600, 10..86400).
    public int CacheReadaheadSeconds() => ReadInt("cacheReadaheadSecs", 600, min: 10, max: 86400);

    /// Megabytes the demuxer may hold ahead of the playhead (`cacheForwardMb`, default 700, 50..8192).
    public int CacheForwardMegabytes() => ReadInt("cacheForwardMb", 700, min: 50, max: 8192);

    /// Megabytes the demuxer keeps behind the playhead for cheap rewinds (`cacheBackMb`, default 300, 0..8192).
    public int CacheBackMegabytes() => ReadInt("cacheBackMb", 300, min: 0, max: 8192);

    /// A whole-number setting clamped to [min, max]. The ceiling is not a taste call: mpv rejects an
    /// out-of-range value and the caller would silently be left on mpv's own default, so a hand-edit
    /// typo like 99999999 is pulled back to something mpv accepts instead. Absent, wrong-typed or
    /// fractional values give the default.
    private int ReadInt(string key, int fallback, int min, int max)
    {
        if (Load()[key] is JsonValue v && v.TryGetValue(out int n)) return Math.Clamp(n, min, max);
        return fallback;
    }
}
