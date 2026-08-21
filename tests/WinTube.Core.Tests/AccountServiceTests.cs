using System.Text.Json;
using WinTube.Core.Auth;

namespace WinTube.Core.Tests;

public class AccountServiceTests
{
    private const string AccountsList = """
        {"contents":[{"accountSectionListRenderer":{"contents":[
          {"accountItemSectionRenderer":{"contents":[
            {"accountItem":{
               "accountName":{"simpleText":"Other"},"isSelected":false,
               "serviceEndpoint":{"selectActiveIdentityEndpoint":{"supportedTokens":[
                 {"accountStateToken":{"obfuscatedGaiaId":"GAIA_OTHER"}}]}}}},
            {"accountItem":{
               "accountName":{"simpleText":"Troels"},
               "channelHandle":{"simpleText":"@troels"},
               "accountPhoto":{"thumbnails":[
                 {"url":"//a/small","width":88},{"url":"//a/big","width":216}]},
               "isSelected":true,
               "serviceEndpoint":{"selectActiveIdentityEndpoint":{"supportedTokens":[
                 {"accountStateToken":{"obfuscatedGaiaId":"GAIA_ME"}}]}}}}]}}]}}]}
        """;

    [Fact]
    public void Parse_PicksSelectedAccountGaiaIdAndLargestAvatar()
    {
        var info = AccountService.Parse(JsonDocument.Parse(AccountsList).RootElement);
        Assert.NotNull(info);
        Assert.Equal("GAIA_ME", info!.Key);
        Assert.Equal("Troels", info.Name);
        Assert.Equal("https://a/big", info.AvatarUrl);
    }

    [Fact]
    public void Parse_NoGaiaId_FallsBackToHandle()
    {
        var json = JsonDocument.Parse("""
            {"accountItem":{"accountName":{"simpleText":"N"},
             "channelHandle":{"simpleText":"@handle"},"isSelected":true}}
            """).RootElement;
        Assert.Equal("@handle", AccountService.Parse(json)!.Key);
    }

    [Fact]
    public void Parse_NothingUsable_ReturnsNull()
    {
        Assert.Null(AccountService.Parse(JsonDocument.Parse("{}").RootElement));
    }

    [Fact]
    public void ProfileId_IsLowercaseHexSha256()
    {
        // sha256("abc") — a known vector.
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ProfileId.From("abc"));
    }
}
