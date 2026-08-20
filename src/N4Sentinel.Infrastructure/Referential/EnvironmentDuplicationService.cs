using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Security;

namespace N4Sentinel.Infrastructure.Referential;

/// <summary>
/// Duplication d'un environnement pour en préparer un autre de même topologie
/// — le cas courant : monter UAT à partir de PROD.
///
/// LE DANGER DE CETTE FONCTION EST LE NOM D'HÔTE. Copier PROD à l'identique
/// produirait un environnement nommé « UAT » dont les fiches pointent les
/// machines de PRODUCTION. Un arrêt complet lancé sur cet « UAT » arrêterait
/// la production, et rien à l'écran ne le laisserait deviner : le bandeau
/// dirait UAT.
///
/// La copie ne transporte donc AUCUN nom d'hôte exploitable. Chaque serveur
/// naît avec un nom à renseigner, en brouillon, et l'environnement lui-même
/// naît en brouillon — ce qui suffit à interdire toute exécution
/// (<see cref="Orchestration.ExecutionService"/> refuse un environnement qui
/// n'est ni validé ni actif). L'opérateur doit saisir les vrais noms, un par
/// un, consciemment.
/// </summary>
public sealed class EnvironmentDuplicationService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    IAuditWriter auditWriter,
    ILogger<EnvironmentDuplicationService> logger)
{
    /// <summary>Préfixe des noms d'hôte copiés. Volontairement non résoluble.</summary>
    public const string PrefixeAResoudre = "A-RENSEIGNER-";

    public sealed record Resultat(
        Guid? EnvironmentId, int Serveurs, int Composants, int Dependances, string? Erreur)
    {
        public bool Succeeded => Erreur is null;
        public static Resultat Echec(string erreur) => new(null, 0, 0, 0, erreur);
    }

    public async Task<Resultat> DupliquerAsync(
        Guid sourceId, string codeCible, string nomCible, EnvironmentKind natureCible,
        string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(codeCible)) return Resultat.Echec("Le code du nouvel environnement est obligatoire.");
        if (string.IsNullOrWhiteSpace(nomCible)) return Resultat.Echec("Le nom du nouvel environnement est obligatoire.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var source = await db.Environments.AsNoTracking().FirstOrDefaultAsync(e => e.Id == sourceId, ct);
        if (source is null) return Resultat.Echec("Environnement source introuvable.");

        codeCible = codeCible.Trim();
        if (await db.Environments.AnyAsync(e => e.Code == codeCible, ct))
            return Resultat.Echec($"Un environnement porte déjà le code « {codeCible} ».");

        var serveurs = await db.Servers.AsNoTracking()
            .Where(s => s.EnvironmentId == sourceId).ToListAsync(ct);

        var composants = await db.Components.AsNoTracking()
            .Where(c => c.EnvironmentId == sourceId).ToListAsync(ct);

        var dependances = await db.ComponentDependencies.AsNoTracking()
            .Where(d => d.Component!.EnvironmentId == sourceId).ToListAsync(ct);

        // --- Environnement -----------------------------------------------
        // Naît en BROUILLON : aucune opération ne peut le viser tant que
        // quelqu'un ne l'a pas relu et validé.
        var cible = new N4Environment
        {
            Code = codeCible,
            Name = nomCible.Trim(),
            Kind = natureCible,
            Status = LifecycleStatus.Brouillon,
            Criticality = source.Criticality,
            TimeZoneId = source.TimeZoneId,
            ClockToleranceSeconds = source.ClockToleranceSeconds,
            ExpectedTimeSource = source.ExpectedTimeSource,
            Description =
                $"Copie de « {source.Code} » du {DateTimeOffset.Now:dd/MM/yyyy}. "
                + "LES NOMS D'HÔTE SONT À RENSEIGNER : ils n'ont pas été copiés, "
                + "pour qu'aucune fiche ne pointe par accident les machines de l'environnement d'origine."
        };

        db.Environments.Add(cible);
        await db.SaveChangesAsync(ct);

        // --- Serveurs -----------------------------------------------------
        var correspondanceServeurs = new Dictionary<Guid, Guid>();

        foreach (var s in serveurs)
        {
            var copie = new N4Server
            {
                EnvironmentId = cible.Id,
                // Le nom d'origine est conservé DANS LA DESCRIPTION, jamais
                // dans le champ qui sert à se connecter.
                HostName = $"{PrefixeAResoudre}{s.HostName}",
                OperatingSystem = s.OperatingSystem,
                WinRmPort = s.WinRmPort,
                UseSsl = s.UseSsl,
                Criticality = s.Criticality,
                Status = LifecycleStatus.Brouillon,
                Description = $"Copié depuis « {s.HostName} » ({source.Code}). "
                            + "Remplacer le nom d'hôte par celui de la machine réelle.",
                TechnicalOwner = s.TechnicalOwner
                // CredentialReference volontairement NON copiée : les comptes
                // techniques sont propres à un environnement.
            };

            db.Servers.Add(copie);
            await db.SaveChangesAsync(ct);
            correspondanceServeurs[s.Id] = copie.Id;
        }

        // --- Composants ---------------------------------------------------
        var correspondanceComposants = new Dictionary<Guid, Guid>();

        foreach (var c in composants)
        {
            // Un composant peut n'être rattaché à aucun serveur (systèmes
            // externes) : il se copie quand même, sans serveur.
            Guid? nouveauServeur = null;

            if (c.ServerId is { } ancienServeur)
            {
                if (!correspondanceServeurs.TryGetValue(ancienServeur, out var trouve)) continue;
                nouveauServeur = trouve;
            }

            var copie = new N4Component
            {
                EnvironmentId = cible.Id,
                ServerId = nouveauServeur,
                LogicalName = c.LogicalName,
                Role = c.Role,
                WindowsServiceName = c.WindowsServiceName,
                ProcessName = c.ProcessName,
                Port = c.Port,
                Endpoint = c.Endpoint,
                StartOrder = c.StartOrder,
                Criticality = c.Criticality,
                ControlMode = c.ControlMode,
                Status = LifecycleStatus.Brouillon,
                TechnicalOwner = c.TechnicalOwner,
                Description = c.Description,
                Readiness = Copier(c.Readiness),
                SharedFolder = Copier(c.SharedFolder)
            };

            db.Components.Add(copie);
            await db.SaveChangesAsync(ct);
            correspondanceComposants[c.Id] = copie.Id;
        }

        // --- Dépendances ---------------------------------------------------
        // Elles portent l'ordre de démarrage et d'arrêt : les perdre ferait
        // repartir l'exploitant d'une page blanche sur ce qui compte le plus.
        var reportees = 0;

        foreach (var d in dependances)
        {
            if (!correspondanceComposants.TryGetValue(d.ComponentId, out var de)) continue;
            if (!correspondanceComposants.TryGetValue(d.DependsOnComponentId, out var vers)) continue;

            db.ComponentDependencies.Add(new ComponentDependency
            {
                ComponentId = de,
                DependsOnComponentId = vers,
                Kind = d.Kind,
                Notes = d.Notes
            });
            reportees++;
        }

        if (reportees > 0) await db.SaveChangesAsync(ct);

        await auditWriter.WriteAsync(
            AuditAction.Creation, AuditOutcome.Succes, actor,
            entityType: nameof(N4Environment), entityId: cible.Id.ToString(),
            entityLabel: $"{cible.Code} — {cible.Name}",
            environmentId: cible.Id,
            reason: $"Copie de « {source.Code} » : {correspondanceServeurs.Count} serveur(s), "
                  + $"{correspondanceComposants.Count} composant(s), {reportees} dépendance(s). "
                  + "Noms d'hôte non copiés.", ct: ct);

        logger.LogInformation(
            "Environnement {Code} créé par copie de {Source} : {Serveurs} serveur(s), {Composants} composant(s).",
            cible.Code, source.Code, correspondanceServeurs.Count, correspondanceComposants.Count);

        return new Resultat(cible.Id, correspondanceServeurs.Count, correspondanceComposants.Count, reportees, null);
    }

    /// <summary>
    /// Copie du profil de démarrage. Les motifs et les délais se transposent
    /// d'un environnement à l'autre ; le CHEMIN DE JOURNAL est conservé lui
    /// aussi, car il est presque toujours local et identique — mais il reste à
    /// vérifier, et la fiche naît en brouillon pour cela.
    /// </summary>
    private static ReadinessProfile? Copier(ReadinessProfile? p) => p is null ? null : new ReadinessProfile
    {
        LogPath = p.LogPath,
        ReadyPatterns = [.. p.ReadyPatterns],
        ErrorPatterns = [.. p.ErrorPatterns],
        IgnorePatterns = [.. p.IgnorePatterns],
        ActiveRolePatterns = [.. p.ActiveRolePatterns],
        SyncPatterns = [.. p.SyncPatterns],
        ServiceRunningTimeoutSeconds = p.ServiceRunningTimeoutSeconds,
        LogReadyTimeoutSeconds = p.LogReadyTimeoutSeconds,
        StopTimeoutSeconds = p.StopTimeoutSeconds,
        PollIntervalSeconds = p.PollIntervalSeconds,
        ProgressEverySeconds = p.ProgressEverySeconds,
        PostReadySettleSeconds = p.PostReadySettleSeconds,
        SyncDelayThresholdMinutes = p.SyncDelayThresholdMinutes
    };

    private static SharedFolderProfile? Copier(SharedFolderProfile? p) => p is null ? null : new SharedFolderProfile
    {
        RootPath = p.RootPath,
        Category = p.Category,
        PendingSubfolder = p.PendingSubfolder,
        ConsumedSubfolder = p.ConsumedSubfolder,
        BlockedSubfolder = p.BlockedSubfolder,
        ErrorSubfolder = p.ErrorSubfolder,
        EdiFileNamingPattern = p.EdiFileNamingPattern,
        MaxPendingAgeHours = p.MaxPendingAgeHours,
        MaxHoursSinceLastIntegration = p.MaxHoursSinceLastIntegration,
        MaxWriteLatencyMs = p.MaxWriteLatencyMs,
        MaxGrowthBytesPerHour = p.MaxGrowthBytesPerHour
    };
}
