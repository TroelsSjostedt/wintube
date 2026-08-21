using WinTube.Core.InnerTube;

namespace WinTube.Core.Tests;

public class VisitorDataStoreTests
{
    [Fact]
    public void Extract_DecodesJsonEscapedToken()
    {
        // Real tokens routinely contain JSON-escaped '=' padding (=).
        var html = """... {"visitorData":"CgtXaFFhUQ%3D%3D==","other":1} ...""";
        Assert.Equal("CgtXaFFhUQ%3D%3D==", VisitorDataStore.Extract(html));
    }

    [Fact]
    public void Extract_ReturnsNullWhenAbsentOrEmpty()
    {
        Assert.Null(VisitorDataStore.Extract("<html>no token here</html>"));
        Assert.Null(VisitorDataStore.Extract("""{"visitorData":""}"""));
    }

    [Fact]
    public async Task GetToken_CachesAndInvalidates()
    {
        var calls = 0;
        var handler = new StubHttpHandler((request, _) =>
        {
            calls++;
            Assert.Equal("SOCS=CAE=", request.Headers.GetValues("Cookie").Single());
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent($$$"""{"visitorData":"tok{{{calls}}}"}"""),
            };
        });
        var store = new VisitorDataStore(new HttpClient(handler));

        Assert.Equal("tok1", await store.GetTokenAsync());
        Assert.Equal("tok1", await store.GetTokenAsync());   // cached — no second request
        Assert.Equal(1, calls);
        store.Invalidate();
        Assert.Equal("tok2", await store.GetTokenAsync());
        Assert.Equal(2, calls);
    }
}
