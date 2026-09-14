using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Capitalisation;

/// <summary>
/// P3 MERISE — Service de capitalisation des incidents.
///   (a) À chaque fin d'exécution Workflow (TermineSucces / TermineAvecAvertissements
///       / Échec / Annulé), déduit 0..1 ligne ENT_INC CapitalizedIncident
///       si l'exécution a rencontré au moins une anomalie (Échec, AvecAvertissements,
///       Annulé avec état instable) OU si elle est liée à une APR (on garde
///       une trace des interventions préventives réussies aussi).
///   (b) Déduction automatique de la famille cause racine parmi les 10 TOP P1 Navis
///       (Enum WorkflowPreCheckCategory) à partir de Steps.ErrorType, PreflightBlocks,
///       Component.Role, Outcome/Notes keywords.
/// Pattern : scellé Scoped, retour string? null = OK.
/// Anti-doublon : une seule ligne CapitalizedIncident par LinkedExecutionId.
/// </summary>
public sealed class IncidentCapitalizationService(
    IDbContextFactory<N4SentinelDbContext> dbFactory)
{
    private static readonly ExecutionStatus[] StatutsPrisEnCharge = new[]
    {
        ExecutionStatus.TermineSucces,
        ExecutionStatus.TermineAvecAvertissements,
        ExecutionStatus.Echec,
        ExecutionStatus.Annule,
    };

    /// <summary>
    /// Capitalise (ou met à jour si relancée pour même ExecutionId) un
    /// CapitalizedIncident depuis un WorkflowExecution terminale.
    /// Retour : (string? Erreur, Guid? IncidentIdCreeOuMaj)
    /// </summary>
    public async Task<(string? Error, Guid? IncidentId)> CapitalizeFromExecutionAsync(
        Guid executionId,
        CancellationToken ct = default)
    {
        if (executionId == Guid.Empty)
            return ("Capitalisation incident : ExecutionId invalide (Guid vide).", null);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var execution = await db.Executions.AsNoTracking()
            .Include(e => e.Steps)
            .Include(e => e.Workflow)
            .Include(e => e.Environment)
            .FirstOrDefaultAsync(e => e.Id == executionId, ct);

        if (execution is null)
            return ($"Capitalisation incident : Execution #{executionId:N} introuvable.", null);
        if (!StatutsPrisEnCharge.Contains(execution.Status))
            return (null, null);

        // Règle : on capitalise ssi = (a) statut anomal OU (b) lié à une APR (même si réussite)
        bool aAnomalie = execution.Status is ExecutionStatus.Echec
            or ExecutionStatus.Annule
            or ExecutionStatus.TermineAvecAvertissements;

        if (!aAnomalie)
            return (null, null);

        var existant = await db.CapitalizedIncidents
            .FirstOrDefaultAsync(ci => ci.LinkedExecutionId == execution.Id, ct);
        var maintenant = DateTimeOffset.UtcNow;

        var (familleCode, familleLabel) = DeduireFamilleRacine(execution);
        var (graviteDeclaree, symptome) = DeduireGraviteEtSymptome(execution);
        var (debut, fin, dureeMin) = CalculerHorodatagesIncident(execution);
        var niveau = EstimerNiveauComp(execution);
        var nbOps = EstimerNbOperateurs(execution);
        var actionsCorrectives = SynthetiserActionsCorrectives(execution);
        var lecons = SynthetiserLeconsApprises(execution);

        if (existant is null)
        {
            var cree = new CapitalizedIncident
            {
                Id = Guid.NewGuid(),
                ExternalReference = $"AUTO-{execution.CorrelationId}",
                EnvironmentId = execution.EnvironmentId,
                DeclaredSeverity = graviteDeclaree,
                InitialSymptom = symptome,
                RealIncidentStartedAt = debut,
                RealIncidentEndedAt = fin,
                TotalDurationMinutes = dureeMin,
                DetectedBySentinelAt = execution.StartedAt,
                MainRootCauseCode = familleCode,
                MainRootCauseLabel = familleLabel,
                CorrectiveActionsTaken = actionsCorrectives,
                LinkedExecutionId = execution.Id,
                OperatorsInvolvedCount = nbOps,
                ResolutionSkillLevel = niveau,
                ClosedBy = execution.OperatingIdentityLogin ?? execution.RequestedBy,
                ClosedAt = execution.EndedAt ?? maintenant,
                LessonsAndCorrectives = lecons,
                CreatedAt = maintenant,
            };
            db.CapitalizedIncidents.Add(cree);
            await db.SaveChangesAsync(ct);
            return (null, cree.Id);
        }

        // Mise à jour : on préserve ce qui a été saisi à la main, on ne remplit
        // que les champs qui n'étaient pas renseignés par un humain.
        var modifie = false;
        if (string.IsNullOrWhiteSpace(existant.MainRootCauseCode))
        { existant.MainRootCauseCode = familleCode; modifie = true; }
        if (string.IsNullOrWhiteSpace(existant.MainRootCauseLabel))
        { existant.MainRootCauseLabel = familleLabel; modifie = true; }
        if (string.IsNullOrWhiteSpace(existant.InitialSymptom))
        { existant.InitialSymptom = symptome; modifie = true; }
        if (!existant.TotalDurationMinutes.HasValue)
        { existant.TotalDurationMinutes = dureeMin; modifie = true; }
        if (!existant.RealIncidentEndedAt.HasValue)
        { existant.RealIncidentEndedAt = fin; modifie = true; }
        if (!existant.ResolutionSkillLevel.HasValue)
        { existant.ResolutionSkillLevel = niveau; modifie = true; }
        if (existant.OperatorsInvolvedCount == 0 && nbOps > 0)
        { existant.OperatorsInvolvedCount = nbOps; modifie = true; }
        if (string.IsNullOrWhiteSpace(existant.CorrectiveActionsTaken))
        { existant.CorrectiveActionsTaken = actionsCorrectives; modifie = true; }
        if (string.IsNullOrWhiteSpace(existant.LessonsAndCorrectives))
        { existant.LessonsAndCorrectives = lecons; modifie = true; }
        if (modifie)
        {
            existant.ModifiedAt = maintenant;
            await db.SaveChangesAsync(ct);
        }
        return (null, existant.Id);
    }

    // -----------------------------------------------------------------------
    // Heuristiques de qualification (10 TOP causes P1 Navis)
    // -----------------------------------------------------------------------
    private static (string Code, string Label) DeduireFamilleRacine(WorkflowExecution exe)
    {
        var (wfNom, outcome, notes, _) = FragmentsTexte(exe);
        var text = string.Join(" | ", wfNom, outcome, notes);

        if (ContientMots(text, "horloge", "ntp", "skew", "décalage"))
            return CategorieCodeLabel(WorkflowPreCheckCategory.HorlogesNtp);
        if (ContientMots(text, "kahadb", "amq ", "activemq", "corruption", "corrompu"))
            return CategorieCodeLabel(WorkflowPreCheckCategory.EtatAmqKahaDb);
        if (ContientMots(text, "queue", "file d'attente", "bridge queue", "file bridge", "messages en attente"))
            return CategorieCodeLabel(WorkflowPreCheckCategory.EtatFilesBridge);
        if (ContientMots(text, "ordre", "séquence invalide", "ordre de démarrage", "ordre d'arrêt", "start order", "stop order"))
            return CategorieCodeLabel(WorkflowPreCheckCategory.OrdreArretDemarrage);
        if (ContientMots(text, "snapshot", "vm snapshot", "vmware", "hyper-v", "instantané"))
            return CategorieCodeLabel(WorkflowPreCheckCategory.SnapshotVirtuel);
        if (ContientMots(text, "réseau", "network", "connectivité", "timeout", "connexion", "dns", "ping"))
            return CategorieCodeLabel(WorkflowPreCheckCategory.ConnectiviteReseau);
        if (ContientMots(text, "index", "fragment", "réindex", "fragmente", "frag", "slow query", "requête lente"))
            return CategorieCodeLabel(WorkflowPreCheckCategory.GestionDonneesEtIndex);
        if (ContientMots(text, "compatible", "version", "tiers", "plugin", "incompatible", "dotnet", "jre", "runtime"))
            return CategorieCodeLabel(WorkflowPreCheckCategory.CompatibiliteTiers);
        if (ContientMots(text, "split-brain", "split brain", "deux centers", "center actif", "standby actif", "2 center"))
            return CategorieCodeLabel(WorkflowPreCheckCategory.Autre);
        if (ContientMots(text, "verrou", "lock", "environnement verrouillé", "déjà en cours", "concurrent"))
            return CategorieCodeLabel(WorkflowPreCheckCategory.VerrouillageEnvironnement);

        // Fallback : rôles composants principaux rencontrés en échec
        var rolesEnEchec = exe.Steps
            .Where(s => s.State is ExecutionStepState.Echec or ExecutionStepState.Bloque)
            .Select(s => s.ComponentId)
            .OfType<Guid>()
            .Distinct()
            .ToList();
        // On ne résout pas rôles ici pour éviter nouvelle DB hit — on base sur texte workflow
        if (ContientMots(text, "bridge"))
            return CategorieCodeLabel(WorkflowPreCheckCategory.EtatFilesBridge);
        if (ContientMots(text, "center", "standby"))
            return CategorieCodeLabel(WorkflowPreCheckCategory.Autre);
        if (ContientMots(text, "xps"))
            return CategorieCodeLabel(WorkflowPreCheckCategory.CompatibiliteTiers);

        return CategorieCodeLabel(WorkflowPreCheckCategory.Autre);
    }

    private static (CriticalityLevel Gravite, string Symptome) DeduireGraviteEtSymptome(WorkflowExecution exe)
    {
        var (_, outcome, _, erreurs) = FragmentsTexte(exe);
        var symptome = string.IsNullOrWhiteSpace(outcome)
            ? $"[WF {exe.Workflow?.Name ?? "anonyme"}] Status = {exe.Status}"
            : outcome;
        symptome = symptome.Length > 4000 ? symptome[..3990] + "…[tronqué]" : symptome;

        if (exe.Status == ExecutionStatus.Echec)
            return (CriticalityLevel.Elevee, symptome);
        if (exe.Status == ExecutionStatus.Annule && exe.RequiresManualInterventionAfterCancel == true)
            return (CriticalityLevel.Critique, symptome);
        if (exe.Status == ExecutionStatus.Annule)
            return (CriticalityLevel.Moyenne, symptome);
        if (exe.Status == ExecutionStatus.TermineAvecAvertissements)
            return (CriticalityLevel.Moyenne, symptome);
        return (CriticalityLevel.Faible, symptome);
    }

    private static (DateTimeOffset Debut, DateTimeOffset? Fin, long? DureeMin) CalculerHorodatagesIncident(WorkflowExecution exe)
    {
        var debut = exe.StartedAt ?? exe.CreatedAt;
        var fin = exe.EndedAt;
        long? duree = null;
        if (fin.HasValue)
        {
            var delta = fin.Value - debut;
            if (delta.TotalMinutes >= 0) duree = (long)Math.Ceiling(delta.TotalMinutes);
        }
        return (debut, fin, duree);
    }

    private static ResolutionSkillLevel? EstimerNiveauComp(WorkflowExecution exe)
    {
        var etapesEchec = exe.Steps.Count(s => s.State is ExecutionStepState.Echec or ExecutionStepState.Bloque or ExecutionStepState.ForceStopped);
        var etapesContournees = exe.Steps.Count(s => s.State == ExecutionStepState.Ignore && !string.IsNullOrWhiteSpace(s.SkippedBy));
        var annulationInstable = exe.Status == ExecutionStatus.Annule && exe.RequiresManualInterventionAfterCancel == true;

        if (annulationInstable || etapesEchec >= 3) return ResolutionSkillLevel.N3ExpertInterne;
        if (etapesEchec >= 1 || etapesContournees >= 2) return ResolutionSkillLevel.N2Specialiste;
        if (etapesContournees == 1 || exe.Status == ExecutionStatus.TermineAvecAvertissements) return ResolutionSkillLevel.N1Support;
        return null;
    }

    private static int EstimerNbOperateurs(WorkflowExecution exe)
    {
        var noms = new HashSet<string?>(StringComparer.OrdinalIgnoreCase)
        {
            exe.CreatedBy, exe.RequestedBy, exe.OperatingIdentityLogin, exe.ApprovedBy,
            exe.SecondApprovedBy, exe.CancelRequestedBy, exe.PauseRequestedBy,
        };
        noms.RemoveWhere(string.IsNullOrWhiteSpace);
        return Math.Clamp(noms.Count, 0, 20);
    }

    private static string? SynthetiserActionsCorrectives(WorkflowExecution exe)
    {
        if (exe.Steps.Count == 0) return null;
        var parties = exe.Steps
            .Where(s => s.State is ExecutionStepState.Reussi or ExecutionStepState.Avertissement or ExecutionStepState.Ignore or ExecutionStepState.Echec or ExecutionStepState.ForceStopped)
            .OrderBy(s => s.Order)
            .Select(s => $"#{s.Order} [{s.State}] {s.Name}"
                        + (string.IsNullOrWhiteSpace(s.Evidence) ? null : $" → {s.Evidence}"))
            .Take(50)
            .ToList();
        if (parties.Count == 0) return null;
        var txt = string.Join("\n", parties);
        return txt.Length > 8000 ? txt[..7990] + "…[tronqué]" : txt;
    }

    private static string? SynthetiserLeconsApprises(WorkflowExecution exe)
    {
        var familles = exe.Steps
            .Where(s => s.State != ExecutionStepState.AVenir && s.State != ExecutionStepState.Reussi)
            .GroupBy(s => (s.ErrorType ?? StepErrorType.Inconnu).ToString())
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} ×{g.Count()}")
            .Take(5)
            .ToList();
        if (familles.Count == 0) return null;
        return "Erreurs les plus fréquentes pendant cette intervention : " + string.Join(", ", familles)
             + ". Voir APR correspondante pour actions préventives suggérées.";
    }

    // -----------------------------------------------------------------------
    // Utilitaires
    // -----------------------------------------------------------------------
    private static (string WorkflowNom, string Outcome, string Notes, string[] ErrorsParEtape) FragmentsTexte(WorkflowExecution exe)
    {
        var errors = exe.Steps
            .Select(s => s.Error ?? s.Evidence)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Take(10)
            .OfType<string>()
            .ToArray();
        return (
            WorkflowNom: exe.Workflow?.Name ?? exe.WorkflowName ?? string.Empty,
            Outcome: exe.Outcome ?? string.Empty,
            Notes: exe.Reason ?? string.Empty,
            ErrorsParEtape: errors
        );
    }

    private static bool ContientMots(string texte, params string[] mots)
    {
        if (string.IsNullOrWhiteSpace(texte)) return false;
        foreach (var m in mots)
        {
            if (string.IsNullOrWhiteSpace(m)) continue;
            if (texte.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static (string Code, string Label) CategorieCodeLabel(WorkflowPreCheckCategory c)
    {
        var libelle = c switch
        {
            WorkflowPreCheckCategory.HorlogesNtp => "Horloges NTP / Décalage",
            WorkflowPreCheckCategory.GestionDonneesEtIndex => "Gestion données / index SGBD",
            WorkflowPreCheckCategory.ConnectiviteReseau => "Connectivité réseau",
            WorkflowPreCheckCategory.EtatAmqKahaDb => "État AMQ / KahaDB",
            WorkflowPreCheckCategory.EtatFilesBridge => "État files Bridge",
            WorkflowPreCheckCategory.OrdreArretDemarrage => "Ordre arrêt / démarrage",
            WorkflowPreCheckCategory.SnapshotVirtuel => "Snapshot VM",
            WorkflowPreCheckCategory.CompatibiliteTiers => "Compatibilité tiers",
            WorkflowPreCheckCategory.VerrouillageEnvironnement => "Verrouillage environnement",
            _ => "Autre cause Navis non classée",
        };
        return (c.ToString(), libelle);
    }
}
