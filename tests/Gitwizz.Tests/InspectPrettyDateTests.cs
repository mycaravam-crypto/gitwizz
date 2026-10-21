using Gitwizz;

namespace Gitwizz.Tests;

public class InspectPrettyDateTests
{
    [Theory]
    [InlineData("2026-10-21T07:47:30+02:00", "21.10.2026 07:47")]
    [InlineData("2026-01-03T12:05:59Z", "03.01.2026 12:05")]
    [InlineData("not-a-date", "not-a-date")]
    public void German_minute_is_formatted_consistently(string input, string expected) =>
        Assert.Equal(expected, InspectPretty.GermanMinute(input));
}
