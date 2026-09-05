namespace WinTube.Core.Player;

/// The preview rules, UI-free: one active preview at a time, started only after the pointer
/// (or keyboard focus) has rested on a card for the dwell, preempted the moment another card
/// goes warm, and never resurrected by a stale stream resolution. The caller owns the actual
/// timer and player; this only decides. Not thread-safe — drive it from the UI thread.
public sealed class PreviewGate(TimeSpan dwell)
{
    private string? warmId;
    private DateTimeOffset warmSince;
    private string? activeId;

    /// A card went warm (pointer entered / focus landed). Returns an id whose preview must
    /// stop now — a new card preempts the previous preview immediately — or null.
    public string? Warm(string id, DateTimeOffset now)
    {
        if (id != warmId)
        {
            warmId = id;
            warmSince = now;
        }
        if (activeId is null || activeId == id) return null;
        var stopped = activeId;
        activeId = null;
        return stopped;
    }

    /// The caller's dwell timer fired for `id`. Returns the id to start resolving when the
    /// card is still warm and the dwell has truly elapsed; null otherwise.
    public string? DwellElapsed(string id, DateTimeOffset now)
    {
        if (id != warmId || now - warmSince < dwell) return null;
        activeId = id;
        return id;
    }

    /// A card went cold (pointer left / focus moved / card unloaded). Returns the id whose
    /// preview must stop, or null when nothing of that card's is running.
    public string? Cold(string id)
    {
        if (warmId == id) warmId = null;
        if (activeId != id) return null;
        activeId = null;
        return id;
    }

    /// Whether `id` still owns the active slot — checked after an async resolution completes,
    /// so a preview preempted mid-resolve is discarded instead of starting to play.
    public bool IsActive(string id) => activeId == id;
}
