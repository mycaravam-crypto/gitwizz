using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Gitwizz;

/// <summary>A release tag or an active release branch. Commit SHAs are Git identities, not inferred change identities.</summary>
public record VersionEntry(string Version, string Status, string Source, string Reference, string CommitSha,
    string CommittedAt, string? BaseVersion, string? BaseSha, List<VersionChange> Changes,
    bool Truncated, bool Shallow, List<string> Warnings);

public record VersionChange(string Sha, string Subject, string CommittedAt, string Kind);

public record VersionHistoryExport(string Schema, string? Repository, List<VersionEntry> Versions, List<string> Warnings);

/// <summary>
/// Read-only local Git version discovery. Tags identify released snapshots; release/* branches identify moving lines.
/// No network access and no checkout. The nearest reachable prior SemVer tag defines each change range.
/// </summary>
public static class VersionHistory
{
    public const string Schema = "gitwizz.version-history/v1";
    public const int DefaultLimit = 200;
    public const int MaxLimit = History.MaxLimit;
    static readonly Regex TagPattern = new(@"^v?(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    readonly record struct Tag(string Name, string Sha, int Major, int Minor, int Patch, string? Pre);

    public static VersionHistoryExport Export(Git git, int limit = DefaultLimit, string branchPrefix = "release/")
    {
        if (limit is < 1 or > MaxLimit) throw new ArgumentException($"--limit must be a number from 1 to {MaxLimit}");
        if (string.IsNullOrWhiteSpace(branchPrefix) || branchPrefix.StartsWith('-') || branchPrefix.Contains(' ') ||
            branchPrefix.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("invalid --branch-pattern: expected a literal prefix such as release/*");

        var tags = git.Run("for-each-ref", "--format=%(refname:short)", "refs/tags")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(name => (name, match: TagPattern.Match(name)))
            .Where(t => t.match.Success)
            .Select(t => new Tag(t.name, git.RevParse("refs/tags/" + t.name),
                int.Parse(t.match.Groups["major"].Value), int.Parse(t.match.Groups["minor"].Value),
                int.Parse(t.match.Groups["patch"].Value),
                t.match.Groups["pre"].Success ? t.match.Groups["pre"].Value : null))
            .OrderByDescending(t => t, Comparer<Tag>.Create(CompareTags)).ToList();

        var branches = git.Run("for-each-ref", "--format=%(refname:short)", "refs/heads")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(name => name.StartsWith(branchPrefix, StringComparison.Ordinal) && name.Length > branchPrefix.Length)
            .Order(StringComparer.Ordinal).ToList();

        var entries = new List<VersionEntry>();
        foreach (var tag in tags)
            entries.Add(Build(git, tag.Name.TrimStart('v'), "released", "tag", "refs/tags/" + tag.Name,
                tag.Sha, tags.Where(t => t.Name != tag.Name && git.IsAncestor(t.Sha, tag.Sha)).ToList(), limit));
        foreach (var branch in branches)
        {
            var sha = git.RevParse("refs/heads/" + branch);
            entries.Add(Build(git, branch[branchPrefix.Length..], "unreleased", "branch",
                "refs/heads/" + branch, sha, tags.Where(t => git.IsAncestor(t.Sha, sha)).ToList(), limit));
        }

        var warnings = new List<string>();
        if (entries.Count == 0) warnings.Add("no SemVer tags or matching local release branches found");
        return new VersionHistoryExport(Schema, History.RepositoryIdentity(git), entries, warnings);
    }

    static VersionEntry Build(Git git, string version, string status, string source, string reference, string sha,
        List<Tag> reachable, int limit)
    {
        // Prefer the nearest ancestor, not simply the highest version number: maintenance lines may diverge.
        // Git's topo-order yields the closest reachable tag commit in this history.
        var ancestorOrder = git.Run("rev-list", "--topo-order", sha)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var distance = ancestorOrder.Select((commit, i) => (commit, i))
            .GroupBy(x => x.commit, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().i, StringComparer.Ordinal);
        var baseline = reachable.OrderBy(t => distance.GetValueOrDefault(t.Sha, int.MaxValue))
            .ThenByDescending(t => t, Comparer<Tag>.Create(CompareTags)).FirstOrDefault();
        var baseSha = baseline?.Sha;
        var history = History.ReadGit(git, baseSha, sha, limit);
        var warnings = new List<string>();
        if (history.Truncated) warnings.Add($"commit range truncated at {limit} commits");
        if (history.Shallow) warnings.Add("shallow clone: version history may be incomplete");
        if (baseSha is null) warnings.Add("no reachable earlier release tag; showing repository history up to the limit");
        var changes = history.Commits.Select(c =>
            new VersionChange(c.Sha, c.Subject, c.CommittedAt, c.Kind)).ToList();
        var date = git.Run("show", "-s", "--format=%cI", sha).Trim();
        return new VersionEntry(version, status, source, reference, sha, date,
            baseline?.Name.TrimStart('v'), baseSha, changes, history.Truncated, history.Shallow, warnings);
    }

    static int CompareTags(Tag a, Tag b)
    {
        int v = a.Major.CompareTo(b.Major);
        if (v != 0) return v;
        v = a.Minor.CompareTo(b.Minor);
        if (v != 0) return v;
        v = a.Patch.CompareTo(b.Patch);
        if (v != 0) return v;
        if (a.Pre == null) return b.Pre == null ? StringComparer.Ordinal.Compare(a.Name, b.Name) : 1;
        if (b.Pre == null) return -1;
        var aa = a.Pre.Split('.'); var bb = b.Pre.Split('.');
        for (int i = 0; i < Math.Min(aa.Length, bb.Length); i++)
        {
            bool an = long.TryParse(aa[i], out var ai), bn = long.TryParse(bb[i], out var bi);
            v = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : StringComparer.Ordinal.Compare(aa[i], bb[i]);
            if (v != 0) return v;
        }
        return aa.Length.CompareTo(bb.Length);
    }

    public static string Json(VersionHistoryExport result) =>
        JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        });

    public static string Text(VersionHistoryExport result)
    {
        var lines = new List<string>();
        foreach (var v in result.Versions)
        {
            lines.Add($"{v.Version} [{v.Status}] {v.Reference} ({v.CommitSha[..Math.Min(8, v.CommitSha.Length)]})");
            lines.Add($"  since: {v.BaseVersion ?? "(initial)"} | commits: {v.Changes.Count}" +
                (v.Truncated ? " (truncated)" : ""));
            foreach (var c in v.Changes) lines.Add($"    {c.Sha[..Math.Min(8, c.Sha.Length)]} {c.Subject}");
            foreach (var warning in v.Warnings) lines.Add($"  warning: {warning}");
        }
        foreach (var warning in result.Warnings) lines.Add("warning: " + warning);
        return string.Join("\n", lines) + "\n";
    }
}
