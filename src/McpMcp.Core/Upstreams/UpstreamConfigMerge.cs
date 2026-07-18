using McpMcp.Abstractions;

namespace McpMcp.Core.Upstreams;

/// <summary>
/// Zusammenführen einer bearbeiteten Server-Konfiguration mit der bestehenden (FR-34).
/// </summary>
public static class UpstreamConfigMerge
{
    /// <summary>
    /// Übernimmt Secrets (Env-Variablen, Header, OpenAPI-Credential) aus der Vorgänger-Version,
    /// wenn das Bearbeiten-Formular sie nicht gesetzt hat.
    ///
    /// Hintergrund: Die UI zeigt bestehende Secrets bewusst nicht an — sie stünden sonst im
    /// Klartext im DOM. Ohne diese Übernahme würde jedes Speichern die Zugangsdaten löschen,
    /// nur weil der Bearbeiter sie nie zu Gesicht bekommen hat.
    /// </summary>
    public static UpstreamServerConfig CarryOverSecrets(
        UpstreamServerConfig edited, UpstreamServerConfig previous)
    {
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(previous);

        return edited with
        {
            Stdio = edited.Stdio is { EnvironmentVariables: null } stdio
                ? stdio with { EnvironmentVariables = previous.Stdio?.EnvironmentVariables }
                : edited.Stdio,
            Http = edited.Http is { Headers: null } http
                ? http with { Headers = previous.Http?.Headers }
                : edited.Http,
            OpenApi = edited.OpenApi is { Credential: null } api
                ? api with { Credential = previous.OpenApi?.Credential }
                : edited.OpenApi,
        };
    }
}
