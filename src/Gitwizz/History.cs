using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Gitwizz;

// --- gitwizz.history/v2 (contract: docs/history/README.md, schema: docs/history/gitwizz.history.v2.schema.json) -------

/// <summary>A changed line range: OldStart/OldCount in the parent's file, NewStart/NewCount in the commit's (git diff -U0).</summary>
public record HistoryRange(int OldStart, int OldCount, int NewStart, int NewCount);

/// <summary>
/// A file a commit changed. Status: added, modified, deleted, renamed or copied (OldPath and Similarity set for the
/// last two). Granularity: lines when Ranges say which lines changed; file when only the file is known to have changed
/// (binary, a rename or copy without content change, a mode change): a consumer must not map that to a symbol.
/// </summary>
public record HistoryFile(string Path, string? OldPath, string Status, int? Similarity, string Granularity, List<HistoryRange> Ranges);

/// <summary>
/// A work item linked to a commit or a pull request. Source: commit-message (the commit's own message names it: a
/// mention, not a verified link) or provider (the provider keeps the link). Kind: how it was named: ab (AB#123), closes
/// (a GitHub closing keyword such as "fixes #12"), mention (#123 in an Azure DevOps commit message), pr-link (linked to
/// the pull request) or commit-link (linked to the commit itself). Evidence: the matched text, or what the provider
/// returned. Uri: where the provider's link can be checked, when it names one.
/// </summary>
public record HistoryLink(string WorkItem, string Source, string Kind, string Evidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Uri = null);

/// <summary>
/// The work items the provider links to a commit itself. Status: verified (the provider answered for this commit and
/// repository; Items is complete, an empty list is a verified "no links"), unavailable (it couldn't be read: Problem
/// says why; Items is null), denied (the provider refused access; Items is null) or unknown (not asked: the provider
/// keeps no commit links, or the lookup budget ran out; Items is null).
/// </summary>
public record HistoryCommitLinks(string Status, List<HistoryLink>? Items, string? Problem)
{
    public static HistoryCommitLinks Unknown(string why) => new("unknown", null, why);
}

/// <summary>
/// The pull request a commit came in with. Resolution: exact (the provider confirms it: the PR's merge commit, or a
/// commit of a merged branch whose merge the provider confirms), inferred (only the commit messages say so), ambiguous
/// (more than one PR claims it: Candidates), unknown (nothing says which PR, e.g. a rebase merge or a direct push).
/// Basis: the evidence used.
/// </summary>
public record HistoryPrRef(string? Id, string Resolution, List<string> Basis, List<string> Candidates);

/// <summary>
/// A commit in the exported range. Kind: commit, or merge (its changes are its parents'; it carries no files).
/// WorkItems: what the message names (source commit-message); ProviderLinks: what the provider links to the commit.
/// </summary>
public record HistoryCommit(string Sha, List<string> Parents, string Subject, string CommittedAt, string Kind, List<HistoryFile> Files,
    List<HistoryLink> WorkItems, HistoryCommitLinks ProviderLinks, HistoryPrRef PullRequest);

/// <summary>
/// A completed pull request found in the range. Strategy: merge or squash as the history shows it, or unknown.
/// Status: confirmed (the provider says it is completed), unconfirmed (the provider couldn't be asked or doesn't know
/// it: Problem says why), or local (no provider). WorkItemsStatus: verified (the provider answered; an empty list is a
/// verified empty list), unavailable (it couldn't be read: Problem), denied (the provider refused access: Problem),
/// unknown (no provider to ask). Its work items are candidates for the PR's commits, not links of any one commit.
/// </summary>
public record HistoryPr(string Id, string? Title, string? Url, string? Target, string Status, string Strategy, string? MergeCommit, List<string> Commits,
    List<HistoryLink> WorkItems, string WorkItemsStatus, string? Problem);

/// <summary>
/// The exported range: refs as given, the commits they resolved to, whether the limit cut it short and whether the
/// clone is shallow (commits beyond its boundary are missing).
/// </summary>
public record HistoryBounds(string? Since, string? SinceSha, string Until, string UntilSha, int Limit, int Commits, bool Truncated, bool Shallow);

/// <summary>What each kind of link was read from and how complete it is.</summary>
public record HistorySources(string CommitMessages, string ProviderCommitLinks, string PullRequests, string PullRequestWorkItems);

public record HistoryExport(string Schema, string Provider, string? Repository, HistoryBounds Range, HistorySources Sources, List<HistoryCommit> Commits,
    List<HistoryPr> PullRequests, List<string> Warnings);

/// <summary>What a provider says about one completed pull request, or Problem when it couldn't say.</summary>
public record ProviderPr(string Id, string? Title, string? Url, string? Target, bool Completed, string? MergeCommit, List<string> Commits,
    List<HistoryLink>? WorkItems, string? Problem = null);

/// <summary>Read-only lookup of completed pull requests. Throws InvalidOperationException when the provider can't be asked.</summary>
public interface IHistoryProvider
{
    string Name { get; }
    /// <summary>Where commit → work item links are read from, or why they aren't.</summary>
    string ProviderCommitLinks { get; }
    /// <summary>Whether CommitLinks asks the provider; when false, every commit's provider links are unknown.</summary>
    bool HasCommitLinks => false;
    ProviderPr PullRequest(int number);
    /// <summary>The work items the provider links to one commit. Never throws: a failure is a status with its problem.</summary>
    HistoryCommitLinks CommitLinks(string sha) => HistoryCommitLinks.Unknown(ProviderCommitLinks);
}

/// <summary>No provider: pull requests come from commit messages only, their work items are unknown.</summary>
public sealed class LocalHistory : IHistoryProvider
{
    public string Name => "local";
    public string ProviderCommitLinks => "unknown: local provider";
    public ProviderPr PullRequest(int number) => throw new InvalidOperationException("local provider: no pull requests to ask");
}

/// <summary>GitHub through gh: a merged PR's merge commit and the issues it closes.</summary>
public sealed class GitHubHistory(string dir) : IHistoryProvider
{
    public string Name => "github";
    public string ProviderCommitLinks => "unknown: GitHub links issues to pull requests, not to commits";

    public ProviderPr PullRequest(int number)
    {
        var r = Git.Exec(dir, "gh", ["pr", "view", number.ToString(), "--json", "number,title,state,url,baseRefName,mergeCommit,closingIssuesReferences"],
            timeout: TimeSpan.FromMinutes(2));
        if (r.ExitCode != 0)
            throw new InvalidOperationException(r.ExitCode == 127 ? "gh not found" : $"gh pr view {number} failed (exit {r.ExitCode}): {FirstLine(r.Stderr)}");
        return Parse(JsonDocument.Parse(r.Stdout).RootElement);
    }

    /// <summary>gh pr view --json number,title,state,url,baseRefName,mergeCommit,closingIssuesReferences.</summary>
    public static ProviderPr Parse(JsonElement e)
    {
        var id = "#" + e.GetProperty("number").GetInt32();
        var merge = e.TryGetProperty("mergeCommit", out var m) && m.ValueKind == JsonValueKind.Object ? Str(m, "oid") : null;
        var issues = e.TryGetProperty("closingIssuesReferences", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(i => new HistoryLink("#" + i.GetProperty("number").GetInt32(),
                "provider", "pr-link", "closingIssuesReferences", Str(i, "url"))).ToList()
            : null;
        return new ProviderPr(id, Str(e, "title"), Str(e, "url"), Str(e, "baseRefName"), Str(e, "state") == "MERGED", merge, [], issues,
            issues == null ? "gh returned no closingIssuesReferences" : null);
    }

    static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static string FirstLine(string s) => s.Trim().Split('\n')[0];
}

/// <summary>
/// Azure DevOps Server through ado: pr context for a PR's status, commits and linked work items; commit context for the
/// work items linked to a commit itself. Repository: the repository name the export is about (from the origin URL);
/// a commit context for another repository is not this commit's evidence.
/// </summary>
public sealed class AdoHistory(IAdoClient ado, string? repository = null) : IHistoryProvider
{
    public const string CommitContextSchema = "ado.commit-context/v1";

    public string Name => "azure-devops";
    public string ProviderCommitLinks => "ado commit context, per commit (see commits[].providerLinks)";
    public bool HasCommitLinks => true;

    public ProviderPr PullRequest(int number) => Parse(JsonDocument.Parse(ado.Run("pr", "context", number.ToString())).RootElement);

    /// <summary>ado pr context: {pullRequest, commits, workItems}. lastMergeCommit is used when ado passes it on.</summary>
    public static ProviderPr Parse(JsonElement e)
    {
        var pr = e.GetProperty("pullRequest");
        var target = Str(pr, "targetRefName") is { } t && t.StartsWith("refs/heads/") ? t["refs/heads/".Length..] : Str(pr, "targetRefName");
        var commits = e.TryGetProperty("commits", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(x => Str(x, "commitId")).OfType<string>().ToList() : [];
        var items = e.TryGetProperty("workItems", out var w) && w.ValueKind == JsonValueKind.Array
            ? w.EnumerateArray().Select(x => new HistoryLink($"AB#{x.GetProperty("id").GetInt32()}", "provider", "pr-link", "ado pr context")).ToList()
            : null;
        return new ProviderPr("#" + pr.GetProperty("pullRequestId").GetInt32(), Str(pr, "title"), null, target,
            Str(pr, "status") == "completed", pr.TryGetProperty("lastMergeCommit", out var lm) ? Str(lm, "commitId") : null, commits, items,
            items == null ? "ado pr context returned no workItems" : null);
    }

    string? _unusable;

    /// <summary>
    /// ado commit context &lt;full sha&gt;. Denied (exit 4) stays denied; any other failure is unavailable, never "no
    /// links". Once ado can't be asked at all (missing, not logged in), later commits get that reason without asking.
    /// Thread-safe.
    /// </summary>
    public HistoryCommitLinks CommitLinks(string sha)
    {
        if (Volatile.Read(ref _unusable) is { } why) return new("unavailable", null, why);
        string json;
        try { json = ado.Run("commit", "context", sha); }
        catch (AdoCommandException e) when (e.ExitCode == 4) { return new("denied", null, History.Redact(e.Message)); }
        catch (InvalidOperationException e)
        {
            var problem = History.Redact(e.Message);
            if (e is AdoCommandException { ExitCode: 127 or 3 }) Interlocked.CompareExchange(ref _unusable, problem, null);
            return new("unavailable", null, problem);
        }
        try { return ParseCommitLinks(JsonDocument.Parse(json).RootElement, sha, repository); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return new("unavailable", null, $"ado commit context {Short(sha)}: unreadable answer ({e.GetType().Name})");
        }
    }

    /// <summary>
    /// An ado.commit-context/v1 document as the links of commit sha in repository (null: any). Verified only when the
    /// schema, the full SHA and the repository match and ado says verified; otherwise unavailable with the reason.
    /// </summary>
    public static HistoryCommitLinks ParseCommitLinks(JsonElement e, string sha, string? repository)
    {
        HistoryCommitLinks Unavailable(string why) => new("unavailable", null, $"ado commit context {Short(sha)}: {why}");
        if (Str(e, "schema") is var schema && schema != CommitContextSchema)
            return Unavailable($"unsupported schema {schema ?? "(none)"}, expected {CommitContextSchema}");
        var commit = e.TryGetProperty("commit", out var c) ? Str(c, "commitId") : null;
        if (commit == null || !string.Equals(commit, sha, StringComparison.OrdinalIgnoreCase))
            return Unavailable($"answered for commit {commit ?? "(none)"}");
        var repo = e.TryGetProperty("repository", out var r) ? Str(r, "name") : null;
        if (repository != null && !string.Equals(repo, repository, StringComparison.OrdinalIgnoreCase))
            return Unavailable($"answered for repository {repo ?? "(none)"}, not {repository}");
        var wi = e.TryGetProperty("workItems", out var w) && w.ValueKind == JsonValueKind.Object ? w : default;
        var status = Str(wi, "status");
        if (status == "verified" && wi.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            return new("verified", [.. items.EnumerateArray()
                .Select(x => new HistoryLink($"AB#{x.GetProperty("id").GetInt32()}", "provider", "commit-link",
                    "ado commit context: " + (Str(x, "source") ?? "commit-workitem"), Str(x, "webUrl") ?? Str(x, "url")))
                .DistinctBy(l => l.WorkItem).OrderBy(l => l.WorkItem, StringComparer.Ordinal)], null);
        var warnings = e.TryGetProperty("warnings", out var ws) && ws.ValueKind == JsonValueKind.Array
            ? string.Join("; ", ws.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).OfType<string>()) : "";
        return Unavailable(status == "unavailable" ? (warnings.Length > 0 ? warnings : "ado reports the links unavailable") : $"unknown workItems status {status ?? "(none)"}");
    }

    /// <summary>The repository name of an Azure DevOps remote URL (…/_git/name), or null.</summary>
    public static string? RepositoryName(string? url) =>
        url != null && url.IndexOf("/_git/", StringComparison.Ordinal) is var i and >= 0
            ? Uri.UnescapeDataString(url[(i + 6)..].Split('/', '?', '#')[0]) is { Length: > 0 } n ? n.EndsWith(".git") ? n[..^4] : n : null
            : null;

    static string? Str(JsonElement x, string p) => x.ValueKind == JsonValueKind.Object && x.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static string Short(string sha) => sha[..Math.Min(12, sha.Length)];
}

/// <summary>
/// The historical evidence export (gitwizz history): which commits changed which lines, which pull request
/// each came in with, and which work items the commits and the pull requests name, each with where that came from.
/// Read-only and bounded: a range and a commit limit, never the whole history by default. Deterministic: the same
/// commits and provider answers give the same document. It never claims more than the evidence says: a PR it can't
/// confirm is inferred, one it can't find is unknown, links it can't read are unavailable, not empty.
/// </summary>
public static partial class History
{
    public const string Schema = "gitwizz.history/v2";
    public const int DefaultLimit = 200;
    public const int MaxLimit = 10000;
    /// <summary>At most this many commits are looked up with the provider by default (--commit-lookups); the rest are unknown.</summary>
    public const int DefaultCommitLookups = 500;
    /// <summary>Provider lookups running at once.</summary>
    public const int LookupConcurrency = 4;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The git part of an export: everything that follows from the commits alone, so it can be stored by their ids.
    /// Mainline: the commits on until's first-parent chain (where a squash merge lands).
    /// </summary>
    public record GitHistory(List<HistoryCommit> Commits, bool Truncated, bool Shallow, List<string> Mainline);

    /// <summary>
    /// Exports commits reachable from until and not from since (all of until's history when since is null), newest
    /// first, at most limit of them. The git part is reused from the store when the range, limit and gitwizz build match;
    /// provider answers are never stored, as they change without a commit. commitLookups bounds how many commits (newest
    /// first) are asked for their provider links.
    /// </summary>
    public static HistoryExport Export(Git git, WorkspaceStore store, IHistoryProvider provider, string? since, string until, int limit,
        ProgressBars? progress = null, int commitLookups = DefaultCommitLookups)
    {
        if (limit < 1 || limit > MaxLimit) throw new ArgumentException($"--limit must be between 1 and {MaxLimit}");
        if (commitLookups < 0 || commitLookups > MaxLimit) throw new ArgumentException($"--commit-lookups must be between 0 and {MaxLimit}");
        var untilSha = git.RevParse(until);
        var sinceSha = since != null ? git.RevParse(since) : null;

        progress?.Start("Reading commits");
        var inputs = new SortedDictionary<string, string>
        {
            ["gitwizz"] = Fingerprint.Build, ["kind"] = "history", ["schema"] = Schema, ["until"] = untilSha, ["since"] = sinceSha ?? "(none)",
            ["limit"] = limit.ToString(), ["shallow"] = Shallow(git).ToString(),
        };
        GitHistory history;
        if (store.Reuse<GitHistory>("history", inputs) is { } stored) history = stored.Value;
        else
        {
            history = ReadGit(git, sinceSha, untilSha, limit);
            store.Keep("history", inputs, history);
        }

        progress?.Start("Resolving pull requests");
        var (commits, prs, warnings) = Resolve(git, history.Commits, [.. history.Mainline], provider, progress);
        commits = ProviderLinks(commits, provider, commitLookups, warnings, progress);
        if (history.Shallow) warnings.Insert(0, "shallow repository: commits beyond the shallow boundary are missing; fetch more history for a complete export");
        if (history.Truncated) warnings.Insert(0, $"truncated: the range holds more than {limit} commits; narrow it with --since or raise --limit");

        var local = provider is LocalHistory;
        return new HistoryExport(Schema, provider.Name, RepositoryIdentity(git),
            new HistoryBounds(since, sinceSha, until, untilSha, limit, commits.Count, history.Truncated, history.Shallow),
            new HistorySources(
                "complete: every exported commit's message was read",
                provider.ProviderCommitLinks,
                local ? "commit messages only (no provider)" : $"commit messages, confirmed with {provider.Name}",
                local ? "unknown: no provider" : $"{provider.Name}, per pull request (see workItemsStatus)"),
            commits, prs, warnings);
    }

    public static string Json(HistoryExport e) => JsonSerializer.Serialize(e, JsonOptions);

    /// <summary>
    /// Each commit's provider links: the newest commitLookups commits are asked, LookupConcurrency at a time; the rest
    /// stay unknown. Order and content don't depend on timing.
    /// </summary>
    static List<HistoryCommit> ProviderLinks(List<HistoryCommit> commits, IHistoryProvider provider, int budget, List<string> warnings,
        ProgressBars? progress)
    {
        if (!provider.HasCommitLinks)
            return [.. commits.Select(c => c with { ProviderLinks = HistoryCommitLinks.Unknown(provider.ProviderCommitLinks) })];
        var asked = Math.Min(budget, commits.Count);
        var links = new HistoryCommitLinks[commits.Count];
        for (int i = asked; i < commits.Count; i++)
            links[i] = HistoryCommitLinks.Unknown($"not looked up: over --commit-lookups {budget}");
        progress?.Start($"Reading commit links ({provider.Name})", asked);
        Parallel.For(0, asked, new ParallelOptions { MaxDegreeOfParallelism = LookupConcurrency }, i =>
        {
            links[i] = provider.CommitLinks(commits[i].Sha);
            lock (links) progress?.Advance();
        });
        var counts = links.GroupBy(l => l.Status).ToDictionary(g => g.Key, g => g.Count());
        if (counts.Keys.Any(k => k != "verified"))
            warnings.Add("provider commit links: " + string.Join(", ", new[] { "verified", "unavailable", "denied", "unknown" }
                .Where(counts.ContainsKey).Select(k => $"{counts[k]} {k}")) + " (see commits[].providerLinks)");
        foreach (var p in links.Where(l => l.Status is "unavailable" or "denied").Select(l => l.Problem).OfType<string>().Distinct().Take(3))
            warnings.Add(p);
        return [.. commits.Select((c, i) => c with { ProviderLinks = links[i] })];
    }

    [GeneratedRegex(@"(?<=://)[^/@\s]+@")]
    private static partial Regex UrlUserInfo();

    /// <summary>A message safe to export: user info (user:token@) removed from any URL in it.</summary>
    public static string Redact(string message) => UrlUserInfo().Replace(message, "");

    static bool Shallow(Git git) => git.Try("rev-parse", "--is-shallow-repository").Stdout.Trim() == "true";

    /// <summary>The origin URL without credentials, query or fragment, or null without an origin.</summary>
    public static string? RepositoryIdentity(Git git) =>
        git.Try("remote", "get-url", "origin") is { ExitCode: 0 } r && r.Stdout.Trim() is { Length: > 0 } url ? Sanitize(url) : null;

    /// <summary>A remote URL safe to export: user info (user:token@), query and fragment removed.</summary>
    public static string Sanitize(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "ssh" or "git")
            return new UriBuilder(u) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.GetComponents(
                UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
        return url; // scp-like (git@host:path) or a local path: no secret in it
    }

    // --- The git part ----------------------------------------------------------------------------------------------

    const char Rec = '\x1e', Unit = '\x1f';

    /// <summary>Commits since..until (newest first, topological), their changed files and lines, message links and PR hints.</summary>
    public static GitHistory ReadGit(Git git, string? sinceSha, string untilSha, int limit)
    {
        var range = sinceSha != null ? $"{sinceSha}..{untilSha}" : untilSha;
        var shas = git.Run("rev-list", "--topo-order", $"--max-count={limit + 1}", range).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        var truncated = shas.Count > limit;
        if (truncated) shas = shas[..limit];
        if (shas.Count == 0) return new([], false, Shallow(git), []);
        var mainline = git.Run("rev-list", "--first-parent", $"--max-count={limit}", range).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

        // One git process: per commit a header (sha, parents, date, message) and its -U0 patch against the first parent.
        // Merge commits get no patch (git log shows none without -m): their changes are those of their parents.
        var r = Git.Exec(git.RepoDir, "git", ["-c", "core.quotePath=false", "log", "--no-walk=unsorted", "--stdin", "-p", "-U0", "-M", "-C",
            "--no-color", "--no-ext-diff", "--no-textconv", "--src-prefix=a/", "--dst-prefix=b/",
            $"--format={Rec}%H{Unit}%P{Unit}%cI{Unit}%B{Unit}"], stdin: string.Concat(shas.Select(s => s + "\n")));
        if (r.ExitCode != 0) throw new InvalidOperationException($"git log failed: {r.Stderr.Trim()}");

        var commits = new List<HistoryCommit>();
        foreach (var record in r.Stdout.Split(Rec, StringSplitOptions.RemoveEmptyEntries))
        {
            var f = record.Split(Unit, 5);
            if (f.Length < 5) continue;
            var parents = f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            var message = f[3].TrimEnd();
            commits.Add(new HistoryCommit(f[0], parents, message.Split('\n')[0], IsoDate(f[2]), parents.Count > 1 ? "merge" : "commit",
                parents.Count > 1 ? [] : ParsePatch(f[4]), MessageLinks(message), HistoryCommitLinks.Unknown("not looked up"),
                new HistoryPrRef(null, "unknown", [], [])));
        }
        var bySha = commits.ToDictionary(c => c.Sha);
        return new([.. shas.Where(bySha.ContainsKey).Select(s => bySha[s])], truncated, Shallow(git), mainline);
    }

    /// <summary>
    /// A git ISO 8601 date in one form, whatever the git version: newer git writes UTC as "Z", older as "+00:00".
    /// Always yyyy-MM-ddTHH:mm:ss+HH:mm (or -HH:mm), so the same commits give the same document.
    /// </summary>
    public static string IsoDate(string date) =>
        DateTimeOffset.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture) : date;

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeader();

    /// <summary>Files and line ranges of a -U0 patch (git diff with a/ and b/ prefixes).</summary>
    public static List<HistoryFile> ParsePatch(string patch)
    {
        var files = new List<HistoryFile>();
        foreach (var block in Regex.Split(patch, @"(?m)^(?=diff --git )").Where(b => b.StartsWith("diff --git ")))
        {
            string? oldPath = null, newPath = null, renameFrom = null, renameTo = null;
            string status = "modified";
            int? similarity = null;
            bool binary = false;
            var ranges = new List<HistoryRange>();
            foreach (var line in block.Split('\n'))
            {
                // After the first hunk header only hunk headers count: a removed "-- x" line reads "--- x".
                if (ranges.Count > 0 && !line.StartsWith("@@")) continue;
                if (line.StartsWith("new file mode")) status = "added";
                else if (line.StartsWith("deleted file mode")) status = "deleted";
                else if (line.StartsWith("rename from ")) (status, renameFrom) = ("renamed", Unquote(line[12..]));
                else if (line.StartsWith("rename to ")) renameTo = Unquote(line[10..]);
                else if (line.StartsWith("copy from ")) (status, renameFrom) = ("copied", Unquote(line[10..]));
                else if (line.StartsWith("copy to ")) renameTo = Unquote(line[8..]);
                else if (line.StartsWith("similarity index ") && int.TryParse(line[17..].TrimEnd('%'), out var s)) similarity = s;
                else if (line.StartsWith("Binary files ")) binary = true;
                else if (line.StartsWith("--- ")) oldPath = PatchPath(line[4..]);
                else if (line.StartsWith("+++ ")) newPath = PatchPath(line[4..]);
                else if (HunkHeader().Match(line) is { Success: true } m)
                    ranges.Add(new HistoryRange(int.Parse(m.Groups[1].Value), m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1,
                        int.Parse(m.Groups[3].Value), m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 1));
            }
            var header = HeaderPaths(block.Split('\n')[0]);
            var path = renameTo ?? newPath ?? oldPath ?? header.New;
            var old = renameFrom ?? (status == "modified" && oldPath != null && oldPath != path ? oldPath : null);
            files.Add(new HistoryFile(path, old, status, similarity, ranges.Count > 0 && !binary ? "lines" : "file", binary ? [] : ranges));
        }
        return files;
    }

    /// <summary>A path from a ---/+++ line: null for /dev/null, else without its a/ or b/ prefix (and git's trailing tab).</summary>
    static string? PatchPath(string s)
    {
        s = s.TrimEnd('\t');
        if (s == "/dev/null") return null;
        s = Unquote(s);
        return s.Length > 2 && s[1] == '/' && s[0] is 'a' or 'b' ? s[2..] : s;
    }

    /// <summary>Paths from "diff --git a/x b/x" when there are no ---/+++ lines (mode change, empty file): both sides are equal then.</summary>
    static (string Old, string New) HeaderPaths(string header)
    {
        var rest = header["diff --git ".Length..];
        if (rest.StartsWith('"'))
        {
            var end = QuotedEnd(rest);
            var a = Unquote(rest[..(end + 1)]);
            var b = Unquote(rest[(end + 2)..]);
            return (a.Length > 2 ? a[2..] : a, b.Length > 2 ? b[2..] : b);
        }
        // Unquoted and equal on both sides: "a/<p> b/<p>", so the split is in the middle.
        var half = (rest.Length - 1) / 2;
        var p = rest[..half];
        return (p.Length > 2 ? p[2..] : p, p.Length > 2 ? p[2..] : p);
    }

    static int QuotedEnd(string s)
    {
        for (int i = 1; i < s.Length; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == '"') return i;
        }
        return s.Length - 1;
    }

    /// <summary>A git C-style quoted path ("a\tb", octal escapes for bytes) as text; unquoted text unchanged.</summary>
    public static string Unquote(string s)
    {
        if (s.Length < 2 || s[0] != '"' || s[^1] != '"') return s;
        var bytes = new List<byte>();
        for (int i = 1; i < s.Length - 1; i++)
        {
            if (s[i] != '\\') { bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString())); continue; }
            var c = s[++i];
            if (c is >= '0' and <= '7' && i + 2 < s.Length - 1)
            {
                bytes.Add(Convert.ToByte(s.Substring(i, 3), 8));
                i += 2;
                continue;
            }
            bytes.Add((byte)(c switch { 'n' => '\n', 't' => '\t', 'r' => '\r', 'a' => '\a', 'b' => '\b', 'f' => '\f', 'v' => '\v', _ => c }));
        }
        return Encoding.UTF8.GetString([.. bytes]);
    }

    // --- Links named in commit messages ----------------------------------------------------------------------------------

    [GeneratedRegex(@"\bAB#(\d+)\b")]
    private static partial Regex AbReference();

    [GeneratedRegex(@"(?i)\b(close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s*:?\s+#(\d+)\b")]
    private static partial Regex ClosingKeyword();

    [GeneratedRegex(@"(?<![\w#])#(\d+)\b")]
    private static partial Regex Mention();

    /// <summary>
    /// Work items a commit message names: AB#123 (Azure Boards), issues closed with a GitHub keyword ("fixes #12"), and
    /// plain #123 mentions (kept as "#123", kind mention: Azure DevOps reads them as work items, GitHub doesn't link them).
    /// A pull request marker in the subject ("Merge pull request #12", "(#12)") is not a mention. One link per work item
    /// and kind, ordered.
    /// </summary>
    public static List<HistoryLink> MessageLinks(string message)
    {
        var subject = message.Split('\n')[0];
        var rest = PrMarker(subject) is { } pr ? message.Replace(pr.Marker, "") : message;
        return AbReference().Matches(message).Select(m => new HistoryLink("AB#" + m.Groups[1].Value, "commit-message", "ab", m.Value))
            .Concat(ClosingKeyword().Matches(message).Select(m => new HistoryLink("#" + m.Groups[2].Value, "commit-message", "closes", m.Value)))
            .Concat(Mention().Matches(AbReference().Replace(rest, "")).Select(m => new HistoryLink("#" + m.Groups[1].Value, "commit-message", "mention", m.Value)))
            .DistinctBy(l => (l.WorkItem, l.Kind)).OrderBy(l => l.WorkItem, StringComparer.Ordinal).ThenBy(l => l.Kind, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The links a provider reads from a message: Azure DevOps turns #123 into work item AB#123; GitHub and local
    /// histories drop plain mentions (they may name an issue or a pull request: no link).
    /// </summary>
    public static List<HistoryLink> ForProvider(List<HistoryLink> links, IHistoryProvider provider) =>
        provider is AdoHistory
            ? links.Select(l => l.Kind == "mention" ? l with { WorkItem = "AB" + l.WorkItem } : l).DistinctBy(l => (l.WorkItem, l.Kind))
                .OrderBy(l => l.WorkItem, StringComparer.Ordinal).ThenBy(l => l.Kind, StringComparer.Ordinal).ToList()
            : links.Where(l => l.Kind != "mention").ToList();

    [GeneratedRegex(@"^Merged PR (\d+):")]
    private static partial Regex AdoMerge();

    [GeneratedRegex(@"^Merge pull request #(\d+)\b")]
    private static partial Regex GitHubMerge();

    [GeneratedRegex(@"\(#(\d+)\)$")]
    private static partial Regex GitHubSquash();

    /// <summary>The pull request a commit subject names as the one it completes, and how (merge-message or squash-subject).</summary>
    public static (string Id, string Basis, string Marker)? PrMarker(string subject) =>
        AdoMerge().Match(subject) is { Success: true } a ? ("#" + a.Groups[1].Value, "merge-message", a.Value)
        : GitHubMerge().Match(subject) is { Success: true } g ? ("#" + g.Groups[1].Value, "merge-message", g.Value)
        : GitHubSquash().Match(subject) is { Success: true } s ? ("#" + s.Groups[1].Value, "squash-subject", s.Value)
        : null;

    // --- Pull requests ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Assigns each commit its pull request and builds the PR list. A merge commit's PR also owns the commits its merge
    /// brought in (first parent..second parent). A squash subject ("title (#12)") counts only on the mainline and outside
    /// every merged branch: inside one, "(#12)" names an issue, not the PR it came with. A commit claimed by two PRs is
    /// ambiguous. The provider confirms each PR; without confirmation the attribution stays inferred.
    /// </summary>
    static (List<HistoryCommit>, List<HistoryPr>, List<string>) Resolve(Git git, List<HistoryCommit> commits, HashSet<string> mainline,
        IHistoryProvider provider, ProgressBars? progress)
    {
        var warnings = new List<string>();
        var claims = new Dictionary<string, List<(string Pr, string Basis)>>();
        void Claim(string sha, string pr, string basis) => (claims.TryGetValue(sha, out var l) ? l : claims[sha] = []).Add((pr, basis));

        var found = new SortedDictionary<int, (HistoryCommit Commit, string Basis, List<string> Members)>();
        bool Add(HistoryCommit c, (string Id, string Basis, string Marker) marker, List<string> members)
        {
            var number = int.Parse(marker.Id[1..]);
            if (found.TryGetValue(number, out var seen))
            {
                warnings.Add($"{marker.Id} is named by more than one commit ({Short(seen.Commit.Sha)}, {Short(c.Sha)}); the newest is used");
                return false;
            }
            found[number] = (c, marker.Basis, members);
            Claim(c.Sha, marker.Id, marker.Basis);
            foreach (var m in members) Claim(m, marker.Id, "merged-branch");
            return true;
        }
        foreach (var c in commits.Where(c => c.Kind == "merge" && c.Parents.Count == 2))
            if (PrMarker(c.Subject) is { Basis: "merge-message" } marker)
                Add(c, marker, git.Try("rev-list", "--topo-order", $"{c.Parents[0]}..{c.Parents[1]}") is { ExitCode: 0 } r
                    ? r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList() : []);
        foreach (var c in commits.Where(c => c.Kind == "commit" && !claims.ContainsKey(c.Sha)))
            if (PrMarker(c.Subject) is { } marker && (marker.Basis == "merge-message" || mainline.Contains(c.Sha)))
                Add(c, marker, []); // Azure DevOps squash merges keep "Merged PR 12:"; GitHub's end in "(#12)"

        var confirmed = new Dictionary<string, ProviderPr>();
        var prs = new List<HistoryPr>();
        progress?.Start($"Resolving {found.Count} pull requests ({provider.Name})", found.Count);
        foreach (var (number, (commit, basis, members)) in found)
        {
            var id = "#" + number;
            ProviderPr? p = null;
            string? problem = null;
            var denied = false;
            try { p = provider.PullRequest(number); }
            catch (Exception e) when (e is InvalidOperationException or JsonException or KeyNotFoundException)
            {
                problem = Redact(e.Message);
                denied = e is AdoCommandException { ExitCode: 4 };
            }
            progress?.Advance();

            var strategy = commit.Kind == "merge" ? "merge" : "squash"; // one parent and a PR marker: GitHub "(#12)" or Azure DevOps "Merged PR 12:"
            var status = provider is LocalHistory ? "local" : p is { Completed: true } ? "confirmed" : "unconfirmed";
            if (p is { Completed: false }) problem = $"{provider.Name} doesn't report {id} as completed";
            if (p is { MergeCommit: { } mc } && mc != commit.Sha)
            {
                status = "unconfirmed";
                problem = $"{provider.Name} reports merge commit {Short(mc)} for {id}, not {Short(commit.Sha)}";
            }
            // ado names the PR's own commits: the merged branch's head must be one of them for the merge to be this PR's.
            if (status == "confirmed" && p!.MergeCommit == null && commit.Kind == "merge" && p.Commits.Count > 0 && !p.Commits.Contains(commit.Parents[1]))
            {
                status = "unconfirmed";
                problem = $"{provider.Name}'s commits for {id} don't include the merged head {Short(commit.Parents[1])}";
            }
            if (status == "confirmed") confirmed[id] = p!;
            var owned = members.Prepend(commit.Sha).ToList();
            var workItems = p?.WorkItems is { } wi && status == "confirmed" ? wi.OrderBy(l => l.WorkItem, StringComparer.Ordinal).ToList() : [];
            var wiStatus = provider is LocalHistory ? "unknown" : p?.WorkItems != null && status == "confirmed" ? "verified" : denied ? "denied" : "unavailable";
            prs.Add(new HistoryPr(id, p?.Title, p?.Url, p?.Target, status, strategy, commit.Sha, owned, workItems, wiStatus,
                status == "local" ? null : problem ?? (wiStatus != "verified" ? p?.Problem : null)));
            if (problem != null && status != "local") warnings.Add($"{id}: {problem}");
        }

        var resolved = commits.Select(c =>
        {
            var links = ForProvider(c.WorkItems, provider);
            if (!claims.TryGetValue(c.Sha, out var cl)) return c with { WorkItems = links };
            var ids = cl.Select(x => x.Pr).Distinct().Order(StringComparer.Ordinal).ToList();
            if (ids.Count > 1)
                return c with { WorkItems = links, PullRequest = new HistoryPrRef(null, "ambiguous", [.. cl.Select(x => x.Basis).Distinct().Order(StringComparer.Ordinal)], ids) };
            var bases = cl.Select(x => x.Basis).Distinct().Order(StringComparer.Ordinal).ToList();
            var exact = confirmed.ContainsKey(ids[0]);
            if (exact) bases.Add("provider");
            return c with { WorkItems = links, PullRequest = new HistoryPrRef(ids[0], exact ? "exact" : "inferred", bases, []) };
        }).ToList();
        return (resolved, prs, warnings);
    }

    static string Short(string sha) => sha[..Math.Min(12, sha.Length)];
}
