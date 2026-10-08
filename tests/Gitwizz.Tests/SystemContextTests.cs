using System.Text.Json.Nodes;
using Gitwizz;
using Spectre.Console;

namespace Gitwizz.Tests;

/// <summary>docwizz as the tests script it: a fixed model and diff in docwizz's JSON shape, or failures. Counts its runs.</summary>
sealed class FakeDocwizz : IDocwizz
{
    public string? VersionText { get; set; } = "docwizz 9.9.9 (commit test)";
    public JsonObject Model { get; set; } = new();
    public JsonObject Diff { get; set; } = new();
    public bool FailScan { get; set; }
    public bool FailDiff { get; set; }
    public int Scans { get; private set; }
    public int Diffs { get; private set; }

    public string? Version(string dir) => VersionText;

    public GitResult Scan(string dir, string modelFile)
    {
        Scans++;
        if (FailScan) return new GitResult(1, "", "boom: the scanner crashed\nat ...");
        File.WriteAllText(modelFile, Model.ToJsonString());
        return new GitResult(0, "Files 3", "");
    }

    GitResult IDocwizz.Diff(string dir, string baseRef, string headRef)
    {
        Diffs++;
        return FailDiff ? new GitResult(1, "", "unknown ref") : new GitResult(0, Diff.ToJsonString(), "");
    }
}

public class SystemContextTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("gitwizz-context").FullName;
    readonly string _store = Path.Combine(Path.GetTempPath(), "gitwizz-context-ws-" + Guid.NewGuid().ToString("N")[..8]);
    readonly Git _git;
    readonly FakeDocwizz _docwizz = new();

    const string Cancel = "cs:Shop.Orders.OrderService.Cancel(int)";
    static readonly WorkItem Story = new("AB#4711", "User Story", "Cancel orders", "Active", ["Cancelled orders must not be invoiced"]);

    public SystemContextTests()
    {
        _git = new Git(_dir);
        _git.Run("init", "-q", "-b", "main");
        _git.Run("config", "user.email", "t@t"); _git.Run("config", "user.name", "t");
        Write("docwizz.yaml", "architecture:\n  layers:\n    application: [\"src/Orders/*\", \"src/Billing/*\"]\n    domain: [\"src/Domain/*\"]\n");
        Write("src/Orders/OrderService.cs", "namespace Shop.Orders;\n\nclass OrderService\n{\n    /// <summary>Cancels an order.</summary>\n    public void Cancel(int id)\n    {\n        Load(id).Status = \"cancelled\";\n    }\n}\n");
        Write("src/Orders/OrdersController.cs", "class OrdersController { }\n");
        Write("src/Billing/InvoiceJob.cs", "class InvoiceJob { }\n");
        Write("src/Materials/MaterialService.cs", "class MaterialService { }\n");
        Write("src/Misc/Helper.cs", "class Helper { }\n");
        Write("tests/OrderServiceTests.cs", "class OrderServiceTests { }\n");
        Write(".gitwizz.yml", "tests:\n  - { id: orders, run: \"true\", files: [\"tests/*\"], covers: [\"src/Orders/*\"] }\n");
        Commit("init");
        Branch("feature", () => Write("src/Orders/OrderService.cs",
            "namespace Shop.Orders;\n\nclass OrderService\n{\n    /// <summary>Cancels an order.</summary>\n    public void Cancel(int id)\n    {\n        var order = Load(id);\n        order.Status = \"cancelled\";\n        audit.Write(\"cancelled\");\n    }\n}\n"));
        Branch("feature2", () => Write("src/Orders/OrdersController.cs", "class OrdersController { int Cancelled; }\n"));
        _git.Run("checkout", "-q", "main");
        _docwizz.Model = Model();
        _docwizz.Diff = Diff();
    }

    public void Dispose()
    {
        Directory.Delete(_dir, true);
        if (Directory.Exists(_store)) Directory.Delete(_store, true);
    }

    void Write(string f, string c) { var p = Path.Combine(_dir, f); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, c); }
    void Commit(string m) { _git.Run("add", "-A"); _git.Run("commit", "-q", "-m", m); }
    void Branch(string name, Action change) { _git.Run("checkout", "-q", "-b", name, "main"); change(); Commit(name); }

    // --- docwizz output, in its JSON shape ---------------------------------------------------------------------------

    static JsonObject Node(string id, string kind, string name, string file, int line, string[]? tags = null, string? route = null, bool doc = false, int? end = null)
    {
        var n = new JsonObject { ["id"] = id, ["kind"] = kind, ["name"] = name, ["file"] = file, ["line"] = line, ["endLine"] = end ?? line, ["hash"] = $"h{id.Length}{line}" };
        if (tags != null) n["tags"] = new JsonArray([.. tags.Select(t => (JsonNode)t)]);
        if (route != null) n["route"] = route;
        if (doc) n["doc"] = "<member><summary>Documented.</summary></member>";
        return n;
    }

    static JsonObject Edge(string from, string to, string kind, string? label = null)
    {
        var e = new JsonObject { ["from"] = from, ["to"] = to, ["kind"] = kind };
        if (label != null) e["label"] = label;
        return e;
    }

    /// <summary>
    /// The shop: OrdersController.Cancel (POST endpoint) calls OrderService.Cancel, which writes Order and calls AuditLog;
    /// InvoiceJob.Process reads Order and calls InvoiceRepository; ShopDb persists Order on SQL Server (inferred).
    /// MaterialService has nothing to do with any of it.
    /// </summary>
    static JsonObject Model(params JsonObject[] extraNodes)
    {
        JsonObject[] nodes =
        [
            Node("cs:Shop.Orders.OrderService", "class", "OrderService", "src/Orders/OrderService.cs", 3, ["service"], end: 11),
            Node(Cancel, "method", "Cancel", "src/Orders/OrderService.cs", 6, doc: true, end: 9),
            Node("cs:Shop.Orders.OrdersController", "class", "OrdersController", "src/Orders/OrdersController.cs", 1, ["controller"]),
            Node("cs:Shop.Orders.OrdersController.Cancel(int)", "method", "Cancel", "src/Orders/OrdersController.cs", 1, ["endpoint", "POST"], "orders/{id}/cancel"),
            Node("cs:Shop.Orders.AuditLog", "class", "AuditLog", "src/Orders/AuditLog.cs", 1),
            Node("cs:Shop.Orders.AuditLog.Write(string)", "method", "Write", "src/Orders/AuditLog.cs", 2),
            Node("cs:Shop.Domain.Order", "class", "Order", "src/Domain/Order.cs", 1, ["entity"]),
            Node("cs:Shop.Data.ShopDb", "class", "ShopDb", "src/Data/ShopDb.cs", 1, ["dbcontext"]),
            Node("cs:Shop.Billing.InvoiceJob", "class", "InvoiceJob", "src/Billing/InvoiceJob.cs", 1),
            Node("cs:Shop.Billing.InvoiceJob.Process()", "method", "Process", "src/Billing/InvoiceJob.cs", 1),
            Node("cs:Shop.Billing.InvoiceRepository.Add()", "method", "Add", "src/Billing/InvoiceRepository.cs", 1),
            Node("cs:Shop.Materials.MaterialService", "class", "MaterialService", "src/Materials/MaterialService.cs", 1),
            Node("cs:Shop.Materials.MaterialService.Reserve()", "method", "Reserve", "src/Materials/MaterialService.cs", 1),
            Node("cs:Shop.Materials.MaterialRepo.Save()", "method", "Save", "src/Materials/MaterialRepo.cs", 1),
            Node("ext:sqlserver", "external", "SQL Server", "src/Shop.csproj", 1, ["database", "inferred"]),
            Node("ext:http:audit.example.com", "external", "audit.example.com", "src/Orders/AuditLog.cs", 1, ["http-api", "detected"]),
            Node("ext:http:stock.example.com", "external", "stock.example.com", "src/Materials/MaterialRepo.cs", 1, ["http-api", "detected"]),
            .. extraNodes,
        ];
        JsonObject[] edges =
        [
            Edge("cs:Shop.Orders.OrderService", Cancel, "contains"),
            Edge("cs:Shop.Orders.OrdersController", "cs:Shop.Orders.OrdersController.Cancel(int)", "contains"),
            Edge("cs:Shop.Orders.AuditLog", "cs:Shop.Orders.AuditLog.Write(string)", "contains"),
            Edge("cs:Shop.Billing.InvoiceJob", "cs:Shop.Billing.InvoiceJob.Process()", "contains"),
            Edge("cs:Shop.Materials.MaterialService", "cs:Shop.Materials.MaterialService.Reserve()", "contains"),
            Edge("cs:Shop.Orders.OrdersController.Cancel(int)", Cancel, "calls"),
            Edge(Cancel, "cs:Shop.Domain.Order", "accesses"),
            Edge(Cancel, "cs:Shop.Orders.AuditLog.Write(string)", "calls"),
            Edge("cs:Shop.Billing.InvoiceJob.Process()", "cs:Shop.Domain.Order", "accesses"),
            Edge("cs:Shop.Billing.InvoiceJob.Process()", "cs:Shop.Billing.InvoiceRepository.Add()", "calls"),
            Edge("cs:Shop.Data.ShopDb", "cs:Shop.Domain.Order", "persists"),
            Edge("cs:Shop.Data.ShopDb", "ext:sqlserver", "connects", "inferred"),
            Edge("cs:Shop.Orders.AuditLog", "ext:http:audit.example.com", "connects", "detected"),
            Edge("cs:Shop.Materials.MaterialService.Reserve()", "cs:Shop.Materials.MaterialRepo.Save()", "calls"),
            Edge("cs:Shop.Materials.MaterialService", "ext:http:stock.example.com", "connects", "detected"),
        ];
        return new JsonObject
        {
            ["commit"] = "abc", ["nodes"] = new JsonArray([.. nodes.Select(n => (JsonNode)n)]), ["edges"] = new JsonArray([.. edges.Select(e => (JsonNode)e)]),
        };
    }

    static JsonArray Symbols(params (string Id, string Kind, string Location)[] s) =>
        new([.. s.Select(x => (JsonNode)new JsonObject { ["id"] = x.Id, ["kind"] = x.Kind, ["location"] = x.Location })]);

    /// <summary>docwizz diff --format json for the feature branch: OrderService.Cancel changed (and its class with it).</summary>
    static JsonObject Diff(JsonArray? stale = null, JsonArray? gaps = null, JsonArray? flags = null, JsonArray? pages = null, JsonArray? added = null,
        JsonArray? changed = null, JsonArray? removed = null) => new()
    {
        ["baseline"] = "main",
        ["added"] = added ?? new JsonArray(),
        ["changed"] = changed ?? Symbols(("cs:Shop.Orders.OrderService", "class", "src/Orders/OrderService.cs:3-12"), (Cancel, "method", "src/Orders/OrderService.cs:6-11")),
        ["removed"] = removed ?? new JsonArray(),
        ["pages"] = pages ?? new JsonArray("modules/src-Orders.md"),
        ["dependencies"] = new JsonObject { ["added"] = new JsonArray(), ["removed"] = new JsonArray() },
        ["decisions"] = new JsonArray(),
        ["stale"] = stale ?? new JsonArray(),
        ["introduced"] = new JsonObject { ["gaps"] = gaps ?? new JsonArray(), ["violations"] = new JsonArray(), ["flags"] = flags ?? new JsonArray() },
        ["packages"] = new JsonArray(),
        ["tests"] = new JsonObject
        {
            ["note"] = "a link means test code uses the symbol; it is not code coverage",
            ["linked"] = new JsonArray(new JsonObject
            {
                ["id"] = Cancel, ["location"] = "src/Orders/OrderService.cs:6", ["change"] = "changed", ["level"] = "none",
                ["tests"] = new JsonArray(new JsonObject { ["test"] = "cs:Shop.Tests.OrderServiceTests.Cancel_sets_status()", ["link"] = "direct" }),
            }),
            ["unlinked"] = new JsonArray(),
        },
    };

    // --- Helpers -------------------------------------------------------------------------------------------------------

    T With<T>(Func<T> work)
    {
        SystemContexts.Override = _ => _docwizz;
        try { return work(); }
        finally { SystemContexts.Override = null; }
    }

    WorkspaceStore Store => WorkspaceStore.At(_store);

    PullRequestWorkflow Workflow(string branch = "feature", WorkspaceStore? store = null, WorkItem[]? items = null)
    {
        var (sha, prs) = Providers.Local(_git, "main", [branch]);
        var pr = prs.Single();
        Analyzer.Analyze(_git, sha, pr);
        pr.WorkItems = [.. items ?? [Story]];
        return new PullRequestWorkflow(_git, "local", "main", sha, pr, new BranchPolicy([]), RepoConfig.Load(_git, sha), store: store);
    }

    SystemContext Context(string branch = "feature", WorkspaceStore? store = null, WorkItem[]? items = null) => With(() =>
    {
        using var wf = Workflow(branch, store, items);
        return wf.SystemContext();
    });

    JsonObject Package(string branch = "feature") => With(() =>
    {
        using var wf = Workflow(branch);
        return wf.EvidencePackage();
    });

    void MainPolicy(string yaml)
    {
        _git.Run("checkout", "-q", "main");
        Write(".gitwizz.yml", yaml);
        Commit("policy");
    }

    static bool Has(SystemContext c, string kind, string subject, string relation, string obj) =>
        c.Facts.Any(f => f.Kind == kind && f.Subject == subject && f.Relation == relation && f.Object == obj);

    // --- Selection, provenance, quality --------------------------------------------------------------------------------

    [Fact]
    public void Complete_context_is_GOOD_and_holds_the_impacted_subgraph_with_provenance()
    {
        var c = Context();

        Assert.Equal("available", c.Status);
        Assert.Equal("GOOD", c.Quality);
        Assert.Empty(c.Reasons);
        var symbol = Assert.Single(c.Symbols); // the class is represented by its changed member
        Assert.Equal(("OrderService.Cancel", "changed", "src/Orders", "application"), (symbol.Name, symbol.Change, symbol.Module, symbol.Layer));

        Assert.True(Has(c, "caller", "OrdersController.Cancel", "calls", "OrderService.Cancel"));
        Assert.True(Has(c, "callee", "OrderService.Cancel", "calls", "AuditLog.Write"));
        Assert.True(Has(c, "endpoint", "OrdersController.Cancel", "exposes", "POST orders/{id}/cancel"));
        Assert.True(Has(c, "data", "OrderService.Cancel", "accesses", "Order"));
        Assert.True(Has(c, "data", "Order", "is also accessed by", "InvoiceJob.Process")); // the downstream consumer
        Assert.True(Has(c, "data", "Order", "is persisted by", "ShopDb"));

        // Provenance: what docwizz detected stays detected, what is derived or only inferred says so.
        var audit = c.Facts.Single(f => f.Kind == "external" && f.Object == "audit.example.com");
        Assert.Equal(("AuditLog", "detected", "docwizz"), (audit.Subject, audit.Origin, audit.Source));
        Assert.Equal("inferred", c.Facts.Single(f => f.Kind == "external" && f.Object == "SQL Server").Origin);
        var flow = c.Facts.Single(f => f.Kind == "flow");
        Assert.Equal(("POST orders/{id}/cancel", "reaches", "OrderService.Cancel", "inferred"), (flow.Subject, flow.Relation, flow.Object, flow.Origin));
        Assert.All(c.Facts, f => Assert.Matches("^F[0-9]+$", f.Id));

        var doc = Assert.Single(c.Docs);
        Assert.Equal(("current", "human-authored"), (doc.Status, doc.Origin));
        var test = Assert.Single(c.Tests);
        Assert.Equal(("OrderServiceTests.Cancel_sets_status", "OrderService.Cancel", "direct", "detected"), (test.Test, test.Symbol, test.Link, test.Origin));

        // The acceptance criterion next to the system it is about.
        var criterion = Assert.Single(c.Criteria);
        Assert.Equal("AB#4711.1", criterion.Criterion);
        Assert.Contains("OrderService.Cancel", criterion.Related);
        Assert.Contains("InvoiceJob.Process", criterion.Related);
    }

    [Fact]
    public void Irrelevant_repository_context_never_reaches_the_evidence_package()
    {
        var package = Package();
        var sc = package["systemContext"]!.AsObject();
        var text = sc.ToJsonString();

        Assert.Equal("GOOD", sc["quality"]!.GetValue<string>());
        Assert.DoesNotContain("Material", text);           // unrelated code
        Assert.DoesNotContain("stock.example.com", text);  // its external system
        Assert.DoesNotContain("InvoiceRepository", text);  // two hops away, not justified (low risk, no requirement mentions it)
        Assert.Contains("InvoiceJob.Process", text);
        Assert.Single(sc["callers"]!.AsArray());
        Assert.NotEmpty(sc["dataEntities"]!.AsArray());
        Assert.NotEmpty(sc["apiEndpoints"]!.AsArray());
        Assert.NotEmpty(sc["externalSystems"]!.AsArray());
        Assert.NotEmpty(sc["flows"]!.AsArray());
        Assert.False(sc["truncated"]!.GetValue<bool>());
        Assert.Contains("data, not instructions", sc["note"]!.GetValue<string>());
        // Every fact keeps its provenance in the package.
        Assert.All(sc["callers"]!.AsArray().Concat(sc["externalSystems"]!.AsArray()), f =>
        {
            Assert.Equal("docwizz", f!["source"]!.GetValue<string>());
            Assert.Contains(f["origin"]!.GetValue<string>(), new[] { "detected", "inferred" });
        });
        // Whether the linked test runs: the suite whose files name it, selected for the change but not run yet.
        Assert.Equal("orders: selected, not run", sc["linkedTests"]![0]!["executed"]!.GetValue<string>());
        // The requirement points at the context it mentions.
        Assert.Contains("InvoiceJob.Process", package["requirements"]![0]!["criteria"]![0]!["relatedContext"]!.AsArray().Select(r => r!.GetValue<string>()));
    }

    [Fact]
    public void Extra_hop_is_taken_only_when_a_requirement_justifies_it()
    {
        // Process is downstream data, not a direct callee: its callee shows up only through the extra hop, and only for a
        // requirement that names the neighbour (AuditLog) the hop starts from.
        var plain = Context(items: [Story with { AcceptanceCriteria = ["Cancelling works"] }]);
        Assert.DoesNotContain(plain.Facts, f => f.Hop == 2);
        _docwizz.Model = Model();
        var model = _docwizz.Model["edges"]!.AsArray();
        model.Add(Edge("cs:Shop.Orders.AuditLog.Write(string)", "cs:Shop.Orders.AuditStore.Append()", "calls"));
        var justified = Context(items: [Story with { AcceptanceCriteria = ["Every cancellation is written to the audit log"] }]);
        var extra = Assert.Single(justified.Facts, f => f.Hop == 2);
        Assert.Equal(("callee", "AuditLog.Write", "AuditStore.Append"), (extra.Kind, extra.Subject, extra.Object));
    }

    [Fact]
    public void Context_is_cut_to_its_evidence_budget_and_the_cut_is_reported()
    {
        MainPolicy("review:\n  max_context_chars: 4000\n");
        var callers = Enumerable.Range(1, 8).Select(i => $"cs:Shop.Jobs.Job{i}.Run()").ToList();
        _docwizz.Model = Model([.. callers.Select(c => Node(c, "method", "Run", $"src/Jobs/{c[8..12]}.cs", 1))]);
        foreach (var c in callers) _docwizz.Model["edges"]!.AsArray().Add(Edge(c, Cancel, "calls"));
        var package = Package();
        var sc = package["systemContext"]!.AsObject();
        Assert.True(sc["truncated"]!.GetValue<bool>());
        Assert.Contains(package["budget"]!["truncated"]!.AsArray(), t => t!.GetValue<string>().StartsWith("systemContext ("));
        Assert.InRange(sc.ToJsonString().Length, 1000, 1500); // its share: 12 %, at least 1500 characters
        Assert.NotEmpty(sc["changedSymbols"]!.AsArray()); // the changed symbols come first
    }

    [Fact]
    public void Signature_change_is_one_changed_symbol_that_keeps_its_relations()
    {
        // docwizz reports a new signature as the old id removed and a new id added.
        _docwizz.Diff = Diff(
            changed: Symbols(("cs:Shop.Orders.OrderService", "class", "src/Orders/OrderService.cs:3-12")),
            removed: Symbols((Cancel, "method", "src/Orders/OrderService.cs:6")),
            added: Symbols(("cs:Shop.Orders.OrderService.Cancel(int, string)", "method", "src/Orders/OrderService.cs:6")));
        var c = Context();
        var symbol = Assert.Single(c.Symbols);
        Assert.Equal(("changed", Cancel), (symbol.Change, symbol.Was));
        Assert.True(Has(c, "caller", "OrdersController.Cancel", "calls", "OrderService.Cancel"));
    }

    // --- Missing, failed, stale ----------------------------------------------------------------------------------------

    [Fact]
    public void Without_docwizz_context_is_MISSING_and_advisory_by_default()
    {
        _docwizz.VersionText = null;
        var c = Context();
        Assert.Equal(("unavailable", "MISSING"), (c.Status, c.Quality));
        Assert.Contains("docwizz is not available", c.Reasons.Single());

        var e = With(() => { using var wf = Workflow(); return wf.Evaluate(); });
        Assert.Equal("ready", e.Verdict);
        Assert.DoesNotContain(e.Gates, g => g.Type == "system-context");
        var sc = Package()["systemContext"]!;
        Assert.Equal("unavailable", sc["status"]!.GetValue<string>());
        Assert.Null(sc["callers"]);
    }

    [Fact]
    public void Required_context_that_is_missing_makes_the_verdict_undetermined()
    {
        MainPolicy("context:\n  docwizz:\n    required: true\n");
        _docwizz.VersionText = null;
        var e = With(() => { using var wf = Workflow(); return wf.Evaluate(); });
        var gate = e.Gates.Single(g => g.Id == "system-context");
        Assert.Equal(GateStatus.Error, gate.Status);
        Assert.True(gate.BlocksMerge);
        Assert.Equal("undetermined", e.Verdict);
        Assert.Contains("context.docwizz.required", gate.Summary);

        _docwizz.VersionText = "docwizz 9.9.9";
        Assert.Equal("ready", With(() => { using var wf = Workflow(); return wf.Evaluate(); }).Verdict);
    }

    [Fact]
    public void Failed_unavailable_and_no_relationships_are_told_apart()
    {
        _docwizz.FailScan = true;
        var failed = Context();
        Assert.Equal(("failed", "MISSING"), (failed.Status, failed.Quality));
        Assert.Equal("docwizz failed: docwizz scan failed (exit code 1): boom: the scanner crashed", failed.Reasons.Single());

        _docwizz.FailScan = false;
        _docwizz.Model = new JsonObject
        {
            ["nodes"] = new JsonArray(Node(Cancel, "method", "Cancel", "src/Orders/OrderService.cs", 6)), ["edges"] = new JsonArray(),
        };
        var isolated = Context();
        Assert.Equal("available", isolated.Status);
        Assert.All(isolated.Facts, f => Assert.Equal("ownership", f.Kind)); // found nothing: not a failure
    }

    [Fact]
    public void Facts_from_an_earlier_analysis_are_STALE_when_docwizz_fails_now()
    {
        var first = Context(store: Store);
        Assert.Equal("current", first.Freshness);
        var oldTarget = first.AnalyzedCommit;

        Write("src/Billing/InvoiceJob.cs", "class InvoiceJob { int Skip; }\n");
        Commit("main moves on");
        _docwizz.FailScan = true;
        var diffs = _docwizz.Diffs;
        var c = Context(store: Store);

        Assert.Equal(("stale", "STALE"), (c.Freshness, c.Quality));
        Assert.Equal(oldTarget, c.AnalyzedCommit);
        Assert.Contains($"facts are from {oldTarget[..12]}", c.Reasons.Single());
        Assert.Equal(diffs, _docwizz.Diffs); // a failing docwizz isn't asked for the diff: the changed lines stand in
        Assert.Contains(c.Symbols, s => s.Id == Cancel);
        Assert.True(Has(c, "caller", "OrdersController.Cancel", "calls", "OrderService.Cancel"));
        Assert.Contains(c.Docs, d => d.Status == "unknown");

        MainPolicy("context:\n  docwizz:\n    required: true\n");
        var e = With(() => { using var wf = Workflow(store: Store); return wf.Evaluate(); });
        Assert.Equal(GateStatus.Error, e.Gates.Single(g => g.Id == "system-context").Status);
    }

    [Fact]
    public void Failed_diff_falls_back_to_the_changed_lines_and_says_so()
    {
        _docwizz.FailDiff = true;
        var c = Context();
        Assert.Equal("PARTIAL", c.Quality);
        Assert.Contains(c.Reasons, r => r.StartsWith("changed symbols taken from the changed lines: docwizz diff failed (exit code 1): unknown ref"));
        Assert.Contains(c.Symbols, s => s.Id == Cancel);
        Assert.Equal("unknown", c.Docs.Single(d => d.AffectedSymbol == "OrderService.Cancel").Status);
    }

    // --- Workspace (#71) -------------------------------------------------------------------------------------------------

    [Fact]
    public void Repository_facts_are_reused_across_pull_requests_until_the_target_changes()
    {
        var first = Context("feature", Store);
        Assert.Equal((1, 1, false), (_docwizz.Scans, _docwizz.Diffs, first.Reused));

        var again = Context("feature", Store);
        Assert.Equal((1, 1, true), (_docwizz.Scans, _docwizz.Diffs, again.Reused)); // nothing ran again
        Assert.Equal(SystemContexts.Json(first, new PullRequest { Id = "x", HeadSha = "x" }).Replace("\"reused\": false", "\"reused\": true"),
            SystemContexts.Json(again, new PullRequest { Id = "x", HeadSha = "x" }));

        Context("feature2", Store);
        Assert.Equal((1, 2), (_docwizz.Scans, _docwizz.Diffs)); // another PR: same repository facts, its own diff

        Write("src/Billing/InvoiceJob.cs", "class InvoiceJob { int Skip; }\n");
        Commit("main moves on");
        Context("feature", Store);
        Assert.Equal((2, 3), (_docwizz.Scans, _docwizz.Diffs)); // new target: both recomputed

        _docwizz.VersionText = "docwizz 10.0.0";
        Context("feature", Store);
        Assert.Equal(3, _docwizz.Scans); // another docwizz: recomputed
        Assert.Equal("current", With(() => SystemContexts.ModelState(_git, Store, RepoConfig.Default, _git.Run("rev-parse", "main").Trim())).Status);
    }

    [Fact]
    public void Workspace_status_and_refresh_keep_the_docwizz_model_current()
    {
        var sha = _git.Run("rev-parse", "main").Trim();
        var (_, prs) = Providers.Local(_git, "main", ["feature"]);
        var status = With(() => Repository.Status(_git, Store, "local", "main", new BranchPolicy([]), sha, prs));
        Assert.Equal("missing", status.Facts.Single(f => f.Name == "docwizz system model").State.Status);
        With(() => Repository.Refresh(_git, Store, "local", "main", new BranchPolicy([]), sha, prs, allOpen: false));
        status = With(() => Repository.Status(_git, Store, "local", "main", new BranchPolicy([]), sha, prs));
        Assert.True(status.Facts.Single(f => f.Name == "docwizz system model").State.Current);

        _docwizz.VersionText = null; // without docwizz there is nothing to keep current
        status = With(() => Repository.Status(_git, Store, "local", "main", new BranchPolicy([]), sha, prs));
        Assert.DoesNotContain(status.Facts, f => f.Name == "docwizz system model");
    }

    // --- Documentation freshness ---------------------------------------------------------------------------------------

    [Fact]
    public void Code_changed_doc_unchanged_is_STALE_and_the_conflict_is_exposed()
    {
        _docwizz.Diff = Diff(stale: new JsonArray(new JsonObject
        {
            ["id"] = Cancel, ["location"] = "src/Orders/OrderService.cs:6", ["changes"] = new JsonArray("parameters", "exceptions"),
        }));
        var c = Context();
        var doc = c.Docs.Single();
        Assert.Equal(("stale", "OrderService.Cancel", "human-authored"), (doc.Status, doc.AffectedSymbol, doc.Origin));
        Assert.Equal("parameters, exceptions changed while the doc comment stayed the same", doc.Reason);
        Assert.Contains("the current code is authoritative", doc.Conflict);
        Assert.StartsWith("code ", doc.Fingerprint);
        Assert.Equal("PARTIAL", c.Quality);
        Assert.Contains("stale documentation: OrderService.Cancel (parameters, exceptions changed while the doc comment stayed the same)", c.Reasons);

        var json = Package()["systemContext"]!["relevantDocs"]![0]!;
        Assert.Equal("stale", json["status"]!.GetValue<string>());
        Assert.NotNull(json["conflict"]);
    }

    [Fact]
    public void Contradiction_is_stale_doubt_is_unknown_gap_is_missing()
    {
        _docwizz.Diff = Diff(
            added: Symbols(("cs:Shop.Orders.OrderService.Refund(int)", "method", "src/Orders/OrderService.cs:12")),
            flags: new JsonArray(new JsonObject { ["id"] = Cancel, ["rule"] = "DOC-PARAM", ["basis"] = "fact", ["detail"] = "documents parameter 'reason' that doesn't exist" }),
            gaps: new JsonArray(new JsonObject
            {
                ["id"] = "cs:Shop.Orders.OrderService.Refund(int)", ["location"] = "src/Orders/OrderService.cs:12", ["level"] = "high", ["critical"] = true,
                ["missing"] = new JsonArray("summary"),
            }));
        var c = Context();
        Assert.Equal("stale", c.Docs.Single(d => d.AffectedSymbol == "OrderService.Cancel").Status);
        Assert.Contains("contradicts the code (DOC-PARAM", c.Docs.Single(d => d.AffectedSymbol == "OrderService.Cancel").Reason);
        var missing = c.Docs.Single(d => d.AffectedSymbol == "OrderService.Refund");
        Assert.Equal(("missing", "needs documentation: missing summary (critical)"), (missing.Status, missing.Reason));

        _docwizz.Diff = Diff(flags: new JsonArray(new JsonObject { ["id"] = Cancel, ["rule"] = "DOC-VAGUE", ["basis"] = "inferred", ["detail"] = "may not describe the result" }));
        Assert.Equal("unknown", Context().Docs.Single().Status);
    }

    [Fact]
    public void Generated_pages_are_stale_until_regenerated_in_the_change()
    {
        _git.Run("checkout", "-q", "main");
        Write("docs/.docwizz/model.json", "{}\n");
        Write("docs/modules/src-Orders.md", "# src/Orders\n");
        Commit("generated docs");
        Branch("regen", () =>
        {
            Write("src/Orders/OrderService.cs", "class OrderService { void Cancel(int id) { } }\n");
            Write("docs/modules/src-Orders.md", "# src/Orders\n\nregenerated\n");
        });
        _git.Run("checkout", "-q", "main");
        _docwizz.Diff = Diff(pages: new JsonArray("modules/src-Orders.md", "api.md"));

        var stale = Context("feature").Docs.Where(d => d.Origin == "generated").ToList();
        Assert.Equal(("stale", "generated from code this change touches, but not regenerated (docwizz generate)"),
            (stale.Single(d => d.Document == "docs/modules/src-Orders.md").Status, stale.Single(d => d.Document == "docs/modules/src-Orders.md").Reason));
        Assert.Equal("missing", stale.Single(d => d.Document == "docs/api.md").Status);

        var regenerated = Context("regen").Docs.Single(d => d.Document == "docs/modules/src-Orders.md");
        Assert.Equal(("current", "regenerated in this pull request"), (regenerated.Status, regenerated.Reason));
        Assert.StartsWith("doc ", regenerated.Fingerprint);
    }

    // --- Architecture policy -------------------------------------------------------------------------------------------

    [Fact]
    public void Changed_symbol_without_architecture_owner_is_partial_or_blocks_when_required()
    {
        _docwizz.Diff = Diff(changed: Symbols(("cs:Shop.Misc.Helper.Run()", "method", "src/Misc/Helper.cs:1")));
        var c = Context("feature2");
        Assert.Equal("PARTIAL", c.Quality);
        Assert.Contains("architecture owner missing for Helper.Run (src/Misc/Helper.cs:1)", c.Reasons);

        MainPolicy("context:\n  docwizz:\n    require_architecture_for_changed_symbols: true\n");
        var e = With(() => { using var wf = Workflow("feature2"); return wf.Evaluate(); });
        var gate = e.Gates.Single(g => g.Id == "system-context");
        Assert.Equal((GateStatus.Fail, "blocked"), (gate.Status, e.Verdict));
        Assert.Equal(("src/Misc/Helper.cs", 1, "architecture-owner"), (gate.Findings[0].File, gate.Findings[0].Line, gate.Findings[0].Rule));
    }

    [Fact]
    public void Configured_gate_is_advisory_unless_the_policy_requires_context()
    {
        MainPolicy("gates:\n  - id: system-context\n");
        _docwizz.Diff = Diff(changed: Symbols(("cs:Shop.Misc.Helper.Run()", "method", "src/Misc/Helper.cs:1")));
        var e = With(() => { using var wf = Workflow("feature2"); return wf.Evaluate(); });
        var gate = e.Gates.Single(g => g.Id == "system-context");
        Assert.Equal((GateStatus.Warn, false), (gate.Status, gate.Blocking));
        Assert.Equal("ready", e.Verdict);
        Assert.Contains(gate.Findings, f => f.Message.StartsWith("architecture owner missing for Helper.Run"));
    }

    // --- AI review -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Findings_must_cite_context_from_the_package_and_not_rest_on_inferred_or_stale_context_alone()
    {
        _docwizz.Diff = Diff(stale: new JsonArray(new JsonObject { ["id"] = Cancel, ["location"] = "src/Orders/OrderService.cs:6", ["changes"] = new JsonArray("parameters") }));
        var package = Package();
        var sc = package["systemContext"]!;
        var caller = sc["callers"]![0]!["id"]!.GetValue<string>();
        var flow = sc["flows"]!.AsArray().First(f => f!["origin"]!.GetValue<string>() == "inferred")!["id"]!.GetValue<string>();
        var doc = sc["relevantDocs"]![0]!["id"]!.GetValue<string>();
        static JsonObject Finding(params string[] context) => new()
        {
            ["rule"] = "AB#4711.1", ["file"] = "src/Orders/OrderService.cs", ["line"] = 9, ["severity"] = "error", ["confidence"] = 0.9,
            ["message"] = "InvoiceJob.Process still invoices cancelled orders", ["evidence"] = "order.Status = \"cancelled\";",
            ["context"] = new JsonArray([.. context.Select(c => (JsonNode)c)]),
        };
        var answer = new JsonObject
        {
            ["findings"] = new JsonArray(Finding(caller), Finding("F999"), Finding(flow), Finding(doc), Finding(flow, caller)),
        }.ToJsonString();
        var changed = new Dictionary<string, List<(int Start, int End)>> { ["src/Orders/OrderService.cs"] = [(8, 10)] };

        var findings = AiReviewGate.Validate(answer, package, changed, 0.5);

        Assert.Equal(("kept", "error"), (findings[0].Verdict, findings[0].Severity));
        Assert.Equal([caller], findings[0].Context!);
        Assert.Equal(("downgraded", "info"), (findings[1].Verdict, findings[1].Severity));
        Assert.Equal("cites system context not in the evidence package: F999", findings[1].Note);
        Assert.Equal($"rests only on inferred system context ({flow})", findings[2].Note);
        Assert.Equal($"rests only on stale system context ({doc})", findings[3].Note);
        Assert.Equal("kept", findings[4].Verdict); // inferred context corroborated by a detected fact

        var result = AiReviewGate.ToResult(findings, "meta", new ReviewPolicy { Model = "m", Endpoint = "http://llm.internal/v1" }, "sha256:x");
        Assert.EndsWith($"[context: {caller}]", result.Findings[0].Message);
    }

    [Fact]
    public void Prompt_tells_the_model_how_to_treat_system_context()
    {
        Assert.Equal("review-v2", AiReviewGate.PromptVersion);
        Assert.Contains("systemContext", AiReviewGate.SystemPrompt);
        Assert.Contains("Never claim a relationship", AiReviewGate.SystemPrompt);
        Assert.Contains("\"context\": [", AiReviewGate.SystemPrompt);
    }

    // --- Parsing the real docwizz output -------------------------------------------------------------------------------

    [Fact]
    public void Parses_docwizz_diff_json_as_docwizz_writes_it()
    {
        // Captured from docwizz 0.1.0: `docwizz diff . main feature --format json` after adding a parameter to Cancel.
        var diff = SystemContexts.ParseDiff("""
            {"baseline": "main, at feature",
             "added": [{"id": "cs:Shop.Orders.OrderService.Cancel(int, string)", "kind": "method", "location": "src/Orders/OrderService.cs:6"}],
             "changed": [{"id": "cs:Shop.Orders.OrderService", "kind": "class", "location": "src/Orders/OrderService.cs:2-7"}],
             "removed": [{"id": "cs:Shop.Orders.OrderService.Cancel(int)", "kind": "method", "location": "src/Orders/OrderService.cs:6"}],
             "pages": ["index.md", "modules/src-Orders.md", "quality.md"], "dependencies": {"added": [], "removed": []}, "decisions": [],
             "stale": [{"id": "cs:Shop.Orders.OrderService.Cancel(int, string)", "location": "src/Orders/OrderService.cs:6", "changes": ["parameters"]}],
             "introduced": {"gaps": [], "violations": [], "flags": []}, "packages": [],
             "tests": {"note": "a link means test code uses the symbol; it is not code coverage",
               "linked": [{"id": "cs:Shop.Orders.OrderService", "location": "src/Orders/OrderService.cs:2-7", "change": "changed", "level": "none",
                           "tests": [{"test": "cs:Shop.Tests.OrderServiceTests.Cancel_works()", "link": "indirect", "via": "cs:Shop.Orders.OrderService.Cancel(int, string)"}]}],
               "unlinked": []}}
            """);
        Assert.Equal(["added", "changed", "removed"], diff.Symbols.Select(s => s.Change));
        Assert.Equal(["parameters"], diff.Stale.Single().Changes);
        Assert.Equal(("indirect", "cs:Shop.Orders.OrderService.Cancel(int, string)"), (diff.Tests.Single().Link, diff.Tests.Single().Via));
        Assert.Throws<InvalidOperationException>(() => SystemContexts.ParseDiff("Documentation impact (vs main)"));
        Assert.Throws<InvalidOperationException>(() => SystemContexts.ParseModel("{\"files\": 3}", "abc", []));
        Assert.Equal(["application", "domain"], SystemContexts.Layers("architecture:\n  layers:\n    application: [\"src/*\"]\n    domain: [\"d/*\"]\n  allow: {}\n").Select(l => l.Name));
        Assert.Empty(SystemContexts.Layers("architecture:\n  layers: {}\n"));
    }

    // --- Guide ---------------------------------------------------------------------------------------------------------

    (int Code, string Output) Guide(string branch, IGuidePrompts? prompts) => With(() =>
    {
        using var wf = Workflow(branch);
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.No, Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }, ColorSystem = ColorSystemSupport.NoColors, Interactive = InteractionSupport.No,
        });
        console.Profile.Width = 200;
        var code = new Guide(wf, new GuideOptions($"gitwizz guide {branch}"), console, console, prompts).Run();
        return (code, writer.ToString());
    });

    [Fact]
    public void Guide_shows_the_system_context_before_tests_and_leads_through_partial_context()
    {
        _docwizz.Diff = Diff(stale: new JsonArray(new JsonObject { ["id"] = Cancel, ["location"] = "src/Orders/OrderService.cs:6", ["changes"] = new JsonArray("parameters") }));
        var script = new GuideTests.Script(GuideAction.ShowContextGraph, GuideAction.ShowMissingContext, GuideAction.ContinueWithContext, GuideAction.Exit);
        var (_, output) = Guide("feature", script);

        Assert.Contains("Step 2/6  System context", output);
        Assert.True(output.IndexOf("Step 2/6  System context") < output.IndexOf("Step 3/6  Test impact"));
        Assert.Contains("Technical context from docwizz 9.9.9", output);
        Assert.Contains("✓ 1 changed symbol mapped", output);
        Assert.Contains("✓ 1 caller, 1 callee", output);
        Assert.Contains("✓ 1 API endpoint", output);
        Assert.Contains("✓ 1 data entity", output);
        Assert.Contains("✓ 2 external systems (some inferred)", output);
        Assert.Contains("! 1 stale", output);
        Assert.Contains("STALE doc comment of OrderService.Cancel", output);
        Assert.Contains("AB#4711.1 ↔", output);
        Assert.Contains("no AI review in this policy", output);
        Assert.Contains("Context quality: PARTIAL", output);
        // The graph and the gaps, on request; then on to the tests.
        Assert.Contains("<- calls by OrdersController.Cancel", output);
        Assert.Contains("Order is also accessed by InvoiceJob.Process", output);
        Assert.Contains("conflict: the doc comment may still describe the previous parameters", output);
        Assert.Contains("docwizz check . --since main", output);
        Assert.Equal([GuideAction.ContinueWithContext, GuideAction.ShowContextGraph, GuideAction.ShowMissingContext, GuideAction.RefreshContext, GuideAction.Exit],
            script.Asked[0]);
        Assert.Contains("Step 3/6  Test impact", output);
    }

    [Fact]
    public void Guide_without_docwizz_continues_with_reduced_context()
    {
        _docwizz.VersionText = null;
        var script = new GuideTests.Script(GuideAction.ShowContextHelp, GuideAction.ContinueWithContext, GuideAction.RunTests, GuideAction.Exit);
        var (_, output) = Guide("feature", script);
        Assert.Contains("! docwizz is not available", output);
        Assert.Contains("AI review can continue with reduced context", output);
        Assert.Contains("Context quality: MISSING", output);
        Assert.Contains("context.docwizz.enabled: false", output); // the help
        Assert.Equal([GuideAction.ContinueWithContext, GuideAction.ShowContextHelp, GuideAction.Exit], script.Asked[0]);
        Assert.Contains("VERDICT: READY", output); // advisory: nothing blocked
    }

    [Fact]
    public void Guide_asks_nothing_when_the_context_is_good()
    {
        var script = new GuideTests.Script(GuideAction.Exit);
        var (_, output) = Guide("feature", script);
        Assert.Contains("Context quality: GOOD", output);
        Assert.DoesNotContain(script.Asked, a => a.Contains(GuideAction.ContinueWithContext));
    }
}
