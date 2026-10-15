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

    sealed record Tag(string Name, string Sha, int Major, int Minor, int Patch, string? Pre);

    /// <summary>
    /// Released SemVer tags (highest version first), then local branches under branchPrefix (ordinal order), each with the
    /// commits since its nearest reachable earlier tag. Reads local refs only; throws ArgumentException for a limit
    /// outside 1..MaxLimit or an unusable prefix.
    /// </summary>
    public static VersionHistoryExport Export(Git git, int limit = DefaultLimit, string branchPrefix = "release/")
    {
        if (limit is < 1 or > MaxLimit) throw new ArgumentException($"--limit must be a number from 1 to {MaxLimit}");
        if (string.IsNullOrWhiteSpace(branchPrefix) || branchPrefix.StartsWith('-') || branchPrefix.Contains(' ') ||
            branchPrefix.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("invalid --branch-pattern: expected a literal prefix such as release/*");

        var tags = new List<Tag>();
        foreach (var name in Refs(git, "refs/tags"))
        {
            var match = TagPattern.Match(name);
            // A tag of a tree or blob names no commit, so no release.
            if (!match.Success || git.Try("rev-parse", "--verify", "--quiet", $"refs/tags/{name}^{{commit}}") is not { ExitCode: 0 } r)
                continue;
            tags.Add(new Tag(name, r.Stdout.Trim(), int.Parse(match.Groups["major"].Value), int.Parse(match.Groups["minor"].Value),
                int.Parse(match.Groups["patch"].Value), match.Groups["pre"].Success ? match.Groups["pre"].Value : null));
        }
        tags.Sort((a, b) => CompareTags(b, a));
        // v1.0.0 and 1.0.0 are one version: the first in order (the v-prefixed one) stands for it.
        var warnings = new List<string>();
        tags = [.. tags.GroupBy(t => t.Name.TrimStart('v'), StringComparer.Ordinal).Select(g =>
        {
            foreach (var other in g.Skip(1)) warnings.Add($"tag {other.Name} skipped: {g.First().Name} is the same version");
            return g.First();
        })];
        var tagsBySha = tags.ToLookup(t => t.Sha, StringComparer.Ordinal);

        var branches = Refs(git, "refs/heads")
            .Where(name => name.StartsWith(branchPrefix, StringComparison.Ordinal) && name.Length > branchPrefix.Length)
            .Order(StringComparer.Ordinal).ToList();

        var entries = new List<VersionEntry>();
        foreach (var tag in tags)
            entries.Add(Build(git, tag.Name.TrimStart('v'), "released", "tag", "refs/tags/" + tag.Name, tag.Sha, tagsBySha, limit));
        foreach (var branch in branches)
            entries.Add(Build(git, branch[branchPrefix.Length..], "unreleased", "branch", "refs/heads/" + branch,
                git.RevParse("refs/heads/" + branch), tagsBySha, limit));

        if (entries.Count == 0) warnings.Add("no SemVer tags or matching local release branches found");
        return new VersionHistoryExport(Schema, History.RepositoryIdentity(git), entries, warnings);
    }

    /// <summary>
    /// Ref names below prefix, without it. lstrip, not :short: a tag and a branch of the same name would otherwise come
    /// back as tags/... and heads/..., and the tag would be lost.
    /// </summary>
    static IEnumerable<string> Refs(Git git, string prefix) =>
        git.Run("for-each-ref", "--format=%(refname:lstrip=2)", prefix).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    static VersionEntry Build(Git git, string version, string status, string source, string reference, string sha,
        ILookup<string, Tag> tagsBySha, int limit)
    {
        var baseline = Baseline(git, sha, source == "tag", tagsBySha);
        var baseSha = baseline?.Sha;
        var history = History.ReadGit(git, baseSha, sha, limit);
        var warnings = new List<string>();
        if (history.Truncated) warnings.Add($"commit range truncated at {limit} commits");
        if (history.Shallow) warnings.Add("shallow clone: version history may be incomplete");
        if (baseSha is null) warnings.Add("no reachable earlier release tag; showing repository history up to the limit");
        var changes = history.Commits.Select(c =>
            new VersionChange(c.Sha, c.Subject, c.CommittedAt, c.Kind)).ToList();
        var date = History.IsoDate(git.Run("show", "-s", "--format=%cI", sha).Trim());
        return new VersionEntry(version, status, source, reference, sha, date,
            baseline?.Name.TrimStart('v'), baseSha, changes, history.Truncated, history.Shallow, warnings);
    }

    /// <summary>
    /// The release sha's changes are counted from: of the tagged commits it contains, those no other one contains, the
    /// one with the fewest commits up to sha (git describe's rule), the higher version on a tie. Not the highest version,
    /// since maintenance lines diverge, and not the first tag in topological order, which after a maintenance line is
    /// merged back is one of its tags. A tag skips its own commit (other tags there name the same snapshot); a branch
    /// does not, so a branch whose head is tagged has no unreleased changes.
    /// </summary>
    static Tag? Baseline(Git git, string sha, bool skipOwn, ILookup<string, Tag> tagsBySha)
    {
        var tagged = git.Run("rev-list", sha).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(c => tagsBySha.Contains(c) && !(skipOwn && c == sha)).ToHashSet(StringComparer.Ordinal);
        if (tagged.Count > 1)
        {
            // Everything reachable from the candidates' parents is contained in another candidate. Through stdin, as the
            // candidates may outgrow a command line.
            var covered = Git.Exec(git.RepoDir, "git", ["rev-list", "--stdin"], stdin: string.Concat(tagged.Select(c => c + "^@\n")));
            if (covered.ExitCode != 0) throw new InvalidOperationException($"git rev-list failed: {covered.Stderr.Trim()}");
            tagged.ExceptWith(covered.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
        return tagged
            .Select(c => (Tag: tagsBySha[c].First(), Distance: int.Parse(git.Run("rev-list", "--count", $"{c}..{sha}"))))
            .OrderBy(x => x.Distance).ThenByDescending(x => x.Tag, Comparer<Tag>.Create(CompareTags))
            .Select(x => x.Tag).FirstOrDefault();
    }

    static int CompareTags(Tag a, Tag b)
    {
        int v = a.Major.CompareTo(b.Major);
        if (v != 0) return v;
        v = a.Minor.CompareTo(b.Minor);
        if (v != 0) return v;
        v = a.Patch.CompareTo(b.Patch);
        if (v != 0) return v;
        if (a.Pre == null || b.Pre == null)
            return a.Pre == b.Pre ? StringComparer.Ordinal.Compare(a.Name, b.Name) : a.Pre == null ? 1 : -1;
        var aa = a.Pre.Split('.'); var bb = b.Pre.Split('.');
        for (int i = 0; i < Math.Min(aa.Length, bb.Length); i++)
        {
            bool an = long.TryParse(aa[i], out var ai), bn = long.TryParse(bb[i], out var bi);
            v = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : StringComparer.Ordinal.Compare(aa[i], bb[i]);
            if (v != 0) return v;
        }
        v = aa.Length.CompareTo(bb.Length);
        // Equal precedence (v1.0.0-rc.1 and 1.0.0-rc.1): the name decides, so the order never depends on the input.
        return v != 0 ? v : StringComparer.Ordinal.Compare(a.Name, b.Name);
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
