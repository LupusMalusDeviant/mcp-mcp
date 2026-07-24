using System.Text.RegularExpressions;
using McpMcp.Abstractions;

namespace McpMcp.Core.Upstreams;

/// <summary>Validiert Upstream-Konfigurationen vor Add/Reconfigure. Wirft <see cref="ArgumentException"/> mit präziser Meldung (DON'T Nr. 6: keine stillen Teilerfolge).</summary>
public static partial class UpstreamConfigValidator
{
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex SlugPattern();

    public static void Validate(UpstreamServerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(config.Slug) || !SlugPattern().IsMatch(config.Slug))
        {
            throw new ArgumentException(
                $"Slug '{config.Slug}' ist ungültig: erlaubt sind a-z, 0-9, '-' und '_', 1-64 Zeichen, Beginn mit a-z/0-9.",
                nameof(config));
        }

        if (config.Slug.Contains(NamespacedToolName.Separator, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Slug '{config.Slug}' darf den Namespace-Separator '{NamespacedToolName.Separator}' nicht enthalten (FR-03).",
                nameof(config));
        }

        if (config.Slug.Equals(AssetDelivery.Namespace, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Slug '{AssetDelivery.Namespace}' ist reserviert für die zentrale Asset-Auslieferung (FR-40).",
                nameof(config));
        }

        if (string.IsNullOrWhiteSpace(config.DisplayName))
        {
            throw new ArgumentException("DisplayName darf nicht leer sein.", nameof(config));
        }

        ValidateTransport(config);

        if (config.CallTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("CallTimeout muss positiv sein.", nameof(config));
        }

        if (config.Restart is { } restart)
        {
            if (restart.MaxRetries < 0 ||
                restart.InitialBackoff <= TimeSpan.Zero ||
                restart.BackoffMultiplier < 1.0 ||
                restart.MaxBackoff < restart.InitialBackoff)
            {
                throw new ArgumentException(
                    "RestartPolicy ungültig: MaxRetries ≥ 0, InitialBackoff > 0, Multiplier ≥ 1, MaxBackoff ≥ InitialBackoff.",
                    nameof(config));
            }
        }
    }

    private static void ValidateTransport(UpstreamServerConfig config)
    {
        var (expected, actualSet) = config.Kind switch
        {
            UpstreamTransportKind.Stdio => ("Stdio", config.Stdio is not null),
            UpstreamTransportKind.StreamableHttp => ("Http", config.Http is not null),
            UpstreamTransportKind.OpenApi => ("OpenApi", config.OpenApi is not null),
            UpstreamTransportKind.Cli => ("Cli", config.Cli is not null),
            _ => throw new ArgumentException($"Unbekannter Transport: {config.Kind}.", nameof(config)),
        };

        if (!actualSet)
        {
            throw new ArgumentException(
                $"Transport {config.Kind} verlangt gesetzte {expected}-Optionen.", nameof(config));
        }

        var extras = new[]
        {
            (Name: "Stdio", Set: config.Stdio is not null, For: UpstreamTransportKind.Stdio),
            (Name: "Http", Set: config.Http is not null, For: UpstreamTransportKind.StreamableHttp),
            (Name: "OpenApi", Set: config.OpenApi is not null, For: UpstreamTransportKind.OpenApi),
            (Name: "Cli", Set: config.Cli is not null, For: UpstreamTransportKind.Cli),
        };
        foreach (var extra in extras)
        {
            if (extra.Set && extra.For != config.Kind)
            {
                throw new ArgumentException(
                    $"{extra.Name}-Optionen sind gesetzt, aber Kind ist {config.Kind} — widersprüchliche Konfiguration.",
                    nameof(config));
            }
        }

        if (config.Kind == UpstreamTransportKind.Stdio && string.IsNullOrWhiteSpace(config.Stdio!.Command))
        {
            throw new ArgumentException("Stdio.Command darf nicht leer sein.", nameof(config));
        }

        if (config.Kind == UpstreamTransportKind.Cli)
        {
            var cli = config.Cli!;
            if (string.IsNullOrWhiteSpace(cli.Executable))
            {
                throw new ArgumentException("Cli.Executable darf nicht leer sein.", nameof(config));
            }

            if (cli.Tools is null || cli.Tools.Count == 0)
            {
                throw new ArgumentException("Cli verlangt mindestens ein Tool (CliToolSpec).", nameof(config));
            }

            if (cli.Tools.Any(t => string.IsNullOrWhiteSpace(t.Name)))
            {
                throw new ArgumentException("Jeder CliToolSpec braucht einen nicht-leeren Namen.", nameof(config));
            }

            if (cli.TimeoutSeconds is { } ts && ts <= 0)
            {
                throw new ArgumentException("Cli.TimeoutSeconds muss positiv sein.", nameof(config));
            }
        }
    }
}
