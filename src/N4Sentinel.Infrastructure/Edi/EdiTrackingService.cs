using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Edi;

/// <summary>
/// Suivi individuel des fichiers EDI (FR-059H) — réutilise directement
/// l'infrastructure de la Phase D : les sous-dossiers déclarés sur
/// <see cref="SharedFolderProfile"/> (en attente/consommé/erreur) et les
/// méthodes <see cref="IN4Connector.ListFilesAsync"/> déjà éprouvées contre
/// de vrais fichiers.
///
/// PARTENAIRE ET TYPE DE MESSAGE NE SONT JAMAIS DEVINÉS. Sans motif déclaré
/// sur le composant, ces deux champs restent null — voir
/// <see cref="SharedFolderProfile.EdiFileNamingPattern"/>.
///
/// UN FICHIER GARDE SON IDENTITÉ D'UN RELEVÉ À L'AUTRE : contrairement à
/// <see cref="Supervision.SharedFolderHealthService"/>, qui ne compte que des
/// agrégats, ce service reconnaît un fichier déjà vu et suit sa transition
/// d'un sous-dossier à l'autre (ex. en attente → intégré).
/// </summary>
public sealed class EdiTrackingService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    ConnectorTargetFactory targetFactory,
    IN4Connector connector,
    ILogger<EdiTrackingService> logger)
{
    public async Task<List<EdiFile>> SyncAsync(N4Component composant, CancellationToken ct = default)
    {
        var profil = composant.SharedFolder;
        if (!profil.IsConfigured || composant.Server is null)
            return await GetForComponentAsync(composant.Id, ct);

        var resolution = await targetFactory.CreateAsync(composant.Server, ct);
        if (!resolution.Succeeded)
        {
            logger.LogDebug("Synchronisation EDI impossible pour {Composant} : {Erreur}", composant.LogicalName, resolution.Error);
            return await GetForComponentAsync(composant.Id, ct);
        }

        var cible = resolution.Target!;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existants = await db.EdiFiles.Where(f => f.ComponentId == composant.Id).ToListAsync(ct);
        var parNom = existants.ToDictionary(f => f.FileName, f => f, StringComparer.OrdinalIgnoreCase);

        await SyncSousDossierAsync(db, parNom, composant, profil.PendingSubfolder, EdiFileStatus.EnAttente, cible, ct);
        await SyncSousDossierAsync(db, parNom, composant, profil.ConsumedSubfolder, EdiFileStatus.Integre, cible, ct);
        await SyncSousDossierAsync(db, parNom, composant, profil.ErrorSubfolder, EdiFileStatus.Rejete, cible, ct);

        await db.SaveChangesAsync(ct);

        return await GetForComponentAsync(composant.Id, ct);
    }

    private async Task SyncSousDossierAsync(
        N4SentinelDbContext db, Dictionary<string, EdiFile> parNom, N4Component composant,
        string? sousDossier, EdiFileStatus statut, ConnectorTarget cible, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sousDossier)) return;

        var chemin = composant.SharedFolder.RootPath!.TrimEnd('\\', '/') + "\\" + sousDossier.TrimStart('\\', '/');
        var resultat = await connector.ListFilesAsync(cible, chemin, ct);
        if (!resultat.Succeeded || !resultat.Value!.Exists) return;

        foreach (var fichier in resultat.Value.Files)
        {
            if (parNom.TryGetValue(fichier.Name, out var existant))
            {
                var vientDEtreIntegre = statut == EdiFileStatus.Integre && existant.Status != EdiFileStatus.Integre;

                existant.Status = statut;
                existant.LastSeenAt = DateTimeOffset.UtcNow;
                if (vientDEtreIntegre) existant.IntegratedAt = DateTimeOffset.UtcNow;
                existant.ConsecutiveRejections = statut == EdiFileStatus.Rejete ? existant.ConsecutiveRejections + 1 : 0;
            }
            else
            {
                var (partenaire, type) = ExtraireMetadonnees(fichier.Name, composant.SharedFolder.EdiFileNamingPattern);

                var nouveau = new EdiFile
                {
                    ComponentId = composant.Id,
                    FileName = fichier.Name,
                    Status = statut,
                    Partner = partenaire,
                    MessageType = type,
                    IntegratedAt = statut == EdiFileStatus.Integre ? DateTimeOffset.UtcNow : null,
                    ConsecutiveRejections = statut == EdiFileStatus.Rejete ? 1 : 0
                };

                db.EdiFiles.Add(nouveau);
                parNom[fichier.Name] = nouveau;
            }
        }
    }

    /// <summary>
    /// N'extrait RIEN sans motif explicitement déclaré. Un motif qui ne
    /// correspond pas, ou absent, laisse les deux champs null — jamais une
    /// tentative de deviner à partir du nom seul.
    /// </summary>
    private static (string? Partner, string? MessageType) ExtraireMetadonnees(string fileName, string? motif)
    {
        if (string.IsNullOrWhiteSpace(motif)) return (null, null);

        try
        {
            var m = Regex.Match(fileName, motif, RegexOptions.None, TimeSpan.FromSeconds(1));
            if (!m.Success) return (null, null);

            var partenaire = m.Groups["partner"] is { Success: true } gp ? gp.Value : null;
            var type = m.Groups["type"] is { Success: true } gt ? gt.Value : null;
            return (partenaire, type);
        }
        catch (ArgumentException) { return (null, null); }
        catch (RegexMatchTimeoutException) { return (null, null); }
    }

    public async Task<List<EdiFile>> GetForComponentAsync(Guid componentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.EdiFiles
            .AsNoTracking()
            .Where(f => f.ComponentId == componentId)
            .OrderByDescending(f => f.LastSeenAt)
            .ToListAsync(ct);
    }
}
