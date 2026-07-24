using System.Diagnostics;
using System.Text.Json;
using McpMcp.Abstractions;

namespace McpMcp.Upstream.Cli;

/// <summary>
/// CLI→MCP-Brücke (ADR-0014): ein fest konfiguriertes Kommandozeilen-Programm erscheint als
/// normaler Upstream — hot-swappable, profilierbar und auditiert wie jeder MCP-Server. Jedes
/// <see cref="CliToolSpec"/> wird ein Tool; Aufrufe laufen strikt shell-frei über
/// <see cref="ProcessStartInfo.ArgumentList"/> (keine Injection, keine Befehlsverkettung).
/// </summary>
public sealed class CliUpstreamConnector : IUpstreamConnector
{
    public UpstreamTransportKind Kind => UpstreamTransportKind.Cli;

    public Task<IUpstreamConnection> ConnectAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        var options = config.Cli
            ?? throw new ArgumentException($"Config '{config.Slug}' hat keine Cli-Optionen.", nameof(config));
        return Task.FromResult<IUpstreamConnection>(new CliUpstreamConnection(id, options));
    }
}

internal sealed class CliUpstreamConnection : IUpstreamConnection
{
    private const int DefaultTimeoutSeconds = 30;
    private readonly CliTransportOptions _options;
    private readonly Dictionary<string, CliToolSpec> _tools;

    public CliUpstreamConnection(ServerId id, CliTransportOptions options)
    {
        Id = id;
        _options = options;
        _tools = options.Tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public ServerId Id { get; }

    // CLI-Upstreams pushen keine Server-Notifications — das Event bleibt bewusst leer verdrahtet.
    public event EventHandler<UpstreamNotificationEventArgs>? NotificationReceived
    {
        add { }
        remove { }
    }

    public Task<UpstreamInventory> DiscoverAsync(CancellationToken ct)
        => Task.FromResult(new UpstreamInventory(
            [.. _options.Tools.Select(t => new ToolDescriptor(t.Name, t.Description, BuildSchema(t)))],
            [],
            []));

    public async Task<JsonElement> CallToolAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        if (!_tools.TryGetValue(toolName, out var spec))
        {
            throw new InvalidOperationException($"Kommando '{toolName}' existiert nicht in diesem CLI-Upstream.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = _options.Executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false, // KEIN Shell: Argumente literal, keine Interpolation/Verkettung.
            CreateNoWindow = true,
            WorkingDirectory = _options.WorkingDirectory ?? string.Empty,
        };
        foreach (var fixedArg in spec.FixedArguments ?? [])
        {
            psi.ArgumentList.Add(fixedArg);
        }

        if (spec.AllowCallerArguments)
        {
            foreach (var callerArg in ReadCallerArgs(args))
            {
                psi.ArgumentList.Add(callerArg);
            }
        }

        if (_options.EnvironmentVariables is { } env)
        {
            foreach (var (key, value) in env)
            {
                psi.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Prozess '{_options.Executable}' ließ sich nicht starten.");
        }

        // Beide Streams nebenläufig lesen (sonst Pipe-Deadlock bei großer Ausgabe). Der Read läuft
        // mit CancellationToken.None und endet, sobald der Prozess (regulär oder per Kill) EOF liefert.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var timeout = _options.TimeoutSeconds is { } s and > 0 ? TimeSpan.FromSeconds(s) : TimeSpan.FromSeconds(DefaultTimeoutSeconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            var killedErr = await stderrTask.ConfigureAwait(false);
            await stdoutTask.ConfigureAwait(false); // Task beobachten, kein unbeobachteter Fehler.
            if (ct.IsCancellationRequested)
            {
                throw; // Aufrufer hat abgebrochen — kein Timeout-Ergebnis vortäuschen.
            }

            var suffix = killedErr.Length > 0 ? "\n" + Cap(killedErr) : string.Empty;
            return Result($"CLI timeout after {timeout.TotalSeconds:0}s - process killed.{suffix}", isError: true);
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var body = stdout.Length > 0 ? stdout : stderr;
        return Result(
            body.Length > 0 ? Cap(body) : $"(exit {process.ExitCode}, no output)",
            isError: process.ExitCode != 0);
    }

    public Task<JsonElement> ReadResourceAsync(Uri uri, CancellationToken ct)
        => throw new NotSupportedException("CLI-Upstreams haben keine Resources.");

    public Task<JsonElement> GetPromptAsync(string promptName, JsonElement? args, CancellationToken ct)
        => throw new NotSupportedException("CLI-Upstreams haben keine Prompts.");

    public Task PingAsync(CancellationToken ct)
    {
        // "Erreichbar" = das Binary ist auflösbar. Bei einem Pfad wird die Existenz geprüft; ein
        // blanker Name wird der PATH-Auflösung überlassen (der echte Aufruf meldet Fehler klar).
        var exe = _options.Executable;
        if ((exe.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || exe.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            && !File.Exists(exe))
        {
            throw new FileNotFoundException($"CLI-Binary '{exe}' nicht gefunden.");
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static IEnumerable<string> ReadCallerArgs(JsonElement args)
    {
        if (args.ValueKind is not JsonValueKind.Object
            || !args.TryGetProperty("args", out var array)
            || array.ValueKind is not JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var element in array.EnumerateArray())
        {
            yield return element.ValueKind is JsonValueKind.String ? element.GetString()! : element.GetRawText();
        }
    }

    private static JsonElement BuildSchema(CliToolSpec spec)
    {
        if (!spec.AllowCallerArguments)
        {
            return JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });
        }

        return JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                args = new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "Extra arguments, appended literally (no shell) after the fixed arguments.",
                },
            },
        });
    }

    private static JsonElement Result(string text, bool isError)
        => JsonSerializer.SerializeToElement(new
        {
            content = new[] { new { type = "text", text } },
            isError,
        });

    private string Cap(string value)
    {
        var max = _options.MaxOutputBytes > 0 ? _options.MaxOutputBytes : 64 * 1024;
        return value.Length <= max
            ? value
            : value[..max] + $"\n... [truncated, {value.Length - max} more chars]";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Prozess ist zwischen Prüfung und Kill beendet worden — nichts zu tun.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Kill ließ sich nicht zustellen — im Prototyp toleriert.
        }
    }
}
