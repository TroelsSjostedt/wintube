using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinTube.Core.Stores;

/// Small plain-text player preferences — currently volume and subtitle language. Not per-profile
/// and not secret, so an ordinary JSON file rather than the DPAPI stores the tokens use. Anything
/// unreadable falls back to defaults; losing a setting must never cost a launch.
public sealed class PlayerSettingsStore(string dataDirectory)
{
    private string FilePath => Path.Combine(dataDirectory, "settings.json");

    /// Loads the settings from the JSON file as a JsonObject, tolerating missing file, bad JSON, and non-object roots.
    /// Missing file, bad JSON, or duplicate keys return an empty object (defaults).
    private JsonObject Load()
    {
        try
        {
            var text = File.ReadAllText(FilePath);
            return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
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
}
