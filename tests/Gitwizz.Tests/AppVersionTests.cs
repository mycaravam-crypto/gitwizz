using System.Reflection;
using Gitwizz;

namespace Gitwizz.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("0.0.0-dev", "0.0.0-dev")]
    [InlineData("0.0.0-dev+gabc123", "0.0.0-dev")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3+abcdef", "1.2.3")]
    [InlineData("2.0.0-rc.1+git.sha", "2.0.0-rc.1")]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("  ", "unknown")]
    public void Displays_semver_without_build_metadata(string? informationalVersion, string expected) =>
        Assert.Equal(expected, AppVersion.Normalize(informationalVersion));

    [Fact]
    public void Reads_the_actual_assembly_informational_version()
    {
        var assembly = typeof(Cli).Assembly;
        var metadata = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();

        Assert.NotNull(metadata);
        Assert.Equal(AppVersion.Normalize(metadata.InformationalVersion), AppVersion.Display(assembly));
    }
}
