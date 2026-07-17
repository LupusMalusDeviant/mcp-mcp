using System.Reflection;

namespace McpMcp.Integration.Tests;

internal static class TestPaths
{
    /// <summary>Pfad zur gebauten EchoServer-Executable (gleiche Konfiguration/TFM wie das Testprojekt).</summary>
    public static string EchoServerExecutable
    {
        get
        {
            var configuration = typeof(TestPaths).Assembly
                .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
            var exeName = OperatingSystem.IsWindows()
                ? "McpMcp.TestServers.EchoServer.exe"
                : "McpMcp.TestServers.EchoServer";
            var path = Path.Combine(
                RepoRoot, "tests", "McpMcp.TestServers", "EchoServer",
                "bin", configuration, "net10.0", exeName);

            return File.Exists(path)
                ? path
                : throw new FileNotFoundException(
                    $"EchoServer-Executable nicht gefunden: {path}. Zuerst die Solution bauen.", path);
        }
    }

    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null
                && !File.Exists(Path.Combine(dir.FullName, "MCPMCP.slnx"))
                && !File.Exists(Path.Combine(dir.FullName, "MCPMCP.sln")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName
                ?? throw new InvalidOperationException("Repo-Root (MCPMCP.slnx) oberhalb des Test-Verzeichnisses nicht gefunden.");
        }
    }
}
