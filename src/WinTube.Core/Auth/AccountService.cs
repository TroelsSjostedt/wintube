using System.Text.Json;
using WinTube.Core.InnerTube;

namespace WinTube.Core.Auth;

public sealed record AccountInfo(string Key, string Name, string? AvatarUrl);

public sealed class AccountException()
    : Exception("YouTube didn't say which account these credentials belong to");

/// Reads the signed-in account's name and avatar from account/accounts_list — the only call
/// that answers "who is this?" for a set of credentials. Port of the tvOS AccountService.
/// (account/account_menu, the web client's equivalent, answers HTTP 400 on TVHTML5.)
public sealed class AccountService(InnerTubeClient innerTube)
{
    public async Task<AccountInfo> LoadAsync(string accessToken, CancellationToken ct = default)
    {
        using var doc = await innerTube.PostAsync(
            "account/accounts_list", ClientKind.Tv,
            new Dictionary<string, object?>(), bearer: accessToken, ct: ct);
        return Parse(doc.RootElement) ?? throw new AccountException();
    }

    /// A device-flow token is bound to one account, but the response is a *list* —
    /// isSelected names the right entry, and only its absence falls back to the first.
    public static AccountInfo? Parse(JsonElement json)
    {
        var items = Json.FindAllRenderers(json, "accountItem");
        var item = items.FirstOrDefault(i =>
            i.TryGetProperty("isSelected", out var sel) && sel.ValueKind == JsonValueKind.True);
        if (item.ValueKind != JsonValueKind.Object)
            item = items.FirstOrDefault();
        if (item.ValueKind != JsonValueKind.Object) return null;

        var name = Json.InnerTubeText(item.TryGetProperty("accountName", out var n) ? n : null) ?? "";
        var key = new[]
        {
            GaiaId(item),
            Json.InnerTubeText(item.TryGetProperty("channelHandle", out var h) ? h : null),
            Json.InnerTubeText(item.TryGetProperty("accountByline", out var b) ? b : null),
            name,
        }.FirstOrDefault(c => !string.IsNullOrEmpty(c));
        if (key is null) return null;

        return new AccountInfo(key, name.Length > 0 ? name : key, LargestAvatar(item));
    }

    /// Google's own id for the account, tucked into the endpoint that would switch to it.
    /// Survives renames of both the account and its channel.
    private static string? GaiaId(JsonElement item) =>
        Json.FindAllRenderers(item, "accountStateToken")
            .Select(t => Json.StringAt(t, "obfuscatedGaiaId"))
            .FirstOrDefault(id => !string.IsNullOrEmpty(id));

    private static string? LargestAvatar(JsonElement item)
    {
        if (Json.ValueAt(item, "accountPhoto/thumbnails") is not
            { ValueKind: JsonValueKind.Array } thumbs) return null;
        var best = thumbs.EnumerateArray()
            .OrderByDescending(t => t.TryGetProperty("width", out var w) ? Json.IntValue(w) ?? 0 : 0)
            .FirstOrDefault();
        var url = best.ValueKind == JsonValueKind.Object ? Json.StringAt(best, "url") : null;
        if (url is null) return null;
        return url.StartsWith("//") ? "https:" + url : url;
    }
}
