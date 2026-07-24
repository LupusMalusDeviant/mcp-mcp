using System.Reflection;

namespace McpMcp.Abstractions;

/// <summary>Eine Build-Quelle für Protokoll-, Telemetrie-, Assembly- und Paketversion.</summary>
public static class McpMcpProductInfo
{
    public static string Version { get; } =
        typeof(McpMcpProductInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+', 2)[0]
        ?? typeof(McpMcpProductInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}
