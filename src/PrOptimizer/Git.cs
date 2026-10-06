using System.Diagnostics;

namespace PrOptimizer;

public record GitResult(int ExitCode, string Stdout, string Stderr);

/// <summary>Runs git as an external process so simulation matches real git behaviour.</summary>
public class Git(string repoDir)
{
    public string RepoDir { get; } = repoDir;

    // Fixed identity and dates make synthetic commits deterministic, so cache keys stay stable.
    static readonly Dictionary<string, string> SyntheticEnv = new()
    {
        ["GIT_AUTHOR_NAME"] = "pr-optimizer",
        ["GIT_AUTHOR_EMAIL"] = "pr-optimizer@localhost",
        ["GIT_COMMITTER_NAME"] = "pr-optimizer",
        ["GIT_COMMITTER_EMAIL"] = "pr-optimizer@localhost",
        ["GIT_AUTHOR_DATE"] = "1970-01-01T00:00:00Z",
        ["GIT_COMMITTER_DATE"] = "1970-01-01T00:00:00Z",
    };

    public GitResult Try(params string[] args) => Exec(RepoDir, "git", args);

    public string Run(params string[] args)
    {
        var r = Try(args);
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({r.ExitCode}): {r.Stderr.Trim()}");
        return r.Stdout.TrimEnd('\n');
    }

    public string RevParse(string rev) => Run("rev-parse", "--verify", rev + "^{commit}");

    public string MergeBase(string a, string b) => Run("merge-base", a, b);

    public bool IsAncestor(string ancestor, string descendant) =>
        Try("merge-base", "--is-ancestor", ancestor, descendant).ExitCode == 0;

    /// <summary>git merge-tree --write-tree: returns tree sha and conflicted paths (empty when clean).</summary>
    public (string Tree, List<string> Conflicts) MergeTree(string ours, string theirs)
    {
        var r = Try("merge-tree", "--write-tree", "--name-only", "--no-messages", ours, theirs);
        if (r.ExitCode > 1)
            throw new InvalidOperationException($"git merge-tree failed: {r.Stderr.Trim()}");
        var lines = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return (lines[0], lines.Skip(1).Distinct().ToList());
    }

    public string CommitTree(string tree, string message, params string[] parents)
    {
        var args = new List<string> { "commit-tree", tree, "-m", message };
        foreach (var p in parents) { args.Add("-p"); args.Add(p); }
        var r = Exec(RepoDir, "git", [.. args], SyntheticEnv);
        if (r.ExitCode != 0) throw new InvalidOperationException($"git commit-tree failed: {r.Stderr.Trim()}");
        return r.Stdout.Trim();
    }

    public static GitResult Exec(string dir, string file, string[] args, IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        // Dedicated thread: a pool-based read starves when callers block pool threads (Parallel.ForEach).
        var stderr = Task.Factory.StartNew(p.StandardError.ReadToEnd, TaskCreationOptions.LongRunning);
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return new GitResult(p.ExitCode, stdout, stderr.Result);
    }
}
