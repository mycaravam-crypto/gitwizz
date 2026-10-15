using System.Text.Json;
using System.Text.Json.Nodes;
using Gitwizz;
using Json.Schema;
using Xunit;

namespace Gitwizz.Tests;

/// <summary>
/// The gitwizz.version-history/v2 contract (docs/version-history): example.json and example.md are the export of a
/// scripted repository with fixed dates and identity, so their SHAs and content are reproducible, and they validate
/// against the schema. Set GITWIZZ_UPDATE_GOLDEN=1 to rewrite them after an intended change (and review the diff).
/// </summary>
public sealed class VersionHistoryContractTests : IDisposable
{
    public static readonly string Docs = Path.Combine(FindRoot(), "docs", "version-history");
    static readonly JsonSchema Schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(Docs, "gitwizz.version-history.v2.schema.json")));

    readonly string _dir = Directory.CreateTempSubdirectory("gitwizz-version-contract").FullName;
    readonly Dictionary<string, string> _env = new()
    {
        ["GIT_AUTHOR_NAME"] = "Ada", ["GIT_AUTHOR_EMAIL"] = "ada@example.com",
        ["GIT_COMMITTER_NAME"] = "Ada", ["GIT_COMMITTER_EMAIL"] = "ada@example.com",
        ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = "/dev/null",
    };
    int _tick;

    public void Dispose() => Directory.Delete(_dir, true);

    static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "gitwizz.slnx"))) return d.FullName;
        throw new InvalidOperationException("gitwizz.slnx not found above the test directory");
    }

    string G(params string[] args)
    {
        var r = Git.Exec(_dir, "git", args, env: _env);
        if (r.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {r.Stderr}");
        return r.Stdout.Trim();
    }

    /// <summary>The clock: one hour per step from 2026-09-01 08:00 UTC, for commits and tags alike.</summary>
    void Tick()
    {
        var date = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero).AddHours(++_tick).ToString("yyyy-MM-ddTHH:mm:ssZ");
        _env["GIT_AUTHOR_DATE"] = _env["GIT_COMMITTER_DATE"] = date;
    }

    void Commit(string file, string message)
    {
        Tick();
        File.WriteAllText(Path.Combine(_dir, file), $"{_tick}\n");
        G("add", "-A");
        G("commit", "-q", "--no-gpg-sign", "-m", message);
    }

    void Tag(string name)
    {
        Tick();
        G("tag", "-a", name, "-m", "release " + name);
    }

    /// <summary>
    /// main: v1.0.0, v1.1.0 (a feature and a performance change), release/1.0 (v1.0.1) merged back, a breaking change in
    /// v2.0.0-rc.1, a fix for v2.0.0. release/1.0 has an unreleased fix; release/2.1 exists only on origin.
    /// </summary>
    VersionHistoryExport Scenario()
    {
        G("init", "-q", "-b", "main");
        G("remote", "add", "origin", "https://github.com/example/shop.git");
        Commit("app.txt", "chore: initial import"); Tag("v1.0.0");
        G("checkout", "-q", "-b", "release/1.0");
        Commit("api.txt", "fix(api): handle empty orders"); Tag("v1.0.1");
        G("checkout", "-q", "main");
        Commit("orders.txt", "feat(orders): export orders as CSV");
        Commit("prices.txt", "perf: cache price lookups");
        Tag("v1.1.0");
        Tick();
        G("merge", "-q", "--no-ff", "--no-gpg-sign", "-m", "Merge branch 'release/1.0'", "release/1.0");
        Commit("api2.txt", "feat(api)!: drop the v1 order endpoint\n\nBREAKING CHANGE: clients must call /v2/orders.");
        Tag("v2.0.0-rc.1");
        Commit("docs.txt", "fix: correct the CSV column order");
        Tag("v2.0.0");
        G("checkout", "-q", "release/1.0");
        Commit("api3.txt", "fix(api): reject negative quantities");
        G("checkout", "-q", "-b", "release/2.1", "main");
        Commit("ui.txt", "feat(ui): dark mode");
        G("update-ref", "refs/remotes/origin/release/2.1", "HEAD");
        G("checkout", "-q", "main");
        G("branch", "-q", "-D", "release/2.1");
        return VersionHistory.Export(new Git(_dir));
    }

    /// <summary>The first JSON path where actual differs from expected, with both values; null when equal.</summary>
    static string? FirstDifference(JsonNode? expected, JsonNode? actual, string path = "$")
    {
        switch (expected, actual)
        {
            case (JsonObject e, JsonObject a):
                foreach (var key in e.Select(p => p.Key).Union(a.Select(p => p.Key)))
                    if (FirstDifference(e[key], a[key], $"{path}.{key}") is { } d) return d;
                return null;
            case (JsonArray e, JsonArray a):
                for (var i = 0; i < Math.Max(e.Count, a.Count); i++)
                    if (FirstDifference(i < e.Count ? e[i] : null, i < a.Count ? a[i] : null, $"{path}[{i}]") is { } d) return d;
                return null;
            default:
                var (x, y) = (expected?.ToJsonString() ?? "null", actual?.ToJsonString() ?? "null");
                return x == y ? null : $"{path}: expected {x}, got {y}";
        }
    }

    [Fact]
    public void Examples_are_the_export_of_the_scripted_repository()
    {
        var export = Scenario();
        var json = VersionHistory.Json(export) + "\n";
        var markdown = VersionHistory.Markdown(export);
        var jsonPath = Path.Combine(Docs, "example.json");
        var mdPath = Path.Combine(Docs, "example.md");
        if (Environment.GetEnvironmentVariable("GITWIZZ_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(jsonPath, json);
            File.WriteAllText(mdPath, markdown);
        }
        var expected = File.ReadAllText(jsonPath).ReplaceLineEndings("\n");
        Assert.Null(FirstDifference(JsonNode.Parse(expected), JsonNode.Parse(json)));
        Assert.Equal(expected, json);
        Assert.Equal(File.ReadAllText(mdPath).ReplaceLineEndings("\n"), markdown);
    }

    [Fact]
    public void Example_covers_every_case_the_contract_names()
    {
        var v = Scenario().Versions.ToDictionary(x => x.Version);
        Assert.Equal(["2.1", "1.0", "2.0.0", "2.0.0-rc.1", "1.1.0", "1.0.1", "1.0.0"], v.Keys);
        Assert.Equal(("unreleased", "remote-branch"), (v["2.1"].Status, v["2.1"].Source));
        Assert.Equal(("unreleased", "branch"), (v["1.0"].Status, v["1.0"].Source));
        Assert.Equal(["1.1.0", "1.0.1"], v["2.0.0"].Bases.Select(b => b.Version));        // backport not repeated
        Assert.Equal(["1.1.0", "1.0.1"], v["2.0.0-rc.1"].Bases.Select(b => b.Version));
        Assert.Contains(v["2.0.0"].Changes, c => c.Breaking && c.Scope == "api");          // a release spans its rc
        Assert.Equal(["feature", "fix", "merge"], v["2.0.0"].Changes.Select(c => c.Category).Order());
        Assert.Equal(["feature", "performance"], v["1.1.0"].Changes.Select(c => c.Category).Order());
        Assert.Empty(v["1.0.0"].Bases);
        Assert.All(v.Values.Where(x => x.Status == "released"), x => Assert.NotNull(x.TaggedAt));
    }

    [Fact]
    public void Example_document_satisfies_the_schema()
    {
        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Docs, "example.json"))).RootElement;
        Assert.True(Schema.Evaluate(doc).IsValid);
        var broken = JsonNode.Parse(File.ReadAllText(Path.Combine(Docs, "example.json")))!;
        broken["versions"]![0]!["status"] = "shipped";
        Assert.False(Schema.Evaluate(JsonDocument.Parse(broken.ToJsonString()).RootElement).IsValid);
    }
}
