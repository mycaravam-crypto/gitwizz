using Gitwizz;

namespace Gitwizz.Tests;

public class FileInspectTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("gitwizz-inspect").FullName;
    readonly Git git;

    public FileInspectTests()
    {
        git = new Git(dir);
        git.Run("init", "-q", "-b", "main");
        git.Run("config", "user.name", "Test Author");
        git.Run("config", "user.email", "test@example.invalid");
    }

    public void Dispose() => Directory.Delete(dir, true);

    void Commit(string path, string body, string message)
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
        git.Run("add", "-A");
        git.Run("commit", "-q", "-m", message);
    }

    [Fact]
    public void Shows_author_hunks_and_commit_message_work_item_without_claiming_verification()
    {
        Commit("src/a.cs", "one\n", "Initial AB#42");
        Commit("src/a.cs", "one\ntwo\n", "Change via PR (#23)");
        var result = FileInspect.Inspect(git, "src/a.cs");
        Assert.Equal(2, result.Changes.Count);
        Assert.Equal("Test Author", result.Changes[0].Author);
        Assert.Equal("#23", result.Changes[0].PullRequest);
        Assert.Contains("@@", result.Changes[0].Diff);
        Assert.Contains("AB#42", result.Changes[1].WorkItems);
        Assert.Contains("unverified", FileInspect.Text(result));
        Assert.Contains("gitwizz.inspect/v1", FileInspect.Json(result));
    }

    [Fact]
    public void Bounds_history_and_rejects_unsafe_paths()
    {
        Commit("a.txt", "a\n", "one");
        Commit("a.txt", "b\n", "two");
        var result = FileInspect.Inspect(git, "a.txt", limit: 1);
        Assert.Single(result.Changes);
        Assert.True(result.Truncated);
        Assert.Throws<ArgumentException>(() => FileInspect.Inspect(git, "../other"));
        Assert.Throws<ArgumentException>(() => FileInspect.Inspect(git, "--help"));
        Assert.Throws<ArgumentException>(() => FileInspect.Inspect(git, "a.txt", limit: 0));
    }

    [Fact]
    public void Follows_renames()
    {
        Commit("old.txt", "old\n", "original");
        git.Run("mv", "old.txt", "new.txt");
        git.Run("commit", "-q", "-m", "Rename file");
        var result = FileInspect.Inspect(git, "new.txt");
        Assert.Equal(2, result.Changes.Count);
    }
}
