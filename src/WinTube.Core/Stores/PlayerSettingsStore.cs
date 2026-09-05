using System.Text.Json;

namespace WinTube.Core.Stores;

/// Small plain-text player preferences — currently just the volume. Not per-profile and not
/// secret, so an ordinary JSON file rather than the DPAPI stores the tokens use. Anything
/// unreadable falls back to defaults; losing a volume setting must never cost a launch.
public sealed class PlayerSettingsStore(string dataDirectory)
{
    private string FilePath => Path.Combine(dataDirectory, "settings.json");

    /// The stored volume, clamped to 0..1. Full volume when nothing usable is stored —
    /// MediaPlayer's own default, so a fresh install behaves exactly as before this store.
    public double LoadVolume()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (!doc.RootElement.TryGetProperty("volume", out var volume)) return 1.0;
            if (volume.ValueKind != JsonValueKind.Number || !volume.TryGetDouble(out var value)) return 1.0;
            if (double.IsNaN(value)) return 1.0;
            return Math.Clamp(value, 0.0, 1.0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return 1.0;
        }
    }

    public void SaveVolume(double volume)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(
                new Dictionary<string, double> { ["volume"] = Math.Clamp(volume, 0.0, 1.0) }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A failed save costs one remembered volume, not the session.
        }
    }
}
