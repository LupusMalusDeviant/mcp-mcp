namespace McpMcp.Abstractions;

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
}
