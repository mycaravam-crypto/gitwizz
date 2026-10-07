using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Gitwizz;

/// <summary>
/// AI review settings from .gitwizz.yml review:. Endpoint is an OpenAI-compatible base URL (…/v1) of a self-hosted
/// model; public inference services are refused. The context settings bound what the evidence package may hold.
/// </summary>
public record ReviewPolicy
{
    public string? Endpoint { get; init; }
    public string Model { get; init; } = "";
    public string? ApiKeyEnv { get; init; }                 // variable holding the endpoint's key, if it needs one
    public List<string> AllowedHosts { get; init; } = [];   // self-hosted hosts with public-looking names
    public int Timeout { get; init; } = 120;                // seconds per request
    public int MaxTokens { get; init; } = 2000;             // answer length
    public int MaxContextChars { get; init; } = 60000;      // evidence package size
    public double MinConfidence { get; init; } = 0.5;       // below: downgraded to info
    public List<string> Rules { get; init; } = [];          // architecture and coding rules, one per entry
    public List<string> RuleFiles { get; init; } = [];      // files holding more rules (e.g. docs/CODING.md)
    public List<string> Docs { get; init; } = [];           // ADRs and docs to excerpt where they mention the change
}

/// <summary>
/// The bounded, evidence-based context of a pull request for review: only what the change touches, never the whole
/// repository. Built deterministically, so the same state gives the same package and the same hash.
/// </summary>
public static class Evidence
{
    /// <summary>Line ranges (new side) the PR adds or changes, per file: a finding must point into one of them.</summary>
    public static Dictionary<string, List<(int Start, int End)>> ChangedLines(Git git, PullRequest pr) =>
        Analyzer.ParseHunks(git.Run("-c", "core.quotePath=false", "diff", "-M", "--no-ext-diff", "--no-color", "--src-prefix=a/", "--dst-prefix=b/",
                "-U0", pr.BaseSha, pr.HeadSha), newSide: true)
            .GroupBy(h => h.Path).ToDictionary(g => g.Key, g => g.Select(h => (h.Start, h.Start + Math.Max(h.Count, 1) - 1)).ToList());

    /// <summary>
    /// The package for pr at commit (the merged state or the PR head): PR text, requirements and criteria, changed files
    /// and symbols, the diff, references to changed symbols elsewhere, applicable rules, doc excerpts and selected tests.
    /// Each section has a share of review.max_context_chars; what doesn't fit is cut and listed under budget.truncated.
    /// </summary>
    public static JsonObject Build(Git git, PullRequest pr, RepoConfig config, string target, string targetSha, string commit, Trace? trace = null)
    {
        var budget = Math.Max(4000, config.Review.MaxContextChars);
        var truncated = new List<string>();
        string Cut(string text, int max, string what)
        {
            if (text.Length <= max) return text;
            truncated.Add(what);
            return text[..Math.Max(0, max)] + "\n…[truncated]";
        }

        var symbols = Traceability.ChangedSymbols(pr);
        var package = new JsonObject
        {
            ["schema"] = "gitwizz.evidence/v1",
            ["pr"] = new JsonObject { ["id"] = pr.Id, ["title"] = pr.Title, ["description"] = Cut(pr.Body, budget / 15, "description") },
            ["target"] = new JsonObject { ["name"] = target, ["sha"] = targetSha },
            ["commit"] = commit,
            ["requirements"] = new JsonArray([.. pr.WorkItems.Select(w => (JsonNode)new JsonObject
            {
                ["id"] = w.Id, ["type"] = w.Type, ["title"] = w.Title, ["state"] = w.State,
                ["criteria"] = new JsonArray([.. w.AcceptanceCriteria.Select((c, i) => (JsonNode)new JsonObject { ["id"] = Traceability.CriterionId(w, i), ["text"] = c })]),
            })]),
        };

        // Changed files; lockfiles, generated and binary files are named but their diff is left out.
        var omitted = new List<string>();
        var files = new JsonArray();
        foreach (var f in pr.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var cls = config.Classify(f.Path);
            files.Add(new JsonObject { ["path"] = f.Path, ["change"] = f.Kind.ToString().ToLowerInvariant(), ["oldPath"] = f.OldPath, ["class"] = cls.ToString().ToLowerInvariant() });
            if (cls is FileClass.Lockfile or FileClass.Generated or FileClass.Binary) omitted.Add($"{f.Path} ({cls.ToString().ToLowerInvariant()}: diff omitted)");
        }
        package["changes"] = new JsonObject
        {
            ["files"] = files,
            ["symbols"] = new JsonArray([.. pr.Members.Order(StringComparer.Ordinal).Select(m => (JsonNode)m)]),
            ["apiChanges"] = new JsonArray([.. pr.Api.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => (JsonNode)new JsonObject
            {
                ["name"] = a.Key,
                ["argsBefore"] = new JsonArray([.. a.Value.Before.Order().Select(n => (JsonNode)n)]),
                ["argsAfter"] = new JsonArray([.. a.Value.After.Order().Select(n => (JsonNode)n)]),
            })]),
        };

        // The diff, file by file, within its share.
        var patch = git.Run("-c", "core.quotePath=false", "diff", "-M", "--no-ext-diff", "--no-color", "-U3", pr.BaseSha, pr.HeadSha);
        var diffBudget = budget * 55 / 100;
        var diff = new JsonArray();
        foreach (var part in Regex.Split(patch, @"(?m)^(?=diff --git )").Where(p => p.StartsWith("diff --git ")))
        {
            var path = Regex.Match(part, @"^\+\+\+ b/(.+)$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value
                : Regex.Match(part, @"^diff --git a/\S+ b/(\S+)").Groups[1].Value;
            if (omitted.Any(o => o.StartsWith(path + " "))) continue;
            var text = Cut(part, Math.Max(0, diffBudget), $"diff of {path}");
            diffBudget -= text.Length;
            diff.Add(new JsonObject { ["path"] = path, ["patch"] = text });
        }
        package["diff"] = diff;

        // Where changed symbols are used outside the change (callers, implementers), from git grep at commit.
        var callers = new JsonArray();
        var changedPaths = pr.Files.Select(f => f.Path).ToHashSet();
        if (symbols.Count > 0)
        {
            var grep = git.Try([.. new[] { "grep", "-n", "-I", "-w", "-F" }, .. symbols.Take(20).SelectMany(s => new[] { "-e", s }), commit, "--"]);
            var callerBudget = budget / 10;
            foreach (var line in grep.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Split(':', 4); // commit:path:line:text
                if (p.Length < 4 || changedPaths.Contains(p[1])) continue;
                var text = p[3].Trim();
                if ((callerBudget -= p[1].Length + text.Length + 16) < 0) { truncated.Add("callers"); break; }
                callers.Add(new JsonObject
                {
                    ["symbol"] = symbols.FirstOrDefault(s => Regex.IsMatch(text, $@"\b{Regex.Escape(s)}\b")) ?? "", ["path"] = p[1], ["line"] = int.Parse(p[2]),
                    ["text"] = text.Length > 200 ? text[..200] + "…" : text,
                });
            }
        }
        package["callers"] = callers;

        // Rules and doc excerpts that mention what changed.
        var tree = git.Run("ls-tree", "-r", "--name-only", "-z", commit).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var ruleFiles = tree.Where(p => config.Review.RuleFiles.Any(g => RepoConfig.Matches(g, p))).Order(StringComparer.Ordinal).ToList();
        var docFiles = tree.Where(p => config.Review.Docs.Any(g => RepoConfig.Matches(g, p))).Order(StringComparer.Ordinal).ToList();
        var blobs = git.ReadBlobs([.. ruleFiles.Concat(docFiles).Select(p => $"{commit}:{p}")]);
        var rulesBudget = budget * 15 / 100;
        var rules = new JsonArray([.. config.Review.Rules.Select(r => (JsonNode)new JsonObject { ["source"] = RepoConfig.FileName, ["text"] = r })]);
        foreach (var (path, text) in ruleFiles.Zip(blobs))
        {
            var t = Cut(text, Math.Max(0, rulesBudget), $"rules in {path}");
            rulesBudget -= t.Length;
            rules.Add(new JsonObject { ["source"] = path, ["text"] = t });
        }
        package["rules"] = rules;

        var keywords = symbols.Concat(pr.Files.Select(f => Path.GetFileNameWithoutExtension(f.Path))).Where(k => k.Length >= 4).Distinct().ToList();
        var docsBudget = budget * 15 / 100;
        var docs = new JsonArray();
        foreach (var (path, text) in docFiles.Zip(blobs.Skip(ruleFiles.Count)))
            foreach (var para in Regex.Split(text, @"\n\s*\n").Where(p => keywords.Any(k => p.Contains(k, StringComparison.OrdinalIgnoreCase))))
            {
                if ((docsBudget -= para.Length) < 0) { truncated.Add($"docs ({path})"); break; }
                docs.Add(new JsonObject { ["path"] = path, ["excerpt"] = para.Trim() });
            }
        package["docs"] = docs;
        package["tests"] = new JsonArray([.. (trace?.Selected ?? []).Select(s => (JsonNode)new JsonObject
        {
            ["id"] = s.Suite.Id, ["kind"] = s.Suite.Kind, ["reasons"] = new JsonArray([.. s.Reasons.Select(r => (JsonNode)r)]),
            ["result"] = trace!.Runs.FirstOrDefault(r => r.Suite == s.Suite.Id)?.Summary,
        })]);
        package["omitted"] = new JsonArray([.. omitted.Select(o => (JsonNode)o)]);
        package["budget"] = new JsonObject { ["maxChars"] = budget, ["truncated"] = new JsonArray([.. truncated.Distinct().Select(t => (JsonNode)t)]) };
        package["budget"]!["usedChars"] = package.ToJsonString().Length;
        package["hash"] = Hash(package);
        return package;
    }

    /// <summary>sha256 of the package without its hash field, so anyone can check that a review saw exactly this package.</summary>
    public static string Hash(JsonObject package)
    {
        var copy = JsonNode.Parse(package.ToJsonString())!.AsObject();
        copy.Remove("hash");
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(copy.ToJsonString())));
    }

    /// <summary>Indented JSON for gitwizz evidence and evidence directories.</summary>
    public static string Json(JsonObject package) =>
        package.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
