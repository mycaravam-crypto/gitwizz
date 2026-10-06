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

public static partial class Analyzer
{
    /// <summary>Fills BaseSha, Files, Hunks and Members relative to the merge-base with the target.</summary>
    public static void Analyze(Git git, string target, PullRequest pr)
    {
        pr.BaseSha = git.MergeBase(target, pr.HeadSha);
        // Pin output format against user config (noprefix, external diff, quoted paths).
        string[] diff = ["-c", "core.quotePath=false", "diff", "-M", "--no-ext-diff", "--no-color", "--src-prefix=a/", "--dst-prefix=b/"];
        pr.Files = ParseNameStatus(git.Run([.. diff, "--name-status", pr.BaseSha, pr.HeadSha]));
        pr.Hunks = ParseHunks(git.Run([.. diff, "-U0", pr.BaseSha, pr.HeadSha]));
        // Hunks of added files start at 0 and have no old-side members.
        pr.Members = pr.Hunks.Where(h => h.Start > 0 && Structure.Supports(h.Path)).GroupBy(h => h.Path)
            .SelectMany(g => Structure.TouchedMembers(git.Run("show", $"{pr.BaseSha}:{g.Key}"), g))
            .ToHashSet();
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

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+")]
    private static partial Regex HunkHeader();

    /// <summary>Hunks on the old (merge-base) side, so hunks of different PRs with the same base are comparable.</summary>
    public static List<Hunk> ParseHunks(string diff)
    {
        var hunks = new List<Hunk>();
        string? oldPath = null, newPath = null;
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("--- ")) oldPath = line == "--- /dev/null" ? null : line[6..];
            else if (line.StartsWith("+++ ")) newPath = line == "+++ /dev/null" ? null : line[6..];
            else if (HunkHeader().Match(line) is { Success: true } m)
            {
                var count = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
                hunks.Add(new Hunk((oldPath ?? newPath)!, int.Parse(m.Groups[1].Value), count));
            }
        }
        return hunks;
    }

    static IEnumerable<string> Paths(PullRequest pr) =>
        pr.Files.SelectMany(f => f.OldPath is null ? [f.Path] : new[] { f.Path, f.OldPath }).Distinct();

    /// <summary>Pairwise conflict risk in [0,1]. Heuristic; the simulation is the source of truth.</summary>
    // ponytail: hunk comparison assumes both PRs share a merge-base; diverging bases make it approximate.
    public static double ConflictWeight(PullRequest a, PullRequest b, out List<string> sharedFiles)
    {
        sharedFiles = Paths(a).Intersect(Paths(b)).ToList();
        if (sharedFiles.Count == 0) return 0;

        double w = 0;
        foreach (var f in sharedFiles)
            w += FileClasses.IsRegenerable(FileClasses.Classify(f)) ? 0.05 : 0.1;

        var shared = sharedFiles.ToHashSet();
        var hb = b.Hunks.Where(h => shared.Contains(h.Path)).ToList();
        w += 0.3 * a.Hunks.Where(h => shared.Contains(h.Path)).Count(h => hb.Any(h.Overlaps));

        bool Risky(PullRequest p, string f) =>
            p.Files.Any(c => (c.Kind is ChangeKind.Deleted && c.Path == f) || (c.Kind is ChangeKind.Renamed && c.OldPath == f));
        w += 0.4 * sharedFiles.Count(f => Risky(a, f) != Risky(b, f));

        // Same member touched, even on different lines, is a likely semantic conflict.
        w += 0.2 * a.Members.Intersect(b.Members).Count();

        return Math.Min(w, 1.0);
    }

    [GeneratedRegex(@"(?:depends[- ]on|blocked[- ]by)[:\s-]*#(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DependsOn();

    public static IEnumerable<string> ExplicitDependencies(PullRequest pr) =>
        DependsOn().Matches(pr.Body + "\n" + string.Join('\n', pr.Labels)).Select(m => "#" + m.Groups[1].Value);

    /// <summary>Explicit (body/labels) + structural (stacked branches / commit ancestry). Throws on cycles.</summary>
    // ponytail: dependencies on PRs outside the input set are ignored, not treated as blocking.
    public static void ResolveDependencies(Git git, string target, List<PullRequest> prs)
    {
        var ids = prs.Select(p => p.Id).ToHashSet();
        // One rev-list per PR instead of an ancestry check per pair: a is an ancestor of b iff a's head is among
        // b's commits not yet in the target. An empty set means the PR is already merged and can't be a dependency.
        var own = prs.AsParallel().ToDictionary(p => p, p =>
            git.Run("rev-list", $"{target}..{p.HeadSha}").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet());
        foreach (var a in prs.Where(a => own[a].Count > 0))
            foreach (var b in prs.Where(b => b != a && b.HeadSha != a.HeadSha))
                if ((a.HeadRef != "" && b.BaseRef == a.HeadRef) || own[b].Contains(a.HeadSha))
                    b.Dependencies.Add(a.Id);
        foreach (var b in prs)
            foreach (var d in ExplicitDependencies(b).Where(d => ids.Contains(d) && d != b.Id)) b.Dependencies.Add(d);
        var cycle = FindCycle(prs);
        if (cycle != null) throw new InvalidOperationException("Dependency cycle: " + string.Join(" -> ", cycle));
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

    /// <summary>Readiness constraints; null means ready. Kept separate from the conflict score.</summary>
    public static string? NotReadyReason(PullRequest pr) =>
        pr.IsDraft ? "draft"
        : pr.ReviewDecision is "CHANGES_REQUESTED" or "REVIEW_REQUIRED" ? $"review: {pr.ReviewDecision}"
        : pr.CiStatus is "FAILURE" or "PENDING" ? $"CI: {pr.CiStatus}"
        : null;
}
