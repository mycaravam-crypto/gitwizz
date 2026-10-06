namespace PrOptimizer;

public static class Verify
{
    /// <summary>Runs a command in a temporary detached worktree on the given commit, then removes it.</summary>
    // ponytail: only verifies the final state; per-batch/critical-step modes can call this per step later.
    public static (bool Ok, string Output) Run(Git git, string commit, string command)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pr-optimizer-" + Guid.NewGuid().ToString("N")[..8]);
        git.Run("worktree", "add", "--detach", "--quiet", dir, commit);
        try
        {
            var r = Git.Exec(dir, "sh", ["-c", command]);
            return (r.ExitCode == 0, r.Stdout + r.Stderr);
        }
        finally
        {
            git.Try("worktree", "remove", "--force", dir);
        }
    }
}
