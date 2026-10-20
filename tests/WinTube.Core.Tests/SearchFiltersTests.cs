using WinTube.Core.Search;

namespace WinTube.Core.Tests;

/// Expected strings are the exact `params` values live-verified against the TV InnerTube
/// client on 2026-10-09 (probe: duration and upload-date filters honored 100%).
public class SearchFiltersTests
{
    [Theory]
    [InlineData(SearchDuration.Short, "EgIYAQ==")]
    [InlineData(SearchDuration.Long, "EgIYAg==")]
    [InlineData(SearchDuration.Medium, "EgIYAw==")]
    public void Duration_EncodesFieldThree(SearchDuration duration, string expected) =>
        Assert.Equal(expected, new SearchFilters(Duration: duration).ToParams());

    [Theory]
    [InlineData(SearchUploadDate.LastHour, "EgIIAQ==")]
    [InlineData(SearchUploadDate.Today, "EgIIAg==")]
    [InlineData(SearchUploadDate.ThisWeek, "EgIIAw==")]
    [InlineData(SearchUploadDate.ThisMonth, "EgIIBA==")]
    [InlineData(SearchUploadDate.ThisYear, "EgIIBQ==")]
    public void UploadDate_EncodesFieldOne(SearchUploadDate date, string expected) =>
        Assert.Equal(expected, new SearchFilters(UploadDate: date).ToParams());

    [Fact]
    public void Both_EncodesUploadDateBeforeDuration() =>
        Assert.Equal("EgQIAhgB",
            new SearchFilters(SearchUploadDate.Today, SearchDuration.Short).ToParams());

    [Fact]
    public void Empty_YieldsNull_SoNoParamsFieldIsSent()
    {
        Assert.Null(new SearchFilters().ToParams());
        Assert.True(new SearchFilters().IsEmpty);
        Assert.False(new SearchFilters(Duration: SearchDuration.Short).IsEmpty);
    }
}
