using WinTube.Core;

namespace WinTube.Core.Tests;

public class SecretsTests
{
    [Fact]
    public void Load_ReadsAllThreeValues()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(path,
            """{"innerTubeApiKey":"KEY","oauthClientId":"ID","oauthClientSecret":"SECRET"}""");
        try
        {
            var secrets = Secrets.Load(path);
            Assert.Equal("KEY", secrets.InnerTubeApiKey);
            Assert.Equal("ID", secrets.OAuthClientId);
            Assert.Equal("SECRET", secrets.OAuthClientSecret);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyStrings()
    {
        var secrets = Secrets.Load(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
        Assert.Equal("", secrets.InnerTubeApiKey);
        Assert.Equal("", secrets.OAuthClientId);
        Assert.Equal("", secrets.OAuthClientSecret);
    }
}
