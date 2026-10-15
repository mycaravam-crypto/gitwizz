using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gitwizz;

/// <summary>
/// A released tag or an unreleased release branch (local or remote-only), the releases it builds on and the commits it
/// adds to them. Commit SHAs are Git identities, not inferred change identities.
/// </summary>
public record VersionEntry(string Version, string Status, string Source, string Reference, string CommitSha,
    string CommittedAt, string? TaggedAt, List<VersionBase> Bases, List<VersionChange> Changes, bool Truncated,
    List<string> Warnings, List<VersionPr> PullRequests);

/// <summary>A release an entry builds on: its changes are the commits none of its bases contain.</summary>
public record VersionBase(string Version, string Reference, string Sha);

/// <summary>
/// One commit of a version. Category from its Conventional Commits type: feature (feat), fix, performance (perf), merge
/// (more than one parent) or other; Breaking from a "!" before the colon or a BREAKING CHANGE footer. PullRequest: the
/// pull request it came with ("#12"), null for a commit made or merged without one.
/// </summary>
public record VersionChange(string Sha, string Subject, string CommittedAt, string Kind, string Category, string? Type,
    string? Scope, string Description, bool Breaking, string? PullRequest = null);

/// <summary>
/// A pull request completed in a version, read from the history alone: a merge commit ("Merge pull request #12",
/// "Merged PR 12:") with the commits its merge brought in, or a squash commit ("title (#12)"). Category, type, scope
/// and description from its title; a title that isn't a Conventional Commits header takes the most significant category
/// of its commits (feature, fix, performance, other). Breaking when the title or any of its commits is.
/// </summary>
public record VersionPr(string Id, string Title, string Strategy, string MergeCommit, string CommittedAt, string Category,
    string? Type, string? Scope, string Description, bool Breaking, List<string> Commits);

/// <summary>The gitwizz.version-history/v2 document (docs/version-history/README.md).</summary>
public record VersionHistoryExport(string Schema, string? Repository, string BranchPattern, VersionTarget? Target, bool Shallow,
    List<VersionEntry> Versions, List<string> Warnings);

/// <summary>The branch a history is about (--target): its unreleased entry comes first, and only releases it contains are listed.</summary>
public record VersionTarget(string Name, string Reference, string Sha);

/// <summary>
/// Read-only local Git version discovery. Tags identify released snapshots; branches under a prefix (local, or only on a
/// remote) identify versions still in progress. No network access, no checkout and no fetch.
/// </summary>
public static class VersionHistory
{
    public const string Schema = "gitwizz.version-history/v2";
    public const int DefaultLimit = 200;
    public const int MaxLimit = History.MaxLimit;

    static readonly Regex TagPattern = new(@"^v?(?<major>0|[1-9]\d{0,8})\.(?<minor>0|[1-9]\d{0,8})\.(?<patch>0|[1-9]\d{0,8})(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex Conventional = new(@"^(?<type>[A-Za-z]+)(?:\((?<scope>[^()\r\n]+)\))?(?<bang>!)?:[ \t]+(?<desc>\S.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex BreakingFooter = new(@"^BREAKING[ -]CHANGE:[ \t]", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    static readonly Regex ListMarker = new(@"^(?:[-+]|\d+[.)])(?=\s|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly string[] None = [];

    const char Rec = '\x1e', Unit = '\x1f';

    sealed record Tag(string Name, string Sha, string? TaggedAt, int Major, int Minor, int Patch, string? Pre);

    sealed record Line(string Name, string Source, string Reference, string Sha);

    /// <summary>
    /// Unreleased lines first (branches under branchPrefix, newest version first; a local branch wins over the same
    /// branch on a remote), then released SemVer tags, highest version first. target (a branch, else any ref): its
    /// unreleased entry first (its commits since the releases it contains), then only the release branches cut from it
    /// and the tags it or those branches contain, so apps kept on separate branches never show up in each other's
    /// history. A release branch also builds on the release branch before it on its line (release/cxm/1.9.0 on
    /// release/cxm/1.8.0), so a repository without tags still gets one range per release. Each version groups its
    /// commits into the pull requests they came with. from (exclusive) and to (inclusive) limit the listed release
    /// branches and tags by version; the release at from is still the base of the one after it.
    /// Throws ArgumentException for a limit outside 1..MaxLimit, an unusable prefix or a to not above from,
    /// InvalidOperationException for an unknown target.
    /// </summary>
    public static VersionHistoryExport Export(Git git, int limit = DefaultLimit, string branchPrefix = "release/", string? target = null,
        string? from = null, string? to = null)
    {
        if (limit is < 1 or > MaxLimit) throw new ArgumentException($"--limit must be a number from 1 to {MaxLimit}");
        if (string.IsNullOrWhiteSpace(branchPrefix) || branchPrefix.StartsWith('-') || branchPrefix.Contains(' ') ||
            branchPrefix.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("invalid --branch-pattern: expected a literal prefix such as release/*");
        (from, to) = (from?.Trim().TrimStart('v'), to?.Trim().TrimStart('v'));
        if (from is "" || to is "") throw new ArgumentException("--from and --to need a version, such as 1.0.6");
        if (from != null && to != null && CompareNatural(Number(to), Number(from)) <= 0)
            throw new ArgumentException($"--to must be a higher version than --from: {from} -> {to}");
        bool InRange(string version) => (from == null || CompareNatural(Number(version), Number(from)) > 0) &&
                                        (to == null || CompareNatural(Number(version), Number(to)) <= 0);

        var warnings = new List<string>();
        var tags = ReadTags(git, warnings);
        var tagsBySha = tags.ToLookup(t => t.Sha, StringComparer.Ordinal);
        var scope = target == null ? null : ResolveTarget(git, target);
        // The target is listed once, as the target, even when it is a release branch itself.
        var lines = ReadLines(git, branchPrefix).Where(l => l.Reference != scope?.Reference).ToList();
        var others = scope == null ? [] : OtherBranches(git, branchPrefix, scope);
        var shallow = git.Try("rev-parse", "--is-shallow-repository").Stdout.Trim() == "true";

        var heads = tags.Select(t => t.Sha).Concat(lines.Select(l => l.Sha)).Concat(others)
            .Concat(scope == null ? Array.Empty<string>() : new[] { scope.Sha }).Distinct(StringComparer.Ordinal).ToList();
        var graph = heads.Count == 0 ? new Dictionary<string, string[]>() : ReadGraph(git, heads);
        var dates = heads.Count == 0 ? new Dictionary<string, string>() : CommitDates(git, heads);

        var entries = new List<VersionEntry>();
        if (scope != null)
        {
            // Only the release branches cut from the target, and the releases it or they contain; bases still see
            // every tag, so a release branch builds on its own.
            lines = [.. lines.Where(l => CutFrom(graph, l.Sha, scope.Sha, others))];
            var contained = Ancestors(graph, scope.Sha);
            foreach (var line in lines) contained.UnionWith(Ancestors(graph, line.Sha));
            tags = [.. tags.Where(t => contained.Contains(t.Sha))];
            entries.Add(Build(git, scope.Name, "unreleased", "target", scope.Reference, scope.Sha, dates[scope.Sha], null,
                TagBases(Bases(graph, scope.Sha, false, false, tagsBySha)), limit, shallow));
        }
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var version = line.Name[branchPrefix.Length..];
            if (!InRange(version)) continue;
            var tagBases = Bases(graph, line.Sha, false, false, tagsBySha);
            // The release branch before it on its line: the next older version in the same folder (cxm/ apart from bfm/).
            var previous = lines.Skip(i + 1).FirstOrDefault(l => Folder(l.Name[branchPrefix.Length..]) == Folder(version));
            var bases = TagBases(tagBases);
            if (previous != null)
            {
                // Tags the previous release branch contains are behind it: it stands for them.
                var behind = Ancestors(graph, previous.Sha);
                bases = [new VersionBase(previous.Name[branchPrefix.Length..], previous.Reference, previous.Sha),
                    .. TagBases(tagBases.Where(t => !behind.Contains(t.Sha)).ToList())];
            }
            entries.Add(Build(git, version, "unreleased", line.Source, line.Reference, line.Sha, dates[line.Sha], null, bases, limit, shallow));
        }
        foreach (var tag in tags.Where(t => InRange(t.Name.TrimStart('v'))))
            entries.Add(Build(git, tag.Name.TrimStart('v'), "released", "tag", "refs/tags/" + tag.Name, tag.Sha,
                dates[tag.Sha], tag.TaggedAt, TagBases(Bases(graph, tag.Sha, true, tag.Pre == null, tagsBySha)), limit, shallow));

        if (entries.Count == 0) warnings.Add("no SemVer tags or matching release branches found");
        if (shallow) warnings.Add("shallow clone: commits beyond the shallow boundary are missing; fetch more history for complete versions");
        return new VersionHistoryExport(Schema, History.RepositoryIdentity(git), branchPrefix + "*", scope, shallow, entries, warnings);
    }

    /// <summary>
    /// The target's ref: a local branch, else the branch on origin (a CI checkout may have no local one), else any ref or
    /// commit git resolves (HEAD, a tag). Throws InvalidOperationException ("unknown branch or ref") otherwise.
    /// </summary>
    static VersionTarget ResolveTarget(Git git, string target)
    {
        foreach (var reference in new[] { "refs/heads/" + target, "refs/remotes/origin/" + target })
            if (git.Try("rev-parse", "--verify", "--quiet", reference + "^{commit}") is { ExitCode: 0 } r)
                return new VersionTarget(target, reference, r.Stdout.Trim());
        return new VersionTarget(target, target, git.RevParse(target));
    }

    /// <summary>
    /// The heads of the branches a release branch may have been cut from instead of the target: local branches and
    /// origin's, outside the release prefix, other than the target.
    /// </summary>
    static List<string> OtherBranches(Git git, string prefix, VersionTarget target)
    {
        var heads = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (root, local) in new[] { ("refs/heads/", true), ("refs/remotes/origin/", false) })
            foreach (var (name, sha) in Refs(git, root))
                if (!name.StartsWith(prefix, StringComparison.Ordinal) && name != "HEAD" && root + name != target.Reference &&
                    !(local ? "refs/remotes/origin/" + name == target.Reference : "refs/heads/" + name == target.Reference))
                    heads.Add(sha);
        heads.Remove(target.Sha);
        return [.. heads];
    }

    /// <summary>
    /// Whether the release branch at release was cut from target: target's first-parent line meets the release's
    /// history, and no other branch's first-parent line meets it at a newer commit (a release of another app cut from
    /// its own branch meets that branch at the cut, and the target only where the apps split, if at all). Branches merged
    /// into the release (pull requests: in its history, off its first-parent line) or that start at its head (made from
    /// it) are not lines it was cut from.
    /// </summary>
    static bool CutFrom(Dictionary<string, string[]> graph, string release, string target, List<string> others)
    {
        var history = Ancestors(graph, release);
        var line = FirstParents(graph, release).ToHashSet(StringComparer.Ordinal);
        string? Meet(string head) => FirstParents(graph, head).FirstOrDefault(history.Contains);
        if (Meet(target) is not { } cut) return false;
        if (cut == release) return true;
        var older = Ancestors(graph, cut);
        foreach (var other in others)
            if ((!history.Contains(other) || line.Contains(other)) && Meet(other) is { } meet && meet != release && !older.Contains(meet))
                return false;
        return true;
    }

    /// <summary>head and its first parents, newest first.</summary>
    static IEnumerable<string> FirstParents(Dictionary<string, string[]> graph, string head)
    {
        for (var c = head; c != null; c = graph.GetValueOrDefault(c, None).FirstOrDefault()) yield return c;
    }

    /// <summary>Every commit sha contains, itself included, from the graph.</summary>
    static HashSet<string> Ancestors(Dictionary<string, string[]> graph, string sha)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(new[] { sha });
        while (stack.Count > 0)
        {
            var c = stack.Pop();
            if (!seen.Add(c)) continue;
            foreach (var p in graph.GetValueOrDefault(c, None)) stack.Push(p);
        }
        return seen;
    }

    /// <summary>
    /// SemVer tags that name a commit, highest version first; v1.0.0 and 1.0.0 are one version, reported by the first in
    /// that order (the v-prefixed one). Names come from lstrip, not :short, which writes tags/v1.0.0 when a branch has
    /// the same name. One cat-file for all of them; a tag of a tree or blob is no release and is skipped.
    /// </summary>
    static List<Tag> ReadTags(Git git, List<string> warnings)
    {
        var named = new List<(string Name, string? TaggedAt, Match Match)>();
        foreach (var row in git.Run("for-each-ref", "--format=%(refname:lstrip=2)%09%(taggerdate:iso-strict)", "refs/tags")
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var cols = row.Split('\t');
            var match = TagPattern.Match(cols[0]);
            if (match.Success)
                named.Add((cols[0], cols.Length > 1 && cols[1].Length > 0 ? History.IsoDate(cols[1]) : null, match));
        }
        if (named.Count == 0) return [];

        var peeled = Git.Exec(git.RepoDir, "git", ["cat-file", "--batch-check=%(objectname) %(objecttype)"],
            stdin: string.Concat(named.Select(n => $"refs/tags/{n.Name}^{{commit}}\n")));
        if (peeled.ExitCode != 0) throw new InvalidOperationException($"git cat-file failed: {peeled.Stderr.Trim()}");
        var objects = peeled.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var tags = new List<Tag>();
        for (var i = 0; i < named.Count && i < objects.Length; i++)
        {
            var o = objects[i].Split(' ');
            if (o.Length != 2 || o[1] != "commit") continue;
            var (name, taggedAt, m) = named[i];
            tags.Add(new Tag(name, o[0], taggedAt, int.Parse(m.Groups["major"].Value), int.Parse(m.Groups["minor"].Value),
                int.Parse(m.Groups["patch"].Value), m.Groups["pre"].Success ? m.Groups["pre"].Value : null));
        }
        tags.Sort((a, b) => CompareTags(b, a));
        return [.. tags.GroupBy(t => t.Name.TrimStart('v'), StringComparer.Ordinal).Select(g =>
        {
            foreach (var other in g.Skip(1)) warnings.Add($"tag {other.Name} skipped: {g.First().Name} is the same version");
            return g.First();
        })];
    }

    /// <summary>
    /// Branches under prefix, newest version first: local ones, then those only a remote has (origin first, then the
    /// other remotes by name; the first remote to have a branch stands for it).
    /// </summary>
    static List<Line> ReadLines(Git git, string prefix)
    {
        var lines = new Dictionary<string, Line>(StringComparer.Ordinal);
        foreach (var (name, sha) in Refs(git, "refs/heads/"))
            if (name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length)
                lines[name] = new Line(name, "branch", "refs/heads/" + name, sha);

        var remotes = git.Run("remote").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(r => r == "origin" ? 0 : 1).ThenBy(r => r, StringComparer.Ordinal);
        foreach (var remote in remotes)
            foreach (var (name, sha) in Refs(git, $"refs/remotes/{remote}/"))
                if (name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length && !lines.ContainsKey(name))
                    lines[name] = new Line(name, "remote-branch", $"refs/remotes/{remote}/{name}", sha);

        var result = lines.Values.ToList();
        result.Sort((a, b) => CompareNatural(b.Name[prefix.Length..], a.Name[prefix.Length..]));
        return result;
    }

    /// <summary>Ref names below prefix, without it, and the commit each points at.</summary>
    static IEnumerable<(string Name, string Sha)> Refs(Git git, string prefix) =>
        git.Run("for-each-ref", "--format=%(refname)%09%(objectname)", prefix).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(row => row.Split('\t'))
            .Where(c => c.Length == 2 && c[0].StartsWith(prefix, StringComparison.Ordinal))
            .Select(c => (c[0][prefix.Length..], c[1]));

    /// <summary>Every commit reachable from heads with its parents, from one rev-list. A shallow boundary has none.</summary>
    static Dictionary<string, string[]> ReadGraph(Git git, List<string> heads)
    {
        var r = Git.Exec(git.RepoDir, "git", ["rev-list", "--parents", "--stdin"], stdin: string.Concat(heads.Select(h => h + "\n")));
        if (r.ExitCode != 0) throw new InvalidOperationException($"git rev-list failed: {r.Stderr.Trim()}");
        var graph = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var row in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var ids = row.Split(' ');
            graph[ids[0]] = ids[1..];
        }
        return graph;
    }

    static Dictionary<string, string> CommitDates(Git git, List<string> shas)
    {
        var r = Git.Exec(git.RepoDir, "git", ["log", "--no-walk=unsorted", "--stdin", "--format=%H%x09%cI"],
            stdin: string.Concat(shas.Select(s => s + "\n")));
        if (r.ExitCode != 0) throw new InvalidOperationException($"git log failed: {r.Stderr.Trim()}");
        return r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(row => row.Split('\t'))
            .Where(c => c.Length == 2).ToDictionary(c => c[0], c => History.IsoDate(c[1]), StringComparer.Ordinal);
    }

    /// <summary>
    /// The releases sha builds on: the tagged commits it contains that no other tagged commit it contains contains, highest
    /// version first. Its changes are the commits none of them has, so a backport released in v1.0.1 and merged back is
    /// not listed again in v1.2.0. A tag skips its own commit (other tags there name the same snapshot); a release
    /// (no prerelease part) builds on releases only, so v2.0.0 lists everything since v1.x, not since v2.0.0-rc.1.
    /// A branch counts every tag, its own commit's too: a branch whose head is tagged has no unreleased changes.
    /// </summary>
    static List<Tag> Bases(Dictionary<string, string[]> graph, string sha, bool skipOwn, bool releasesOnly, ILookup<string, Tag> tagsBySha)
    {
        Tag? TagOf(string commit) => tagsBySha[commit].FirstOrDefault(t => !releasesOnly || t.Pre == null);

        // Walk back from sha, stopping at tagged commits: every base is met, plus tagged commits some base contains.
        var hits = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(skipOwn ? graph.GetValueOrDefault(sha, None) : new[] { sha });
        while (stack.Count > 0)
        {
            var c = stack.Pop();
            if (!seen.Add(c)) continue;
            if (TagOf(c) != null) { hits.Add(c); continue; }
            foreach (var p in graph.GetValueOrDefault(c, None)) stack.Push(p);
        }
        if (hits.Count > 1)
        {
            // Drop hits another hit contains: what their parents reach.
            var covered = new HashSet<string>(StringComparer.Ordinal);
            var walk = new Stack<string>(hits.SelectMany(h => graph.GetValueOrDefault(h, None)));
            while (walk.Count > 0)
            {
                var c = walk.Pop();
                if (!covered.Add(c)) continue;
                foreach (var p in graph.GetValueOrDefault(c, None)) walk.Push(p);
            }
            hits.ExceptWith(covered);
        }
        var bases = hits.Select(h => TagOf(h)!).ToList();
        bases.Sort((a, b) => CompareTags(b, a));
        return bases;
    }

    static List<VersionBase> TagBases(List<Tag> tags) => [.. tags.Select(t => new VersionBase(t.Name.TrimStart('v'), "refs/tags/" + t.Name, t.Sha))];

    /// <summary>The folder of a release branch's version ("cxm" for cxm/1.9.0, "" for 1.9.0): release lines are per folder.</summary>
    static string Folder(string version) => version.LastIndexOf('/') is var i and >= 0 ? version[..i] : "";

    /// <summary>The version number of a release branch's version (1.9.0 for cxm/1.9.0), what --from and --to compare.</summary>
    static string Number(string version) => version[(version.LastIndexOf('/') + 1)..];

    static VersionEntry Build(Git git, string version, string status, string source,
        string reference, string sha, string committedAt, string? taggedAt, List<VersionBase> bases, int limit, bool shallow)
    {
        // Newest first in topological order, at most limit (one more to know whether there are more).
        var range = Git.Exec(git.RepoDir, "git", ["rev-list", "--topo-order", $"--max-count={limit + 1}", "--stdin"],
            stdin: sha + "\n" + string.Concat(bases.Select(b => "^" + b.Sha + "\n")));
        if (range.ExitCode != 0) throw new InvalidOperationException($"git rev-list failed: {range.Stderr.Trim()}");
        var shas = range.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        var truncated = shas.Count > limit;
        if (truncated) shas = shas[..limit];

        var warnings = new List<string>();
        if (truncated) warnings.Add($"truncated: more than {limit} commits since its bases; raise --limit to list them all");
        // Without bases a version lists the history up to the limit: right for a first release, but in a shallow clone
        // the releases before it may only be missing.
        if (bases.Count == 0 && shallow)
            warnings.Add("no earlier release reachable in this shallow clone: the commits listed may include released ones");
        var (changes, prs) = PullRequests(ReadChanges(git, shas, out var commits), commits, warnings);
        return new VersionEntry(version, status, source, reference, sha, committedAt, taggedAt, bases, changes, truncated, warnings, prs);
    }

    /// <summary>
    /// The pull requests a version's commits came with, newest first, and the commits marked with theirs. Merges are
    /// read oldest first, so a pull request merged into another one's branch keeps its own commits. A merge's pull
    /// request owns what the merge brought in: the version's commits its second parent reaches and its first doesn't
    /// (a commit outside the version is in a base, and so is everything it reaches). A squash subject ("(#12)") counts
    /// outside every pull request's branch only: inside one it names an issue.
    /// </summary>
    static (List<VersionChange>, List<VersionPr>) PullRequests(List<VersionChange> changes, Dictionary<string, (string[] Parents, string Message)> commits,
        List<string> warnings)
    {
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        var found = new List<(string Id, VersionChange Commit, string Strategy, string Title, List<VersionChange> Members)>();
        var bySha = changes.ToDictionary(c => c.Sha, StringComparer.Ordinal);
        var order = changes.Select((c, i) => (c.Sha, i)).ToDictionary(x => x.Sha, x => x.i, StringComparer.Ordinal);
        HashSet<string> Reach(string from)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>(new[] { from });
            while (stack.Count > 0)
                if (stack.Pop() is var c && bySha.ContainsKey(c) && seen.Add(c))
                    foreach (var p in commits[c].Parents) stack.Push(p);
            return seen;
        }
        bool Claim(string id, VersionChange c, string strategy, string title, List<VersionChange> members)
        {
            if (found.FirstOrDefault(f => f.Id == id) is { Id: not null } seen)
            {
                warnings.Add($"{id} is named by more than one commit ({Short(seen.Commit.Sha)}, {Short(c.Sha)}); listed once, from {Short(seen.Commit.Sha)}");
                return false;
            }
            found.Add((id, c, strategy, title, members));
            foreach (var m in members.Prepend(c)) owner[m.Sha] = id;
            return true;
        }

        foreach (var c in Enumerable.Reverse(changes).Where(c => c.Kind == "merge"))
            if (commits[c.Sha].Parents is { Length: 2 } parents && History.PrMarker(c.Subject) is { Basis: "merge-message" } marker)
            {
                var brought = Reach(parents[1]);
                brought.ExceptWith(Reach(parents[0]));
                Claim(marker.Id, c, "merge", MergeTitle(c.Subject, marker.Marker, commits[c.Sha].Message),
                    [.. changes.Where(m => brought.Contains(m.Sha) && !owner.ContainsKey(m.Sha))]);
            }
        foreach (var c in changes.Where(c => c.Kind == "commit" && !owner.ContainsKey(c.Sha)))
            if (History.PrMarker(c.Subject) is { } marker)
                Claim(marker.Id, c, "squash", marker.Basis == "squash-subject" ? c.Subject[..^marker.Marker.Length].TrimEnd()
                    : c.Subject[marker.Marker.Length..].Trim(), []);

        var prs = found.Select(f =>
        {
            var commitsOf = f.Members.Prepend(f.Commit).Where(m => m.Kind == "commit").ToList();
            var title = Classify(f.Commit.Sha, false, f.Commit.CommittedAt, f.Title.Length > 0 ? f.Title : f.Commit.Subject);
            var category = title.Type != null ? title.Category
                : Sections.Select(s => s.Category).FirstOrDefault(cat => commitsOf.Any(m => m.Category == cat)) ?? "other";
            return (Order: order[f.Commit.Sha], Pr: new VersionPr(f.Id, title.Subject, f.Strategy, f.Commit.Sha, f.Commit.CommittedAt,
                category, title.Type, title.Scope, title.Description, title.Breaking || commitsOf.Any(m => m.Breaking),
                [.. f.Members.Prepend(f.Commit).OrderBy(m => order[m.Sha]).Select(m => m.Sha)]));
        }).OrderBy(x => x.Order).Select(x => x.Pr).ToList();
        return ([.. changes.Select(c => owner.TryGetValue(c.Sha, out var id) ? c with { PullRequest = id } : c)], prs);
    }

    /// <summary>A merge's pull request title: GitHub's is the message's first line after the subject, Azure DevOps' follows "Merged PR 12:".</summary>
    static string MergeTitle(string subject, string marker, string message)
    {
        if (subject.StartsWith("Merged PR ", StringComparison.Ordinal)) return subject[marker.Length..].Trim();
        return message.Split('\n').Skip(1).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
    }

    static List<VersionChange> ReadChanges(Git git, List<string> shas, out Dictionary<string, (string[] Parents, string Message)> commits)
    {
        commits = new Dictionary<string, (string[], string)>(StringComparer.Ordinal);
        if (shas.Count == 0) return [];
        var r = Git.Exec(git.RepoDir, "git", ["log", "--no-walk=unsorted", "--stdin", $"--format={Rec}%H{Unit}%P{Unit}%cI{Unit}%B"],
            stdin: string.Concat(shas.Select(s => s + "\n")));
        if (r.ExitCode != 0) throw new InvalidOperationException($"git log failed: {r.Stderr.Trim()}");
        var bySha = new Dictionary<string, VersionChange>(StringComparer.Ordinal);
        foreach (var record in r.Stdout.Split(Rec, StringSplitOptions.RemoveEmptyEntries))
        {
            var f = record.Split(Unit, 4);
            if (f.Length < 4) continue;
            var parents = f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            commits[f[0]] = (parents, f[3].Trim());
            bySha[f[0]] = Classify(f[0], parents.Length > 1, History.IsoDate(f[2]), f[3].Trim());
        }
        return [.. shas.Where(bySha.ContainsKey).Select(s => bySha[s])];
    }

    /// <summary>A commit by its Conventional Commits header (type(scope)!: description) and BREAKING CHANGE footer.</summary>
    public static VersionChange Classify(string sha, bool merge, string committedAt, string message)
    {
        var subject = message.Split('\n')[0].Trim();
        if (merge) return new VersionChange(sha, subject, committedAt, "merge", "merge", null, null, subject, false);
        var m = Conventional.Match(subject);
        if (!m.Success) return new VersionChange(sha, subject, committedAt, "commit", "other", null, null, subject, BreakingFooter.IsMatch(message));
        var type = m.Groups["type"].Value.ToLowerInvariant();
        var category = type switch { "feat" => "feature", "fix" => "fix", "perf" => "performance", _ => "other" };
        return new VersionChange(sha, subject, committedAt, "commit", category, type,
            m.Groups["scope"].Success ? m.Groups["scope"].Value.Trim() : null, m.Groups["desc"].Value.Trim(),
            m.Groups["bang"].Success || BreakingFooter.IsMatch(message));
    }

    /// <summary>SemVer precedence; equal precedence (v1.0.0-rc.1 and 1.0.0-rc.1) is decided by the name.</summary>
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
        return v != 0 ? v : StringComparer.Ordinal.Compare(a.Name, b.Name);
    }

    /// <summary>Dot-separated parts, numbers by value: 1.10 after 1.9. Equal parts: the name decides.</summary>
    static int CompareNatural(string a, string b)
    {
        var aa = a.Split('.'); var bb = b.Split('.');
        for (int i = 0; i < Math.Min(aa.Length, bb.Length); i++)
        {
            bool an = long.TryParse(aa[i], out var ai), bn = long.TryParse(bb[i], out var bi);
            var v = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : StringComparer.Ordinal.Compare(aa[i], bb[i]);
            if (v != 0) return v;
        }
        var n = aa.Length.CompareTo(bb.Length);
        return n != 0 ? n : StringComparer.Ordinal.Compare(a, b);
    }

    /// <summary>The document as JSON with history export's options: indented, camelCase, nulls written, "+" kept.</summary>
    public static string Json(VersionHistoryExport result) => JsonSerializer.Serialize(result, History.JsonOptions);

    /// <summary>A terminal listing: per version its reference, bases, pull requests and the commits that came without one.</summary>
    public static string Text(VersionHistoryExport result)
    {
        var lines = new List<string>();
        foreach (var v in result.Versions)
        {
            lines.Add($"{v.Version} [{v.Status}] {v.Reference} ({Short(v.CommitSha)})");
            lines.Add($"  since: {(v.Bases.Count == 0 ? "(initial)" : string.Join(", ", v.Bases.Select(b => b.Version)))} | " +
                      $"pull requests: {v.PullRequests.Count} | commits: {v.Changes.Count}" + (v.Truncated ? " (truncated)" : ""));
            foreach (var pr in v.PullRequests) lines.Add($"    {pr.Id} {pr.Title}");
            foreach (var c in v.Changes.Where(c => c.PullRequest == null)) lines.Add($"    {Short(c.Sha)} {c.Subject}");
            foreach (var warning in v.Warnings) lines.Add($"  warning: {warning}");
        }
        foreach (var warning in result.Warnings) lines.Add("warning: " + warning);
        return string.Join("\n", lines) + "\n";
    }

    static readonly (string Category, string Heading)[] Sections =
        [("feature", "Features"), ("fix", "Fixes"), ("performance", "Performance"), ("other", "Other changes")];

    /// <summary>
    /// A changelog page: per version a heading, its reference and bases, then its pull requests, and the commits that
    /// came without one, grouped as breaking changes, features, fixes, performance and other changes. Other merge
    /// commits are counted, not listed. Pull requests and commits link to the repository when its URL is http(s).
    /// Deterministic: no generation time, so the page only changes with the history.
    /// </summary>
    public static string Markdown(VersionHistoryExport result)
    {
        var web = WebUrl(result.Repository);
        string Commit(string sha) => web == null ? $"`{Short(sha)}`" : $"[`{Short(sha)}`]({web}/commit/{sha})";
        var azure = web != null && (web.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) ||
                                    web.Contains(".visualstudio.com", StringComparison.OrdinalIgnoreCase));
        string Pr(string id) => web == null ? id : $"[{id}]({web}/{(azure ? "pullrequest" : "pull")}/{id[1..]})";

        var md = new StringBuilder();
        md.Append("# Version history\n\n");
        if (result.Target != null)
            md.Append($"The history of `{result.Target.Name}`: its unreleased changes first, then the releases `{result.Target.Name}` " +
                      "contains, newest first. ");
        else
            md.Append($"Versions still in progress (branches matching `{result.BranchPattern}`) come first, then released SemVer tags, " +
                      "newest first. ");
        md.Append("Each version lists the pull requests, and the commits made without one, that none of the releases it builds on contain. " +
                  "Generated by `gitwizz version-history`; edits are overwritten.\n");
        if (result.Versions.Count == 0) md.Append("\n_No versions found._\n");

        foreach (var v in result.Versions)
        {
            var date = v.TaggedAt ?? v.CommittedAt;
            md.Append('\n').Append(v.Status == "released"
                ? $"## {v.Version} ({date[..Math.Min(10, date.Length)]})\n\n"
                : $"## {v.Version} (unreleased)\n\n");

            var name = v.Reference.Split('/', 3)[^1];
            var origin = v.Source switch { "tag" => "Tag", "remote-branch" => "Remote branch", _ => "Branch" };
            var direct = v.Changes.Where(c => c.PullRequest == null && c.Kind != "merge").ToList();
            var merges = v.Changes.Count(c => c.PullRequest == null && c.Kind == "merge");
            var counts = new[] { (v.PullRequests.Count, "pull request"), (direct.Count, "direct commit"), (merges, "merge commit") }
                .Where(x => x.Item1 > 0).Select(x => Plural(x.Item1, x.Item2)).ToList();
            md.Append($"{origin} `{name}` at {Commit(v.CommitSha)}")
              .Append(v.Bases.Count == 0 ? " · first release" : " · since " + string.Join(", ", v.Bases.Select(b => b.Version)))
              .Append(" · ").Append(counts.Count == 0 ? "no changes" : counts.Count == 1 ? counts[0] : string.Join(", ", counts[..^1]) + " and " + counts[^1])
              .Append("\n");
            foreach (var warning in v.Warnings) md.Append($"\n> ⚠ {Escape(warning)}\n");

            if (v.PullRequests.Count == 0 && direct.Count == 0)
            {
                md.Append("\n_No changes._\n");
                continue;
            }
            Group("⚠ Breaking changes", v.PullRequests.Where(p => p.Breaking), direct.Where(c => c.Breaking));
            foreach (var (category, heading) in Sections)
                Group(heading, v.PullRequests.Where(p => !p.Breaking && p.Category == category), direct.Where(c => !c.Breaking && c.Category == category));
        }
        if (result.Warnings.Count > 0)
        {
            md.Append("\n## Warnings\n\n");
            foreach (var warning in result.Warnings) md.Append($"- {Escape(warning)}\n");
        }
        return md.ToString();

        // The section names features, fixes and performance; other changes keep their type (chore:, docs:, ...).
        string Line(string category, string? scope, string description, string subject) =>
            category == "other" ? Escape(subject) : (scope != null ? $"**{Escape(scope)}:** " : "") + Escape(description);

        void Group(string heading, IEnumerable<VersionPr> prs, IEnumerable<VersionChange> commits)
        {
            var (p, c) = (prs.ToList(), commits.ToList());
            if (p.Count == 0 && c.Count == 0) return;
            md.Append($"\n### {heading}\n\n");
            foreach (var pr in p)
            {
                var own = pr.Commits.Count - (pr.Strategy == "merge" ? 1 : 0);
                md.Append("- ").Append(Line(pr.Category, pr.Scope, pr.Description, pr.Title))
                  .Append($" ({Pr(pr.Id)}{(pr.Strategy == "merge" ? $", {Plural(own, "commit")}" : "")})\n");
            }
            foreach (var change in c)
                md.Append("- ").Append(Line(change.Category, change.Scope, change.Description, change.Subject)).Append($" ({Commit(change.Sha)})\n");
        }
    }

    /// <summary>The repository's web address for commit links: an http(s) URL without .git, else null.</summary>
    static string? WebUrl(string? repository)
    {
        if (repository == null || !(repository.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                                    repository.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            return null;
        var url = repository.TrimEnd('/');
        return url.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? url[..^4] : url;
    }

    /// <summary>Commit text as literal Markdown: no emphasis, links, HTML, tables or headings from a commit message.</summary>
    static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '|' or '#' or '~' or '!') sb.Append('\\');
            sb.Append(ch);
        }
        // A leading "-", "+" or "1." would open a nested list inside the item: escape its last character.
        if (ListMarker.Match(sb.ToString()) is { Success: true } m) sb.Insert(m.Length - 1, '\\');
        return sb.ToString();
    }

    static string Short(string sha) => sha[..Math.Min(8, sha.Length)];

    static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";
}
