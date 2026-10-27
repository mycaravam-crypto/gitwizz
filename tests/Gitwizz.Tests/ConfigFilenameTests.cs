using Gitwizz;

namespace Gitwizz.Tests;

public sealed class ConfigFilenameTests
{
    [Fact]
    public void CanonicalAndLegacyFilenamesAreDistinct()
    {
        Assert.Equal("gitwizz.yaml", RepoConfig.FileName);
        Assert.Equal(".gitwizz.yml", RepoConfig.LegacyFileName);
    }

    [Fact]
    public void ExistingYamlConfigurationStillParses()
    {
        var config = RepoConfig.Parse("ignored:\n  - '*.generated.cs'\n");
        Assert.Contains("*.generated.cs", config.Ignored);
    }
}
