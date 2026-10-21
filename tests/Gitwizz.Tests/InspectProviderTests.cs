using Gitwizz;

namespace Gitwizz.Tests;

public class InspectProviderTests
{
    [Theory]
    [InlineData("azure-devops", "", "azure-devops")]
    [InlineData("ado", "", "azure-devops")]
    [InlineData("azure", "", "azure-devops")]
    [InlineData("github", "https://example.com/repo.git", "github")]
    [InlineData(null, "https://dev.azure.com/org/project/_git/repo", "azure-devops")]
    [InlineData(null, "https://github.com/org/repo.git", "github")]
    [InlineData(null, "https://example.com/repo.git", null)]
    public void Chooses_correct_provider(string? requested, string remote, string? expected) =>
        Assert.Equal(expected, InspectProvider.Select(requested, remote));

    [Fact]
    public void Unknown_explicit_provider_is_rejected() =>
        Assert.Throws<ArgumentException>(() => InspectProvider.Select("unknown", "https://github.com/org/repo"));
}
