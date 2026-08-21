using System.Text.Json;

namespace WinTube.Core.InnerTube;

/// JSON traversal for InnerTube responses. Ported from the helpers in the tvOS
/// InnerTubeClient.swift / VideoItemParser.swift. System.Text.Json enumerates object
/// properties in document order, which is the order YouTube wants things shown in.
public static class Json
{
    /// Follows a slash path like "playabilityStatus/status". Objects only; no array indices.
    public static JsonElement? ValueAt(JsonElement root, string path)
    {
        var current = root;
        foreach (var key in path.Split('/'))
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(key, out var next))
                return null;
            current = next;
        }
        return current;
    }

    /// The non-empty string at a path, else null.
    public static string? StringAt(JsonElement root, string path) =>
        ValueAt(root, path) is { ValueKind: JsonValueKind.String } el &&
        el.GetString() is { Length: > 0 } s ? s : null;

    /// Every object stored under a property named `name`, anywhere in the subtree,
    /// in document order.
    public static IReadOnlyList<JsonElement> FindAllRenderers(JsonElement root, string name)
    {
        var results = new List<JsonElement>();
        void Walk(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in el.EnumerateObject())
                    {
                        if (property.Name == name && property.Value.ValueKind == JsonValueKind.Object)
                            results.Add(property.Value);
                        Walk(property.Value);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray()) Walk(item);
                    break;
            }
        }
        Walk(root);
        return results;
    }

    /// Text from {"simpleText":…} or {"runs":[{"text":…}]}. Null when neither yields text.
    public static string? InnerTubeText(JsonElement? el)
    {
        if (el is not { ValueKind: JsonValueKind.Object } obj) return null;
        if (obj.TryGetProperty("simpleText", out var simple) &&
            simple.ValueKind == JsonValueKind.String)
            return simple.GetString();
        if (obj.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
        {
            var text = string.Concat(runs.EnumerateArray()
                .Select(r => r.TryGetProperty("text", out var t) &&
                             t.ValueKind == JsonValueKind.String ? t.GetString() : null));
            return text.Length > 0 ? text : null;
        }
        return null;
    }

    /// Whether `name` is a property key anywhere in the subtree.
    public static bool ContainsKey(JsonElement root, string name)
    {
        switch (root.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in root.EnumerateObject())
                {
                    if (property.Name == name) return true;
                    if (ContainsKey(property.Value, name)) return true;
                }
                return false;
            case JsonValueKind.Array:
                return root.EnumerateArray().Any(item => ContainsKey(item, name));
            default:
                return false;
        }
    }

    /// InnerTube encodes numbers as int, double, or string depending on the field.
    public static int? IntValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number when el.TryGetInt32(out var i) => i,
        JsonValueKind.Number => (int)el.GetDouble(),
        JsonValueKind.String when int.TryParse(el.GetString(), out var i) => i,
        _ => null,
    };
}
