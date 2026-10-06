using System.Text.RegularExpressions;

namespace PrOptimizer;

public static class FileClasses
{
    static readonly string[] Lockfiles =
        ["package-lock.json", "pnpm-lock.yaml", "yarn.lock", "packages.lock.json", "Cargo.lock", "go.sum", "poetry.lock", "Gemfile.lock", "composer.lock"];
    static readonly string[] BinaryExt = [".png", ".jpg", ".jpeg", ".gif", ".ico", ".pdf", ".zip", ".dll", ".exe", ".so", ".woff", ".woff2"];
    static readonly string[] ConfigExt = [".json", ".yaml", ".yml", ".toml", ".ini", ".config", ".props", ".targets", ".env"];

    public static FileClass Classify(string path)
    {
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (Lockfiles.Contains(name)) return FileClass.Lockfile;
        if (name.EndsWith(".Designer.cs") || name.EndsWith(".g.cs") || name.EndsWith(".generated.cs") || path.Contains("/generated/"))
            return FileClass.Generated;
        if (Regex.IsMatch(path, @"(^|/)migrations?/", RegexOptions.IgnoreCase)) return FileClass.Migration;
        if (name == ".gitmodules") return FileClass.Submodule;
        if (BinaryExt.Contains(ext)) return FileClass.Binary;
        if (ConfigExt.Contains(ext)) return FileClass.Configuration;
        return FileClass.Normal;
    }

    public static bool IsRegenerable(FileClass c) => c is FileClass.Lockfile or FileClass.Generated;
}

/// <summary>
/// Optional repository rules from .gitwizz.yml at the repository root. Without the file (or for keys it leaves out)
/// the built-in behaviour applies, so Default reproduces the tool without configuration.
/// </summary>
public record RepoConfig
{
    public const string FileName = ".gitwizz.yml";
    public static readonly RepoConfig Default = new();

    public List<Regenerator> Regenerators { get; init; } = [];   // regenerable files and the command that rebuilds them
    public List<string> Generated { get; init; } = [];           // more generated (regenerable) files
    public List<string> Ignored { get; init; } = [];             // left out of overlap scoring; real conflicts still block
    public CostModel Costs { get; init; } = new();

    public record Regenerator { public string Match { get; init; } = ""; public string Command { get; init; } = ""; }
    public record CostModel { public double Regeneration { get; init; } = 0.5; public double Conflict { get; init; } = 1.0; public double DependencyUnblock { get; init; } = 0.1; }

    public static RepoConfig Load(Git git)
    {
        var path = Path.Combine(git.Run("rev-parse", "--show-toplevel"), FileName);
        if (!File.Exists(path)) return Default;
        RepoConfig c;
        try
        {
            c = new YamlDotNet.Serialization.DeserializerBuilder()
                .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
                .Build().Deserialize<RepoConfig?>(File.ReadAllText(path)) ?? Default;
        }
        catch (YamlDotNet.Core.YamlException e) { throw new InvalidOperationException($"invalid {FileName}: {e.Message} {e.InnerException?.Message}".Trim()); }
        var error = c.Regenerators.Any(r => r is null || r.Match.Trim() == "" || r.Command.Trim() == "") ? "every regenerator needs a match and a command"
            : c.Generated.Concat(c.Ignored).Any(g => string.IsNullOrWhiteSpace(g)) ? "empty pattern in generated/ignored"
            : c.Costs is null || new[] { c.Costs.Regeneration, c.Costs.Conflict, c.Costs.DependencyUnblock }.Any(x => x < 0 || double.IsNaN(x)) ? "costs must be non-negative numbers"
            : null;
        return error == null ? c : throw new InvalidOperationException($"invalid {FileName}: {error}");
    }

    /// <summary>Glob with * and ?; matched against the file name, or the whole path when the pattern has a '/'.</summary>
    static bool Matches(string glob, string path) =>
        System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(glob, glob.Contains('/') ? path : Path.GetFileName(path), ignoreCase: false);

    public FileClass Classify(string path) =>
        Regenerators.Any(r => Matches(r.Match, path)) ? FileClass.Lockfile
        : Generated.Any(g => Matches(g, path)) ? FileClass.Generated
        : FileClasses.Classify(path);

    public bool IsRegenerable(string path) => FileClasses.IsRegenerable(Classify(path));
    public bool IsIgnored(string path) => Ignored.Any(g => Matches(g, path));
    public string? RegenerateCommand(string path) => Regenerators.FirstOrDefault(r => Matches(r.Match, path))?.Command;
}

public static partial class Analyzer
{
    /// <summary>Fills BaseSha, Files, Hunks and Members relative to the merge-base with the target.</summary>
    public static void Analyze(Git git, string target, PullRequest pr)
    {
        pr.BaseSha = git.MergeBase(target, pr.HeadSha);
        // Pin output format against user config (noprefix, external diff, quoted paths).
        string[] diff = ["-c", "core.quotePath=false", "diff", "-M", "--no-ext-diff", "--no-color", "--src-prefix=a/", "--dst-prefix=b/"];
        pr.Files = ParseNameStatus(git.Run([.. diff, "--name-status", pr.BaseSha, pr.HeadSha]));
        var patch = git.Run([.. diff, "-U0", pr.BaseSha, pr.HeadSha]);
        pr.Hunks = ParseHunks(patch);
        var added = ParseHunks(patch, newSide: true);

        List<(string, int)> before = [], after = [];
        foreach (var f in pr.Files.Where(f => Structure.Supports(f.Path)))
        {
            var oldPath = f.OldPath ?? f.Path;
            var oldSrc = f.Kind == ChangeKind.Added ? "" : git.Run("show", $"{pr.BaseSha}:{oldPath}");
            var newSrc = f.Kind == ChangeKind.Deleted ? "" : git.Run("show", $"{pr.HeadSha}:{f.Path}");
            pr.Members.UnionWith(Structure.TouchedMembers(oldSrc, pr.Hunks.Where(h => h.Path == oldPath && h.Start > 0)));
            before.AddRange(Structure.Declarations(oldSrc));
            after.AddRange(Structure.Declarations(newSrc));
            pr.Uses.UnionWith(Structure.Uses(newSrc, added.Where(h => h.Path == f.Path)));
        }
        var (b, a) = (before.ToLookup(d => d.Item1, d => d.Item2), after.ToLookup(d => d.Item1, d => d.Item2));
        pr.Api = b.Select(g => g.Key).Union(a.Select(g => g.Key))
            .Select(n => (n, B: b[n].ToHashSet(), A: a[n].ToHashSet())).Where(x => !x.B.SetEquals(x.A))
            .ToDictionary(x => x.n, x => (x.B, x.A));
    }

    public static List<FileChange> ParseNameStatus(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            var p = line.Split('\t');
            return p[0][0] switch
            {
                'A' => new FileChange(p[1], ChangeKind.Added),
                'D' => new FileChange(p[1], ChangeKind.Deleted),
                'R' => new FileChange(p[2], ChangeKind.Renamed, p[1]),
                'C' => new FileChange(p[2], ChangeKind.Added),
                _ => new FileChange(p[1], ChangeKind.Modified),
            };
        }).ToList();

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))?")]
    private static partial Regex HunkHeader();

    /// <summary>
    /// Hunks on the old (merge-base) side, so hunks of different PRs with the same base are comparable.
    /// newSide: the added lines instead, on the PR's version of each file.
    /// </summary>
    public static List<Hunk> ParseHunks(string diff, bool newSide = false)
    {
        var hunks = new List<Hunk>();
        string? oldPath = null, newPath = null;
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("--- ")) oldPath = line == "--- /dev/null" ? null : line[6..];
            else if (line.StartsWith("+++ ")) newPath = line == "+++ /dev/null" ? null : line[6..];
            else if (HunkHeader().Match(line) is { Success: true } m && (!newSide || newPath != null))
            {
                var g = newSide ? 3 : 1;
                var count = m.Groups[g + 1].Success ? int.Parse(m.Groups[g + 1].Value) : 1;
                hunks.Add(new Hunk(newSide ? newPath! : (oldPath ?? newPath)!, int.Parse(m.Groups[g].Value), count));
            }
        }
        return hunks;
    }

    static IEnumerable<string> Paths(PullRequest pr) =>
        pr.Files.SelectMany(f => f.OldPath is null ? [f.Path] : new[] { f.Path, f.OldPath }).Distinct();

    /// <summary>
    /// Pairwise conflict risk in [0,1]. Heuristic; the simulation is the source of truth.
    /// history: per-file conflict rate from past merges (see ConflictHistory); hot files weigh up to 3x.
    /// </summary>
    // ponytail: hunk comparison assumes both PRs share a merge-base; diverging bases make it approximate.
    public static double ConflictWeight(PullRequest a, PullRequest b, out List<string> sharedFiles,
        IReadOnlyDictionary<string, double>? history = null, RepoConfig? config = null)
    {
        config ??= RepoConfig.Default;
        sharedFiles = Paths(a).Intersect(Paths(b)).Where(f => !config.IsIgnored(f)).ToList();
        // Level 4: API changes the other PR's new code relies on, and migrations added side by side.
        double w = 0.3 * SemanticRisks(a, b).Count;

        foreach (var f in sharedFiles)
            w += (config.IsRegenerable(f) ? 0.05 : 0.1) * (1 + 2 * (history?.GetValueOrDefault(f) ?? 0));

        var shared = sharedFiles.ToHashSet();
        var hb = b.Hunks.Where(h => shared.Contains(h.Path)).ToList();
        w += 0.3 * a.Hunks.Where(h => shared.Contains(h.Path)).Count(h => hb.Any(h.Overlaps));

        // A path names one file lineage: a rename is found by its old and its new path.
        static FileChange Change(PullRequest p, string f) => p.Files.First(c => c.Path == f || c.OldPath == f);
        w += 0.4 * sharedFiles.Count(f => LineageClash(Change(a, f), Change(b, f)));

        // Same member touched, even on different lines, is a likely semantic conflict.
        w += 0.2 * a.Members.Intersect(b.Members).Count();

        return Math.Min(w, 1.0);
    }

    /// <summary>
    /// Two changes to one file lineage that clash beyond line overlap: delete or rename against an edit or add,
    /// rename against delete, or renames to different targets. Identical deletes or renames merge cleanly.
    /// </summary>
    public static bool LineageClash(FileChange a, FileChange b) =>
        a.Kind == b.Kind
            ? a.Kind == ChangeKind.Renamed && a.Path != b.Path
            : a.Kind is ChangeKind.Deleted or ChangeKind.Renamed || b.Kind is ChangeKind.Deleted or ChangeKind.Renamed;

    static bool Accepts(HashSet<int> arities, int args) =>
        arities.Count > 0 && (args == -1 || arities.Contains(args) || arities.Contains(-1));

    static string Call(string name, int args) => args < 0 ? name : $"{name}({args} args)";

    /// <summary>
    /// Semantic (level 4) risks between two PRs, in both directions: new code calls a name the other PR removes or
    /// re-signatures ("breaks"), or calls a signature only the other PR adds ("needs"); or both add migrations
    /// to the same folder, where order and numbering matter. Each entry adds 0.3 to the pair's conflict weight.
    /// </summary>
    public static List<string> SemanticRisks(PullRequest a, PullRequest b)
    {
        IEnumerable<string> Directed(PullRequest changer, PullRequest user) => user.Uses
            .Where(u => changer.Api.ContainsKey(u.Name) && !user.Api.ContainsKey(u.Name))
            .Select(u => (u, api: changer.Api[u.Name]))
            .Where(x => Accepts(x.api.Before, x.u.Args) != Accepts(x.api.After, x.u.Args))
            .Select(x => Accepts(x.api.Before, x.u.Args)
                ? $"{user} uses {Call(x.u.Name, x.u.Args)}, changed by {changer}"
                : $"{user} uses {Call(x.u.Name, x.u.Args)}, added by {changer}")
            .Distinct();
        static IEnumerable<string?> MigrationDirs(PullRequest p) => p.Files
            .Where(f => f.Kind == ChangeKind.Added && FileClasses.Classify(f.Path) == FileClass.Migration)
            .Select(f => Path.GetDirectoryName(f.Path)).Distinct();
        return [.. Directed(a, b), .. Directed(b, a),
            .. MigrationDirs(a).Intersect(MigrationDirs(b)).Select(d => $"both add migrations in {d}/")];
    }

    /// <summary>
    /// Hard semantic dependency: b's new code uses a name that a introduces and that appears nowhere in the base,
    /// so b can't build without a.
    /// </summary>
    static void AddSemanticDependencies(Git git, List<PullRequest> prs)
    {
        var known = new System.Collections.Concurrent.ConcurrentDictionary<(string, string), bool>();
        bool InBase(string sha, string name) => known.GetOrAdd((sha, name), _ =>
            git.Try("grep", "-q", "-w", "-F", "-e", name, sha, "--", "*.cs").ExitCode == 0);
        foreach (var b in prs)
            foreach (var a in prs.Where(a => a != b && !b.Dependencies.Contains(a.Id)))
                if (b.Uses.FirstOrDefault(u => a.Api.TryGetValue(u.Name, out var api) && api.Before.Count == 0
                        && Accepts(api.After, u.Args) && !b.Api.ContainsKey(u.Name) && !InBase(a.BaseSha, u.Name)) is { Name: not null } use)
                {
                    b.Dependencies.Add(a.Id);
                    // Name-based inference can be wrong; never let it create a cycle.
                    if (FindCycle(prs) != null) b.Dependencies.Remove(a.Id);
                    else b.DependencyNotes[a.Id] = $"uses {use.Name}";
                }
    }

    /// <summary>
    /// Learns per-file conflict rates from the target's last merge commits: each merge is replayed with
    /// git merge-tree (batched, one process per core), and rate = conflicts / (times the merge brought the file in + 1).
    /// The +1 keeps a single conflict from reading as a certainty.
    /// </summary>
    // ponytail: only true merge commits carry history; squash/rebase-merged repos yield nothing to learn from.
    public static Dictionary<string, double> ConflictHistory(Git git, string target, int maxMerges)
    {
        if (maxMerges <= 0) return [];
        var touches = new Dictionary<string, int>();
        var merges = new List<(string Ours, string Theirs)>();
        foreach (var line in git.Run("log", "--merges", "--first-parent", "-n", maxMerges.ToString(), "--diff-merges=first-parent",
                     "--name-only", "--format=>%P", target).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            if (line.StartsWith('>')) { var p = line[1..].Split(' '); merges.Add((p[0], p[1])); }
            else touches[line] = touches.GetValueOrDefault(line) + 1;

        var conflicts = new Dictionary<string, int>();
        var size = Math.Max(8, (int)Math.Ceiling(merges.Count / (double)Environment.ProcessorCount));
        foreach (var batch in merges.Chunk(size).AsParallel().Select(c => git.MergeTreeBatch(c)).ToList())
            foreach (var f in batch?.SelectMany(r => r.Conflicts) ?? []) // a failed batch just contributes nothing
                conflicts[f] = conflicts.GetValueOrDefault(f) + 1;
        return conflicts.ToDictionary(c => c.Key, c => Math.Min(1, c.Value / (touches.GetValueOrDefault(c.Key) + 1.0)));
    }

    [GeneratedRegex(@"(?:depends[- ]on|blocked[- ]by)[:\s-]*#(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DependsOn();

    public static IEnumerable<string> ExplicitDependencies(PullRequest pr) =>
        DependsOn().Matches(pr.Body + "\n" + string.Join('\n', pr.Labels)).Select(m => "#" + m.Groups[1].Value);

    /// <summary>Explicit (body/labels), structural (stacked branches / commit ancestry) and semantic. Throws on cycles.</summary>
    // ponytail: dependencies on PRs outside the input set are ignored, not treated as blocking.
    public static void ResolveDependencies(Git git, string target, List<PullRequest> prs)
    {
        var ids = prs.Select(p => p.Id).ToHashSet();
        // One rev-list per PR instead of an ancestry check per pair: a is an ancestor of b iff a's head is among
        // b's commits not yet in the target. An empty set means the PR is already merged and can't be a dependency.
        var own = prs.AsParallel().ToDictionary(p => p, p =>
            git.Run("rev-list", $"{target}..{p.HeadSha}").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet());
        foreach (var p in prs) p.AlreadyMerged = own[p].Count == 0;
        foreach (var a in prs.Where(a => own[a].Count > 0))
            foreach (var b in prs.Where(b => b != a && b.HeadSha != a.HeadSha))
                if ((a.HeadRef != "" && b.BaseRef == a.HeadRef) || own[b].Contains(a.HeadSha))
                    b.Dependencies.Add(a.Id);
        foreach (var b in prs)
            foreach (var d in ExplicitDependencies(b).Where(d => ids.Contains(d) && d != b.Id)) b.Dependencies.Add(d);
        var cycle = FindCycle(prs);
        if (cycle != null) throw new InvalidOperationException("Dependency cycle: " + string.Join(" -> ", cycle));
        AddSemanticDependencies(git, prs);
    }

    public static List<string>? FindCycle(List<PullRequest> prs)
    {
        var byId = prs.ToDictionary(p => p.Id);
        var state = new Dictionary<string, int>(); // 1 = visiting, 2 = done
        var stack = new List<string>();
        List<string>? Visit(string id)
        {
            if (state.GetValueOrDefault(id) == 2) return null;
            if (state.GetValueOrDefault(id) == 1) return [.. stack.SkipWhile(s => s != id), id];
            state[id] = 1; stack.Add(id);
            foreach (var d in byId[id].Dependencies)
                if (Visit(d) is { } c) return c;
            stack.RemoveAt(stack.Count - 1); state[id] = 2;
            return null;
        }
        return prs.Select(p => Visit(p.Id)).FirstOrDefault(c => c != null);
    }

    /// <summary>
    /// Policy readiness; null means ready. Kept separate from the conflict score and from structural mergeability.
    /// GitHub's BLOCKED covers rules the other fields don't name (e.g. unresolved conversations, signed commits).
    /// </summary>
    public static string? NotReadyReason(PullRequest pr) =>
        pr.IsDraft ? "draft"
        : pr.ReviewDecision is "CHANGES_REQUESTED" or "REVIEW_REQUIRED" ? $"review: {pr.ReviewDecision}"
        : pr.CiStatus is "FAILURE" or "PENDING" ? $"checks: {pr.CiStatus}"
        : pr.MergeStateStatus == "BLOCKED" ? "GitHub: merging is blocked by branch protection"
        : null;

    /// <summary>What GitHub would say about merging a policy-ready PR, when that differs from "go ahead".</summary>
    public static string? GitHubNote(PullRequest pr) => pr.MergeStateStatus switch
    {
        "BEHIND" => "GitHub: branch is behind its base, update it before merging",
        "DIRTY" => "GitHub: reports conflicts with its base branch",
        "UNSTABLE" => "GitHub: non-required checks are failing",
        "UNKNOWN" => "GitHub: merge state not computed yet",
        _ => pr.AutoMerge ? "GitHub: auto-merge is already enabled" : null,
    };
}
