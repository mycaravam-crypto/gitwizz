using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace Gitwizz;

/// <summary>
/// System context from docwizz, from .gitwizz.yml context: docwizz:. Advisory by default: Required makes a missing or
/// failed analysis block (verdict undetermined); RequireArchitectureForChangedSymbols makes a changed symbol outside every
/// configured architecture layer block (verdict blocked). Command is the docwizz command line.
/// </summary>
public record DocwizzPolicy
{
    public bool Enabled { get; init; } = true;
    public bool Required { get; init; }
    public bool RequireArchitectureForChangedSymbols { get; init; }
    public string Command { get; init; } = "docwizz";
    public int Timeout { get; init; } = 600; // seconds per docwizz run

    /// <summary>The policy asks for something the system-context gate has to enforce.</summary>
    [YamlIgnore] public bool Enforced => Enabled && (Required || RequireArchitectureForChangedSymbols);
}

/// <summary>.gitwizz.yml context: which system context the review uses, and whether its absence blocks.</summary>
public record ContextPolicy
{
    public DocwizzPolicy Docwizz { get; init; } = new();
}

/// <summary>
/// The docwizz commands gitwizz consumes, as data: never its console report. Scan writes the code model (nodes and
/// edges) of a directory to a file; Diff prints the change between two commits as JSON (diff --format json).
/// </summary>
public interface IDocwizz
{
    /// <summary>docwizz --version, or null when docwizz can't be run (not installed).</summary>
    string? Version(string dir);
    GitResult Scan(string dir, string modelFile);
    GitResult Diff(string dir, string baseRef, string headRef);
}

/// <summary>docwizz as a process. Command may carry leading arguments ("dotnet tools/DocWizz.dll").</summary>
public sealed class DocwizzCli(DocwizzPolicy policy) : IDocwizz
{
    string? _version;

    GitResult Run(string dir, params string[] args)
    {
        var words = policy.Command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return new GitResult(127, "", "context.docwizz.command is empty");
        return Git.Exec(dir, words[0], [.. words[1..], .. args], timeout: TimeSpan.FromSeconds(policy.Timeout));
    }

    public string? Version(string dir) => _version ??= Run(dir, "--version") is { ExitCode: 0 } r && r.Stdout.Trim() is { Length: > 0 } v ? v.Split('\n')[0].Trim() : null;
    public GitResult Scan(string dir, string modelFile) => Run(dir, "scan", dir, modelFile);
    public GitResult Diff(string dir, string baseRef, string headRef) => Run(dir, "diff", dir, baseRef, headRef, "--format", "json");
}

// --- The docwizz contract gitwizz relies on --------------------------------------------------------------------------

/// <summary>A code element from docwizz scan. Documented: it carries a doc comment (its text is not kept).</summary>
public record DocwizzNode(string Id, string Kind, string Name, string File, int Line, int? EndLine, List<string> Tags, string? Route, bool Documented, string? Hash);

/// <summary>A relation from docwizz scan; Label carries detail, e.g. detected or inferred on a connects edge.</summary>
public record DocwizzEdge(string From, string To, string Kind, string? Label);

/// <summary>An architecture layer from docwizz.yaml: path globs, first match wins.</summary>
public record DocwizzLayer(string Name, List<string> Globs);

/// <summary>The repository facts of one commit: docwizz's code model plus the layers it was configured with.</summary>
public record DocwizzModel(string Commit, List<DocwizzNode> Nodes, List<DocwizzEdge> Edges, List<DocwizzLayer> Layers);

/// <summary>A symbol docwizz diff reports. Change: added, changed or removed.</summary>
public record DocwizzSymbol(string Id, string Kind, string Location, string Change);
public record DocwizzStale(string Id, string Location, List<string> Changes);
public record DocwizzGap(string Id, string Location, string Level, bool Critical, List<string> Missing);
public record DocwizzFlag(string Id, string Rule, string Basis, string Detail);
public record DocwizzViolation(string Rule, string FromLayer, string ToLayer, string FromFile, string To, string Severity);
public record DocwizzTestLink(string Symbol, string Test, string Link, string? Via);

/// <summary>What docwizz diff says about a change: symbols, possibly stale docs, new gaps and flags, violations, tests.</summary>
public record DocwizzDiff(List<DocwizzSymbol> Symbols, List<DocwizzStale> Stale, List<string> Pages, List<DocwizzGap> Gaps, List<DocwizzFlag> Flags,
    List<DocwizzViolation> Violations, List<string> Decisions, List<DocwizzTestLink> Tests, List<string> Unlinked);

// --- The system context of a pull request ----------------------------------------------------------------------------

/// <summary>
/// One fact about the system around the change, with provenance. Origin: detected (docwizz found it in the code),
/// inferred (docwizz or gitwizz derived it; it may be wrong), human-authored, ai-drafted or generated (documents).
/// Hop: 1 for direct relations of a changed symbol, 2 for the one extra hop taken when risk or a requirement justifies it.
/// </summary>
public record ContextFact(string Id, string Kind, string Subject, string Relation, string Object, string Origin, string? File, int? Line,
    int Hop = 1, List<string>? Via = null, string Source = "docwizz");

/// <summary>
/// A changed symbol and where it belongs. Layer is null when no configured layer owns its file. Was: the id before the
/// change when its signature changed (docwizz reports that as one symbol removed and one added).
/// </summary>
public record ContextSymbol(string Id, string Name, string Kind, string Change, string Location, string Module, string? Layer, string? Was = null);

/// <summary>
/// Freshness of a relevant document. Status: current (docwizz found it consistent with the analyzed code), stale (the
/// code changed after it, or it contradicts the code), missing (needed, none exists), unknown (can't be established).
/// </summary>
public record DocumentState(string Id, string Document, string Status, string Reason, string? AffectedSymbol, string Origin, string? Conflict,
    string? Fingerprint, string AnalyzedCommit, string Source = "docwizz");

/// <summary>Test code docwizz links to a changed symbol (it uses it; this is not coverage). Link: direct or indirect (Via).</summary>
public record ContextTest(string Id, string Test, string Symbol, string Link, string Origin, string? Via);

/// <summary>An acceptance criterion and the context items whose names it mentions.</summary>
public record CriterionContext(string Criterion, string Text, List<string> Related);

/// <summary>
/// The system context of a pull request: the impacted subgraph of the target's repository facts, starting from the
/// changed symbols, with documentation freshness and linked tests. Status: available, unavailable (docwizz isn't
/// installed or is disabled) or failed (it ran and failed). Freshness: current, or stale when docwizz failed now and
/// the facts come from an earlier analysis. Quality: GOOD, PARTIAL, MISSING or STALE, with the reasons.
/// </summary>
public record SystemContext
{
    public string Status { get; init; } = "available";
    public string? Problem { get; init; }
    public string? Tool { get; init; }
    public string Freshness { get; init; } = "current";
    public string? FreshnessReason { get; init; }
    public string AnalyzedCommit { get; init; } = "";
    public string Commit { get; init; } = "";
    public bool Reused { get; init; }          // repository facts came from the workspace
    public bool DiffAvailable { get; init; } = true;
    public bool LayersConfigured { get; init; }
    public string Quality { get; init; } = "MISSING";
    public List<string> Reasons { get; init; } = [];
    public List<ContextSymbol> Symbols { get; init; } = [];
    public List<ContextFact> Facts { get; init; } = [];
    public List<DocumentState> Docs { get; init; } = [];
    public List<ContextTest> Tests { get; init; } = [];
    public List<string> Unlinked { get; init; } = [];
    public List<CriterionContext> Criteria { get; init; } = [];
    public List<string> UnanalyzedFiles { get; init; } = [];

    public bool Usable => Status == "available";
    public IEnumerable<ContextSymbol> Unowned => LayersConfigured ? Symbols.Where(s => s.Layer == null && s.Change != "removed") : [];
    public IEnumerable<ContextFact> Of(params string[] kinds) => Facts.Where(f => kinds.Contains(f.Kind));
}

/// <summary>
/// Builds the system context from docwizz. docwizz does the analysis; gitwizz only selects what is relevant to the
/// change, keeps the provenance of every fact, and decides how much it is trusted. The target's model is reused from
/// the workspace across pull requests while the target's tree and the docwizz version are the same; each PR's
/// docwizz diff is reused while the target and the PR's commit are the same.
/// </summary>
public static class SystemContexts
{
    public const string Schema = "gitwizz.system-context/v1";
    public const string ModelPath = "repo/docwizz/model.json";

    /// <summary>Test seam: replaces the docwizz runner. Per thread.</summary>
    [ThreadStatic] public static Func<DocwizzPolicy, IDocwizz>? Override;

    /// <summary>The docwizz runner for a policy.</summary>
    public static IDocwizz Tool(DocwizzPolicy policy) => Override?.Invoke(policy) ?? new DocwizzCli(policy);

    /// <summary>Source languages docwizz scans: a changed file of these kinds without any symbol isn't analyzed.</summary>
    static readonly string[] Analyzed = [".cs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".vue", ".java", ".sql"];

    static readonly string[] CallKinds = ["calls", "creates", "renders", "routes-to", "http"];
    static readonly string[] TypeKinds = ["class", "record", "struct", "interface", "type", "enum", "component", "store"];
    const int MaxPerRelation = 8;   // callers, accessors or subscribers kept per symbol
    const int MaxFlowDepth = 3;     // caller levels searched for an endpoint that reaches a changed symbol

    /// <summary>The context of the PR in ctx, built once per evaluation and shared by the guide, the gate and the evidence.</summary>
    public static SystemContext For(GateContext ctx, bool refresh = false)
    {
        if (ctx.System != null && !refresh) return ctx.System;
        return ctx.System = Build(ctx.Git, ctx.Store, ctx.Provider, ctx.Pr, ctx.Config, ctx.TargetSha, ctx.MergedState ?? ctx.Pr.HeadSha,
            Gates.Risk(ctx.Pr, ctx.Config).Level, refresh);
    }

    /// <summary>
    /// The system context of pr at commit (merged state or head) against targetSha. Never throws for a docwizz problem:
    /// unavailable and failed are states of the result. refresh: run docwizz again even when the workspace holds a result.
    /// </summary>
    public static SystemContext Build(Git git, WorkspaceStore store, string provider, PullRequest pr, RepoConfig config, string targetSha, string commit,
        string risk, bool refresh = false)
    {
        var policy = config.Context.Docwizz;
        if (!policy.Enabled) return Missing("unavailable", "disabled by context.docwizz.enabled: false", null, targetSha, commit);
        var tool = Tool(policy);
        var version = tool.Version(git.RepoDir);
        if (version == null) return Missing("unavailable", $"docwizz is not available ('{policy.Command}' could not be run)", null, targetSha, commit);

        Worktree? worktree = null;
        string TargetDir() => (worktree ??= new Worktree(git, targetSha)).Dir;
        try
        {
            var (model, modelState, problem) = RepositoryModel(git, store, tool, version, targetSha, TargetDir, refresh);
            if (model == null) return Missing("failed", problem!, version, targetSha, commit);
            // Stale facts mean docwizz fails here: the diff would fail the same way, so the changed lines stand in for it.
            var (diff, diffProblem) = problem == null ? PrDiff(git, store, tool, provider, pr, version, targetSha, commit, TargetDir, refresh) : (null, null);
            var sc = Select(git, pr, config, model, diff, risk, targetSha, commit) with
            {
                Tool = version,
                Reused = modelState.Current && !refresh,
                Freshness = problem == null ? "current" : "stale",
                FreshnessReason = problem == null ? null : $"docwizz failed for {Short(targetSha)} ({problem}); facts are from {Short(model.Commit)}",
                Problem = problem ?? diffProblem,
            };
            return sc with { Quality = Quality(sc, out var reasons), Reasons = reasons };
        }
        finally
        {
            worktree?.Dispose();
        }
    }

    static SystemContext Missing(string status, string problem, string? tool, string target, string commit)
    {
        var sc = new SystemContext { Status = status, Problem = problem, Tool = tool, AnalyzedCommit = target, Commit = commit, DiffAvailable = false };
        return sc with { Quality = Quality(sc, out var reasons), Reasons = reasons };
    }

    static string Short(string sha) => sha[..Math.Min(12, sha.Length)];

    static string Tree(Git git, string commit) => git.Run("rev-parse", $"{commit}^{{tree}}").Trim();

    // --- Repository facts and the PR overlay, through the workspace -------------------------------------------------------

    static SortedDictionary<string, string> ModelInputs(Git git, string version, string targetSha) =>
        new() { ["gitwizz"] = Fingerprint.Build, ["kind"] = "docwizz-model", ["tree"] = Tree(git, targetSha), ["docwizz"] = version };

    /// <summary>State of the stored repository facts for the target, without running anything but docwizz --version.</summary>
    public static ArtifactState ModelState(Git git, WorkspaceStore store, RepoConfig config, string targetSha)
    {
        if (!store.Enabled) return ArtifactState.Off;
        if (!config.Context.Docwizz.Enabled || Tool(config.Context.Docwizz).Version(git.RepoDir) is not { } version) return ArtifactState.Off;
        return WorkspaceStore.Check(store.Read<DocwizzModel>(ModelPath), ModelInputs(git, version, targetSha));
    }

    /// <summary>Brings the stored repository facts up to date for the target (gitwizz refresh). Returns the problem, if docwizz failed.</summary>
    public static string? RefreshModel(Git git, WorkspaceStore store, RepoConfig config, string targetSha)
    {
        var policy = config.Context.Docwizz;
        if (!policy.Enabled) return null;
        var tool = Tool(policy);
        if (tool.Version(git.RepoDir) is not { } version) return "docwizz is not available";
        Worktree? worktree = null;
        try { return RepositoryModel(git, store, tool, version, targetSha, () => (worktree ??= new Worktree(git, targetSha)).Dir, true).Problem; }
        finally { worktree?.Dispose(); }
    }

    /// <summary>
    /// The target's model: reused when its inputs match, else scanned and stored. When docwizz fails and an earlier
    /// model is stored, that one is returned with the problem (the facts are stale); without one, null.
    /// </summary>
    static (DocwizzModel? Model, ArtifactState State, string? Problem) RepositoryModel(Git git, WorkspaceStore store, IDocwizz tool, string version,
        string targetSha, Func<string> dir, bool refresh)
    {
        var inputs = ModelInputs(git, version, targetSha);
        var stored = store.Read<DocwizzModel>(ModelPath);
        var state = WorkspaceStore.Check(stored, inputs);
        if (state.Current && !refresh) return (stored!.Value, state, null);
        try
        {
            var model = Scan(tool, dir(), targetSha);
            store.Write(ModelPath, inputs, model);
            return (model, state, null);
        }
        catch (InvalidOperationException e)
        {
            return stored != null ? (stored.Value, state, e.Message) : (null, state, e.Message);
        }
    }

    static DocwizzModel Scan(IDocwizz tool, string dir, string commit)
    {
        var file = Path.Combine(Path.GetTempPath(), $"gitwizz-docwizz-{Guid.NewGuid():N}.json");
        try
        {
            var r = tool.Scan(dir, file);
            if (r.TimedOut) throw new InvalidOperationException("docwizz scan timed out");
            if (r.ExitCode != 0 || !File.Exists(file)) throw new InvalidOperationException($"docwizz scan failed (exit code {r.ExitCode}): {FirstLine(r.Stderr)}");
            var config = Path.Combine(dir, "docwizz.yaml");
            return ParseModel(File.ReadAllText(file), commit, File.Exists(config) ? Layers(File.ReadAllText(config)) : []);
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    static string FirstLine(string text) => text.Trim().Split('\n')[0].Trim() is { Length: > 0 } l ? l : "no output";

    /// <summary>The PR's docwizz diff against the target, reused while target and commit are the same.</summary>
    static (DocwizzDiff? Diff, string? Problem) PrDiff(Git git, WorkspaceStore store, IDocwizz tool, string provider, PullRequest pr, string version,
        string targetSha, string commit, Func<string> dir, bool refresh)
    {
        var inputs = new SortedDictionary<string, string>
        {
            ["gitwizz"] = Fingerprint.Build, ["kind"] = "docwizz-diff", ["target"] = Tree(git, targetSha), ["commit"] = Tree(git, commit), ["docwizz"] = version,
        };
        var path = WorkspaceStore.PrPath(provider, pr.Id, "docwizz-diff.json");
        if (!refresh && store.Read<DocwizzDiff>(path) is { } stored && WorkspaceStore.Check(stored, inputs).Current) return (stored.Value, null);
        var r = tool.Diff(dir(), targetSha, commit);
        if (r.TimedOut) return (null, "docwizz diff timed out");
        if (r.ExitCode != 0) return (null, $"docwizz diff failed (exit code {r.ExitCode}): {FirstLine(r.Stderr)}");
        try
        {
            var diff = ParseDiff(r.Stdout);
            store.Write(path, inputs, diff);
            return (diff, null);
        }
        catch (InvalidOperationException e) { return (null, e.Message); }
    }

    // --- Parsing ---------------------------------------------------------------------------------------------------------

    static string Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    static string? StrOrNull(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int? Int(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    static IEnumerable<JsonElement> Items(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
    static List<string> Strings(JsonElement e, string key) => [.. Items(e, key).Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)];
    static JsonElement Prop(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : default;

    /// <summary>The model from docwizz scan's JSON (nodes, edges). Throws InvalidOperationException when it isn't one.</summary>
    public static DocwizzModel ParseModel(string json, string commit, List<DocwizzLayer> layers)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("nodes", out _) || !root.TryGetProperty("edges", out _))
                throw new InvalidOperationException("docwizz scan output has no nodes and edges: unsupported docwizz version?");
            var nodes = Items(root, "nodes").Where(n => Str(n, "id") != "").Select(n => new DocwizzNode(Str(n, "id"), Str(n, "kind"), Str(n, "name"),
                Str(n, "file").Replace('\\', '/'), Int(n, "line") ?? 0, Int(n, "endLine"), Strings(n, "tags"), StrOrNull(n, "route"),
                StrOrNull(n, "doc") is { Length: > 0 }, StrOrNull(n, "hash"))).ToList();
            var edges = Items(root, "edges").Select(e => new DocwizzEdge(Str(e, "from"), Str(e, "to"), Str(e, "kind"), StrOrNull(e, "label")))
                .Where(e => e.From != "" && e.To != "").ToList();
            return new DocwizzModel(commit, nodes, edges, layers);
        }
        catch (JsonException e) { throw new InvalidOperationException($"docwizz scan output is not JSON ({e.Message})"); }
    }

    /// <summary>docwizz diff --format json. Throws InvalidOperationException when it isn't one.</summary>
    public static DocwizzDiff ParseDiff(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("changed", out _))
                throw new InvalidOperationException("docwizz diff output has no changed symbols: unsupported docwizz version?");
            var symbols = new[] { "added", "changed", "removed" }.SelectMany(change => Items(root, change)
                .Select(s => new DocwizzSymbol(Str(s, "id"), Str(s, "kind"), Str(s, "location"), change))).Where(s => s.Id != "").ToList();
            var introduced = Prop(root, "introduced");
            var tests = Prop(root, "tests");
            return new DocwizzDiff(
                symbols,
                [.. Items(root, "stale").Select(s => new DocwizzStale(Str(s, "id"), Str(s, "location"), Strings(s, "changes")))],
                Strings(root, "pages"),
                [.. Items(introduced, "gaps").Select(g => new DocwizzGap(Str(g, "id"), Str(g, "location"), Str(g, "level"),
                    g.TryGetProperty("critical", out var c) && c.ValueKind == JsonValueKind.True, Strings(g, "missing")))],
                [.. Items(introduced, "flags").Select(f => new DocwizzFlag(Str(f, "id"), Str(f, "rule"), Str(f, "basis"), Str(f, "detail")))],
                [.. Items(introduced, "violations").Select(v => new DocwizzViolation(Str(v, "rule"), Str(v, "fromLayer"), Str(v, "toLayer"), Str(v, "fromFile"),
                    Str(v, "to"), Str(v, "severity")))],
                Strings(root, "decisions"),
                [.. Items(tests, "linked").SelectMany(l => Items(l, "tests").Select(t => new DocwizzTestLink(Str(l, "id"), Str(t, "test"),
                    Str(t, "link") is { Length: > 0 } k ? k : "direct", StrOrNull(t, "via"))))],
                [.. Items(tests, "unlinked").Select(u => Str(u, "id")).Where(id => id != "")]);
        }
        catch (JsonException e) { throw new InvalidOperationException($"docwizz diff output is not JSON ({e.Message})"); }
    }

    sealed class DocwizzConfig { public DocwizzArchitecture? Architecture { get; set; } }
    sealed class DocwizzArchitecture { public Dictionary<string, List<string>>? Layers { get; set; } }

    /// <summary>architecture.layers from a docwizz.yaml, in file order; none when absent or unreadable.</summary>
    public static List<DocwizzLayer> Layers(string yaml)
    {
        try
        {
            var c = new DeserializerBuilder().IgnoreUnmatchedProperties()
                .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance).Build().Deserialize<DocwizzConfig?>(yaml);
            return [.. (c?.Architecture?.Layers ?? []).Where(l => l.Value is { Count: > 0 }).Select(l => new DocwizzLayer(l.Key, l.Value))];
        }
        catch (YamlDotNet.Core.YamlException) { return []; }
    }

    // --- Selection: the impacted subgraph -------------------------------------------------------------------------------

    /// <summary>The folder a file is in, as docwizz names modules.</summary>
    public static string Module(string file) => Path.GetDirectoryName(file)?.Replace('\\', '/') is { Length: > 0 } d ? d : ".";

    /// <summary>The first layer whose glob matches the file, as docwizz assigns them.</summary>
    public static string? Layer(List<DocwizzLayer> layers, string file) =>
        layers.FirstOrDefault(l => l.Globs.Any(g => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(g, file)))?.Name;

    static (string File, int Line) Location(string location)
    {
        var m = Regex.Match(location, @"^(.*):(\d+)(-\d+)?$");
        return m.Success ? (m.Groups[1].Value, int.Parse(m.Groups[2].Value)) : (location, 0);
    }

    /// <summary>A readable name for a docwizz id: Type.Member without namespace or parameters.</summary>
    public static string Display(string id, IReadOnlyDictionary<string, DocwizzNode>? nodes = null)
    {
        if (nodes != null && nodes.TryGetValue(id, out var n) && n.Kind is "external" or "endpoint" or "table" or "config" or "package") return n.Name;
        var s = id[(id.IndexOf(':') + 1)..];
        var paren = s.IndexOf('(');
        if (paren >= 0) s = s[..paren];
        var parts = s.Split('.');
        return parts.Length >= 2 ? $"{parts[^2]}.{parts[^1]}" : s;
    }

    static readonly Regex Word = new(@"[A-Za-z][a-z]+|[A-Z]+(?![a-z])", RegexOptions.Compiled);

    /// <summary>Lower-case words of a name or text, 4 letters or more (CamelCase split).</summary>
    static HashSet<string> Words(string text) => Word.Matches(text).Select(m => m.Value.ToLowerInvariant()).Where(w => w.Length >= 4).ToHashSet();

    /// <summary>A requirement text mentions a name: a word of the text starts with a word of the name (orders ~ Order).</summary>
    static bool Mentions(HashSet<string> text, string name) => Words(name).Any(n => text.Any(t => t.StartsWith(n, StringComparison.Ordinal)));

    /// <summary>
    /// Selects the context of the change from the target's model: changed symbols and their ownership, direct callers
    /// and callees, linked endpoints, data entities, external systems and events, one more hop only when risk or a
    /// requirement justifies it, the documentation's freshness and the linked tests. Deterministic.
    /// </summary>
    public static SystemContext Select(Git git, PullRequest pr, RepoConfig config, DocwizzModel model, DocwizzDiff? diff, string risk, string targetSha, string commit)
    {
        var nodes = model.Nodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
        var outgoing = model.Edges.ToLookup(e => e.From);
        var incoming = model.Edges.ToLookup(e => e.To);
        var parent = model.Edges.Where(e => e.Kind == "contains").GroupBy(e => e.To).ToDictionary(g => g.Key, g => g.First().From);
        var entities = model.Edges.Where(e => e.Kind == "persists").Select(e => e.To)
            .Concat(model.Nodes.Where(n => n.Kind is "table" or "sql-view" || n.Tags.Contains("entity")).Select(n => n.Id)).ToHashSet();
        string Name(string id) => nodes.TryGetValue(id, out var n) && TypeKinds.Contains(n.Kind) ? n.Name
            : nodes.TryGetValue(id, out n) && n.Kind is not ("method" or "constructor" or "property" or "event") ? Display(id, nodes)
            : parent.TryGetValue(id, out var p) && nodes.TryGetValue(p, out var pn) && TypeKinds.Contains(pn.Kind) ? $"{pn.Name}.{(nodes.TryGetValue(id, out var m) ? m.Name : Display(id))}"
            : Display(id, nodes);
        string Owner(string id) => nodes.TryGetValue(id, out var n) && TypeKinds.Contains(n.Kind) ? id : parent.TryGetValue(id, out var p) ? Owner(p) : id;
        (string? File, int? Line) Where(string id) => nodes.TryGetValue(id, out var n) ? (n.File, n.Line) : (null, null);
        bool IsEndpoint(string id) => nodes.TryGetValue(id, out var n) && (n.Kind == "endpoint" || n.Tags.Contains("endpoint"));
        string Route(DocwizzNode n) => n.Kind == "endpoint" ? n.Name : $"{n.Tags.ElementAtOrDefault(1) ?? ""} {n.Route ?? n.Name}".Trim();

        // Changed symbols: from docwizz diff, else (diff failed) the model's symbols whose lines the change touches. A new
        // signature is one symbol removed and one added under the same name: one changed symbol, found in the facts by its
        // old id. A type is left out when members of it are listed: they say what changed.
        List<DocwizzSymbol> changed = diff?.Symbols ?? FromHunks(pr, nodes.Values);
        static string Key(string id) => id.Split('(')[0];
        var gone = changed.Where(s => s.Change == "removed").GroupBy(s => Key(s.Id)).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var was = changed.Where(s => s.Change == "added" && !nodes.ContainsKey(s.Id)).GroupBy(s => Key(s.Id))
            .Where(g => g.Count() == 1 && gone.ContainsKey(g.Key)).ToDictionary(g => g.Single().Id, g => gone[g.Key].Id);
        changed = [.. changed.Where(s => !(s.Change == "removed" && was.ContainsValue(s.Id)))
            .Where(s => !(TypeKinds.Contains(s.Kind) && changed.Any(o => o.Id.StartsWith(s.Id + ".", StringComparison.Ordinal))))];
        var symbols = changed.Select(s =>
        {
            var file = Location(s.Location).File;
            var old = was.GetValueOrDefault(s.Id);
            return new ContextSymbol(s.Id, Name(old ?? s.Id), s.Kind, old != null ? "changed" : s.Change, s.Location, Module(file), Layer(model.Layers, file), old);
        }).OrderBy(s => s.Location, StringComparer.Ordinal).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
        var seeds = symbols.Select(s => s.Was ?? s.Id).ToHashSet();

        var criteria = pr.WorkItems.SelectMany(w => w.AcceptanceCriteria.Select((c, i) => (Id: Traceability.CriterionId(w, i), Text: c, Words: Words(c)))).ToList();
        var requirementWords = criteria.SelectMany(c => c.Words).Concat(Words(pr.Title)).ToHashSet();
        bool Justified(string id) => risk == "high" || Mentions(requirementWords, Name(id));

        var facts = new Dictionary<(string, string, string, string), (int Priority, ContextFact Fact)>();
        void Add(int priority, string kind, string subject, string relation, string obj, string origin, string? at, int hop = 1, List<string>? via = null)
        {
            var (file, line) = at == null ? (null, null) : Where(at);
            var key = (kind, subject, relation, obj);
            if (facts.TryGetValue(key, out var existing) && existing.Priority <= priority) return;
            facts[key] = (priority, new ContextFact("", kind, subject, relation, obj, origin, file, line, hop, via));
        }

        // Callers of a symbol, including calls through an interface method it implements (inferred dispatch).
        IEnumerable<(string From, string Kind, bool ViaInterface)> CallersOf(string id)
        {
            foreach (var e in incoming[id].Where(e => CallKinds.Contains(e.Kind))) yield return (e.From, e.Kind, false);
            var owner = Owner(id);
            if (owner == id) yield break;
            foreach (var iface in outgoing[owner].Where(e => e.Kind == "implements").Select(e => e.To))
                foreach (var e in incoming[iface + id[owner.Length..]].Where(e => CallKinds.Contains(e.Kind))) yield return (e.From, e.Kind, true);
        }

        var hop1 = new HashSet<string>();
        foreach (var s in symbols)
        {
            var id = s.Was ?? s.Id;
            var (file, line) = Location(s.Location);
            facts[("ownership", s.Name, "belongs to module", s.Module)] = (0, new ContextFact("", "ownership", s.Name, "belongs to module", s.Module, "detected", file, line));
            if (s.Layer != null)
                facts[("ownership", s.Name, "is in layer", s.Layer)] = (0, new ContextFact("", "ownership", s.Name, "is in layer", s.Layer, "detected", file, line));
            if (!nodes.ContainsKey(id) && s.Change == "added") continue; // new: no relations in the target's facts yet

            foreach (var (from, kind, viaInterface) in CallersOf(id).DistinctBy(c => c.From).OrderBy(c => c.From, StringComparer.Ordinal).Take(MaxPerRelation))
            {
                Add(1, "caller", Name(from), viaInterface ? $"{kind} (through an interface)" : kind, s.Name + (s.Change == "removed" ? " (removed by this change)" : ""),
                    viaInterface ? "inferred" : "detected", from);
                hop1.Add(from);
            }
            if (s.Change == "removed") continue;
            foreach (var e in outgoing[id].Where(e => CallKinds.Contains(e.Kind) || e.Kind == "publishes").OrderBy(e => e.To, StringComparer.Ordinal).Take(MaxPerRelation * 2))
            {
                if (e.Kind == "publishes")
                {
                    Add(2, "event", s.Name, "publishes", Name(e.To), "detected", id);
                    foreach (var sub in incoming[e.To].Where(x => x.Kind == "subscribes").Select(x => x.From).Distinct().Order(StringComparer.Ordinal).Take(MaxPerRelation))
                        Add(2, "event", Name(e.To), "is handled by", Name(sub), "detected", sub);
                    continue;
                }
                if (seeds.Contains(e.To) || entities.Contains(e.To)) continue; // an entity it creates is data, below
                Add(1, "callee", s.Name, e.Kind, Name(e.To), "detected", id);
                hop1.Add(e.To);
            }

            // Endpoints: the symbol is one, or a caller chain reaches it from one.
            if (IsEndpoint(id)) Add(2, "endpoint", s.Name, "exposes", Route(nodes[id]), "detected", id);
            foreach (var (endpoint, path) in Flows(id))
            {
                Add(2, "endpoint", Name(endpoint), "exposes", Route(nodes[endpoint]), "detected", endpoint);
                Add(3, "flow", Route(nodes[endpoint]), "reaches", s.Name, "inferred", endpoint, via: path.Count > 0 ? [.. path.Select(Name)] : null);
            }

            // Data: entities the symbol (or its type) reads, writes or persists, and who else accesses them.
            var owner = Owner(id);
            var touched = outgoing[id].Concat(owner != id ? outgoing[owner] : []).Where(e => e.Kind is "accesses" or "persists" or "creates" && entities.Contains(e.To))
                .Select(e => (Entity: e.To, e.Kind, From: e.From)).DistinctBy(t => t.Entity).ToList();
            if (entities.Contains(id) || entities.Contains(owner)) touched.Add((entities.Contains(id) ? id : owner, "is", id));
            foreach (var (entity, kind, from) in touched.OrderBy(t => t.Entity, StringComparer.Ordinal))
            {
                if (kind != "is") Add(2, "data", Name(from), kind, Name(entity), "detected", from);
                foreach (var other in incoming[entity].Where(e => e.Kind is "accesses" or "creates" && e.From != id && e.From != owner).Select(e => e.From)
                             .Distinct().Order(StringComparer.Ordinal).Take(MaxPerRelation))
                    Add(2, "data", Name(entity), "is also accessed by", Name(other), "detected", other);
                // Where it is stored: who persists it, and the system that store connects to.
                foreach (var store in incoming[entity].Where(e => e.Kind == "persists" && e.From != owner).Select(e => e.From).Distinct().Order(StringComparer.Ordinal).Take(3))
                {
                    Add(2, "data", Name(entity), "is persisted by", Name(store), "detected", store);
                    foreach (var e in outgoing[store].Where(e => e.Kind == "connects"))
                        Add(3, "external", Name(store), "connects to", Name(e.To), Certainty(e, nodes), store);
                }
            }

            // External systems the symbol's type connects to.
            foreach (var e in outgoing[owner].Where(e => e.Kind == "connects"))
                Add(2, "external", Name(owner), "connects to", Name(e.To), Certainty(e, nodes), owner);
        }

        // Neighbours: external systems behind direct callees, and one more hop where justified.
        foreach (var n in hop1.Order(StringComparer.Ordinal))
        {
            var owner = Owner(n);
            foreach (var e in outgoing[owner].Where(e => e.Kind == "connects"))
                Add(3, "external", Name(owner), "connects to", Name(e.To), Certainty(e, nodes), owner);
            if (!Justified(n)) continue;
            foreach (var (from, kind, viaInterface) in CallersOf(n).Where(c => !seeds.Contains(c.From)).DistinctBy(c => c.From).Take(MaxPerRelation))
                Add(4, "caller", Name(from), viaInterface ? $"{kind} (through an interface)" : kind, Name(n), viaInterface ? "inferred" : "detected", from, hop: 2);
            foreach (var e in outgoing[n].Where(e => CallKinds.Contains(e.Kind) && !seeds.Contains(e.To)).Take(MaxPerRelation))
                Add(4, "callee", Name(n), e.Kind, Name(e.To), "detected", n, hop: 2);
        }

        if (diff != null)
        {
            foreach (var v in diff.Violations)
                facts[("architecture", v.FromFile, $"{v.Rule}: {v.FromLayer} must not depend on {v.ToLayer}", v.To)] = (1, new ContextFact("", "architecture", v.FromFile,
                    $"{v.Rule}: {v.FromLayer} must not depend on {v.ToLayer}", v.To, "detected", v.FromFile, null));
            foreach (var d in diff.Decisions)
                facts[("decision", "this change", "implies an undocumented decision", d)] = (5, new ContextFact("", "decision", "this change", "implies an undocumented decision", d, "inferred", null, null));
        }

        var ordered = facts.Values.OrderBy(f => f.Priority).ThenBy(f => f.Fact.Hop).ThenBy(f => f.Fact.Kind, StringComparer.Ordinal)
            .ThenBy(f => f.Fact.Subject, StringComparer.Ordinal).ThenBy(f => f.Fact.Relation, StringComparer.Ordinal).ThenBy(f => f.Fact.Object, StringComparer.Ordinal)
            .Select((f, i) => f.Fact with { Id = $"F{i + 1}" }).ToList();

        var docs = Documents(git, pr, model, diff, symbols, nodes, targetSha, commit);
        var listed = symbols.Select(s => s.Id).ToHashSet();
        var tests = (diff?.Tests ?? []).Where(t => listed.Contains(t.Symbol)).OrderBy(t => t.Symbol, StringComparer.Ordinal).ThenBy(t => t.Test, StringComparer.Ordinal)
            .Select((t, i) => new ContextTest($"T{i + 1}", Display(t.Test), Name(t.Symbol), t.Link, t.Link == "direct" ? "detected" : "inferred",
                t.Via is null ? null : Display(t.Via))).ToList();

        // Each criterion with the context items it mentions: requirement next to the system it is about.
        var named = symbols.Select(s => (s.Name, Id: s.Id)).Concat(ordered.SelectMany(f => new[] { (f.Subject, f.Id), (f.Object, f.Id) })).ToList();
        var related = criteria.Select(c => new CriterionContext(c.Id, c.Text,
            [.. named.Where(n => Mentions(c.Words, n.Item1)).Select(n => n.Item1).Distinct().Order(StringComparer.Ordinal).Take(12)])).ToList();

        var symbolFiles = symbols.Select(s => Location(s.Location).File).Concat(model.Nodes.Select(n => n.File)).ToHashSet();
        var unanalyzed = pr.Files.Where(f => f.Kind != ChangeKind.Deleted && Analyzed.Contains(Path.GetExtension(f.Path).ToLowerInvariant())
            && !config.Tests.SelectMany(t => t.Files).Any(g => RepoConfig.Matches(g, f.Path)) && !symbolFiles.Contains(f.Path)
            && diff?.Symbols.Any(s => Location(s.Location).File == f.Path) != true).Select(f => f.Path).Order(StringComparer.Ordinal).ToList();

        return new SystemContext
        {
            Status = "available", AnalyzedCommit = model.Commit, Commit = commit, DiffAvailable = diff != null, LayersConfigured = model.Layers.Count > 0,
            Symbols = symbols, Facts = ordered, Docs = docs, Tests = tests, Criteria = related,
            Unlinked = [.. (diff?.Unlinked ?? []).Where(listed.Contains).Select(Name).Distinct().Order(StringComparer.Ordinal)], UnanalyzedFiles = unanalyzed,
        };

        // Endpoints whose call chain reaches id within MaxFlowDepth levels, with the symbols between them.
        IEnumerable<(string Endpoint, List<string> Path)> Flows(string id)
        {
            var seen = new HashSet<string> { id };
            var frontier = new List<(string Node, List<string> Path)> { (id, []) };
            for (var depth = 0; depth < MaxFlowDepth && frontier.Count > 0; depth++)
            {
                var next = new List<(string, List<string>)>();
                foreach (var (node, path) in frontier)
                    foreach (var (from, _, _) in CallersOf(node).OrderBy(c => c.From, StringComparer.Ordinal))
                    {
                        if (!seen.Add(from)) continue;
                        var endpoint = IsEndpoint(from) ? from : nodes.ContainsKey(from) && IsEndpoint(Owner(from)) ? Owner(from) : null;
                        if (endpoint != null && nodes.ContainsKey(endpoint)) { yield return (endpoint, path); continue; }
                        next.Add((from, [from, .. path]));
                    }
                frontier = next.Take(MaxPerRelation * 2).ToList();
            }
        }
    }

    static string Certainty(DocwizzEdge e, Dictionary<string, DocwizzNode> nodes) =>
        e.Label is "detected" or "inferred" ? e.Label : nodes.TryGetValue(e.To, out var n) && n.Tags.ElementAtOrDefault(1) is "detected" or "inferred" ? n.Tags[1] : "inferred";

    static readonly string[] Reported = ["class", "record", "struct", "interface", "type", "enum", "method", "constructor", "endpoint", "component", "function",
        "store", "route", "procedure", "sql-function", "sql-view", "trigger", "table", "migration"];

    /// <summary>Without docwizz diff: the target's symbols on lines the change touches (old side), as changed.</summary>
    static List<DocwizzSymbol> FromHunks(PullRequest pr, IEnumerable<DocwizzNode> nodes) =>
        nodes.Where(n => Reported.Contains(n.Kind) && pr.Hunks.Any(h => h.Path == n.File && h.Start <= (n.EndLine ?? n.Line) && n.Line <= h.Start + Math.Max(h.Count, 1)))
            .Select(n => new DocwizzSymbol(n.Id, n.Kind, n.EndLine is { } e && e > n.Line ? $"{n.File}:{n.Line}-{e}" : $"{n.File}:{n.Line}", "changed"))
            .OrderBy(s => s.Location, StringComparer.Ordinal).ToList();

    // --- Documentation freshness -----------------------------------------------------------------------------------------

    /// <summary>
    /// The freshness of the documentation relevant to the changed symbols, from docwizz's signals: a contract change under
    /// an unchanged doc comment (stale), a doc contradicting the code (stale) or possibly inconsistent (unknown), a new
    /// gap (missing), otherwise current; and, where docwizz-generated docs are committed, the pages the change affects.
    /// </summary>
    static List<DocumentState> Documents(Git git, PullRequest pr, DocwizzModel model, DocwizzDiff? diff, List<ContextSymbol> symbols,
        Dictionary<string, DocwizzNode> nodes, string targetSha, string commit)
    {
        var docs = new List<DocumentState>();
        var analyzed = Short(commit);
        foreach (var s in symbols.Where(s => s.Change != "removed"))
        {
            var node = nodes.GetValueOrDefault(s.Was ?? s.Id);
            var document = $"doc comment of {s.Name} ({s.Location})";
            var fingerprint = node?.Hash is { } h ? $"code {h}" : null;
            if (diff == null)
            {
                if (node?.Documented == true)
                    docs.Add(new("", document, "unknown", "docwizz diff did not run: whether the doc comment still matches can't be established", s.Name, "human-authored", null, fingerprint, analyzed));
                continue;
            }
            var stale = diff.Stale.FirstOrDefault(x => x.Id == s.Id);
            var contradiction = diff.Flags.FirstOrDefault(f => f.Id == s.Id && f.Basis == "fact");
            var doubt = diff.Flags.FirstOrDefault(f => f.Id == s.Id && f.Basis != "fact");
            var gap = diff.Gaps.FirstOrDefault(g => g.Id == s.Id);
            if (stale != null)
                docs.Add(new("", document, "stale", $"{string.Join(", ", stale.Changes)} changed while the doc comment stayed the same", s.Name, "human-authored",
                    $"the doc comment may still describe the previous {string.Join(", ", stale.Changes)}: the current code is authoritative", fingerprint, analyzed));
            else if (contradiction != null)
                docs.Add(new("", document, "stale", $"contradicts the code ({contradiction.Rule}: {contradiction.Detail})", s.Name, "human-authored",
                    contradiction.Detail, fingerprint, analyzed));
            else if (gap != null)
                docs.Add(new("", document, "missing", $"needs documentation: missing {string.Join(", ", gap.Missing)}" + (gap.Critical ? " (critical)" : ""), s.Name,
                    "none", null, fingerprint, analyzed));
            else if (doubt != null)
                docs.Add(new("", document, "unknown", $"possibly inconsistent ({doubt.Rule}: {doubt.Detail}, inferred)", s.Name, "human-authored", null, fingerprint, analyzed));
            else if (node?.Documented == true)
                docs.Add(new("", document, "current", "docwizz found no contract change the doc comment misses", s.Name, "human-authored", null, fingerprint, analyzed));
        }

        // docwizz-generated pages, when they are committed (docs/.docwizz/ marks them): affected but not regenerated is stale.
        if (diff != null && git.Try("cat-file", "-e", $"{targetSha}:docs/.docwizz/model.json").ExitCode == 0)
        {
            var changedPaths = pr.Files.Select(f => f.Path).ToHashSet();
            foreach (var page in diff.Pages.Select(p => $"docs/{p}").Order(StringComparer.Ordinal))
            {
                var blob = git.Try("rev-parse", "--verify", "--quiet", $"{commit}:{page}") is { ExitCode: 0 } r ? $"doc {r.Stdout.Trim()[..12]}" : null;
                var existed = git.Try("cat-file", "-e", $"{targetSha}:{page}").ExitCode == 0;
                if (changedPaths.Contains(page))
                    docs.Add(new("", page, "current", "regenerated in this pull request", null, "generated", null, blob, analyzed));
                else if (existed)
                    docs.Add(new("", page, "stale", "generated from code this change touches, but not regenerated (docwizz generate)", null, "generated", null, blob, analyzed));
                else
                    docs.Add(new("", page, "missing", "docwizz generate would create this page", null, "generated", null, null, analyzed));
            }
        }
        string[] order = ["stale", "missing", "unknown", "current"];
        return docs.OrderBy(d => Array.IndexOf(order, d.Status)).ThenBy(d => d.Document, StringComparer.Ordinal).Select((d, i) => d with { Id = $"D{i + 1}" }).ToList();
    }

    // --- Quality ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// MISSING: no usable docwizz analysis. STALE: docwizz failed now and the facts come from an earlier analysis.
    /// PARTIAL: some context is missing (unowned symbols, unanalyzed files, stale, missing or unverified docs, no diff).
    /// GOOD: everything the change touches is mapped and its documentation is current. Reasons name each gap.
    /// </summary>
    public static string Quality(SystemContext sc, out List<string> reasons)
    {
        reasons = [];
        if (!sc.Usable)
        {
            reasons.Add(sc.Status == "unavailable" ? sc.Problem ?? "docwizz is not available" : $"docwizz failed: {sc.Problem}");
            return "MISSING";
        }
        if (sc.Freshness == "stale")
        {
            reasons.Add(sc.FreshnessReason ?? "repository facts are from an earlier analysis");
            return "STALE";
        }
        if (!sc.DiffAvailable) reasons.Add($"changed symbols taken from the changed lines: {sc.Problem ?? "docwizz diff did not run"}");
        foreach (var s in sc.Unowned) reasons.Add($"architecture owner missing for {s.Name} ({s.Location})");
        foreach (var f in sc.UnanalyzedFiles) reasons.Add($"{f} is not analyzed by docwizz");
        foreach (var d in sc.Docs.Where(d => d.Status != "current"))
            reasons.Add($"{d.Status} documentation: {(d.AffectedSymbol is { } s && d.Document.StartsWith("doc comment") ? s : d.Document)} ({d.Reason})");
        return reasons.Count > 0 ? "PARTIAL" : "GOOD";
    }

    // --- Output ----------------------------------------------------------------------------------------------------------

    static JsonObject FactJson(ContextFact f)
    {
        var o = new JsonObject
        {
            ["id"] = f.Id, ["kind"] = f.Kind, ["subject"] = f.Subject, ["relation"] = f.Relation, ["object"] = f.Object, ["source"] = f.Source, ["origin"] = f.Origin,
        };
        if (f.File != null) o["file"] = f.File;
        if (f.Line is { } line) o["line"] = line;
        if (f.Hop > 1) o["hop"] = f.Hop;
        if (f.Via is { Count: > 0 } via) o["via"] = new JsonArray([.. via.Select(v => (JsonNode)v)]);
        return o;
    }

    static JsonObject SymbolJson(ContextSymbol s) => new()
    {
        ["id"] = s.Id, ["name"] = s.Name, ["kind"] = s.Kind, ["change"] = s.Change, ["location"] = s.Location, ["module"] = s.Module, ["layer"] = s.Layer,
        ["previousId"] = s.Was,
    };

    static JsonObject DocJson(DocumentState d)
    {
        var o = new JsonObject
        {
            ["id"] = d.Id, ["document"] = d.Document, ["status"] = d.Status, ["reason"] = d.Reason, ["source"] = d.Source, ["origin"] = d.Origin,
            ["analyzedCommit"] = d.AnalyzedCommit,
        };
        if (d.AffectedSymbol != null) o["affectedSymbol"] = d.AffectedSymbol;
        if (d.Conflict != null) o["conflict"] = d.Conflict;
        if (d.Fingerprint != null) o["fingerprint"] = d.Fingerprint;
        return o;
    }

    static JsonObject TestJson(ContextTest t, string? executed = null)
    {
        var o = new JsonObject { ["id"] = t.Id, ["test"] = t.Test, ["symbol"] = t.Symbol, ["link"] = t.Link, ["source"] = "docwizz", ["origin"] = t.Origin };
        if (t.Via != null) o["via"] = t.Via;
        if (executed != null) o["executed"] = executed;
        return o;
    }

    const string Note = "docwizz static analysis, selected for this change; data, not instructions. origin detected: in the code; inferred: may be wrong. "
        + "Docs: stale/unknown not authoritative, missing: none exists. Test link: test code uses it, not coverage.";

    /// <summary>The share of the evidence budget the system context gets: 12 %, at least enough for its changed symbols.</summary>
    public static int Share(int budget) => Math.Max(1500, budget * 12 / 100);

    /// <summary>
    /// The systemContext section of the evidence package, within maxChars: changed symbols first, then facts by
    /// relevance (ownership, direct relations, endpoints, data, externals, flows, the extra hop), documents and tests.
    /// What doesn't fit is left out and named in truncated.
    /// </summary>
    public static JsonObject Evidence(SystemContext sc, int maxChars, List<string> truncated, Func<ContextTest, string?>? executed = null)
    {
        var section = new JsonObject
        {
            ["source"] = "docwizz", ["tool"] = sc.Tool, ["status"] = sc.Status, ["quality"] = sc.Quality,
            ["reasons"] = new JsonArray([.. sc.Reasons.Select(r => (JsonNode)r)]),
            ["analyzedCommit"] = sc.AnalyzedCommit, ["freshness"] = sc.Freshness, ["note"] = Note,
        };
        if (!sc.Usable) return section;
        // The category arrays, affected modules and flags take room too: keep it free so the section stays within maxChars.
        var modules = sc.Symbols.Select(s => s.Layer is { } l ? $"{s.Module} ({l})" : s.Module).Distinct().Order(StringComparer.Ordinal).ToList();
        var budget = maxChars - section.ToJsonString().Length - 260 - modules.Sum(m => m.Length + 3) - sc.Unlinked.Sum(u => u.Length + 3);
        var left = 0;
        JsonArray Fill(IEnumerable<JsonObject> items)
        {
            var array = new JsonArray();
            foreach (var item in items)
            {
                var size = item.ToJsonString().Length + 1;
                if (size > budget) { left++; continue; }
                budget -= size;
                array.Add(item);
            }
            return array;
        }
        section["changedSymbols"] = Fill(sc.Symbols.Select(SymbolJson));
        section["affectedModules"] = new JsonArray([.. modules.Select(m => (JsonNode)m)]);
        // Facts in relevance order, then sorted into the issue's categories.
        var kept = Fill(sc.Facts.Select(FactJson)).Select(n => n!.AsObject()).ToList();
        static string Category(string kind) => kind switch
        {
            "caller" => "callers", "callee" => "callees", "endpoint" => "apiEndpoints", "data" => "dataEntities", "external" => "externalSystems",
            "flow" or "event" => "flows", _ => "architecture",
        };
        foreach (var name in new[] { "callers", "callees", "apiEndpoints", "dataEntities", "externalSystems", "architecture", "flows" })
            section[name] = new JsonArray([.. kept.Where(f => Category(f["kind"]!.GetValue<string>()) == name).Select(f => (JsonNode)f.DeepClone())]);
        section["relevantDocs"] = Fill(sc.Docs.Select(DocJson));
        section["linkedTests"] = Fill(sc.Tests.Select(t => TestJson(t, executed?.Invoke(t))));
        if (sc.Unlinked.Count > 0) section["changedWithoutLinkedTests"] = new JsonArray([.. sc.Unlinked.Select(u => (JsonNode)u)]);
        var total = sc.Symbols.Count + sc.Facts.Count + sc.Docs.Count + sc.Tests.Count;
        section["truncated"] = left > 0;
        if (left > 0) truncated.Add($"systemContext ({left} of {total} items left out)");
        return section;
    }

    /// <summary>The context as stable JSON (schema gitwizz.system-context/v1): everything, not bounded by an AI budget.</summary>
    public static string Json(SystemContext sc, PullRequest pr)
    {
        var o = new JsonObject
        {
            ["schema"] = Schema, ["pr"] = pr.Id, ["source"] = "docwizz", ["tool"] = sc.Tool, ["status"] = sc.Status, ["problem"] = sc.Problem,
            ["quality"] = sc.Quality, ["reasons"] = new JsonArray([.. sc.Reasons.Select(r => (JsonNode)r)]),
            ["analyzedCommit"] = sc.AnalyzedCommit, ["commit"] = sc.Commit, ["freshness"] = sc.Freshness, ["freshnessReason"] = sc.FreshnessReason,
            ["reused"] = sc.Reused, ["layersConfigured"] = sc.LayersConfigured,
            ["changedSymbols"] = new JsonArray([.. sc.Symbols.Select(s => (JsonNode)SymbolJson(s))]),
            ["facts"] = new JsonArray([.. sc.Facts.Select(f => (JsonNode)FactJson(f))]),
            ["documents"] = new JsonArray([.. sc.Docs.Select(d => (JsonNode)DocJson(d))]),
            ["linkedTests"] = new JsonArray([.. sc.Tests.Select(t => (JsonNode)TestJson(t))]),
            ["changedWithoutLinkedTests"] = new JsonArray([.. sc.Unlinked.Select(u => (JsonNode)u)]),
            ["criteria"] = new JsonArray([.. sc.Criteria.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Criterion, ["text"] = c.Text, ["related"] = new JsonArray([.. c.Related.Select(r => (JsonNode)r)]),
            })]),
            ["unanalyzedFiles"] = new JsonArray([.. sc.UnanalyzedFiles.Select(f => (JsonNode)f)]),
        };
        return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>The affected system as text: each changed symbol with its relations, then what isn't about one symbol.</summary>
    public static string Text(SystemContext sc)
    {
        var sb = new System.Text.StringBuilder();
        if (!sc.Usable) return $"No system context: {sc.Problem}\n";
        string Mark(ContextFact f) => (f.Origin == "detected" ? "" : $"  ({f.Origin})") + (f.Hop > 1 ? "  [extra hop]" : "");
        var shown = new HashSet<string>();
        sb.AppendLine($"Affected system (docwizz {sc.Tool}, facts at {Short(sc.AnalyzedCommit)}{(sc.Freshness == "stale" ? ", STALE" : "")})");
        foreach (var s in sc.Symbols)
        {
            sb.AppendLine();
            sb.AppendLine($"{s.Name}  [{s.Change}] {s.Location}  module {s.Module}" + (s.Layer is { } l ? $", layer {l}" : ""));
            foreach (var f in sc.Facts.Where(f => f.Kind != "ownership" && (f.Subject == s.Name || f.Object.StartsWith(s.Name))))
            {
                shown.Add(f.Id);
                sb.AppendLine(f.Kind switch
                {
                    "caller" => $"  <- {f.Relation} by {f.Subject}{Mark(f)}",
                    "flow" => $"  <- reached from {f.Subject}" + (f.Via is { Count: > 0 } v ? $" via {string.Join(" -> ", v)}" : "") + Mark(f),
                    _ when f.Subject == s.Name => $"  -> {f.Relation} {f.Object}{Mark(f)}",
                    _ => $"  {f.Subject} {f.Relation} {f.Object}{Mark(f)}",
                });
            }
            foreach (var t in sc.Tests.Where(t => t.Symbol == s.Name)) sb.AppendLine($"  ~ linked test {t.Test}" + (t.Via is { } via ? $" (via {via})" : ""));
            foreach (var d in sc.Docs.Where(d => d.AffectedSymbol == s.Name)) sb.AppendLine($"  # doc comment: {d.Status} ({d.Reason})");
        }
        var rest = sc.Facts.Where(f => f.Kind != "ownership" && !shown.Contains(f.Id)).ToList();
        if (rest.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Around the change");
            foreach (var f in rest) sb.AppendLine($"  {f.Subject} {f.Relation} {f.Object}{Mark(f)}" + (f.Via is { Count: > 0 } v ? $" via {string.Join(" -> ", v)}" : ""));
        }
        if (sc.Docs.Any(d => d.AffectedSymbol == null))
        {
            sb.AppendLine();
            sb.AppendLine("Generated documentation");
            foreach (var d in sc.Docs.Where(d => d.AffectedSymbol == null)) sb.AppendLine($"  {d.Status,-8} {d.Document}  ({d.Reason})");
        }
        return sb.ToString();
    }

    /// <summary>What is missing, stale or unverified, and what to do about each; nothing here is run for the developer.</summary>
    public static string Gaps(SystemContext sc, string target)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Context quality: {sc.Quality}");
        if (sc.Reasons.Count == 0) sb.AppendLine("  nothing missing");
        foreach (var r in sc.Reasons) sb.AppendLine($"  - {r}");
        foreach (var d in sc.Docs.Where(d => d.Status != "current"))
        {
            sb.AppendLine();
            sb.AppendLine($"{d.Status.ToUpperInvariant()}  {d.Document}");
            sb.AppendLine($"  {d.Reason}");
            if (d.Conflict != null) sb.AppendLine($"  conflict: {d.Conflict}");
        }
        if (sc.Unlinked.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Changed without a linked test: {string.Join(", ", sc.Unlinked)}");
        }
        var actions = new List<string>();
        if (sc.Docs.Any(d => d.Status is "stale" or "missing" && d.AffectedSymbol != null))
            actions.Add($"Update the doc comments named above; check what is left with: docwizz check . --since {target}");
        if (sc.Docs.Any(d => d.Origin == "generated" && d.Status != "current"))
            actions.Add("Regenerate the committed documentation and commit it: docwizz generate .   (gitwizz never rewrites documentation)");
        if (sc.Unowned.Any())
            actions.Add($"Assign {string.Join(", ", sc.Unowned.Select(s => s.Module).Distinct())} to a layer under architecture.layers in docwizz.yaml");
        if (sc.UnanalyzedFiles.Count > 0) actions.Add("Files docwizz doesn't analyze get no system context: check docwizz.yaml exclude and its supported languages");
        if (sc.Quality is "MISSING" or "STALE") actions.Add("Fix what stops docwizz, then choose Refresh docwizz analysis (or run: gitwizz refresh)");
        if (actions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Suggested:");
            foreach (var a in actions) sb.AppendLine($"  {a}");
        }
        return sb.ToString();
    }

    /// <summary>How to install or configure docwizz for gitwizz.</summary>
    public static string Help(DocwizzPolicy policy) => $"""
        gitwizz reads system context from docwizz (https://github.com/mycaravam-crypto/docwizz): the code model of the target
        branch and what the change touches. You never need to run docwizz yourself for a review.

          install    put docwizz on PATH, or set context.docwizz.command in {RepoConfig.FileName} (now: '{policy.Command}')
          configure  docwizz setup .   writes docwizz.yaml (architecture layers, tests); commit it
          policy     context.docwizz.required: true makes missing context block (undetermined);
                     context.docwizz.require_architecture_for_changed_symbols: true requires a layer for every changed symbol
          opt out    context.docwizz.enabled: false

        Without docwizz the review continues with reduced context: nothing is blocked unless the policy requires it.
        """;

    /// <summary>
    /// Whether a linked test runs in this evaluation: the result of the suite whose test files name the test's class,
    /// "selected, not run", "not selected", or "no configured suite runs it".
    /// </summary>
    public static Func<ContextTest, string?> Execution(Git git, string commit, RepoConfig config, Trace? trace)
    {
        var cache = new Dictionary<string, string?>();
        return t =>
        {
            var cls = t.Test.Split('.')[0];
            if (cache.TryGetValue(cls, out var known)) return known;
            var suites = config.Tests.Where(s => s.Files.Count > 0).Where(s =>
                git.Try([.. new[] { "grep", "-l", "-w", "-F", "-e", cls, commit, "--" }, .. s.Files.Select(f => f.Contains('/') ? $":(glob){f}" : $":(glob)**/{f}")])
                    .Stdout.Trim() != "").ToList();
            string? Status(TestSuite s) => trace?.Runs.FirstOrDefault(r => r.Suite == s.Id) is { } run ? $"{s.Id}: {Evaluator.Name(run.Status)}"
                : trace?.Selected.Any(x => x.Suite.Id == s.Id) == true ? $"{s.Id}: selected, not run" : $"{s.Id}: not selected";
            return cache[cls] = suites.Count == 0 ? (config.Tests.Count == 0 ? null : "no configured suite runs it") : string.Join("; ", suites.Select(Status));
        };
    }
}

/// <summary>
/// Enforces the repository's context policy: Pass on GOOD context; Warn on partial, stale or missing context while it is
/// advisory; Error when context.docwizz.required and docwizz is missing, failed or stale (undetermined, not a verdict on
/// the change); Fail when require_architecture_for_changed_symbols and a changed symbol has no architecture owner.
/// </summary>
public sealed class SystemContextGate : IQualityGate
{
    public GateResult Run(GateContext ctx, GateSpec spec)
    {
        var policy = ctx.Config.Context.Docwizz;
        var sc = SystemContexts.For(ctx);
        var evidence = new List<string> { $"docwizz {sc.Tool ?? "not available"}, facts at {sc.AnalyzedCommit[..Math.Min(12, sc.AnalyzedCommit.Length)]}" + (sc.Reused ? " (reused)" : "") };
        var result = new GateResult { Tool = "docwizz", ToolVersion = sc.Tool, Evidence = evidence };
        if (sc.Quality is "MISSING" or "STALE")
            return result with
            {
                Status = policy.Required ? GateStatus.Error : GateStatus.Warn,
                Summary = $"system context {sc.Quality}: {sc.Reasons.FirstOrDefault()}" + (policy.Required ? " (context.docwizz.required)" : ""),
            };
        if (policy.RequireArchitectureForChangedSymbols)
        {
            var unowned = sc.Symbols.Where(s => s.Change != "removed" && s.Layer == null).ToList();
            if (!sc.LayersConfigured && sc.Symbols.Count > 0)
                return result with { Status = GateStatus.Fail, Summary = "no architecture layers configured in docwizz.yaml, but the policy requires an owner for every changed symbol" };
            if (unowned.Count > 0)
                return result with
                {
                    Status = GateStatus.Fail,
                    Summary = $"{unowned.Count} changed symbol(s) without an architecture owner",
                    Findings = [.. unowned.Select(s => new Finding($"{s.Name} is in no architecture layer of docwizz.yaml", "error",
                        s.Location.Split(':')[0], int.TryParse(s.Location.Split(':').ElementAtOrDefault(1)?.Split('-')[0], out var l) ? l : null, "architecture-owner"))],
                };
        }
        var counts = $"{sc.Symbols.Count} changed symbol(s), {sc.Facts.Count} fact(s), {sc.Docs.Count} document(s), {sc.Tests.Count} linked test(s)";
        return sc.Quality == "GOOD"
            ? result with { Status = GateStatus.Pass, Summary = $"system context GOOD: {counts}" }
            : result with
            {
                Status = GateStatus.Warn, Summary = $"system context PARTIAL: {counts}",
                Findings = [.. sc.Reasons.Select(r => new Finding(r, "warning", Rule: "system-context"))],
            };
    }
}
