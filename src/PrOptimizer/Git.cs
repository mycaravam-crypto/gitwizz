using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace PrOptimizer;

public record GitResult(int ExitCode, string Stdout, string Stderr);

/// <summary>Runs git as an external process so simulation matches real git behaviour.</summary>
public class Git(string repoDir)
{
    public string RepoDir { get; } = repoDir;

    readonly Lazy<string> _objectsDir = new(() => Path.GetFullPath(
        Exec(repoDir, "git", ["rev-parse", "--git-path", "objects"]).Stdout.Trim(), repoDir));
    readonly Lazy<bool> _sha256 = new(() => Exec(repoDir, "git", ["rev-parse", "--show-object-format"]).Stdout.Trim() == "sha256");

    public GitResult Try(params string[] args) => Exec(RepoDir, "git", args);

    public string Run(params string[] args)
    {
        var r = Try(args);
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({r.ExitCode}): {r.Stderr.Trim()}");
        return r.Stdout.TrimEnd('\n');
    }

    public string RevParse(string rev) =>
        Try("rev-parse", "--verify", "--quiet", rev + "^{commit}") is { ExitCode: 0 } r
            ? r.Stdout.Trim() : throw new InvalidOperationException($"unknown branch or ref '{rev}'");

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

    /// <summary>
    /// Many merges in one process (git merge-tree --stdin). git buffers the output until stdin closes,
    /// so this is batch-only. Returns null if the batch fails; callers fall back to single merges.
    /// </summary>
    public List<(string Tree, List<string> Conflicts)>? MergeTreeBatch(IReadOnlyList<(string Ours, string Theirs)> merges)
    {
        var input = string.Concat(merges.Select(m => $"{m.Ours} {m.Theirs}\n"));
        var r = Exec(RepoDir, "git", ["merge-tree", "--stdin", "--name-only", "--no-messages", "-z"], stdin: input);
        if (r.ExitCode != 0) return null;
        // Per merge: status \0 tree \0 [conflicted path \0]* \0
        var f = r.Stdout.Split('\0');
        var res = new List<(string, List<string>)>();
        for (int i = 0; res.Count < merges.Count; i++)
        {
            if (i + 1 >= f.Length) return null;
            var tree = f[++i];
            var conflicts = new List<string>();
            while (f[++i] != "") conflicts.Add(f[i]);
            res.Add((tree, conflicts.Distinct().ToList()));
        }
        return res;
    }

    /// <summary>
    /// Writes a synthetic commit as a loose object in-process: no git process per commit.
    /// Fixed identity and epoch date keep commits deterministic, so identical merges get identical ids.
    /// </summary>
    public string CommitTree(string tree, string message, params string[] parents)
    {
        var body = new StringBuilder($"tree {tree}\n");
        foreach (var p in parents) body.Append($"parent {p}\n");
        body.Append("author pr-optimizer <pr-optimizer@localhost> 0 +0000\n");
        body.Append("committer pr-optimizer <pr-optimizer@localhost> 0 +0000\n\n");
        body.Append(message).Append('\n');
        var content = Encoding.UTF8.GetBytes(body.ToString());
        byte[] obj = [.. Encoding.ASCII.GetBytes($"commit {content.Length}\0"), .. content];

        var id = Convert.ToHexStringLower(_sha256.Value ? SHA256.HashData(obj) : SHA1.HashData(obj));
        var path = Path.Combine(_objectsDir.Value, id[..2], id[2..]);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Write to a temp file and rename, so concurrent writers and readers never see a partial object.
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var z = new ZLibStream(File.Create(tmp), CompressionLevel.Fastest)) z.Write(obj);
            try { File.Move(tmp, path); } catch (IOException) { File.Delete(tmp); } // already written by another thread
        }
        return id;
    }

    public static GitResult Exec(string dir, string file, string[] args, IDictionary<string, string>? env = null, string? stdin = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        // Dedicated thread: a pool-based read starves when callers block pool threads (Parallel.ForEach).
        var stderr = Task.Factory.StartNew(p.StandardError.ReadToEnd, TaskCreationOptions.LongRunning);
        if (stdin != null)
            Task.Factory.StartNew(() => { p.StandardInput.Write(stdin); p.StandardInput.Close(); }, TaskCreationOptions.LongRunning);
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return new GitResult(p.ExitCode, stdout, stderr.Result);
    }
}
