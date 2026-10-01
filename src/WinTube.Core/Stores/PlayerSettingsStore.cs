using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinTube.Core.Stores;

/// Small plain-text player preferences — currently volume and subtitle language. Not per-profile
/// and not secret, so an ordinary JSON file rather than the DPAPI stores the tokens use. Anything
/// unreadable falls back to defaults; losing a setting must never cost a launch.
public sealed class PlayerSettingsStore(string dataDirectory)
{
    private string FilePath => Path.Combine(dataDirectory, "settings.json");

    /// Loads the settings from the JSON file as a JsonNode, tolerating missing file and unknown keys.
    /// Missing file or bad JSON returns an empty object (defaults).
    private JsonNode Load()
    {
        try
        {
            var text = File.ReadAllText(FilePath);
            return JsonNode.Parse(text) ?? new JsonObject();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new JsonObject();
        }
    }

    /// Saves the settings back to the JSON file, preserving all keys.
    /// A failed save costs one setting, not the session.
    private void Save(JsonNode settings)
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
        try
        {
            var settings = Load();
            if (settings["volume"] == null) return 1.0;
            var value = settings["volume"]!.AsValue().TryGetValue(out double d) ? d : 1.0;
            if (double.IsNaN(value)) return 1.0;
            return Math.Clamp(value, 0.0, 1.0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return 1.0;
        }
    }

    /// Saves the volume, preserving any other settings in the file.
    public void SaveVolume(double volume)
    {
        var settings = (JsonObject)Load();
        settings["volume"] = Math.Clamp(volume, 0.0, 1.0);
        Save(settings);
    }

    /// The stored subtitle language preference, or null if not set or unreadable.
    public string? LoadSubtitleLanguage()
    {
        try
        {
            var settings = Load();
            if (settings["subtitleLanguage"] == null) return null;
            return settings["subtitleLanguage"]!.AsValue().TryGetValue(out string? s) ? s : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// Saves the subtitle language preference. Pass null to clear the preference.
    public void SaveSubtitleLanguage(string? language)
    {
        var settings = (JsonObject)Load();
        if (language == null)
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
