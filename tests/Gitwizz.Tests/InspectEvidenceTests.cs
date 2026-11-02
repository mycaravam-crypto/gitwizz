using System.Text.Json;
using Gitwizz;

namespace Gitwizz.Tests;

public class InspectEvidenceTests
{
    [Theory]
    [InlineData("provider-confirmed")]
    [InlineData(null)]
    public void DirectoryJsonDoesNotExposePresentationOnlyEvidence(string? evidence)
    {
        var entry = new FileInspect.DirectoryEntry(
            "src/Example.cs", new string('a', 40), "author",
            "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z",
            "Change", "#12", Array.Empty<string>(), 1)
        {
            PullRequestEvidence = evidence
        };
        var report = new FileInspect.DirectoryResult(
            "gitwizz.inspect-directory/v1", ".", "HEAD", 1, 200, false,
            new[] { entry }, Array.Empty<string>());

        using var json = JsonDocument.Parse(FileInspect.DirectoryJson(report));
        Assert.False(json.RootElement.GetProperty("files")[0].TryGetProperty("pullRequestEvidence", out _));
    }
}
