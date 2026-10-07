using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Gitwizz;

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

    /// <summary>
    /// git merge-tree --write-tree: returns tree sha and conflicted paths (empty when clean).
    /// mergeBase: explicit base instead of the computed one, e.g. a commit's parent to cherry-pick it (git ≥ 2.40).
    /// </summary>
    public (string Tree, List<string> Conflicts) MergeTree(string ours, string theirs, string? mergeBase = null)
    {
        string[] args = ["merge-tree", "--write-tree", "--name-only", "--no-messages"];
        var r = Try([.. args, .. mergeBase != null ? new[] { "--merge-base=" + mergeBase } : [], ours, theirs]);
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
    /// Tree with the given paths replaced by their version in commit (removed where commit lacks them).
    /// Uses a throwaway index file, so the user's index and working tree are untouched.
    /// </summary>
    public string ReplacePaths(string tree, string commit, IReadOnlyCollection<string> paths)
    {
        var index = Path.Combine(Path.GetTempPath(), "gitwizz-index-" + Guid.NewGuid().ToString("N"));
        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
        string Must(GitResult r, string what) =>
            r.ExitCode == 0 ? r.Stdout.Trim() : throw new InvalidOperationException($"git {what} failed: {r.Stderr.Trim()}");
        try
        {
            Must(Exec(RepoDir, "git", ["read-tree", tree], env), "read-tree");
            var theirs = Must(Exec(RepoDir, "git", ["ls-tree", "-r", "-z", commit, "--", .. paths]), "ls-tree")
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Split('\t', 2)).ToDictionary(e => e[1], e => e[0].Split(' ')); // mode, type, sha
            var zero = new string('0', tree.Length);
            // --index-info: "mode sha<TAB>path"; mode 0 removes the entry.
            var info = string.Concat(paths.Select(p => theirs.TryGetValue(p, out var m) ? $"{m[0]} {m[2]}\t{p}\n" : $"0 {zero}\t{p}\n"));
            Must(Exec(RepoDir, "git", ["update-index", "--index-info"], env, info), "update-index");
            return Must(Exec(RepoDir, "git", ["write-tree"], env), "write-tree");
        }
        finally { File.Delete(index); }
    }

    // Far in the future: date-ordered history walks (rev-list a..b) assume children are newer than parents, and a 1970
    // commit on top of real history makes them stop early and list commits that are actually reachable.
    const long SyntheticDate = 4102444800; // 2100-01-01

    /// <summary>
    /// Writes a synthetic commit as a loose object in-process: no git process per commit.
    /// Fixed identity and date keep commits deterministic, so identical merges get identical ids.
    /// </summary>
    public string CommitTree(string tree, string message, params string[] parents)
    {
        var body = new StringBuilder($"tree {tree}\n");
        foreach (var p in parents) body.Append($"parent {p}\n");
        body.Append($"author gitwizz <gitwizz@localhost> {SyntheticDate} +0000\n");
        body.Append($"committer gitwizz <gitwizz@localhost> {SyntheticDate} +0000\n\n");
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

    /// <summary>
    /// Contents of many blobs ("rev:path") from one git cat-file process instead of one git show each: process
    /// start-up dominates on Windows. Throws if one is missing, as git show would.
    /// </summary>
    public List<string> ReadBlobs(IReadOnlyList<string> specs)
    {
        if (specs.Count == 0) return [];
        using var p = Start(RepoDir, "git", ["cat-file", "--batch"], null, stdin: true);
        var stderr = Task.Factory.StartNew(p.StandardError.ReadToEnd, TaskCreationOptions.LongRunning);
        Task.Factory.StartNew(() => { p.StandardInput.Write(string.Concat(specs.Select(s => s + "\n"))); p.StandardInput.Close(); },
            TaskCreationOptions.LongRunning);
        var output = new BufferedStream(p.StandardOutput.BaseStream);
        string Line()
        {
            var bytes = new List<byte>();
            for (int b; (b = output.ReadByte()) != '\n';)
                bytes.Add(b >= 0 ? (byte)b : throw new InvalidOperationException($"git cat-file failed: {stderr.Result.Trim()}"));
            return Encoding.UTF8.GetString([.. bytes]);
        }
        // Per spec: "<sha> <type> <size>\n<content>\n", or "<spec> missing\n".
        var res = new List<string>();
        foreach (var spec in specs)
        {
            var header = Line().Split(' ');
            if (header.Length != 3 || !long.TryParse(header[2], out var size))
                throw new InvalidOperationException($"git show {spec} failed: {string.Join(' ', header.Skip(1))}");
            var content = new byte[size];
            output.ReadExactly(content);
            output.ReadByte(); // trailing newline
            res.Add(Encoding.UTF8.GetString(content).TrimStart('﻿')); // drop a BOM, as git show's reader did
        }
        p.WaitForExit();
        return res;
    }

    /// <summary>Runs any program in dir and captures exit code, stdout and stderr; never throws on a non-zero exit.</summary>
    public static GitResult Exec(string dir, string file, string[] args, IDictionary<string, string>? env = null, string? stdin = null)
    {
        Process p;
        try { p = Start(dir, file, args, env, stdin != null); }
        catch (System.ComponentModel.Win32Exception e) { return new GitResult(127, "", $"{file}: {e.Message}"); } // not installed
        using var _ = p;
        // Dedicated thread: a pool-based read starves when callers block pool threads (Parallel.ForEach).
        var stderr = Task.Factory.StartNew(p.StandardError.ReadToEnd, TaskCreationOptions.LongRunning);
        if (stdin != null)
            Task.Factory.StartNew(() => { p.StandardInput.Write(stdin); p.StandardInput.Close(); }, TaskCreationOptions.LongRunning);
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return new GitResult(p.ExitCode, stdout, stderr.Result);
    }

    static Process Start(string dir, string file, string[] args, IDictionary<string, string>? env, bool stdin)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin,
            // Windows: keep children off our console. Console processes coming and going (and resetting its mode)
            // switch off ANSI processing under Git Bash, which garbles the live spinner into raw escape codes.
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // No console to prompt on: a credential prompt must fail fast instead of waiting unseen.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        return Process.Start(psi)!;
    }
}
