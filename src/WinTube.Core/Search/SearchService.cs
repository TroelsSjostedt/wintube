using WinTube.Core.Feed;
using WinTube.Core.InnerTube;
using WinTube.Core.Models;

namespace WinTube.Core.Search;

/// Search returns its hits as shelves too, but a flat relevance-ordered list is what a
/// results grid wants, so the shelf structure is deliberately discarded.
public sealed class SearchService(InnerTubeClient innerTube)
{
    public async Task<IReadOnlyList<VideoItem>> SearchAsync(
        string query, string accessToken, CancellationToken ct = default)
    {
        using var doc = await innerTube.PostAsync("search", ClientKind.Tv,
            new Dictionary<string, object?> { ["query"] = query },
            bearer: accessToken, ct: ct);
        return VideoItemParser.Items(doc.RootElement, DateTimeOffset.UtcNow)
            .Where(item => !item.IsShort).ToList();
    }
}
