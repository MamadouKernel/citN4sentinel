using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Orchestration;

/// <summary>
/// Règles de gestion MERISE "métier pur" P1 : applicables à la couche
/// Infrastructure et réutilisables depuis tous les UseCases / Services.
///
/// Conventions du projet (pas de FluentValidation) : chaque règle retourne
/// un message si violée, null / liste vide sinon. L'appelant décide comment
/// matérialiser le refus (audit, refus HTTP, échec pré-check bloquant…).
///
/// 6 RG ciblées ici :
///   - RG-0105 : 1 seul CenterNode au maximum + 1 seul StandbyCenterNode par environnement.
///   - RG-0106 : LogicalName unique par environnement, indépendamment de l'état RetraitDuService.
///   - RG-0204 : décalage d'horloge > 1s INTERDIT pour tout contrôle préalable bloquant
///               (seuil avertissement progressif à 0.8s).
///   - RG-0304 : en Production + DoubleApprobation = 3 personnes DISTINCTES
///               (Demandeur ≠ Approbateur 1 ≠ Approbateur 2).
///   - RG-0407 : ArretForce JAMAIS automatique ; seul un Admin N4 habilité peut l'exécuter.
///   - RG-0506 : Restart Bridge REFUSÉ si la queue ActiveMQ dépasse le seuil.
///   - RG-SPLIT-BRAIN : redémarrage conjoint Center + Standby → STANDBY démarré EN PREMIER.
/// </summary>
public sealed class MeriseBusinessPolicyService(IDbContextFactory<N4SentinelDbContext> dbFactory)
{
    // --- RG-0105 ------------------------------------------------------------
    // Un seul Center et un seul Standby par environnement.

    public async Task<List<string>> ValidateComponentRolesAsync(
        Guid environmentId, Guid? modifyingComponentId = null, CancellationToken ct = default)
    {
        var violations = new List<string>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var actifs = await db.Components
            .Where(c => c.EnvironmentId == environmentId)
            .Select(c => new { c.Id, c.LogicalName, c.Role })
            .ToListAsync(ct);

        var centers = actifs
            .Where(c => c.Role == ComponentRole.CenterNode
                     && (modifyingComponentId is null || c.Id != modifyingComponentId))
            .ToList();
        if (centers.Count > 1)
        {
            violations.Add(
                $"RG-0105 : {centers.Count} composants portent le rôle CenterNode dans le même environnement "
                + $"({string.Join(", ", centers.Select(c => c.LogicalName))}). "
                + "Un seul Center actif est autorisé par environnement N4.");
        }

        var standbys = actifs
            .Where(c => c.Role == ComponentRole.StandbyCenterNode
                     && (modifyingComponentId is null || c.Id != modifyingComponentId))
            .ToList();
        if (standbys.Count > 1)
        {
            violations.Add(
                $"RG-0105 : {standbys.Count} composants portent le rôle StandbyCenterNode dans le même environnement "
                + $"({string.Join(", ", standbys.Select(c => c.LogicalName))}). "
                + "Un seul Standby Center est autorisé par environnement N4.");
        }

        return violations;
    }

    // --- RG-0106 ------------------------------------------------------------
    // LogicalName unique par environnement.

    public async Task<string?> ValidateComponentLogicalNameAsync(
        Guid environmentId, Guid componentId, string logicalName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(logicalName))
            return "RG-0106 : le nom logique du composant est requis.";

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // EF Core ne sait pas traduire string.Equals(..., StringComparison) en SQL.
        // On compare en Trim().ToLower() — traduisible (LTRIM/RTRIM + LOWER) et
        // équivalent pour une unicité de nom insensible à la casse et aux espaces.
        var cible = logicalName.Trim().ToLower();
        var doublon = await db.Components.IgnoreQueryFilters()
            .AnyAsync(c =>
                c.EnvironmentId == environmentId
                && c.Id != componentId
                && c.LogicalName.Trim().ToLower() == cible,
                ct);

        if (doublon)
            return $"RG-0106 : un composant nommé « {logicalName.Trim()} » existe déjà dans cet environnement. "
                 + "Chaque nom logique doit être unique, y compris vis-à-vis de composants retirés du service.";

        return null;
    }

    // --- RG-0204 ------------------------------------------------------------
    // Horloge > 1s = BLOQUANT ; > 0.8s = avertissement.

    public const double ClockSkewWarningThresholdSeconds = 0.8;
    public const double ClockSkewBlockingThresholdSeconds = 1.0;

    public (PreflightCheck? Check, double? ObservedSeconds) EvaluateClockSkewPreCheck(
        double? skewSecondsFromSupervision)
    {
        if (skewSecondsFromSupervision.HasValue is false)
        {
            return (
                PreflightCheck.Avertissement(
                    "RG-0204 · Décalage d'horloge NTP",
                    "Aucun relevé de décalage d'horloge n'est disponible depuis la supervision. "
                    + "Un décalage supérieur à 1s provoque des split-brain AMQ et des erreurs de quorum Hazelcast.",
                    "Vérifiez que l'agent de supervision est bien joignable et que le service Win32Time tourne sur l'hôte."),
                null);
        }

        if (skewSecondsFromSupervision.Value <= ClockSkewWarningThresholdSeconds)
        {
            return (
                PreflightCheck.Reussi(
                    "RG-0204 · Décalage d'horloge NTP",
                    $"Décalage mesuré : {skewSecondsFromSupervision.Value:N2} s (seuil bloquant 1,0 s, seuil d'alerte 0,8 s)."),
                skewSecondsFromSupervision.Value);
        }

        if (skewSecondsFromSupervision.Value <= ClockSkewBlockingThresholdSeconds)
        {
            return (
                PreflightCheck.Avertissement(
                    "RG-0204 · Décalage d'horloge NTP",
                    $"Décalage mesuré : {skewSecondsFromSupervision.Value:N2} s. Le seuil de 1 s est proche. "
                    + "Sans correction avant l'opération, un incident split-brain AMQ est probable.",
                    "Lancez w32tm /resync /nowait sur l'hôte concerné, puis vérifiez à nouveau."),
                skewSecondsFromSupervision.Value);
        }

        return (
            PreflightCheck.Echec(
                "RG-0204 · Décalage d'horloge NTP",
                $"Décalage mesuré : {skewSecondsFromSupervision.Value:N2} s — SEUIL BLOQUANT DÉPASSÉ (> 1,0 s). "
                + "Une opération dans cet état provoque typiquement : split-brain KahaDB (2 fichiers db.*), "
                + "perte de quorum Hazelcast, horodatages incohérents dans les logs.",
                "Corrigez la source NTP : w32tm /config /syncfromflags:domhier /update puis w32tm /resync. "
                + "Ne relancez qu'après être revenu sous 0,8 s.",
                bloquant: true),
            skewSecondsFromSupervision.Value);
    }

    // --- RG-0304 ------------------------------------------------------------
    // DoubleApprobation + Production = 3 personnes distinctes.

    public string? ValidateDoubleApprovalDistinctActors(
        string requestedBy,
        string? approvedBy,
        string? secondApprovedBy,
        bool isProduction,
        bool requiresDoubleApproval)
    {
        if (!requiresDoubleApproval) return null;
        if (!isProduction) return null;

        var demandeur = (requestedBy ?? string.Empty).Trim();
        var app1 = (approvedBy ?? string.Empty).Trim();
        var app2 = (secondApprovedBy ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(demandeur) || string.IsNullOrEmpty(app1) || string.IsNullOrEmpty(app2))
            return "RG-0304 : le double circuit d'approbation en Production exige 3 identités renseignées "
                 + "(Demandeur, Approbateur 1, Approbateur 2).";

        if (string.Equals(demandeur, app1, StringComparison.OrdinalIgnoreCase))
            return "RG-0304 : en Production avec double approbation, le Demandeur ne peut pas être aussi le "
                 + "premier Approbateur. 3 personnes distinctes sont nécessaires (Opérateur ≠ App1 ≠ App2).";

        if (string.Equals(demandeur, app2, StringComparison.OrdinalIgnoreCase))
            return "RG-0304 : en Production avec double approbation, le Demandeur ne peut pas être aussi le "
                 + "second Approbateur. 3 personnes distinctes sont nécessaires (Opérateur ≠ App1 ≠ App2).";

        if (string.Equals(app1, app2, StringComparison.OrdinalIgnoreCase))
            return "RG-0304 : en Production avec double approbation, l'Approbateur 1 ne peut pas être identique "
                 + "à l'Approbateur 2. 3 personnes distinctes sont nécessaires (Opérateur ≠ App1 ≠ App2).";

        return null;
    }

    // --- RG-0407 ------------------------------------------------------------
    // StopForcé jamais auto, jamais non-admin.

    public string? ValidateForcedStopAuthorization(
        StepAction action, bool actorIsAdminN4, bool triggeredAutomatically = false)
    {
        if (action != StepAction.ArretForce) return null;

        if (triggeredAutomatically)
            return "RG-0407 : l'action Arrêt Forcé (StepAction.ArretForce) ne peut pas être déclenchée "
                 + "automatiquement. Elle requiert une décision humaine explicite : Admin N4 uniquement.";

        if (!actorIsAdminN4)
            return "RG-0407 : l'action Arrêt Forcé est réservée aux administrateurs N4 habilités. "
                 + "Un opérateur standard ne doit pas être en mesure de sortir de force une étape "
                 + "bloquée en StopPending sans revue N3.";

        return null;
    }

    // --- RG-0506 ------------------------------------------------------------
    // Restart Bridge interdit si Queue trop haute.

    public const int DefaultBridgeQueueBlockingThreshold = 1000;
    public const int DefaultBridgeQueueWarningThreshold = 500;

    public (string? Error, string? Warning) EvaluateBridgeQueueHealth(
        int? currentQueueCount,
        bool restartingBridge,
        int? blockingThreshold = null,
        int? warningThreshold = null)
    {
        if (!restartingBridge) return (null, null);
        if (!currentQueueCount.HasValue) return (null, null);

        var blocage = blockingThreshold ?? DefaultBridgeQueueBlockingThreshold;
        var alerte = warningThreshold ?? DefaultBridgeQueueWarningThreshold;

        if (currentQueueCount.Value >= blocage)
        {
            return (
                "RG-0506 : le Bridge a actuellement " + currentQueueCount.Value
                     + $" messages en queue (seuil blocage = {blocage}). Redémarrer le Bridge dans cet état "
                     + "risque une perte de messages en transit et un écroulement KahaDB. "
                     + "Recommandations : (1) redémarrer d'abord les Cluster Nodes pour les faire vider la queue ; "
                     + "(2) attendre que la consommation ramène le compteur sous " + alerte
                     + " ; (3) ne jamais redémarrer Bridge et Cluster en même temps.",
                null);
        }

        if (currentQueueCount.Value >= alerte)
        {
            return (
                null,
                "RG-0506 : le Bridge a " + currentQueueCount.Value
                     + $" messages en queue (seuil d'alerte = {alerte}). "
                     + "Le redémarrage est autorisé, mais surveillez la consommation pendant les 2 premières minutes.");
        }

        return (null, null);
    }

    // --- RG-SPLIT-BRAIN -----------------------------------------------------
    // Séquence Center + Standby : STANDBY démarré EN PREMIER, jamais l'inverse.

    /// <summary>
    /// Interne — prend une séquence de SequenceStepInfo (internal, issu de SequenceValidator)
    /// et résout les rôles depuis la base Components pour déléguer à la surcharge typée.
    /// Cette surcharge existe pour que SequenceValidator n'ait pas besoin d'exposer
    /// son record internal hors assembly — la logique métier (ordre Standby-first)
    /// vit UNIQUEMENT dans la surcharge publique ci-dessous, pour éviter les doubles
    /// implémentations divergentes.
    /// </summary>
    internal async Task<List<SequenceViolation>> ValidateSplitBrainStandbyFirstOrder(
        Guid environmentId, IReadOnlyList<SequenceStepInfo> steps, CancellationToken ct = default)
    {
        var demarres = steps
            .Where(s => s.Action is StepAction.Demarrer or StepAction.Redemarrer
                     && s.ComponentId is not null)
            .OrderBy(s => s.Order)
            .ToList();
        if (demarres.Count == 0) return new List<SequenceViolation>();

        var ids = demarres.Select(s => s.ComponentId!.Value).Distinct().ToList();
        if (ids.Count == 0) return new List<SequenceViolation>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rolesParId = await db.Components
            .AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.LogicalName, c.Role })
            .ToDictionaryAsync(c => c.Id, ct);

        var tuples = new List<(int Order, string LogicalName, ComponentRole Role)>(demarres.Count);
        foreach (var s in demarres)
        {
            if (rolesParId.TryGetValue(s.ComponentId!.Value, out var meta))
                tuples.Add((s.Order, meta.LogicalName, meta.Role));
            else
                tuples.Add((s.Order, "?", ComponentRole.Autre));
        }

        return ValidateSplitBrainStandbyFirstOrder(tuples);
    }

    /// <summary>Surcharge avec rôles résolus depuis N4Component.Role.</summary>
    public List<SequenceViolation> ValidateSplitBrainStandbyFirstOrder(
        IReadOnlyList<(int Order, string LogicalName, ComponentRole Role)> orderedStartActions)
    {
        var violations = new List<SequenceViolation>();

        var dansSequence = orderedStartActions
            .OrderBy(s => s.Order)
            .ToList();

        int rangCenter = -1, rangStandby = -1;
        string? nomCenter = null, nomStandby = null;

        foreach (var s in dansSequence)
        {
            if (s.Role == ComponentRole.CenterNode && rangCenter < 0)
            {
                rangCenter = s.Order;
                nomCenter = s.LogicalName;
            }
            if (s.Role == ComponentRole.StandbyCenterNode && rangStandby < 0)
            {
                rangStandby = s.Order;
                nomStandby = s.LogicalName;
            }
        }

        if (rangCenter >= 0 && rangStandby >= 0 && rangCenter < rangStandby)
        {
            violations.Add(new SequenceViolation
            {
                Blocking = true,
                Order = rangCenter,
                Message =
                    "RG-SPLIT-BRAIN : le Center (« " + nomCenter + " », ordre " + rangCenter
                    + ") est démarré avant le Standby (« " + nomStandby + " », ordre " + rangStandby
                    + "). C'est l'ordre à proscrire : en cas de split-brain AMQ, le Center doit rester le "
                    + "nœud qui possède la vérité, et le Standby se synchronise sur lui. Démarrer Center d'abord "
                    + "risque de transformer un Standby légèrement avancé en nœud actif avec 2 KahaDB db.* "
                    + "actifs — catastrophe irréversible.",
                Remedy =
                    "Intervertissez les 2 étapes : démarrer d'abord Standby, puis seulement Center. "
                    + "En cas de doute, référez-vous au Guide Navis 3.8.25 §10.6 « Split-Brain Recovery : "
                    + "Standby must be restarted BEFORE Center »."
            });
        }

        return violations;
    }
}
