using System.Runtime.CompilerServices;

namespace CompatibilityLab.Tests;

internal static class TestEnvironment
{
    // Resolved from the test assembly location at runtime (bin/Debug/net10.0 -> project root).
    public static string ProjectRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !dir.GetFiles("*.csproj").Any())
                dir = dir.Parent;
            if (dir is null) throw new InvalidOperationException("找不到项目根目录（.csproj）");
            return dir.FullName;
        }
    }

    [ModuleInitializer]
    internal static void Init()
    {
        // WebApplicationFactory checks this before solution-walk / attribute lookup.
        Environment.SetEnvironmentVariable(
            "ASPNETCORE_TEST_CONTENTROOT_COMPATIBILITYLAB", ProjectRoot);
        Environment.SetEnvironmentVariable("COMPLAB_SEED", "false");
    }
}
