using Bast.Application.Persistence;
using Bast.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bast.Application;

/// <summary>
/// Armazenamento seguro de credenciais (tokens, segredos de aplicativo).
/// Nada do que passa por aqui pode ser gravado em logs nem no banco de dados.
/// </summary>
public interface ISecretStore
{
    string? Get(string key);
    void Set(string key, string value);
    void Delete(string key);
    bool Exists(string key);
}

/// <summary>A plataforma recusou as credenciais: o usuário precisa conectar a conta de novo.</summary>
public sealed class AuthRequiredException(string message) : Exception(message);

/// <summary>Situação das contas conectadas. As credenciais em si ficam no <see cref="ISecretStore"/>.</summary>
public sealed class AccountService(IDbContextFactory<BastDbContext> factory, TimeProvider time)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<PlatformAccount>> GetAllAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.PlatformAccounts.AsNoTracking().ToListAsync(ct);
    }

    public Task SetConnectedAsync(Platform platform, string externalAccountId, string displayName, CancellationToken ct) =>
        UpsertAsync(platform, account =>
        {
            account.Status = AccountStatus.Connected;
            account.ExternalAccountId = externalAccountId;
            account.DisplayName = displayName;
            account.ConnectedAtUtc = Now;
            account.LastError = null;
        }, ct);

    public Task SetNeedsReauthAsync(Platform platform, string error, CancellationToken ct) =>
        UpsertAsync(platform, account =>
        {
            account.Status = AccountStatus.NeedsReauth;
            account.LastError = error;
        }, ct);

    public Task SetDisconnectedAsync(Platform platform, CancellationToken ct) =>
        UpsertAsync(platform, account =>
        {
            account.Status = AccountStatus.Disconnected;
            account.LastError = null;
        }, ct);

    private async Task UpsertAsync(Platform platform, Action<PlatformAccount> change, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var account = await db.PlatformAccounts.FirstOrDefaultAsync(a => a.Platform == platform, ct);
        if (account is null)
        {
            account = new PlatformAccount { Platform = platform };
            db.PlatformAccounts.Add(account);
        }

        change(account);
        account.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(ct);
    }
}
