using System.Text;
using System.Text.Json;

namespace Gitwizz;

/// <summary>Read-only, bounded file-history inspection. Does not modify history/v2 or the index.</summary>
public static class FileInspect
{
    public record Change(string Sha, string Author, string AuthoredAt, string CommittedAt,
        string Subject, string Path, string Action, string Diff, string? PullRequest, string[] WorkItems);
    public record Result(string Schema, string File, string Until, int Limit, bool Truncated,
        bool Shallow, List<Change> Changes, string[] Warnings);

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
        for (var i = 1; i + 7 < parts.Length && changes.Count <= limit; )
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
            var next = i < parts.Length ? parts[i] : "";
            if (next.Length > 0 && next.Trim().Length > 0) diff += next;
            i++;
            var pr = System.Text.RegularExpressions.Regex.Match(subject, @"\(#(?<id>\d+)\)$");
            var work = System.Text.RegularExpressions.Regex.Matches(subject + "\n" + body,
                @"\bAB#\d+\b|\b(?:fixes|closes|resolves)\s+#\d+\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                .Select(x => x.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var action = diff.Contains("new file mode ", StringComparison.Ordinal) ? "added"
                : diff.Contains("deleted file mode ", StringComparison.Ordinal) ? "deleted"
                : diff.Contains("rename from ", StringComparison.Ordinal) ? "renamed" : "modified";
            changes.Add(new Change(sha, author, authored, committed, subject, relative, action,
                diff.Trim(), pr.Success ? "#" + pr.Groups["id"].Value : null, work));
        }
        var truncated = changes.Count > limit;
        if (truncated) changes.RemoveAt(changes.Count - 1);
        var shallow = git.Try("rev-parse", "--is-shallow-repository").Stdout.Trim() == "true";
        var warnings = new List<string>();
        if (truncated) warnings.Add("History truncated by --limit; increase the limit or use --since.");
        if (shallow) warnings.Add("Shallow clone: earlier changes may be missing.");
        warnings.Add("PR ids and work items are unverified commit-message references, not confirmed change rationale.");
        if (changes.Count == 0) warnings.Add("No changes found for this path and revision range.");
        return new Result("gitwizz.inspect/v1", relative, until, limit, truncated, shallow, changes, [.. warnings]);
    }

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
            if (c.WorkItems.Length > 0) sb.AppendLine($"  Work items mentioned: {string.Join(", ", c.WorkItems)} (unverified)");
            sb.AppendLine("  Why: only the commit message is available; no independently verified rationale.");
            var hunks = c.Diff.Split('\n').Where(l => l.StartsWith("@@ ", StringComparison.Ordinal)).ToArray();
            foreach (var h in hunks) sb.AppendLine("  " + h.TrimEnd('\r'));
        }
        foreach (var warning in result.Warnings) sb.AppendLine("Warning: " + warning);
        return sb.ToString();
    }
}
