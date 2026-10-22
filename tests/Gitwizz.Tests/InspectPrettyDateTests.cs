using Gitwizz;

namespace Gitwizz.Tests;

public class InspectPrettyDateTests
{
    [Theory]
    [InlineData("2026-10-21T07:47:30+02:00", "21.10.2026 07:47")]
    [InlineData("2026-01-03T12:05:59Z", "03.01.2026 12:05")]
    [InlineData("not-a-date", "not-a-date")]
    [InlineData("2026-12-31T23:59:00+01:00", "31.12.2026 23:59")]
    public void German_minute_is_formatted_consistently(string input, string expected) =>
        Assert.Equal(expected, InspectPretty.GermanMinute(input));
    [Fact]
    public void Directory_pretty_output_distinguishes_provider_evidence_from_commit_mentions()
    {
        var console = new Spectre.Console.Testing.TestConsole();
        var report = new FileInspect.DirectoryResult("gitwizz.inspect-directory/v1", ".", "HEAD", 0, 200,
            false, Array.Empty<FileInspect.DirectoryEntry>(), new[] { "Sample warning" });
        InspectPretty.Directory(report, console);
        Assert.Contains("provider-enriched PR metadata", console.Output);
    }
}
