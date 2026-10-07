using System.Net;
using System.Text.RegularExpressions;

namespace Gitwizz;

/// <summary>Normalizes requirement text (Azure DevOps HTML fields, GitHub Markdown issues) into individual acceptance criteria.</summary>
public static partial class Requirements
{
    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/h\d|/tr)\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTags();
    [GeneratedRegex(@"<\s*li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemTag();
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();
    [GeneratedRegex(@"^\s*(?:[-*+•]|\d+[.)])\s+(?:\[[ xX]\]\s+)?(?<text>.+)$")]
    private static partial Regex ListItem();
    [GeneratedRegex(@"^\s*(?:#{1,6}\s*|\*\*|__)?\s*acceptance\s+criteri(?:a|on)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex AcceptanceHeading();
    [GeneratedRegex(@"^\s*(#{1,6}\s+\S|\*\*[^*]+\*\*\s*:?\s*$)")]
    private static partial Regex Heading();

    /// <summary>Plain text from an HTML field: block tags become line breaks, list items "- ", entities decoded.</summary>
    public static string PlainText(string html)
    {
        if (!html.Contains('<')) return WebUtility.HtmlDecode(html);
        var text = ListItemTag().Replace(html, "\n- ");
        text = LineBreakTags().Replace(text, "\n");
        return WebUtility.HtmlDecode(Tag().Replace(text, ""));
    }

    /// <summary>
    /// The criteria in a dedicated acceptance-criteria field: its list items, or its non-empty lines when it has no list.
    /// </summary>
    public static List<string> Criteria(string text)
    {
        var lines = PlainText(text).Split('\n').Select(l => l.Trim()).Where(l => l != "").ToList();
        var items = lines.Select(l => ListItem().Match(l)).Where(m => m.Success).Select(m => m.Groups["text"].Value.Trim()).ToList();
        return (items.Count > 0 ? items : lines).Where(l => !AcceptanceHeading().IsMatch(l)).Distinct().ToList();
    }

    /// <summary>
    /// The criteria in a description that may hold other text, e.g. a GitHub issue body: the list under an
    /// "Acceptance criteria" heading, up to the next heading. Empty when there is no such section.
    /// </summary>
    public static List<string> CriteriaSection(string body)
    {
        var lines = PlainText(body).Replace("\r", "").Split('\n');
        var start = Array.FindIndex(lines, l => AcceptanceHeading().IsMatch(l));
        if (start < 0) return [];
        var section = lines.Skip(start + 1).TakeWhile(l => !Heading().IsMatch(l));
        return Criteria(string.Join('\n', section));
    }
}
