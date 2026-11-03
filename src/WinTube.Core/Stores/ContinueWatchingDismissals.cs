using System.Text.Json;

namespace WinTube.Core.Stores;

/// Videos the user removed from the Continue watching row, per profile. The watch position
/// itself is untouched — ContinueWatchingBuilder hides a video while its dismissal is newer
/// than its last watch, so playing it again automatically un-dismisses it.
public sealed class ContinueWatchingDismissals(string rootDirectory, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private Dictionary<string, DateTimeOffset> entries = [];
    private string? profileId;

    public event Action? Changed;

    public void Activate(string? newProfileId)
    {
        if (newProfileId == profileId) return;
        profileId = newProfileId;
        entries = profileId is null ? [] : Load(profileId);
    }

    /// VideoId → when it was dismissed.
    public IReadOnlyDictionary<string, DateTimeOffset> Entries => entries;

    public void Dismiss(string videoId)
    {
        if (profileId is null) return;
        entries[videoId] = now();
        Persist();
        Changed?.Invoke();
    }

    // MARK: storage — <root>\profiles\<profileId>\cw-dismissed.json

    private string FilePath(string profile) =>
        Path.Combine(rootDirectory, "profiles", profile, "cw-dismissed.json");

    private Dictionary<string, DateTimeOffset> Load(string profile)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(
                File.ReadAllText(FilePath(profile))) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException) { return []; }
    }

    private void Persist()
    {
        if (profileId is null) return;
        var path = FilePath(profileId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(entries));
    }
}
