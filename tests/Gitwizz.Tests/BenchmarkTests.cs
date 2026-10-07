using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Gitwizz;

namespace Gitwizz.Tests;

public class BenchmarkTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("gitwizz-bench").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    static readonly ReviewPolicy Review = new() { Endpoint = "http://localhost:8000/v1", Model = "local-coder" };

    /// <summary>Answers with the findings registered for the case whose marker the package contains; records request bodies.</summary>
    sealed class FakeModel(Dictionary<string, string> answers) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content!.ReadAsStringAsync(ct).Result;
            Bodies.Add(body);
            var content = answers.First(a => body.Contains(a.Key)).Value;
            var answer = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["content"] = content } }) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json") };
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(Send(r, ct));
    }

    static string Case(string marker, string expected) => $$"""
        {
          "id": "{{marker}}",
          "notes": "labelled by hand",
          "expected": {{expected}},
          "schema": "gitwizz.evidence/v1",
          "pr": { "id": "#1", "title": "{{marker}}", "description": "" },
          "changes": { "files": [ { "path": "src/A.cs", "change": "modified" } ] },
          "diff": [ { "path": "src/A.cs", "patch": "diff --git a/src/A.cs b/src/A.cs\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -10,1 +10,2 @@\n-old();\n+var x = Danger();\n+Log(x);" } ]
        }
        """;

    const string Hit = """{"findings":[{"rule":"r1","file":"src/A.cs","line":10,"severity":"error","confidence":0.9,"message":"m","evidence":"var x = Danger();"}]}""";
    const string Nothing = """{"findings":[]}""";
    const string Invented = """{"findings":[{"rule":"r1","file":"src/A.cs","line":10,"severity":"error","confidence":0.9,"message":"m","evidence":"Danger(); DropTables();"}]}""";

    [Fact]
    public void Scores_cases_without_leaking_labels_and_compares_with_a_baseline()
    {
        var cases = Path.Combine(_dir, "cases");
        Directory.CreateDirectory(cases);
        File.WriteAllText(Path.Combine(cases, "1-found.json"), Case("case-found", """[{"file":"src/A.cs","line":11}]"""));
        File.WriteAllText(Path.Combine(cases, "2-missed.json"), Case("case-missed", """[{"file":"src/A.cs","line":10,"rule":"r1"}]"""));
        File.WriteAllText(Path.Combine(cases, "3-clean.json"), Case("case-clean", "[]"));
        File.WriteAllText(Path.Combine(cases, "4-clean-fp.json"), Case("case-fp", "[]"));
        File.WriteAllText(Path.Combine(cases, "5-invented.json"), Case("case-invented", "[]"));
        var model = new FakeModel(new()
        {
            ["case-found"] = Hit, ["case-missed"] = Nothing, ["case-clean"] = Nothing, ["case-fp"] = Hit, ["case-invented"] = Invented,
        });

        var loaded = Benchmark.LoadCases(cases);
        Assert.Equal(["case-found", "case-missed", "case-clean", "case-fp", "case-invented"], loaded.Select(c => c.Id));
        var results = Benchmark.Run(loaded, Review, model);
        Assert.All(model.Bodies, b => Assert.DoesNotContain("expected", b));   // labels never reach the model
        Assert.All(model.Bodies, b => Assert.DoesNotContain("labelled by hand", b));

        Assert.Equal((1, 0, 0), (results[0].TruePositives, results[0].FalsePositives, results[0].Missed));
        Assert.Equal(1, results[1].Missed);
        Assert.Equal(1, results[3].FalsePositives);
        Assert.Equal(1, results[4].Rejected); // invented evidence never counts as a finding
        var m = Benchmark.Metrics(results);
        Assert.Equal(0.5, m.Precision);
        Assert.Equal(0.5, m.Recall);
        Assert.Equal(1 / 3.0, m.FalsePositiveRate, 6);

        var t = new BenchmarkThresholds { MinCases = 5 };
        var result = Benchmark.Result(results, Review, t, null);
        Assert.Equal("gitwizz.benchmark/v1", result["schema"]!.GetValue<string>());
        Assert.False(result["passed"]!.GetValue<bool>());
        Assert.Equal(["precision 0.50 < 0.80", "false-positive rate 0.33 > 0.10"], result["failures"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.Null(result["metrics"]!["mutationScore"]); // not measured, never invented

        var better = JsonNode.Parse("""{"fingerprint":"x","metrics":{"precision":0.9,"recall":0.5,"falsePositiveRate":0.0}}""")!;
        var compared = Benchmark.Result(results, Review, t, better);
        Assert.Equal(["precision 0.90 -> 0.50", "false-positive rate 0.00 -> 0.33"],
            compared["baseline"]!["regressions"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.False(compared["baseline"]!["sameConfiguration"]!.GetValue<bool>());
    }

    [Fact]
    public void Errors_are_recorded_per_case_and_fail_the_run()
    {
        var pkg = JsonNode.Parse(Case("x", "[]"))!.AsObject();
        var results = Benchmark.Run([("bad", pkg, [])], Review, new FakeModel(new() { ["x"] = "no json here" }));
        Assert.NotNull(results.Single().Error);
        Assert.Contains("1 case(s) errored", Benchmark.Failures(Benchmark.Metrics(results), new BenchmarkThresholds { MinCases = 1 }));
        Assert.Throws<InvalidOperationException>(() => Benchmark.Run([], Review with { Endpoint = "https://api.openai.com/v1" }));
        Assert.Throws<InvalidOperationException>(() => Benchmark.LoadCases(Path.Combine(_dir, "missing")));
    }

    [Fact]
    public void A_committed_passing_baseline_for_this_model_promotes_the_gate_to_blocking()
    {
        var git = new Git(_dir);
        git.Run("init", "-q", "-b", "main");
        git.Run("config", "user.email", "t@t"); git.Run("config", "user.name", "t");
        var yaml = "review: { endpoint: 'http://localhost:8000/v1', model: local-coder }\nbenchmark: { thresholds: { min_cases: 2 } }\n";
        File.WriteAllText(Path.Combine(_dir, ".gitwizz.yml"), yaml);
        git.Run("add", "-A"); git.Run("commit", "-q", "-m", "config");
        var config = RepoConfig.Load(git, git.RevParse("HEAD"));
        Assert.Contains("no accepted benchmark baseline", Promotion.Check(git, git.RevParse("HEAD"), config));

        string Baseline(string fingerprint, double precision) =>
            "{\"fingerprint\":\"" + fingerprint + "\",\"metrics\":{\"cases\":3,\"errors\":0,\"precision\":" + precision.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"recall\":0.8,\"falsePositiveRate\":0.0}}";
        void Commit(string json)
        {
            Directory.CreateDirectory(Path.Combine(_dir, ".gitwizz"));
            File.WriteAllText(Path.Combine(_dir, ".gitwizz/benchmark-baseline.json"), json);
            git.Run("add", "-A"); git.Run("commit", "-q", "-m", "baseline");
        }
        var fp = AiReviewGate.Fingerprint(config.Review);
        Commit(Baseline("other-model|review-v1|abc", 0.95));
        Assert.Contains("re-run the benchmark", Promotion.Check(git, git.RevParse("HEAD"), config));
        Commit(Baseline(fp, 0.6));
        Assert.Contains("precision 0.60 < 0.80", Promotion.Check(git, git.RevParse("HEAD"), config));
        Commit(Baseline(fp, 0.95));
        Assert.Null(Promotion.Check(git, git.RevParse("HEAD"), config));
        // Same model, different prompt hash: a prompt change needs a new benchmark.
        Assert.NotNull(Promotion.Check(git, git.RevParse("HEAD"), config with { Review = config.Review with { Model = "other" } }));
    }

    [Theory]
    [InlineData("benchmark: { thresholds: { min_precision: 2 } }")]
    [InlineData("benchmark: { thresholds: { min_cases: 0 } }")]
    public void Invalid_thresholds_are_rejected(string yaml) =>
        Assert.Contains("benchmark.thresholds", Assert.Throws<InvalidOperationException>(() => RepoConfig.Parse(yaml)).Message);
}
