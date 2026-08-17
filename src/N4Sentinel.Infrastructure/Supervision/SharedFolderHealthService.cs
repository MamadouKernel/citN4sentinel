using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Supervision;

/// <summary>
/// Santé d'un dossier partagé supervisé (FR-059A à FR-059D) : accessibilité,
/// volumétrie, capacité d'écriture non destructive, fichiers obligatoires et
/// indices de corruption.
///
/// CE QUI EST GROUNDÉ, ET CE QUI NE L'EST PAS. Le corpus de support (script
/// Fix-N4-AMQCorruption.ps1, fiche SOP-2 A) ne documente qu'UN SEUL fichier
/// KahaDB par son nom : « db.data ». Aucun autre nom de fichier, format
/// binaire, ou convention de sous-dossier n'est attesté nulle part. Ce
/// service se limite donc à ce fichier pour tout contrôle « fichier
/// obligatoire », et à des faits observables (présence, taille, verrou,
/// ancienneté) pour tout le reste — jamais une analyse du format KahaDB
/// lui-même.
///
/// La classification en attente/consommé/bloqué/erreur (FR-059C) dépend de
/// sous-dossiers DÉCLARÉS PAR LE SITE (<see cref="SharedFolderProfile"/>) :
/// rien n'impose de convention de nommage, donc rien n'en devine une.
/// </summary>
public sealed class SharedFolderHealthService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    ConnectorTargetFactory targetFactory,
    IN4Connector connector,
    IAuditWriter auditWriter,
    ILogger<SharedFolderHealthService> logger)
{
    /// <summary>Seul nom de fichier KahaDB attesté dans le corpus de support.</summary>
    private const string FichierKahaDb = "db.data";

    /// <summary>
    /// §3.18 : attestation humaine qu'une sauvegarde de ce dossier partagé
    /// vient d'être faite — hors application, jamais capturée automatiquement.
    /// </summary>
    public async Task<string?> DeclareBackupAsync(
        Guid componentId, string actor, string? note, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actor)) return "Acteur manquant.";

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var composant = await db.Components.FirstOrDefaultAsync(c => c.Id == componentId, ct);
        if (composant is null) return "Composant introuvable.";
        if (!composant.SharedFolder.IsConfigured)
            return $"Aucun dossier partagé n'est configuré sur « {composant.LogicalName} ».";

        composant.SharedFolder.LastBackupAt = DateTimeOffset.UtcNow;
        composant.SharedFolder.LastBackupBy = actor;
        composant.SharedFolder.LastBackupNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        await db.SaveChangesAsync(ct);

        await auditWriter.WriteAsync(
            AuditAction.Creation, AuditOutcome.Succes, actor,
            entityType: nameof(SharedFolderProfile), entityId: componentId.ToString(),
            entityLabel: composant.LogicalName, environmentId: composant.EnvironmentId,
            detail: $"Sauvegarde du dossier partagé déclarée" + (note is { Length: > 0 } ? $" — {note}" : string.Empty),
            ct: ct);

        return null;
    }

    public async Task<SharedFolderSnapshot> EvaluateAsync(N4Component composant, CancellationToken ct = default)
    {
        var snapshot = new SharedFolderSnapshot { ComponentId = composant.Id };
        var profil = composant.SharedFolder;

        if (!profil.IsConfigured)
        {
            snapshot.UnreachableReason = $"Aucun chemin de dossier partagé n'est déclaré pour « {composant.LogicalName} ».";
            return await EnregistrerAsync(snapshot, ct);
        }

        if (composant.Server is null)
        {
            snapshot.Reachable = false;
            snapshot.UnreachableReason = "Aucun serveur associé à ce composant.";
            return await EnregistrerAsync(snapshot, ct);
        }

        var resolution = await targetFactory.CreateAsync(composant.Server, ct);
        if (!resolution.Succeeded)
        {
            snapshot.Reachable = false;
            snapshot.UnreachableReason = resolution.Error;
            return await EnregistrerAsync(snapshot, ct);
        }

        var cible = resolution.Target!;
        var racine = profil.RootPath!;

        var listeRacine = await connector.ListFilesAsync(cible, racine, ct);
        if (!listeRacine.Succeeded)
        {
            snapshot.Reachable = false;
            snapshot.UnreachableReason = listeRacine.Error;
            logger.LogDebug("Dossier partagé « {Composant} » injoignable : {Erreur}", composant.LogicalName, listeRacine.Error);
            return await EnregistrerAsync(snapshot, ct);
        }

        if (!listeRacine.Value!.Exists)
        {
            snapshot.Reachable = false;
            snapshot.UnreachableReason = $"Le chemin « {racine} » n'existe pas ou n'est pas accessible.";
            return await EnregistrerAsync(snapshot, ct);
        }

        snapshot.Reachable = true;

        var fichiers = listeRacine.Value.Files;
        snapshot.TotalFileCount = fichiers.Count;
        snapshot.TotalSizeBytes = fichiers.Sum(f => f.SizeBytes);
        snapshot.OldestFileAt = fichiers.Count > 0 ? fichiers.Min(f => f.LastWriteTime) : null;
        snapshot.NewestFileAt = fichiers.Count > 0 ? fichiers.Max(f => f.LastWriteTime) : null;

        await ClassifierAsync(snapshot, profil, cible, ct);

        var chrono = System.Diagnostics.Stopwatch.StartNew();
        var probe = await connector.ProbeWriteAsync(cible, racine, ct);
        chrono.Stop();

        snapshot.CanWrite = probe.Succeeded ? probe.Value!.CanWrite : null;
        if (probe.Succeeded && probe.Value!.CanWrite)
            snapshot.WriteLatencyMs = chrono.Elapsed.TotalMilliseconds;
        if (probe.Succeeded && !probe.Value!.CanWrite && probe.Value.Error is { Length: > 0 } motifEcriture)
            snapshot.CorruptionIndicators.Add($"Écriture impossible dans le dossier : {motifEcriture}");

        // FR-059B : latence et croissance, jugées uniquement contre un seuil
        // DÉCLARÉ sur le composant — jamais une limite devinée.
        if (profil.MaxWriteLatencyMs is { } seuilLatence && snapshot.WriteLatencyMs is { } latence && latence > seuilLatence)
            snapshot.HealthWarnings.Add(
                $"Temps de réponse en écriture de {latence:0} ms, au-delà du seuil déclaré de {seuilLatence} ms.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var precedent = await db.SharedFolderSnapshots.AsNoTracking()
            .Where(s => s.ComponentId == composant.Id && s.Reachable == true)
            .OrderByDescending(s => s.CapturedAt)
            .FirstOrDefaultAsync(ct);

        if (precedent is not null)
        {
            var ecoule = snapshot.CapturedAt - precedent.CapturedAt;
            if (ecoule > TimeSpan.Zero)
            {
                snapshot.GrowthBytesPerHour = (snapshot.TotalSizeBytes - precedent.TotalSizeBytes) / ecoule.TotalHours;

                if (profil.MaxGrowthBytesPerHour is { } seuilCroissance
                    && snapshot.GrowthBytesPerHour is { } croissance && croissance > seuilCroissance)
                    snapshot.HealthWarnings.Add(
                        $"Croissance de {croissance / 1024.0 / 1024.0:0.0} Mo/h, au-delà du seuil déclaré de "
                        + $"{seuilCroissance / 1024.0 / 1024.0:0.0} Mo/h.");
            }
        }

        if (profil.Category == SharedFolderCategory.ActiveMqKahaDb)
            ControlerKahaDb(snapshot, fichiers);
        else
            snapshot.MandatoryFilesPresent = null;

        return await EnregistrerAsync(snapshot, ct);
    }

    /// <summary>
    /// Seul contrôle de fichier obligatoire réellement grounded : la
    /// présence et la taille de « db.data ». Son absence n'est PAS en soi une
    /// preuve d'incident — il est reconstruit sans risque au redémarrage —
    /// mais reste un indice à faire remonter, jamais une conclusion.
    /// </summary>
    private static void ControlerKahaDb(SharedFolderSnapshot snapshot, IReadOnlyList<RemoteFileInfo> fichiers)
    {
        var db = fichiers.FirstOrDefault(f => string.Equals(f.Name, FichierKahaDb, StringComparison.OrdinalIgnoreCase));

        if (db is null)
        {
            snapshot.MandatoryFilesPresent = false;
            snapshot.MissingMandatoryFiles.Add(FichierKahaDb);
            snapshot.CorruptionIndicators.Add(
                $"« {FichierKahaDb} » est absent. Il est reconstruit sans risque au redémarrage de N4 — "
                + "ce n'est un indice que si les services tournent déjà sans qu'il ait réapparu.");
            return;
        }

        snapshot.MandatoryFilesPresent = true;

        if (db.SizeBytes == 0)
            snapshot.CorruptionIndicators.Add($"« {FichierKahaDb} » a une taille nulle.");
    }

    /// <summary>
    /// FR-059C : compte les fichiers de chaque sous-dossier déclaré, et
    /// signale une ancienneté anormale côté « en attente » si un seuil a été
    /// déclaré. Un sous-dossier non déclaré reste « non classifié » (null),
    /// jamais compté comme vide.
    /// </summary>
    private async Task ClassifierAsync(
        SharedFolderSnapshot snapshot, SharedFolderProfile profil, ConnectorTarget cible, CancellationToken ct)
    {
        var pending = await RelireSousDossierAsync(cible, profil.RootPath!, profil.PendingSubfolder, ct);
        snapshot.PendingCount = pending?.Files.Count;

        if (pending is { Files.Count: > 0 })
        {
            var plusAncien = pending.Files.Min(f => f.LastWriteTime);
            var age = (DateTimeOffset.UtcNow - plusAncien).TotalHours;
            snapshot.OldestPendingAgeHours = Math.Round(age, 1);

            if (profil.MaxPendingAgeHours is { } seuil && age > seuil)
                snapshot.CorruptionIndicators.Add(
                    $"Un fichier en attente depuis {age:F0} h, au-delà du seuil déclaré ({seuil} h).");
        }

        snapshot.ConsumedCount = (await RelireSousDossierAsync(cible, profil.RootPath!, profil.ConsumedSubfolder, ct))?.Files.Count;
        snapshot.BlockedCount = (await RelireSousDossierAsync(cible, profil.RootPath!, profil.BlockedSubfolder, ct))?.Files.Count;
        snapshot.ErrorCount = (await RelireSousDossierAsync(cible, profil.RootPath!, profil.ErrorSubfolder, ct))?.Files.Count;
    }

    private async Task<FolderSnapshot?> RelireSousDossierAsync(
        ConnectorTarget cible, string racine, string? sousDossier, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sousDossier)) return null;

        var chemin = racine.TrimEnd('\\', '/') + "\\" + sousDossier.TrimStart('\\', '/');
        var resultat = await connector.ListFilesAsync(cible, chemin, ct);
        return resultat.Succeeded && resultat.Value!.Exists ? resultat.Value : null;
    }

    private async Task<SharedFolderSnapshot> EnregistrerAsync(SharedFolderSnapshot snapshot, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.SharedFolderSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);
        return snapshot;
    }

    /// <summary>Historique des relevés d'un composant, du plus récent au plus ancien.</summary>
    public async Task<List<SharedFolderSnapshot>> GetHistoryAsync(
        Guid componentId, int count = 20, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SharedFolderSnapshots
            .AsNoTracking()
            .Where(s => s.ComponentId == componentId)
            .OrderByDescending(s => s.CapturedAt)
            .Take(count)
            .ToListAsync(ct);
    }
}
