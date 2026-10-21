using System.Text;
using System.Text.Json;

namespace Gitwizz;

/// <summary>Read-only, bounded file-history inspection. Does not modify history/v2 or the index.</summary>
public static class FileInspect
{
    public record Change(string Sha, string Author, string AuthoredAt, string CommittedAt,
        string Subject, string Path, string Action, string Diff, string? PullRequest, IReadOnlyList<string> WorkItems);
    public record Result(string Schema, string File, string Until, int Limit, bool Truncated,
        bool Shallow, IReadOnlyList<Change> Changes, IReadOnlyList<string> Warnings);

    /// <summary>Inspects the bounded commit history for a repository-relative file, including changed line hunks and unverified references.</summary>
    /// <param name="git">Git repository used for read-only history queries.</param>
    /// <param name="path">Repository-relative file path.</param>
    /// <param name="until">Revision at which the inspection ends.</param>
    /// <param name="limit">Maximum number of commits to report.</param>
    /// <param name="since">Optional exclusive starting revision.</param>
    /// <returns>File change history, evidence and completeness warnings.</returns>
    public static Result Inspect(Git git, string path, string until = "HEAD", int limit = 200, string? since = null)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('-') || Path.IsPathRooted(path))
            throw new ArgumentException("need a repository-relative file path (no absolute paths or options)");
        if (limit is < 1 or > 10000) throw new ArgumentException("--limit must be between 1 and 10000");
        var root = Path.GetFullPath(git.Run("rev-parse", "--show-toplevel"));
        var full = Path.GetFullPath(Path.Combine(root, path));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && full != root)
            throw new ArgumentException("file path must stay inside the repository");
        var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        if (relative == ".") throw new ArgumentException("need a file path");
        var end = git.RevParse(until);
        var range = since == null ? end : git.RevParse(since) + ".." + end;
        // --follow tracks renames; -p retains deleted lines and earlier states.
        // Use NUL separators for fields that can contain whitespace.
        var args = new List<string> { "-c", "core.quotePath=false", "log", "--follow",
            "--format=%x00%H%x00%an%x00%aI%x00%cI%x00%s%x00%b%x00",
            "--no-ext-diff", "--find-renames", "-p", "--unified=0",
            "--max-count=" + (limit + 1), range, "--", relative };
        var raw = git.Try([.. args]);
        if (raw.ExitCode != 0) throw new InvalidOperationException("git log failed: " + raw.Stderr.Trim());
        var parts = raw.Stdout.Split('\0');
        var changes = new List<Change>();
        for (var i = 1; i + 6 < parts.Length && changes.Count <= limit; )
        {
            var sha = parts[i++].Trim();
            if (sha.Length < 40 || !sha.All(Uri.IsHexDigit)) break;
            var author = parts[i++];
            var authored = parts[i++];
            var committed = parts[i++];
            var subject = parts[i++];
            var body = parts[i++];
            var diff = parts[i++];
            // git log places a newline between the message and the patch, and may separate entries.
            var pr = System.Text.RegularExpressions.Regex.Match(subject, @"\(#(?<id>\d+)\)$");
            var work = System.Text.RegularExpressions.Regex.Matches(subject + "\n" + body,
                @"\bAB#\d+\b|\b(?:fixes|closes|resolves)\s+#\d+\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                .Select(x => x.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var action = diff.Contains("new file mode ", StringComparison.Ordinal) ? "added"
                : diff.Contains("deleted file mode ", StringComparison.Ordinal) ? "deleted"
                : diff.Contains("rename from ", StringComparison.Ordinal) ? "renamed" : "modified";
            changes.Add(new Change(sha, author, authored, committed, subject, relative, action,
                diff.Trim(), pr.Success ? "#" + pr.Groups["id"].Value : null, Array.AsReadOnly(work)));
        }
        var truncated = changes.Count > limit;
        if (truncated) changes.RemoveAt(changes.Count - 1);
        var shallow = git.Try("rev-parse", "--is-shallow-repository").Stdout.Trim() == "true";
        var warnings = new List<string>();
        if (truncated) warnings.Add("History truncated by --limit; increase the limit or use --since.");
        if (shallow) warnings.Add("Shallow clone: earlier changes may be missing.");
        warnings.Add("PR ids and work items are unverified commit-message references, not confirmed change rationale.");
        if (changes.Count == 0) warnings.Add("No changes found for this path and revision range.");
        return new Result("gitwizz.inspect/v1", relative, until, limit, truncated, shallow, changes.AsReadOnly(), warnings.AsReadOnly());
    }


    /// <summary>One tracked file in a directory tree, with the last touching commit's message evidence.</summary>
    public record DirectoryEntry(string Path, string Sha, string Author, string AuthoredAt,
        string CommittedAt, string Subject, string? PullRequest, IReadOnlyList<string> WorkItems, long CommitTimestamp);
    public record DirectoryResult(string Schema, string Directory, string Until, int TotalFiles,
        int Limit, bool Truncated, IReadOnlyList<DirectoryEntry> Files, IReadOnlyList<string> Warnings);

    /// <summary>Directory mode uses the revision's tracked tree, never the working-tree filesystem.</summary>
    public static DirectoryResult InspectDirectory(Git git, string path, string until = "HEAD", int limit = 200, string? since = null)
    {
        if (limit is < 1 or > 10000) throw new ArgumentException("--limit must be between 1 and 10000");
        var relative = NormalizePath(git, path);
        var end = git.RevParse(until);
        var prefix = relative == "." ? "" : relative.TrimEnd('/') + "/";
        var ls = git.Try("ls-tree", "-r", "-z", "--name-only", end, "--", prefix);
        if (ls.ExitCode != 0) throw new InvalidOperationException("git ls-tree failed: " + ls.Stderr.Trim());
        var paths = ls.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (paths.Length == 0) throw new ArgumentException($"no tracked directory found at '{relative}' in {until}");
        var range = since == null ? end : git.RevParse(since) + ".." + end;
        var entries = new List<DirectoryEntry>();
        foreach (var file in paths)
        {
            // The end-of-revision tree bounds the file set; log is scoped to one tracked path.
            var r = git.Try("log", "-1", "--format=%H%x00%an%x00%aI%x00%cI%x00%s%x00%b%x00%ct",
                range, "--", file);
            if (r.ExitCode != 0) throw new InvalidOperationException("git log failed: " + r.Stderr.Trim());
            var fields = r.Stdout.Trim('\n', '\r').Split('\0');
            if (fields.Length < 7 || fields[0].Length < 40) continue;
            var subject = fields[4];
            var pr = System.Text.RegularExpressions.Regex.Match(subject, @"\(#(?<id>\d+)\)$");
            var work = System.Text.RegularExpressions.Regex.Matches(subject + "\n" + fields[5],
                @"\bAB#\d+\b|\b(?:fixes|closes|resolves)\s+#\d+\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                .Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (!long.TryParse(fields[6].Trim(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out long timestamp))
                throw new InvalidOperationException($"git log returned an invalid timestamp for {file}");
            entries.Add(new DirectoryEntry(file, fields[0], fields[1], fields[2], fields[3], subject,
                pr.Success ? "#" + pr.Groups["id"].Value : null, Array.AsReadOnly(work), timestamp));
        }
        var sorted = entries.OrderByDescending(e => e.CommitTimestamp).ThenBy(e => e.Path, StringComparer.Ordinal).ToList();
        var shallow = git.Try("rev-parse", "--is-shallow-repository").Stdout.Trim() == "true";
        var warnings = new List<string> { "PR/work-item references are unverified commit-message mentions; a subject is not verified rationale." };
        if (shallow) warnings.Add("Shallow repository: older history may be missing.");
        if (sorted.Count > limit) warnings.Add("Directory results truncated by --limit.");
        return new DirectoryResult("gitwizz.inspect-directory/v1", relative, until, paths.Length, limit,
            sorted.Count > limit, sorted.Take(limit).ToList().AsReadOnly(), warnings.AsReadOnly());
    }

    /// <summary>Canonical, repository-relative path shared by file and directory mode.</summary>
    public static string NormalizePath(Git git, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('-') || Path.IsPathRooted(path))
            throw new ArgumentException("need a repository-relative file or directory path");
        var root = Path.GetFullPath(git.Run("rev-parse", "--show-toplevel"));
        var full = Path.GetFullPath(Path.Combine(root, path));
        if (full != root && !full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("path must stay inside the repository");
        return Path.GetRelativePath(root, full).Replace('\\', '/');
    }

    public static string DirectoryJson(DirectoryResult result) => JsonSerializer.Serialize(result,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }) + "\n";

    public static string DirectoryText(DirectoryResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Directory: {result.Directory}  ({result.Files.Count} shown of {result.TotalFiles} tracked files; last change first)");
        sb.AppendLine("Last change               Author                Commit        PR       File / reason");
        sb.AppendLine(new string('-', 105));
        foreach (var f in result.Files)
        {
            var pr = f.PullRequest ?? "-";
            sb.AppendLine($"{f.CommittedAt,-25} {Short(f.Author, 20)} {f.Sha[..12],-13} {pr,-8} {f.Path}");
            sb.AppendLine($"    {f.Subject}" + (f.WorkItems.Count > 0 ? $"  [work items mentioned: {string.Join(", ", f.WorkItems)}]" : ""));
        }
        foreach (var w in result.Warnings) sb.AppendLine("Warning: " + w);
        return sb.ToString();
    }

    static string Short(string value, int width) =>
        value.Length > width ? value[..(width - 1)] + "…" : value.PadRight(width);

    public static string Json(Result result) => JsonSerializer.Serialize(result,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }) + "\n";

    public static string Text(Result result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"File: {result.File}  ({result.Changes.Count} commits, newest first)");
        foreach (var c in result.Changes)
        {
            sb.AppendLine();
            sb.AppendLine($"{c.AuthoredAt}  {c.Author}  {c.Sha[..12]}  [{c.Action}]");
            sb.AppendLine($"  {c.Subject}");
            if (c.PullRequest != null) sb.AppendLine($"  PR mentioned: {c.PullRequest} (unverified)");
            if (c.WorkItems.Count > 0) sb.AppendLine($"  Work items mentioned: {string.Join(", ", c.WorkItems)} (unverified)");
            sb.AppendLine("  Why: only the commit message is available; no independently verified rationale.");
            var hunks = c.Diff.Split('\n').Where(l => l.StartsWith("@@ ", StringComparison.Ordinal)).ToArray();
            foreach (var h in hunks) sb.AppendLine("  " + h.TrimEnd('\r'));
        }
        foreach (var warning in result.Warnings) sb.AppendLine("Warning: " + warning);
        return sb.ToString();
    }
}
