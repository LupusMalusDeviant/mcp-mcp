using McpMcp.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace McpMcp.Persistence;

/// <summary>UI-Nutzerverwaltung (WP6.1, FR-30): Cookie-Login gegen PBKDF2-Passwort-Hashes.</summary>
public sealed class UiUserService : IUiUserService
{
    private readonly IDbContextFactory<McpMcpDbContext> _factory;
    private readonly TimeProvider _time;

    public UiUserService(IDbContextFactory<McpMcpDbContext> factory, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<UiUserInfo?> ValidateCredentialsAsync(string username, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.UiUsers.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Username == username, ct).ConfigureAwait(false);

        return row is not null && Pbkdf2Hasher.Verify(password, row.PasswordHash)
            ? ToInfo(row)
            : null;
    }

    public async Task<UiUserInfo> CreateAsync(string username, string password, UiRole role, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrEmpty(password);

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (await db.UiUsers.AnyAsync(u => u.Username == username, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Ein UI-Nutzer '{username}' existiert bereits.");
        }

        var row = new UiUserRow
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = Pbkdf2Hasher.Hash(password),
            Role = (int)role,
            CreatedAt = _time.GetUtcNow(),
        };
        db.UiUsers.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToInfo(row);
    }

    public async Task SetPasswordAsync(Guid userId, string newPassword, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(newPassword);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var hash = Pbkdf2Hasher.Hash(newPassword);
        await db.UiUsers.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, hash), ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.UiUsers.Where(u => u.Id == userId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UiUserInfo>> ListAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.UiUsers.AsNoTracking().OrderBy(u => u.Username).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(ToInfo)];
    }

    public async Task<bool> AnyExistAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.UiUsers.AnyAsync(ct).ConfigureAwait(false);
    }

    private static UiUserInfo ToInfo(UiUserRow row)
        => new(row.Id, row.Username, (UiRole)row.Role, row.CreatedAt);
}
