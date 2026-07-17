using McpMcp.Abstractions;

namespace McpMcp.Core.Upstreams;

/// <summary>Betriebsparameter des Supervisors. Defaults sind produktionsnah; Tests setzen kurze Intervalle.</summary>
public sealed record SupervisorOptions
{
    public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Nach so langer ununterbrochener Healthy-Zeit wird der Restart-Zähler zurückgesetzt.</summary>
    public TimeSpan HealthyResetWindow { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Timeout pro Upstream-Call, wenn die Server-Config keinen eigenen setzt (FR-09).</summary>
    public TimeSpan DefaultCallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Drain-Gnadenfrist für Reconfigure/Disable, wenn keine explizite DrainPolicy übergeben wird.</summary>
    public TimeSpan DefaultDrainGrace { get; init; } = TimeSpan.FromSeconds(5);

    public RestartPolicy DefaultRestartPolicy { get; init; } = RestartPolicy.Default;
}
