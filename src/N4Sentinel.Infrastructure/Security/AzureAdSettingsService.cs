using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Security;

/// <summary>Lecture/écriture des paramètres Azure AD (SEC-001 V2) — voir <see cref="AzureAdSettings"/>.</summary>
public sealed class AzureAdSettingsService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    IAuditWriter auditWriter)
{
    public async Task<AzureAdSettings> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var parametres = await db.AzureAdSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return parametres ?? new AzureAdSettings();
    }

    public async Task SaveAsync(AzureAdSettings parametres, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existants = await db.AzureAdSettings.FirstOrDefaultAsync(ct);
        if (existants is null)
        {
            db.AzureAdSettings.Add(parametres);
        }
        else
        {
            existants.Enabled = parametres.Enabled;
            existants.TenantId = parametres.TenantId;
            existants.ClientId = parametres.ClientId;
            existants.Authority = parametres.Authority;
            existants.PostLogoutRedirectUri = parametres.PostLogoutRedirectUri;
        }

        await db.SaveChangesAsync(ct);

        await auditWriter.WriteAsync(
            AuditAction.Modification, AuditOutcome.Succes, actor,
            entityType: nameof(AzureAdSettings),
            detail: $"Enabled={parametres.Enabled}, TenantId={parametres.TenantId ?? "—"}. "
                  + "Rappel : ce paramètre prépare la V2, aucune connexion SSO fonctionnelle n'est active en V1.",
            ct: ct);
    }
}
