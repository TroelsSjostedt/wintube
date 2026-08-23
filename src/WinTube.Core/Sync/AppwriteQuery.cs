using System.Text.Json;

namespace WinTube.Core.Sync;

/// The query strings Appwrite expects in queries[], built by hand for the handful the sync
/// uses. The SDK's Query helper is a thin JSON encoder over the same shape.
public static class AppwriteQuery
{
    public static string Equal(string attribute, string value) =>
        Encode("equal", attribute, [value]);

    public static string GreaterThan(string attribute, string value) =>
        Encode("greaterThan", attribute, [value]);

    public static string OrderAsc(string attribute) =>
        Encode("orderAsc", attribute, null);

    public static string Limit(int value) =>
        Encode("limit", null, [value]);

    public static string CursorAfter(string rowId) =>
        Encode("cursorAfter", null, [rowId]);

    private static string Encode(string method, string? attribute, object[]? values)
    {
        var obj = new Dictionary<string, object> { ["method"] = method };
        if (attribute is not null) obj["attribute"] = attribute;
        if (values is not null) obj["values"] = values;
        return JsonSerializer.Serialize(obj);
    }
}
