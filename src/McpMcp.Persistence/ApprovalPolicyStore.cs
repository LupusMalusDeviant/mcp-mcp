using System.Collections.Concurrent;
using McpMcp.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace McpMcp.Persistence;

/// <summary>
/// Welche Tools eine Freigabe erfordern (FR-32, ADR-0012). In-Memory-Cache, weil bei jedem Call
/// gelesen; Write-Through wie bei Guard-Regeln und Description-Overrides — hot-swappable ohne
/// Neustart.
/// </summary>
public sealed class ApprovalPolicyStore : IApprovalPolicy
{
    private readonly IDbContextFactory<McpMcpDbContext> _factory;
    private readonly ConcurrentDictionary<NamespacedToolName, byte> _required = new();

    public ApprovalPolicyStore(IDbContextFactory<McpMcpDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public event EventHandler? Changed;

    public bool RequiresApproval(NamespacedToolName tool) => _required.ContainsKey(tool);

    public IReadOnlyCollection<NamespacedToolName> All => [.. _required.Keys];

    public async Task LoadAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.ApprovalTools.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        _required.Clear();
        foreach (var row in rows)
        {
            _required[new NamespacedToolName(row.Tool)] = 1;
        }
    }

    public async Task SetAsync(NamespacedToolName tool, bool required, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        if (required)
        {
            if (await db.ApprovalTools.FindAsync([tool.Value], ct).ConfigureAwait(false) is null)
            {
                db.ApprovalTools.Add(new ApprovalToolRow { Tool = tool.Value });
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            _required[tool] = 1;
        }
        else
        {
            await db.ApprovalTools.Where(r => r.Tool == tool.Value).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            _required.TryRemove(tool, out _);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
