using System.Text.Json;
using WinTube.Core.Sync;

namespace WinTube.Core.Tests;

public class AppwriteQueryTests
{
    private static JsonElement Parse(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void Equal_EncodesMethodAttributeAndValues()
    {
        var json = Parse(AppwriteQuery.Equal("userId", "yt123"));
        Assert.Equal("equal", json.GetProperty("method").GetString());
        Assert.Equal("userId", json.GetProperty("attribute").GetString());
        Assert.Equal("yt123", json.GetProperty("values")[0].GetString());
    }

    [Fact]
    public void GreaterThan_EncodesLikeEqual()
    {
        var json = Parse(AppwriteQuery.GreaterThan("watchedAt", "2026-01-01T00:00:00.000Z"));
        Assert.Equal("greaterThan", json.GetProperty("method").GetString());
        Assert.Equal("watchedAt", json.GetProperty("attribute").GetString());
    }

    [Fact]
    public void OrderAsc_OmitsValues()
    {
        var json = Parse(AppwriteQuery.OrderAsc("watchedAt"));
        Assert.Equal("orderAsc", json.GetProperty("method").GetString());
        Assert.False(json.TryGetProperty("values", out _));
    }

    [Fact]
    public void Limit_CarriesNumericValue_NoAttribute()
    {
        var json = Parse(AppwriteQuery.Limit(100));
        Assert.Equal(100, json.GetProperty("values")[0].GetInt32());
        Assert.False(json.TryGetProperty("attribute", out _));
    }

    [Fact]
    public void CursorAfter_CarriesRowId()
    {
        var json = Parse(AppwriteQuery.CursorAfter("abc_def"));
        Assert.Equal("cursorAfter", json.GetProperty("method").GetString());
        Assert.Equal("abc_def", json.GetProperty("values")[0].GetString());
    }
}
