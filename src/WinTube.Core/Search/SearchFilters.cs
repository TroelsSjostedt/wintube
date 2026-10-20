namespace WinTube.Core.Search;

/// Enum values ARE the wire values of YouTube's search-filter protobuf — do not renumber.
public enum SearchDuration { Short = 1, Long = 2, Medium = 3 }

public enum SearchUploadDate { LastHour = 1, Today = 2, ThisWeek = 3, ThisMonth = 4, ThisYear = 5 }

/// The /search `params` the TV client honors: duration and upload-date filters, live-verified
/// 2026-10-09. Sort and language are deliberately absent — the TV client ignores sort (probe:
/// upload-date/rating sort returned baseline results) and the protobuf has no language field.
public sealed record SearchFilters(
    SearchUploadDate? UploadDate = null, SearchDuration? Duration = null)
{
    public bool IsEmpty => UploadDate is null && Duration is null;

    /// The base64 `params` value, or null when no filter is set (the field is then omitted
    /// entirely — an empty filters submessage is not the same request as none).
    /// Wire format: message { Filters filters = 2 { uploadDate = 1; duration = 3; } },
    /// all varint, single-byte values by construction.
    public string? ToParams()
    {
        if (IsEmpty) return null;
        var inner = new List<byte>();
        if (UploadDate is { } date) { inner.Add(0x08); inner.Add((byte)date); }
        if (Duration is { } duration) { inner.Add(0x18); inner.Add((byte)duration); }
        return Convert.ToBase64String([0x12, (byte)inner.Count, .. inner]);
    }
}
