using System.Text.Json;
using Gitwizz;
using Spectre.Console;

namespace Gitwizz.Tests;

/// <summary>The command line end to end: Cli.Dispatch on a temporary repository with local branches.</summary>
public class DispatchTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("pr-opt-dispatch").FullName;
    readonly string _out = Directory.CreateTempSubdirectory("pr-opt-dispatch-out").FullName;
    readonly Git _git;

    public DispatchTests()
    {
        _git = new Git(_dir);
        _git.Run("init", "-q", "-b", "main");
        _git.Run("config", "user.email", "t@t"); _git.Run("config", "user.name", "t");
        Write("app.cs", "class App\n{\n    void Run() { }\n}\n");
        Commit("init");
        Branch("feature", () => Write("docs.md", "docs\n"));
        Branch("clash", () => Write("app.cs", "class App\n{\n    void Run() { Clash(); }\n}\n"));
        _git.Run("checkout", "-q", "main");
        Write("app.cs", "class App\n{\n    void Run() { Main(); }\n}\n");
        Commit("main moves on");
    }

    public void Dispose()
    {
        Directory.Delete(_dir, true);
        Directory.Delete(_out, true);
    }

    void Write(string f, string c) => File.WriteAllText(Path.Combine(_dir, f), c);
    void Commit(string m) { _git.Run("add", "-A"); _git.Run("commit", "-q", "-m", m); }
    void Branch(string name, Action change) { _git.Run("checkout", "-q", "-b", name, "main"); change(); Commit(name); }

    /// <summary>Runs the command line; returns the exit code and what went to stderr.</summary>
    (int Code, string Err) Run(params string[] args)
    {
        var text = new StringWriter();
        var err = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(text), Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Interactive = InteractionSupport.No,
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
        });
        err.Profile.Width = 200;
        return (Cli.Dispatch(args, err), text.ToString());
    }

    string Output(string name) => File.ReadAllText(Path.Combine(_out, name));
    string To(string name) => Path.Combine(_out, name);

    [Fact]
    public void Usage_errors_exit_2_and_name_the_usage()
    {
        var (code, err) = Run("nope");
        Assert.Equal(2, code);
        Assert.Contains("unknown command 'nope' (try: plan, guide, doctor, status, evaluate,", err);

        (code, err) = Run("evaluate", "feature", "extra", "-r", _dir);
        Assert.Equal(2, code);
        Assert.Contains("unknown argument 'extra' (usage: gitwizz evaluate <pr>)", err);

        (code, err) = Run("history", "--run", "-r", _dir);
        Assert.Equal(2, code);
        Assert.Contains("unknown option '--run' for history (it belongs to trace)", err);

        (code, err) = Run("evaluate", "-r", _dir);
        Assert.Equal(2, code);
        Assert.Contains("need a pull request: gitwizz evaluate <pr>", err);
    }

    [Fact]
    public void Evaluate_explain_gives_the_verdict_and_its_exit_code()
    {
        var (code, _) = Run("evaluate", "feature", "--explain", "--provider", "local", "-r", _dir, "-o", To("ready.txt"));
        Assert.Equal(0, code);
        Assert.Contains("READY", Output("ready.txt"));

        (code, _) = Run("evaluate", "clash", "--explain", "--provider", "local", "-r", _dir, "-o", To("blocked.txt"));
        Assert.Equal(3, code);
        Assert.Contains("NOT READY (blocked)", Output("blocked.txt"));

        // The old name runs the same, with a note.
        var (old, err) = Run("explain", "clash", "--provider", "local", "-r", _dir, "-o", To("old.txt"));
        Assert.Equal(3, old);
        Assert.Contains("'gitwizz explain' is now 'gitwizz evaluate --explain'", err);
        Assert.Equal(Output("blocked.txt"), Output("old.txt"));
    }

    [Fact]
    public void Context_ai_prints_the_evidence_package()
    {
        var (code, _) = Run("context", "feature", "--ai", "--provider", "local", "-r", _dir, "-o", To("ai.json"));
        Assert.Equal(0, code);
        Assert.Equal("gitwizz.evidence/v1", JsonDocument.Parse(Output("ai.json")).RootElement.GetProperty("schema").GetString());

        (code, _) = Run("context", "feature", "--provider", "local", "-r", _dir, "-o", To("context.json"));
        Assert.Equal(0, code);
        Assert.Equal("gitwizz.context/v1", JsonDocument.Parse(Output("context.json")).RootElement.GetProperty("schema").GetString());

        var (both, err) = Run("context", "feature", "--ai", "--system", "--provider", "local", "-r", _dir);
        Assert.Equal(1, both);
        Assert.Contains("--system and --ai are two views of the context: pick one", err);
    }

    [Fact]
    public void History_is_the_export_and_the_old_form_still_works()
    {
        var (code, _) = Run("history", "--provider", "local", "-r", _dir, "-o", To("h.json"));
        Assert.Equal(0, code);
        Assert.Equal("gitwizz.history/v2", JsonDocument.Parse(Output("h.json")).RootElement.GetProperty("schema").GetString());

        var (old, err) = Run("history", "export", "--provider", "local", "-r", _dir, "-o", To("old.json"));
        Assert.Equal(0, old);
        Assert.Contains("'gitwizz history export' is now 'gitwizz history'", err);
        Assert.Equal("gitwizz.history/v2", JsonDocument.Parse(Output("old.json")).RootElement.GetProperty("schema").GetString());
    }

    [Fact]
    public void Status_options_are_checked_and_clear_cache_empties_the_workspace()
    {
        var (code, err) = Run("status", "--all-open", "-r", _dir);
        Assert.Equal(1, code);
        Assert.Contains("--all-open needs --refresh", err);

        Assert.Equal(1, Run("status", "--clear-cache", "-t", "main", "-r", _dir).Code);

        Assert.Equal(0, Run("evaluate", "feature", "--provider", "local", "-r", _dir, "-o", To("e.txt")).Code);
        (code, err) = Run("status", "--clear-cache", "-r", _dir);
        Assert.Equal(0, code);
        Assert.Contains("removed", err);
        Assert.Contains("workspace already empty", Run("cache", "clear", "-r", _dir).Err);
    }
}
