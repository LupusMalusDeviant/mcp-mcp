using System.Text.Json;
using McpMcp.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpMcp.Upstream;

/// <summary>
/// Kapselt einen SDK-<see cref="McpClient"/> vollständig hinter <see cref="IUpstreamConnection"/> —
/// oberhalb dieser Klasse existieren keine SDK-Typen (DON'T Nr. 1). Discovery ist teil-tolerant:
/// Tools sind Pflicht, Resources/Prompts werden nur gelistet, wenn der Server die Capability meldet.
/// </summary>
internal sealed class SdkUpstreamConnection : IUpstreamConnection
{
    private static readonly string[] ForwardedNotifications =
    [
        NotificationMethods.ToolListChangedNotification,
        NotificationMethods.ResourceListChangedNotification,
        NotificationMethods.PromptListChangedNotification,
    ];

    private readonly McpClient _client;
    private readonly List<IAsyncDisposable> _registrations = [];

    public SdkUpstreamConnection(ServerId id, McpClient client)
    {
        Id = id;
        _client = client;
        foreach (var method in ForwardedNotifications)
        {
            _registrations.Add(_client.RegisterNotificationHandler(method, (notification, _) =>
            {
                NotificationReceived?.Invoke(this, new UpstreamNotificationEventArgs
                {
                    Server = Id,
                    Method = notification.Method,
                    Params = notification.Params is { } p ? JsonSerializer.SerializeToElement(p, McpJsonUtilities.DefaultOptions) : null,
                });
                return default;
            }));
        }
    }

    public ServerId Id { get; }

    public event EventHandler<UpstreamNotificationEventArgs>? NotificationReceived;

    public async Task<UpstreamInventory> DiscoverAsync(CancellationToken ct)
    {
        var tools = await _client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
        var toolDescriptors = tools
            .Select(t => new ToolDescriptor(t.Name, t.Description, t.JsonSchema.Clone()))
            .ToList();

        var resources = new List<ResourceDescriptor>();
        if (_client.ServerCapabilities?.Resources is not null)
        {
            var listed = await _client.ListResourcesAsync(cancellationToken: ct).ConfigureAwait(false);
            resources.AddRange(listed.Select(r => new ResourceDescriptor(
                new Uri(r.Uri, UriKind.RelativeOrAbsolute), r.Name, r.Description, r.MimeType)));
        }

        var prompts = new List<PromptDescriptor>();
        if (_client.ServerCapabilities?.Prompts is not null)
        {
            var listed = await _client.ListPromptsAsync(cancellationToken: ct).ConfigureAwait(false);
            prompts.AddRange(listed.Select(p => new PromptDescriptor(p.Name, p.Description)));
        }

        return new UpstreamInventory(toolDescriptors, resources, prompts);
    }

    public async Task<JsonElement> CallToolAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        var result = await _client
            .CallToolAsync(toolName, JsonArguments.ToDictionary(args), cancellationToken: ct)
            .ConfigureAwait(false);

        return JsonSerializer.SerializeToElement(result, McpJsonUtilities.DefaultOptions);
    }

    public async Task<JsonElement> ReadResourceAsync(Uri uri, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var result = await _client.ReadResourceAsync(uri, cancellationToken: ct).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result, McpJsonUtilities.DefaultOptions);
    }

    public async Task<JsonElement> GetPromptAsync(string promptName, JsonElement? args, CancellationToken ct)
    {
        IReadOnlyDictionary<string, object?>? arguments = args is { } a ? JsonArguments.ToDictionary(a) : null;
        var result = await _client.GetPromptAsync(promptName, arguments, cancellationToken: ct).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result, McpJsonUtilities.DefaultOptions);
    }

    public async Task PingAsync(CancellationToken ct)
        => await _client.PingAsync(cancellationToken: ct).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        foreach (var registration in _registrations)
        {
            try
            {
                await registration.DisposeAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Client bereits weg — irrelevant beim Abbau.
            }
        }

        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
