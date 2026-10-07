using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gitwizz;

/// <summary>
/// Benchmark settings from .gitwizz.yml benchmark:. Cases are labelled evidence packages; the baseline is an accepted
/// result, committed to the repository. Thresholds decide whether the model and prompt are good enough to block.
/// </summary>
public record BenchmarkPolicy
{
    public string Cases { get; init; } = "benchmark";
    public string Baseline { get; init; } = ".gitwizz/benchmark-baseline.json";
    public BenchmarkThresholds Thresholds { get; init; } = new();
}

/// <summary>Minimum quality for an AI gate to block. Defaults are deliberately strict.</summary>
public record BenchmarkThresholds
{
    public double MinPrecision { get; init; } = 0.8;
    public double MinRecall { get; init; } = 0.5;
    public double MaxFalsePositiveRate { get; init; } = 0.1;
    public int MinCases { get; init; } = 10;
    public double MaxRegression { get; init; } = 0.02; // allowed drop against the baseline per metric
}

/// <summary>A finding the case says the review must report: file and new-side line (±3), optionally a rule id.</summary>
public record ExpectedFinding(string File, int Line, string? Rule = null);

/// <summary>One case's outcome: true positives, false positives, missed findings, and findings rejected for missing evidence.</summary>
public record CaseResult(string Id, int TruePositives, int FalsePositives, int Missed, int Rejected, bool Negative, string? Error, List<ReviewFinding> Findings);

/// <summary>Aggregate quality of one model + prompt on the case set.</summary>
public record BenchmarkMetrics(int Cases, int Errors, double Precision, double Recall, double FalsePositiveRate, double EvidenceRejectionRate);

/// <summary>
/// Measures AI review quality offline against labelled cases, compares it with the accepted baseline, and decides
/// whether an ai-review gate may block. Runs only against the configured self-hosted endpoint and never writes to the
/// repository except the result file it is told to write.
/// </summary>
public static class Benchmark
{
    /// <summary>Line tolerance when matching a reported finding to an expected one.</summary>
    public const int LineTolerance = 3;

    /// <summary>Loads the cases: every *.json under dir, sorted by name; each is an evidence package plus "expected" (and optional "id").</summary>
    public static List<(string Id, JsonObject Package, List<ExpectedFinding> Expected)> LoadCases(string dir)
    {
        if (!Directory.Exists(dir)) throw new InvalidOperationException($"benchmark cases not found: {dir}");
        var cases = new List<(string, JsonObject, List<ExpectedFinding>)>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            JsonObject doc;
            try { doc = JsonNode.Parse(File.ReadAllText(file))!.AsObject(); }
            catch (JsonException e) { throw new InvalidOperationException($"benchmark case {file}: {e.Message}"); }
            if (doc["expected"] is not JsonArray expected) throw new InvalidOperationException($"benchmark case {file}: needs an \"expected\" array (empty for a case with nothing to find)");
            var id = doc["id"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(file);
            // Labels never reach the model.
            var package = JsonNode.Parse(doc.ToJsonString())!.AsObject();
            package.Remove("expected");
            package.Remove("id");
            package.Remove("notes");
            cases.Add((id, package, [.. expected.Select(e => new ExpectedFinding(e!["file"]!.GetValue<string>(), e["line"]!.GetValue<int>(), e["rule"]?.GetValue<string>()))]));
        }
        return cases;
    }

    /// <summary>New-side line ranges of each file in the package's diff, as the gate gets them from git.</summary>
    public static Dictionary<string, List<(int Start, int End)>> ChangedLines(JsonObject package) =>
        (package["diff"] as JsonArray ?? []).SelectMany(d => Analyzer.ParseHunks(d!["patch"]!.GetValue<string>(), newSide: true))
            .GroupBy(h => h.Path).ToDictionary(g => g.Key, g => g.Select(h => (h.Start, h.Start + Math.Max(h.Count, 1) - 1)).ToList());

    /// <summary>Scores one case: a kept (not info) finding is a true positive when it matches an unmatched expected finding.</summary>
    public static CaseResult Score(string id, List<ExpectedFinding> expected, List<ReviewFinding> findings)
    {
        var reported = findings.Where(f => f.Verdict != "rejected" && f.Severity != "info").ToList();
        var open = expected.ToList();
        int tp = 0, fp = 0;
        foreach (var f in reported)
        {
            var match = open.FirstOrDefault(e => e.File == f.File && f.Line is { } l && Math.Abs(l - e.Line) <= LineTolerance
                && (e.Rule is null || string.Equals(e.Rule, f.Rule, StringComparison.OrdinalIgnoreCase)));
            if (match != null) { open.Remove(match); tp++; } else fp++;
        }
        return new(id, tp, fp, open.Count, findings.Count(f => f.Verdict == "rejected"), expected.Count == 0, null, findings);
    }

    /// <summary>Precision, recall, false-positive rate (share of clean cases with any reported finding) and evidence rejection rate.</summary>
    public static BenchmarkMetrics Metrics(List<CaseResult> results)
    {
        var ok = results.Where(r => r.Error == null).ToList();
        int tp = ok.Sum(r => r.TruePositives), fp = ok.Sum(r => r.FalsePositives), fn = ok.Sum(r => r.Missed);
        var negatives = ok.Where(r => r.Negative).ToList();
        var raw = ok.Sum(r => r.Findings.Count);
        return new(results.Count, results.Count - ok.Count,
            tp + fp == 0 ? (fn == 0 ? 1 : 0) : (double)tp / (tp + fp),
            tp + fn == 0 ? 1 : (double)tp / (tp + fn),
            negatives.Count == 0 ? 0 : (double)negatives.Count(r => r.FalsePositives > 0) / negatives.Count,
            raw == 0 ? 0 : (double)ok.Sum(r => r.Rejected) / raw);
    }

    /// <summary>Every threshold the metrics miss; empty when they meet all of them. Errored cases count against the run.</summary>
    public static List<string> Failures(BenchmarkMetrics m, BenchmarkThresholds t)
    {
        var f = new List<string>();
        if (m.Cases < t.MinCases) f.Add($"{m.Cases} case(s), need at least {t.MinCases}");
        if (m.Errors > 0) f.Add($"{m.Errors} case(s) errored");
        if (m.Precision < t.MinPrecision) f.Add($"precision {m.Precision:0.00} < {t.MinPrecision:0.00}");
        if (m.Recall < t.MinRecall) f.Add($"recall {m.Recall:0.00} < {t.MinRecall:0.00}");
        if (m.FalsePositiveRate > t.MaxFalsePositiveRate) f.Add($"false-positive rate {m.FalsePositiveRate:0.00} > {t.MaxFalsePositiveRate:0.00}");
        return f;
    }

    /// <summary>Metrics that dropped by more than the allowed regression against a baseline result.</summary>
    public static List<string> Regressions(BenchmarkMetrics now, JsonNode baseline, BenchmarkThresholds t)
    {
        double B(string k) => baseline["metrics"]?[k]?.GetValue<double>() ?? 0;
        var r = new List<string>();
        if (B("precision") - now.Precision > t.MaxRegression) r.Add($"precision {B("precision"):0.00} -> {now.Precision:0.00}");
        if (B("recall") - now.Recall > t.MaxRegression) r.Add($"recall {B("recall"):0.00} -> {now.Recall:0.00}");
        if (now.FalsePositiveRate - B("falsePositiveRate") > t.MaxRegression) r.Add($"false-positive rate {B("falsePositiveRate"):0.00} -> {now.FalsePositiveRate:0.00}");
        return r;
    }

    /// <summary>Runs every case through the same review the gate uses. A case that errors is recorded, not fatal.</summary>
    public static List<CaseResult> Run(List<(string Id, JsonObject Package, List<ExpectedFinding> Expected)> cases, ReviewPolicy review,
        HttpMessageHandler? handler = null, Action<string>? status = null)
    {
        if (Endpoints.Problem(review.Endpoint, review.AllowedHosts) is { } problem) throw new InvalidOperationException(problem);
        var gate = new AiReviewGate(handler);
        var results = new List<CaseResult>();
        foreach (var (id, package, expected) in cases)
        {
            status?.Invoke($"Case {id}…");
            try { results.Add(Score(id, expected, gate.Review(package, review, ChangedLines(package)).Findings)); }
            catch (InvalidOperationException e) { results.Add(new(id, 0, 0, expected.Count, 0, expected.Count == 0, e.Message, [])); }
        }
        return results;
    }

    /// <summary>The machine-readable result (schema gitwizz.benchmark/v1); also the format of an accepted baseline.</summary>
    public static JsonObject Result(List<CaseResult> results, ReviewPolicy review, BenchmarkThresholds t, JsonNode? baseline)
    {
        var m = Metrics(results);
        var failures = Failures(m, t);
        var baselineMatches = baseline?["fingerprint"]?.GetValue<string>() == AiReviewGate.Fingerprint(review);
        var regressions = baseline is null ? [] : Regressions(m, baseline, t);
        return new JsonObject
        {
            ["schema"] = "gitwizz.benchmark/v1",
            ["fingerprint"] = AiReviewGate.Fingerprint(review),
            ["model"] = review.Model,
            ["prompt"] = AiReviewGate.PromptVersion,
            ["endpointHost"] = new Uri(review.Endpoint!).Host,
            ["metrics"] = new JsonObject
            {
                ["cases"] = m.Cases, ["errors"] = m.Errors, ["precision"] = Math.Round(m.Precision, 4), ["recall"] = Math.Round(m.Recall, 4),
                ["falsePositiveRate"] = Math.Round(m.FalsePositiveRate, 4), ["evidenceRejectionRate"] = Math.Round(m.EvidenceRejectionRate, 4),
                // gitwizz doesn't generate tests yet: these stay unmeasured rather than invented.
                ["generatedTestPassRate"] = null, ["acceptanceCriterionCoverage"] = null, ["mutationScore"] = null,
            },
            ["thresholds"] = new JsonObject
            {
                ["minPrecision"] = t.MinPrecision, ["minRecall"] = t.MinRecall, ["maxFalsePositiveRate"] = t.MaxFalsePositiveRate,
                ["minCases"] = t.MinCases, ["maxRegression"] = t.MaxRegression,
            },
            ["passed"] = failures.Count == 0,
            ["failures"] = new JsonArray([.. failures.Select(f => (JsonNode)f)]),
            ["baseline"] = baseline is null ? null : new JsonObject
            {
                ["fingerprint"] = baseline["fingerprint"]?.GetValue<string>(), ["sameConfiguration"] = baselineMatches,
                ["regressions"] = new JsonArray([.. regressions.Select(r => (JsonNode)r)]),
            },
            ["cases"] = new JsonArray([.. results.Select(r => (JsonNode)new JsonObject
            {
                ["id"] = r.Id, ["truePositives"] = r.TruePositives, ["falsePositives"] = r.FalsePositives, ["missed"] = r.Missed,
                ["rejected"] = r.Rejected, ["error"] = r.Error,
                ["findings"] = new JsonArray([.. r.Findings.Select(f => (JsonNode)new JsonObject
                {
                    ["rule"] = f.Rule, ["file"] = f.File, ["line"] = f.Line, ["severity"] = f.Severity, ["confidence"] = f.Confidence, ["verdict"] = f.Verdict,
                })]),
            })]),
        };
    }

    /// <summary>Indented JSON of a result.</summary>
    public static string Json(JsonObject result) =>
        result.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}

/// <summary>
/// The promotion rule: an ai-review gate blocks only when the target branch carries an accepted benchmark baseline
/// for exactly this model and prompt, and its metrics meet the thresholds configured now. Read from the target's
/// commit, so a pull request can't promote the gate that judges it.
/// </summary>
public static class Promotion
{
    /// <summary>Null if the ai-review gate may block, else why not.</summary>
    public static string? Check(GateContext ctx) => Check(ctx.Git, ctx.TargetSha, ctx.Config);

    /// <summary>Null if an ai-review gate configured by config may block at commit, else why not.</summary>
    public static string? Check(Git git, string commit, RepoConfig config)
    {
        var path = config.Benchmark.Baseline;
        if (git.Try("show", $"{commit}:{path}") is not { ExitCode: 0 } r)
            return $"no accepted benchmark baseline ({path}) on the target branch; run gitwizz benchmark --accept and commit it";
        JsonNode baseline;
        try { baseline = JsonNode.Parse(r.Stdout)!; }
        catch (JsonException e) { return $"benchmark baseline {path} is not valid JSON: {e.Message}"; }
        var fingerprint = AiReviewGate.Fingerprint(config.Review);
        if (baseline["fingerprint"]?.GetValue<string>() != fingerprint)
            return $"benchmark baseline is for {baseline["fingerprint"]}, not {fingerprint}: re-run the benchmark for this model and prompt";
        double M(string k) => baseline["metrics"]?[k]?.GetValue<double>() ?? 0;
        var metrics = new BenchmarkMetrics((int)M("cases"), (int)M("errors"), M("precision"), M("recall"), M("falsePositiveRate"), M("evidenceRejectionRate"));
        return Benchmark.Failures(metrics, config.Benchmark.Thresholds) is { Count: > 0 } f ? "benchmark below thresholds: " + string.Join("; ", f) : null;
    }
}
