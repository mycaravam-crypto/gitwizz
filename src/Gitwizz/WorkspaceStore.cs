using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gitwizz;

/// <summary>
/// Deterministic fingerprints. A cached result is reused only when every input that produced it is the same, never
/// because it is recent: the key of a stored artifact is the hash of its inputs.
/// </summary>
public static class Fingerprint
{
    /// <summary>
    /// This build of gitwizz: version plus module id. Deterministic builds of the same source share it; any code change
    /// invalidates everything computed by another build.
    /// </summary>
    public static readonly string Build = $"{typeof(Fingerprint).Assembly.GetName().Version?.ToString(3)}+{typeof(Fingerprint).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..12]}";

    /// <summary>sha256 of the inputs, each named and length-prefixed so no two input sets collide by concatenation.</summary>
    public static string Of(IEnumerable<KeyValuePair<string, string>> inputs) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(
            inputs.OrderBy(i => i.Key, StringComparer.Ordinal).Select(i => $"{i.Key.Length}:{i.Key}={i.Value.Length}:{i.Value}\n")))));

    /// <summary>sha256 of one text.</summary>
    public static string Of(string text) => Of([new("", text)]);

    static readonly string[] Builtins = ["echo", "true", "false", "exit", "test", "[", "printf", ":", "cd", "set", "export", "unset", "read", "eval", "exec", "!"];
    static readonly ConcurrentDictionary<string, string?> Versions = new(); // by location, size and time: the file is re-checked every time

    /// <summary>
    /// What the programs a shell command starts are, as far as it can change their result: the first word of every
    /// pipeline segment. Shell builtins are fixed; a relative path is part of the commit; a well-known tool is its
    /// --version; anything else on PATH is its location, size and modification time; an absolute path likewise.
    /// </summary>
    public static string Tools(string command)
    {
        var words = System.Text.RegularExpressions.Regex.Split(command, @"&&|\|\||[;|\n]")
            .Select(s => s.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).SkipWhile(w => w.Contains('=') && !w.StartsWith('=')).FirstOrDefault())
            .OfType<string>().Select(w => w.Trim('(', ')', '{', '}')).Where(w => w != "").Distinct().Order(StringComparer.Ordinal);
        return Of(string.Join('\n', words.Select(w => $"{w}={Resolve(w)}")));
    }

    static string Resolve(string word)
    {
        if (Builtins.Contains(word)) return "builtin";
        if (word.Contains('/') && !Path.IsPathRooted(word)) return "in-tree";
        var path = Path.IsPathRooted(word) ? word
            : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(d => Path.Combine(d, word)).FirstOrDefault(File.Exists);
        if (path == null || !File.Exists(path)) return "missing";
        var file = new FileInfo(path);
        var stat = $"{path} {file.Length} {file.LastWriteTimeUtc.Ticks}";
        // A launcher (dotnet, npm) can stay the same file while the SDK behind it changes: ask those for their version.
        var version = Versions.GetOrAdd(stat, _ => Gates.ToolOf(word, Directory.GetCurrentDirectory()).Version);
        return version != null ? $"{stat} {version}" : stat;
    }
}

/// <summary>current: the inputs match; stale: they don't (Reason says which); missing: nothing stored, or unreadable.</summary>
public record ArtifactState(string Status, string? Reason = null, DateTimeOffset? ComputedAt = null)
{
    public static readonly ArtifactState Missing = new("missing");
    public static readonly ArtifactState Off = new("off", "workspace disabled (--no-cache)");
    public bool Current => Status == "current";
}

/// <summary>One stored result with the inputs it was computed from. Key: fingerprint of Inputs; Digest: of the stored Value.</summary>
public record Artifact<T>(string Schema, string Key, SortedDictionary<string, string> Inputs, DateTimeOffset ComputedAt, T Value, string Digest = "");

/// <summary>
/// The persistent repository workspace: reusable analysis, repository facts, PR overlays and gate results, stored
/// under the repository's git directory (never in tracked content) or in GITWIZZ_WORKSPACE. It is an optimization only:
/// anything unreadable, from another schema or whose key doesn't match its inputs counts as missing, and is recomputed.
/// </summary>
public sealed class WorkspaceStore
{
    public const string Schema = "gitwizz.workspace/v1";
    const string Marker = ".gitwizz-workspace";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>A store that never reads or writes (--no-cache).</summary>
    public static readonly WorkspaceStore Off = new(null);

    public string? Dir { get; }
    public bool Enabled => Dir != null;

    WorkspaceStore(string? dir) => Dir = dir;

    /// <summary>The store at dir, e.g. a shared CI cache directory.</summary>
    public static WorkspaceStore At(string dir) => new(Path.GetFullPath(dir));

    /// <summary>GITWIZZ_WORKSPACE, else gitwizz/ in the repository's common git directory (shared by its worktrees).</summary>
    public static WorkspaceStore For(Git git) =>
        Environment.GetEnvironmentVariable("GITWIZZ_WORKSPACE") is { Length: > 0 } d ? At(d)
            : At(Path.Combine(Path.GetFullPath(git.Run("rev-parse", "--git-common-dir"), git.RepoDir), "gitwizz"));

    /// <summary>The workspace was created (something was stored).</summary>
    public bool Exists => Dir != null && File.Exists(Path.Combine(Dir, Marker));

    /// <summary>The stored artifact at path, or null when missing, unreadable, of another schema or tampered with.</summary>
    public Artifact<T>? Read<T>(string path)
    {
        if (Dir == null) return null;
        try
        {
            var text = File.ReadAllText(Path.Combine(Dir, path));
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty(nameof(Artifact<T>.Value), out var value)) return null;
            var a = JsonSerializer.Deserialize<Artifact<T>>(text, Json);
            return a is { Schema: Schema, Value: not null, Inputs: not null } && a.Key == Fingerprint.Of(a.Inputs) && a.Digest == Fingerprint.Of(value.GetRawText())
                ? a : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { return null; }
    }

    /// <summary>Stores value with its inputs, atomically (a reader never sees half a file). Failures are ignored: it is a cache.</summary>
    public void Write<T>(string path, SortedDictionary<string, string> inputs, T value)
    {
        if (Dir == null) return;
        try
        {
            var file = Path.Combine(Dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var marker = Path.Combine(Dir, Marker);
            if (!File.Exists(marker)) File.WriteAllText(marker, "Generated by gitwizz; safe to delete (gitwizz cache clear).\n");
            var tmp = $"{file}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            var artifact = new Artifact<T>(Schema, Fingerprint.Of(inputs), inputs, DateTimeOffset.UtcNow, value);
            using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(artifact, Json)))
                artifact = artifact with { Digest = Fingerprint.Of(doc.RootElement.GetProperty(nameof(Artifact<T>.Value)).GetRawText()) };
            File.WriteAllText(tmp, JsonSerializer.Serialize(artifact, Json));
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Whether a stored artifact still applies to the current inputs, and if not, why.</summary>
    public static ArtifactState Check<T>(Artifact<T>? stored, SortedDictionary<string, string> inputs)
    {
        if (stored == null) return ArtifactState.Missing;
        if (stored.Key == Fingerprint.Of(inputs)) return new("current", null, stored.ComputedAt);
        var changed = inputs.Keys.Union(stored.Inputs.Keys).Where(k => inputs.GetValueOrDefault(k) != stored.Inputs.GetValueOrDefault(k)).ToList();
        return new("stale", string.Join(", ", changed.Select(Why).Distinct()), stored.ComputedAt);
    }

    static string Why(string input) => input switch
    {
        "head" => "new commits on the PR",
        "target" => "the target branch moved",
        "config" => $"{RepoConfig.FileName} changed",
        "gitwizz" => "computed by another gitwizz build",
        "provider" => "reviews, checks, description or work items changed",
        "tools" => "a tool changed",
        "strategy" => "other merge strategy",
        "profile" => "other gate profile",
        "commands" => "other commands",
        _ => $"{input} changed",
    };

    /// <summary>Removes everything the workspace holds. Refuses a directory gitwizz didn't create.</summary>
    public (int Files, long Bytes) Clear()
    {
        if (Dir == null || !Directory.Exists(Dir)) return (0, 0);
        if (!File.Exists(Path.Combine(Dir, Marker))) throw new InvalidOperationException($"{Dir} is not a gitwizz workspace: not removing it");
        var size = Size();
        Directory.Delete(Dir, true);
        return size;
    }

    /// <summary>Files and bytes stored.</summary>
    public (int Files, long Bytes) Size()
    {
        if (Dir == null || !Directory.Exists(Dir)) return (0, 0);
        var files = Directory.EnumerateFiles(Dir, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList();
        return (files.Count, files.Sum(f => f.Length));
    }

    /// <summary>Files stored under a sub-directory (prs, results, repo).</summary>
    public int Count(string sub) => Dir != null && Directory.Exists(Path.Combine(Dir, sub))
        ? Directory.EnumerateFiles(Path.Combine(Dir, sub), "*.json", SearchOption.AllDirectories).Count() : 0;

    // --- Paths -------------------------------------------------------------------------------------------------------

    /// <summary>A PR overlay file: prs/&lt;provider&gt;/&lt;id&gt;/&lt;file&gt;.</summary>
    public static string PrPath(string provider, string id, string file) => Path.Combine("prs", provider, Uri.EscapeDataString(id.TrimStart('#')), file);

    static string ResultPath(string kind, SortedDictionary<string, string> inputs) => Path.Combine("results", $"{kind}-{Fingerprint.Of(inputs)[7..39]}.json");

    // --- Inputs ------------------------------------------------------------------------------------------------------

    /// <summary>The policy file's blob at commit, or "none": what every policy-dependent artifact is keyed by.</summary>
    public static string ConfigHash(Git git, string commit) =>
        git.Try("rev-parse", "--verify", "--quiet", $"{commit}:{RepoConfig.FileName}") is { ExitCode: 0 } r ? r.Stdout.Trim() : "none";

    /// <summary>Everything the provider says about a PR that can change a verdict without a commit.</summary>
    public static string ProviderSnapshot(PullRequest pr) => Fingerprint.Of(JsonSerializer.Serialize(new
    {
        pr.BaseRef, pr.Body, pr.IsDraft, pr.ReviewDecision, pr.CiStatus, pr.MergeStateStatus, pr.AutoMerge, pr.Labels, pr.Reviewers, pr.Checks,
        pr.WorkItems, pr.OpenDependencies,
    }));

    // --- PR analysis ---------------------------------------------------------------------------------------------------

    /// <summary>Stored output of Analyzer.Analyze (it depends on the target and head commits only).</summary>
    public record AnalysisData(string BaseSha, List<FileChange> Files, List<Hunk> Hunks, List<string> Members, Dictionary<string, int[][]> Api,
        List<UseData> Uses);
    public record UseData(string Name, int Args);

    /// <summary>
    /// Analyzes pr against targetSha, reusing the stored analysis when target, head and gitwizz build match. Returns the
    /// stored analysis' state before this call (current: reused).
    /// </summary>
    public ArtifactState Analyze(Git git, string targetSha, PullRequest pr, string provider)
    {
        var inputs = new SortedDictionary<string, string> { ["gitwizz"] = Fingerprint.Build, ["target"] = targetSha, ["head"] = pr.HeadSha };
        if (!Enabled) { Analyzer.Analyze(git, targetSha, pr); return ArtifactState.Off; }
        var path = PrPath(provider, pr.Id, "analysis.json");
        var stored = Read<AnalysisData>(path);
        var state = Check(stored, inputs);
        if (state.Current)
        {
            var a = stored!.Value;
            pr.BaseSha = a.BaseSha;
            pr.Files = a.Files;
            pr.Hunks = a.Hunks;
            pr.Members = [.. a.Members];
            pr.Api = a.Api.ToDictionary(x => x.Key, x => (x.Value[0].ToHashSet(), x.Value[1].ToHashSet()));
            pr.Uses = [.. a.Uses.Select(u => (u.Name, u.Args))];
            return state;
        }
        Analyzer.Analyze(git, targetSha, pr);
        Write(path, inputs, new AnalysisData(pr.BaseSha, pr.Files, pr.Hunks, [.. pr.Members.Order(StringComparer.Ordinal)],
            pr.Api.ToDictionary(x => x.Key, x => new[] { x.Value.Before.Order().ToArray(), x.Value.After.Order().ToArray() }),
            [.. pr.Uses.Select(u => new UseData(u.Name, u.Args))]));
        return state;
    }

    // --- Repository facts ----------------------------------------------------------------------------------------------

    static SortedDictionary<string, string> HistoryInputs(string targetSha, int depth) =>
        new() { ["gitwizz"] = Fingerprint.Build, ["target"] = targetSha, ["depth"] = depth.ToString() };

    /// <summary>State of the stored conflict history for the target.</summary>
    public ArtifactState HistoryState(string targetSha, int depth) =>
        Enabled ? Check(Read<Dictionary<string, double>>("repo/conflict-history.json"), HistoryInputs(targetSha, depth)) : ArtifactState.Off;

    /// <summary>Per-file conflict rates from the target's merges, reused while the target hasn't moved.</summary>
    public Dictionary<string, double> ConflictHistory(Git git, string targetSha, int depth, ProgressBars? progress = null)
    {
        if (depth <= 0) return [];
        var inputs = HistoryInputs(targetSha, depth);
        if (Read<Dictionary<string, double>>("repo/conflict-history.json") is { } stored && Check(stored, inputs).Current) return stored.Value;
        var history = Analyzer.ConflictHistory(git, targetSha, depth, progress);
        Write("repo/conflict-history.json", inputs, history);
        return history;
    }

    /// <summary>The gates and test suites the target's policy defines.</summary>
    public record Topology(List<string> Gates, List<string> Suites);

    static SortedDictionary<string, string> TopologyInputs(Git git, string targetSha) =>
        new() { ["gitwizz"] = Fingerprint.Build, ["config"] = ConfigHash(git, targetSha) };

    /// <summary>State of the stored policy and test topology.</summary>
    public ArtifactState TopologyState(Git git, string targetSha) =>
        Enabled ? Check(Read<Topology>("repo/topology.json"), TopologyInputs(git, targetSha)) : ArtifactState.Off;

    /// <summary>Stores the policy and test topology of the target.</summary>
    public Topology RefreshTopology(Git git, string targetSha)
    {
        var config = RepoConfig.Load(git, targetSha);
        var topology = new Topology([.. config.GateSpecs().Select(g => $"{g.Id} ({g.Kind})")], [.. config.Tests.Select(t => $"{t.Id} ({t.Kind})")]);
        Write("repo/topology.json", TopologyInputs(git, targetSha), topology);
        return topology;
    }

    // --- Gate and suite results ----------------------------------------------------------------------------------------

    /// <summary>A stored result for exactly these inputs, or null.</summary>
    public Artifact<T>? Reuse<T>(string kind, SortedDictionary<string, string> inputs) =>
        Read<T>(ResultPath(kind, inputs)) is { } a && Check(a, inputs).Current ? a : null;

    /// <summary>Stores a result under its inputs.</summary>
    public void Keep<T>(string kind, SortedDictionary<string, string> inputs, T value) => Write(ResultPath(kind, inputs), inputs, value);

    /// <summary>
    /// Inputs of a command run on a merged state, or null when its result can't be proven reusable: no workspace, no
    /// merged state, or an environment beyond the GITWIZZ_* variables (a provisioned test environment is never the same).
    /// </summary>
    public SortedDictionary<string, string>? CommandInputs(string kind, string state, string command, object definition, IDictionary<string, string> env)
    {
        if (!Enabled || env.Keys.Any(k => !k.StartsWith("GITWIZZ_"))) return null;
        return new()
        {
            ["gitwizz"] = Fingerprint.Build, ["kind"] = kind, ["state"] = state, ["command"] = command, ["tools"] = Fingerprint.Tools(command),
            ["definition"] = JsonSerializer.Serialize(definition, Json),
            ["env"] = string.Join('\n', env.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}={e.Value}")),
        };
    }

    // --- PR evaluation overlay -----------------------------------------------------------------------------------------

    /// <summary>What status shows of the last evaluation: the verdict and each gate's outcome.</summary>
    public record EvaluationData(string Verdict, string Profile, List<GateData> Gates);
    public record GateData(string Id, string Status, string Summary, bool BlocksMerge);

    /// <summary>
    /// What an evaluation's verdict depends on: commits, policy, provider state, strategy, profile and the tools of the
    /// commands it ran. commands: the commands as stored (re-fingerprinted now), or those of a fresh evaluation.
    /// </summary>
    public static SortedDictionary<string, string> EvaluationInputs(Git git, PullRequest pr, string targetSha, MergeStrategy strategy, string? profile,
        IEnumerable<string> commands)
    {
        var list = commands.Distinct().Order(StringComparer.Ordinal).ToList();
        return new()
        {
            ["gitwizz"] = Fingerprint.Build, ["target"] = targetSha, ["head"] = pr.HeadSha, ["config"] = ConfigHash(git, targetSha),
            ["provider"] = ProviderSnapshot(pr), ["strategy"] = strategy.ToString().ToLowerInvariant(), ["profile"] = profile ?? "(by risk)",
            ["commands"] = string.Join('\n', list), ["tools"] = Fingerprint.Of(string.Join('\n', list.Select(Fingerprint.Tools))),
        };
    }

    /// <summary>Records an evaluation as the PR's latest.</summary>
    public void KeepEvaluation(Git git, string provider, Evaluation e, string? profile)
    {
        if (!Enabled) return;
        var commands = e.Gates.Select(g => g.Command).OfType<string>().Concat(e.Trace?.Selected.Select(s => s.Suite.Run) ?? []);
        Write(PrPath(provider, e.Pr.Id, "evaluation.json"), EvaluationInputs(git, e.Pr, e.TargetSha, e.Strategy, profile, commands),
            new EvaluationData(e.Verdict, e.Profile, [.. e.Gates.Select(g => new GateData(g.Id, Evaluator.Name(g.Status), g.Summary, g.BlocksMerge))]));
    }

    /// <summary>The PR's latest evaluation and whether it still applies (same inputs, tools re-checked now).</summary>
    public (ArtifactState State, EvaluationData? Last) EvaluationState(Git git, string provider, PullRequest pr, string targetSha, MergeStrategy strategy)
    {
        if (!Enabled) return (ArtifactState.Off, null);
        var stored = Read<EvaluationData>(PrPath(provider, pr.Id, "evaluation.json"));
        if (stored == null) return (ArtifactState.Missing, null);
        var profile = stored.Inputs.GetValueOrDefault("profile") is { } p && p != "(by risk)" ? p : null;
        var commands = stored.Inputs.GetValueOrDefault("commands", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return (Check(stored, EvaluationInputs(git, pr, targetSha, strategy, profile, commands)), stored.Value);
    }

    /// <summary>The analysis state of a PR without computing anything.</summary>
    public ArtifactState AnalysisState(string provider, PullRequest pr, string targetSha) => Enabled
        ? Check(Read<AnalysisData>(PrPath(provider, pr.Id, "analysis.json")),
            new SortedDictionary<string, string> { ["gitwizz"] = Fingerprint.Build, ["target"] = targetSha, ["head"] = pr.HeadSha })
        : ArtifactState.Off;
}
