namespace Gitwizz;

/// <summary>Builds a small demo repository that shows every kind of outcome the planner reports.</summary>
public static class Example
{
    const string Billing = """
        namespace Shop.Billing;

        public class BillingService
        {
            public decimal Charge(decimal amount)
            {
                var fee = 1.0m;
                return amount + fee + CalculateTax(amount);
            }

            public decimal CalculateTax(decimal amount)
            {
                return amount * 0.19m;
            }
        }
        """;

    const string Session = """
        namespace Shop.Auth;

        public class SessionManager
        {
            public TimeSpan Timeout { get; } = TimeSpan.FromMinutes(20);

            public bool IsExpired(DateTime lastSeen) => DateTime.UtcNow - lastSeen > Timeout;
        }
        """;

    static string Lock(string version) => $$"""
        {
          "name": "shop-web",
          "lockfileVersion": 3,
          "packages": {
            "node_modules/left-pad": { "version": "{{version}}" }
          }
        }
        """;

    /// <summary>
    /// Builds the demo repository in dir: main plus seven branches covering every plan outcome. Replaces dir only if
    /// an earlier run created it.
    /// </summary>
    public static void Create(string dir)
    {
        // Only ever replace a directory this command created (marker inside .git), never user data.
        var marker = Path.Combine(dir, ".git", "gitwizz-example");
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
        {
            if (!File.Exists(marker)) throw new InvalidOperationException($"'{dir}' exists and is not an example repository; choose another --repo");
            Directory.Delete(dir, true);
        }
        Directory.CreateDirectory(dir);
        var git = new Git(dir);
        void Write(string path, string text)
        {
            var full = Path.Combine(dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text.ReplaceLineEndings("\n") + "\n");
        }
        void Edit(string path, string from, string to) => Write(path, File.ReadAllText(Path.Combine(dir, path)).TrimEnd('\n').Replace(from, to));
        void Commit(string message) { git.Run("add", "-A"); git.Run("-c", "user.name=Example", "-c", "user.email=example@localhost", "commit", "-q", "-m", message); }
        void Branch(string name, string from, string message, Action change)
        {
            git.Run("checkout", "-q", "-b", name, from);
            change();
            Commit(message);
        }

        git.Run("init", "-q", "-b", "main");
        File.WriteAllText(marker, "");
        Write("README.md", "# Shop\n\nAn example shop.");
        Write("src/Billing/BillingService.cs", Billing);
        Write("src/Auth/SessionManager.cs", Session);
        Write("web/package-lock.json", Lock("1.0.0"));
        Commit("Initial shop");

        Branch("docs/getting-started", "main", "Add getting-started guide",
            () => Edit("README.md", "An example shop.", "An example shop.\n\n## Getting started\n\nRun `dotnet run`."));
        Branch("fix/session-timeout", "main", "Extend session timeout to 30 minutes",
            () => Edit("src/Auth/SessionManager.cs", "FromMinutes(20)", "FromMinutes(30)"));
        Branch("feature/billing-tax", "main", "Support reduced tax rate",
            () => Edit("src/Billing/BillingService.cs", "return amount * 0.19m;", "return amount * (amount < 50 ? 0.07m : 0.19m);"));
        Branch("feature/billing-refactor", "feature/billing-tax", "Extract fee into a constant",
            () => Edit("src/Billing/BillingService.cs", "var fee = 1.0m;", "var fee = Fee;"));
        Branch("fix/billing-rounding", "main", "Round tax to cents",
            () => Edit("src/Billing/BillingService.cs", "return amount * 0.19m;", "return Math.Round(amount * 0.19m, 2);"));
        Branch("chore/bump-left-pad", "main", "Bump left-pad to 1.1.0", () => Write("web/package-lock.json", Lock("1.1.0")));
        Branch("chore/pin-left-pad", "main", "Pin left-pad to 1.0.1", () => Write("web/package-lock.json", Lock("1.0.1")));
        git.Run("checkout", "-q", "main");
    }
}
