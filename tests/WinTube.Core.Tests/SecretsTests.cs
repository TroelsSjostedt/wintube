using WinTube.Core;

namespace WinTube.Core.Tests;

public class SecretsTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "wintube-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(directory, "secrets.json");

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void MissingFile_YieldsTheEmbeddedDefaults()
    {
        var secrets = Secrets.Load(FilePath);
        Assert.Equal("AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8", secrets.InnerTubeApiKey);
        Assert.Equal("861556708454-d6dlm3lh05idd8npek18k6be8ba3oc68.apps.googleusercontent.com",
            secrets.OAuthClientId);
        Assert.Equal("SboVhoG9s0rNafixCSGGKXAT", secrets.OAuthClientSecret);
        Assert.Equal("", secrets.AppwriteHost);      // sync stays opt-in
        Assert.Equal("", secrets.AppwriteProjectId);
    }

    [Fact]
    public void PartialFile_OverridesOnlyItsOwnFields()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, "{\"innerTubeApiKey\":\"MY_KEY\",\"appwriteHost\":\"my.host\"}");
        var secrets = Secrets.Load(FilePath);
        Assert.Equal("MY_KEY", secrets.InnerTubeApiKey);
        Assert.Equal(Secrets.Defaults.OAuthClientId, secrets.OAuthClientId);
        Assert.Equal(Secrets.Defaults.OAuthClientSecret, secrets.OAuthClientSecret);
        Assert.Equal("my.host", secrets.AppwriteHost);
        Assert.Equal("", secrets.AppwriteProjectId);
    }

    [Fact]
    public void EmptyFieldInFile_FallsBackToTheDefault()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, "{\"oauthClientId\":\"\"}");
        Assert.Equal(Secrets.Defaults.OAuthClientId, Secrets.Load(FilePath).OAuthClientId);
    }

    [Fact]
    public void FullFile_WinsOnEveryField()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, "{\"innerTubeApiKey\":\"K\",\"oauthClientId\":\"I\"," +
            "\"oauthClientSecret\":\"S\",\"appwriteHost\":\"H\",\"appwriteProjectId\":\"P\"}");
        var secrets = Secrets.Load(FilePath);
        Assert.Equal(("K", "I", "S", "H", "P"),
            (secrets.InnerTubeApiKey, secrets.OAuthClientId, secrets.OAuthClientSecret,
             secrets.AppwriteHost, secrets.AppwriteProjectId));
    }

    [Fact]
    public void CorruptFile_YieldsTheDefaults()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, "{not json");
        Assert.Equal(Secrets.Defaults.InnerTubeApiKey, Secrets.Load(FilePath).InnerTubeApiKey);
    }
}
