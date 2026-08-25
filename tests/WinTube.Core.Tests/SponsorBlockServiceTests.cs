using System.Security.Cryptography;
using System.Text;
using WinTube.Core.SponsorBlock;

namespace WinTube.Core.Tests;

public class SponsorBlockServiceTests
{
    private const string VideoId = "dQw4w9WgXcQ";
    private static readonly string FullHash = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(VideoId))).ToLowerInvariant();

    private static SponsorBlockService Make(
        Func<HttpRequestMessage, string, HttpResponseMessage> respond, out StubHttpHandler handler)
    {
        handler = new StubHttpHandler(respond);
        return new SponsorBlockService(new HttpClient(handler));
    }

    [Fact]
    public async Task Fetch_SendsPrefixSortedCategoriesAndSkipActionType()
    {
        var service = Make((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            Assert.Contains($"/api/skipSegments/{FullHash[..4]}?", url);
            var query = Uri.UnescapeDataString(url);
            Assert.Contains("""["interaction","music_offtopic","selfpromo","sponsor"]""", query);
            Assert.Contains("""["skip"]""", query);
            Assert.Equal("WinTube/0.1", request.Headers.GetValues("User-Agent").Single());
            return StubHttpHandler.JsonResponse("[]");
        }, out var handler);

        Assert.Empty(await service.FetchSegmentsAsync(VideoId));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Fetch_MatchesOwnVideoByIdOrHash_AndParsesSegments()
    {
        var service = Make((_, _) => StubHttpHandler.JsonResponse($$"""
            [
              {"videoID":"otherVideo","segments":[
                {"UUID":"x","category":"sponsor","segment":[0,30],"votes":5}]},
              {"videoID":"{{VideoId}}","hash":"{{FullHash}}","segments":[
                {"UUID":"keep","category":"sponsor","segment":[10,25.5],"votes":3},
                {"UUID":"downvoted","category":"sponsor","segment":[40,60],"votes":-2},
                {"UUID":"tiny","category":"sponsor","segment":[70,70.5],"votes":1},
                {"UUID":"unknowncat","category":"exclusive_access","segment":[80,95],"votes":1},
                {"UUID":"touching","category":"selfpromo","segment":[25.8,33],"votes":0}]}
            ]
            """), out _);

        var segments = await service.FetchSegmentsAsync(VideoId);
        // downvoted, tiny and unknown-category dropped; keep+touching merged, earlier id wins.
        var segment = Assert.Single(segments);
        Assert.Equal(("keep", SponsorCategory.Sponsor, 10.0, 33.0),
            (segment.Id, segment.Category, segment.Start, segment.End));
    }

    [Theory]
    [InlineData(404, "Not Found")]
    [InlineData(500, "boom")]
    [InlineData(200, "not json at all")]
    public async Task Fetch_AnyTroubleYieldsEmpty(int status, string body)
    {
        var service = Make((_, _) => new HttpResponseMessage((System.Net.HttpStatusCode)status)
        {
            Content = new StringContent(body),
        }, out _);
        Assert.Empty(await service.FetchSegmentsAsync(VideoId));
    }

    [Fact]
    public async Task Fetch_TransportExceptionYieldsEmpty()
    {
        var service = Make((_, _) => throw new HttpRequestException("dns down"), out _);
        Assert.Empty(await service.FetchSegmentsAsync(VideoId));
    }

    [Fact]
    public async Task Fetch_EmptyCategorySet_MakesNoRequest()
    {
        var service = Make((_, _) => StubHttpHandler.JsonResponse("[]"), out var handler);
        Assert.Empty(await service.FetchSegmentsAsync(VideoId, new HashSet<SponsorCategory>()));
        Assert.Empty(handler.Requests);
    }
}
