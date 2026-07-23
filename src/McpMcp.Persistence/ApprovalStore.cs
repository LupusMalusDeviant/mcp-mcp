using System.Text.Json;
using McpMcp.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace McpMcp.Persistence;

/// <summary>
/// Persistente Freigabe-Queue (FR-32, ADR-0012). Ein Neustart darf offene Anfragen nicht
/// verlieren, deshalb DB-gestützt statt in-memory.
///
/// Zeitstempel als UTC-Ticks (bigint) wie im übrigen Schema — SQLite kann DateTimeOffset weder
/// sortieren noch in <c>ExecuteUpdate</c> vergleichen.
/// </summary>
public sealed class ApprovalStore : IApprovalStore
{
    private readonly IDbContextFactory<McpMcpDbContext> _factory;
    private readonly TimeProvider _time;

    public ApprovalStore(IDbContextFactory<McpMcpDbContext> factory, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _time = time ?? TimeProvider.System;
    }

    public async Task<bool> TryConsumeApprovalAsync(
        IdentityId caller, NamespacedToolName tool, string argumentFingerprint, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var nowTicks = _time.GetUtcNow().UtcTicks;

        // Genau eine freigegebene, nicht abgelaufene Anfrage für diesen Aufruf finden und als
        // verbraucht markieren. Freigabe ist einmalig (ADR-0012).
        var match = await db.ApprovalRequests
            .Where(r => r.CallerId == caller.Value
                && r.Tool == tool.Value
                && r.Fingerprint == argumentFingerprint
                && r.State == (int)ApprovalState.Approved
                && r.ExpiresAtTicks > nowTicks)
            .OrderBy(r => r.RequestedAtTicks)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (match is null)
        {
            return false;
        }

        match.State = (int)ApprovalState.Consumed;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<Guid> EnqueueAsync(ApprovalRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var nowTicks = _time.GetUtcNow().UtcTicks;

        // Kein Duplikat beim Retry: wartet schon eine identische, nicht abgelaufene Anfrage,
        // dieselbe Id zurückgeben, statt die Queue mit Wiederholungen zu fluten.
        var existing = await db.ApprovalRequests
            .Where(r => r.CallerId == request.Caller.Value
                && r.Tool == request.Tool.Value
                && r.Fingerprint == request.ArgumentFingerprint
                && r.State == (int)ApprovalState.Pending
                && r.ExpiresAtTicks > nowTicks)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (existing is { } id)
        {
            return id;
        }

        var row = new ApprovalRequestRow
        {
            Id = Guid.NewGuid(),
            CallerId = request.Caller.Value,
            CallerDescription = request.CallerDescription,
            Tool = request.Tool.Value,
            Fingerprint = request.ArgumentFingerprint,
            RedactedArgumentsJson = request.RedactedArguments?.GetRawText(),
            State = (int)ApprovalState.Pending,
            RequestedAtTicks = request.RequestedAt.UtcTicks,
            ExpiresAtTicks = request.ExpiresAt.UtcTicks,
        };

        db.ApprovalRequests.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row.Id;
    }

    public async Task<IReadOnlyList<ApprovalRequest>> ListAsync(ApprovalState? state, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.ApprovalRequests.AsNoTracking();
        if (state is { } s)
        {
            query = query.Where(r => r.State == (int)s);
        }

        var rows = await query.OrderByDescending(r => r.RequestedAtTicks).Take(500)
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(ToRequest)];
    }

    public async Task DecideAsync(Guid requestId, bool approved, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.ApprovalRequests.FindAsync([requestId], ct).ConfigureAwait(false);
        if (row is null || row.State != (int)ApprovalState.Pending)
        {
            return; // schon entschieden oder weg — idempotent
        }

        row.State = (int)(approved ? ApprovalState.Approved : ApprovalState.Denied);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static ApprovalRequest ToRequest(ApprovalRequestRow r) => new(
        r.Id,
        new IdentityId(r.CallerId),
        r.CallerDescription,
        new NamespacedToolName(r.Tool),
        r.Fingerprint,
        r.RedactedArgumentsJson is { } json ? JsonSerializer.Deserialize<JsonElement>(json) : null,
        (ApprovalState)r.State,
        new DateTimeOffset(r.RequestedAtTicks, TimeSpan.Zero),
        new DateTimeOffset(r.ExpiresAtTicks, TimeSpan.Zero));
}
