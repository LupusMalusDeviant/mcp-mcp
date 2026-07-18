namespace McpMcp.Abstractions;

/// <summary>Konventionen der Asset-Auslieferung an Agenten (FR-40).</summary>
public static class AssetDelivery
{
    /// <summary>Reservierter Namespace: Assets erscheinen als <c>assets__{name}</c> — kollisionsfrei zu Upstream-Slugs.</summary>
    public const string Namespace = "assets";

    /// <summary>URI-Schema, unter dem Assets zusätzlich als MCP-Resource lesbar sind.</summary>
    public const string UriPrefix = "mcpmcp://assets/";

    public static string PromptName(string assetName) => $"{Namespace}{NamespacedToolName.Separator}{assetName}";

    public static string ResourceUri(string assetName) => $"{UriPrefix}{Uri.EscapeDataString(assetName)}";

    /// <summary>Liefert den Asset-Namen aus einem Prompt-Namen oder einer Resource-URI; null, wenn es keiner ist.</summary>
    public static string? TryGetAssetName(string promptNameOrUri)
    {
        if (promptNameOrUri.StartsWith(UriPrefix, StringComparison.Ordinal))
        {
            return Uri.UnescapeDataString(promptNameOrUri[UriPrefix.Length..]);
        }

        var prefix = Namespace + NamespacedToolName.Separator;
        return promptNameOrUri.StartsWith(prefix, StringComparison.Ordinal)
            ? promptNameOrUri[prefix.Length..]
            : null;
    }
}

public sealed record AssetInfo(
    AssetId Id,
    string Name,
    string? Description,
    AssetVersion LatestVersion,
    DateTimeOffset UpdatedAt);

public sealed record AssetContent(
    AssetId Id,
    AssetVersion Version,
    string Name,
    string Content,
    DateTimeOffset PublishedAt);

/// <summary>
/// Zentrale Verwaltung versionierter Text-Assets (Skills/Prompts/Instructions, FR-40).
/// Auslieferung an Agenten erfolgt MCP-nativ als Prompts/Resources über den Katalog; List ist RBAC-gefiltert.
/// </summary>
public interface IAssetStore
{
    Task<IReadOnlyList<AssetInfo>> ListAsync(IdentityId identity, CancellationToken ct);

    Task<AssetContent> GetAsync(AssetId id, AssetVersion? version, CancellationToken ct);

    Task<AssetVersion> PublishAsync(AssetId id, string content, CancellationToken ct);

    /// <summary>Legt ein neues Asset (Name + Beschreibung) als Version 1 an.</summary>
    Task<AssetId> CreateAsync(string name, string? description, string content, CancellationToken ct);
}
