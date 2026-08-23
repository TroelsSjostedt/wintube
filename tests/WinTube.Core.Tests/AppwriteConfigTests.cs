using WinTube.Core;
using WinTube.Core.Sync;

namespace WinTube.Core.Tests;

public class AppwriteConfigTests
{
    [Fact]
    public void FromSecrets_BuildsEndpointFromHost()
    {
        var config = AppwriteConfig.FromSecrets(new Secrets("K", "I", "S")
        {
            AppwriteHost = "appwrite.example.dk",
            AppwriteProjectId = "metube",
        });
        Assert.NotNull(config);
        Assert.Equal("https://appwrite.example.dk/v1", config!.Endpoint);
        Assert.Equal("metube", config.ProjectId);
    }

    [Theory]
    [InlineData("", "metube")]
    [InlineData("host", "")]
    [InlineData("", "")]
    public void FromSecrets_MissingValue_ReturnsNull(string host, string projectId)
    {
        Assert.Null(AppwriteConfig.FromSecrets(new Secrets("K", "I", "S")
        {
            AppwriteHost = host,
            AppwriteProjectId = projectId,
        }));
    }
}
