using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using McpMcp.Abstractions;

namespace McpMcp.Upstream.OpenApi;

/// <summary>
/// API→MCP-Brücke (FR-19, ADR-0008): eine per OpenAPI-Spec beschriebene REST-API erscheint
/// als normaler Upstream — hot-swappable, profilierbar und auditiert wie jeder MCP-Server.
/// </summary>
public sealed class OpenApiUpstreamConnector : IUpstreamConnector
{
    public UpstreamTransportKind Kind => UpstreamTransportKind.OpenApi;

    public async Task<IUpstreamConnection> ConnectAsync(ServerId id, UpstreamServerConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        var options = config.OpenApi
            ?? throw new ArgumentException($"Config '{config.Slug}' hat keine OpenApi-Optionen.", nameof(config));

        var specJson = await LoadSpecAsync(options.SpecLocation, ct).ConfigureAwait(false);
        var (operations, serverUrl) = OpenApiSpecParser.Parse(specJson);

        var baseAddress = options.BaseAddress
            ?? serverUrl
            ?? throw new OpenApiImportException(
                "Weder BaseAddress konfiguriert noch eine absolute Server-URL in der Spec — Ziel-API unbekannt.");

        var http = new HttpClient { BaseAddress = baseAddress, Timeout = Timeout.InfiniteTimeSpan };
        ApplyAuth(http, options);
        return new OpenApiUpstreamConnection(id, operations, http);
    }

    private const long MaxSpecBytes = 10 * 1024 * 1024;

    private static async Task<string> LoadSpecAsync(Uri location, CancellationToken ct)
    {
        if (location.IsFile)
        {
            var info = new FileInfo(location.LocalPath);
            if (info.Exists && info.Length > MaxSpecBytes)
            {
                throw new OpenApiImportException($"Spec-Datei überschreitet {MaxSpecBytes / (1024 * 1024)} MB.");
            }

            return await File.ReadAllTextAsync(location.LocalPath, ct).ConfigureAwait(false);
        }

        if (location.Scheme is "http" or "https")
        {
            // Größenbegrenzung gegen Memory-Exhaustion-DoS über eine riesige Spec (Security-Audit WP7.2).
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var response = await http.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxSpecBytes)
            {
                throw new OpenApiImportException($"Spec überschreitet {MaxSpecBytes / (1024 * 1024)} MB.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var limited = new StreamReader(stream);
            var buffer = new char[8192];
            var sb = new System.Text.StringBuilder();
            int read;
            while ((read = await limited.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                sb.Append(buffer, 0, read);
                if (sb.Length > MaxSpecBytes)
                {
                    throw new OpenApiImportException($"Spec überschreitet {MaxSpecBytes / (1024 * 1024)} MB.");
                }
            }

            return sb.ToString();
        }

        throw new OpenApiImportException($"Spec-Quelle '{location}' wird nicht unterstützt (nur file:// und http(s)://).");
    }

    private static void ApplyAuth(HttpClient http, OpenApiTransportOptions options)
    {
        switch (options.AuthKind)
        {
            case OpenApiAuthKind.None:
                break;
            case OpenApiAuthKind.Bearer:
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Bearer", RequireCredential(options));
                break;
            case OpenApiAuthKind.Basic:
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(RequireCredential(options))));
                break;
            case OpenApiAuthKind.ApiKeyHeader:
                http.DefaultRequestHeaders.Add(
                    options.ApiKeyHeaderName ?? "X-Api-Key", RequireCredential(options));
                break;
            default:
                throw new OpenApiImportException($"AuthKind {options.AuthKind} wird nicht unterstützt.");
        }
    }

    private static string RequireCredential(OpenApiTransportOptions options)
        => options.Credential
            ?? throw new OpenApiImportException($"AuthKind {options.AuthKind} verlangt ein Credential.");
}

internal sealed class OpenApiUpstreamConnection : IUpstreamConnection
{
    private readonly Dictionary<string, OpenApiOperationSpec> _operations;
    private readonly HttpClient _http;

    public OpenApiUpstreamConnection(ServerId id, IReadOnlyList<OpenApiOperationSpec> operations, HttpClient http)
    {
        Id = id;
        _operations = operations.ToDictionary(o => o.OperationId, StringComparer.Ordinal);
        _http = http;
    }

    public ServerId Id { get; }

    public event EventHandler<UpstreamNotificationEventArgs>? NotificationReceived
    {
        add { }
        remove { }
    }

    public Task<UpstreamInventory> DiscoverAsync(CancellationToken ct)
        => Task.FromResult(new UpstreamInventory(
            [.. _operations.Values.Select(o => new ToolDescriptor(o.OperationId, o.Description, o.InputSchema))],
            [],
            []));

    public async Task<JsonElement> CallToolAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        if (!_operations.TryGetValue(toolName, out var operation))
        {
            throw new InvalidOperationException($"Operation '{toolName}' existiert nicht in der importierten Spec.");
        }

        using var request = BuildRequest(operation, args);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // CallToolResult-Form wie bei echten MCP-Upstreams — der Rest des Gateways bleibt uniform.
        return JsonSerializer.SerializeToElement(new
        {
            content = new[]
            {
                new { type = "text", text = body.Length > 0 ? body : $"HTTP {(int)response.StatusCode}" },
            },
            isError = !response.IsSuccessStatusCode,
        });
    }

    public Task<JsonElement> ReadResourceAsync(Uri uri, CancellationToken ct)
        => throw new NotSupportedException("OpenAPI-Upstreams haben keine Resources.");

    public Task<JsonElement> GetPromptAsync(string promptName, JsonElement? args, CancellationToken ct)
        => throw new NotSupportedException("OpenAPI-Upstreams haben keine Prompts.");

    public async Task PingAsync(CancellationToken ct)
    {
        // Erreichbarkeit genügt — der Statuscode ist egal (viele APIs haben keinen Health-Pfad).
        using var request = new HttpRequestMessage(HttpMethod.Head, _http.BaseAddress);
        using var _ = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private static HttpRequestMessage BuildRequest(OpenApiOperationSpec operation, JsonElement args)
    {
        string? GetArg(string name)
            => args.ValueKind is JsonValueKind.Object && args.TryGetProperty(name, out var value)
                ? value.ValueKind is JsonValueKind.String ? value.GetString() : value.GetRawText()
                : null;

        var path = operation.PathTemplate;
        var query = new List<string>();
        var request = new HttpRequestMessage(HttpMethod.Parse(operation.HttpMethod), (Uri?)null);

        foreach (var parameter in operation.Parameters)
        {
            var value = GetArg(parameter.Name);
            if (value is null)
            {
                if (parameter.Required)
                {
                    throw new InvalidOperationException(
                        $"Pflicht-Parameter '{parameter.Name}' fehlt für Operation '{operation.OperationId}'.");
                }

                continue;
            }

            switch (parameter.Location)
            {
                case OpenApiParameterLocation.Path:
                    path = path.Replace($"{{{parameter.Name}}}", Uri.EscapeDataString(value), StringComparison.Ordinal);
                    break;
                case OpenApiParameterLocation.Query:
                    query.Add($"{Uri.EscapeDataString(parameter.Name)}={Uri.EscapeDataString(value)}");
                    break;
                case OpenApiParameterLocation.Header:
                    // CR/LF aus Aufrufer-Argumenten würde Header-Injection erlauben (Security-Audit WP7.2).
                    if (value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Header-Parameter '{parameter.Name}' enthält unzulässige Zeilenumbrüche.");
                    }

                    request.Headers.TryAddWithoutValidation(parameter.Name, value);
                    break;
            }
        }

        if (operation.HasBody
            && args.ValueKind is JsonValueKind.Object
            && args.TryGetProperty("body", out var bodyElement))
        {
            request.Content = new StringContent(bodyElement.GetRawText(), Encoding.UTF8, "application/json");
        }

        var uri = path + (query.Count > 0 ? "?" + string.Join('&', query) : string.Empty);
        request.RequestUri = new Uri(uri.TrimStart('/'), UriKind.Relative);
        return request;
    }
}
