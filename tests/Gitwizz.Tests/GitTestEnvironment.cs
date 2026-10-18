using System.Runtime.CompilerServices;

namespace Gitwizz.Tests;

static class GitTestEnvironment
{
    /// <summary>
    /// Every git the tests start inherits this: no automatic gc or maintenance. git commit otherwise spawns a detached
    /// "git maintenance run --auto" that can still be writing .git/objects/pack when a test's Dispose deletes its
    /// temporary repository, failing an unrelated test with "Directory not empty".
    /// </summary>
    [ModuleInitializer]
    internal static void DisableAutoMaintenance()
    {
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "2");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "maintenance.auto");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "false");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_1", "gc.auto");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_1", "0");
    }
}
