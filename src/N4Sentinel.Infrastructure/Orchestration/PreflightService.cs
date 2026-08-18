using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Orchestration;

/// <summary>
/// Contrôles préalables à toute opération mutative (FR-012).
///
/// LE PRINCIPE : découvrir qu'un serveur est injoignable à la septième étape
/// d'un arrêt complet est le pire moment possible. L'écosystème est alors à
/// moitié éteint, et le composant qu'on ne peut pas joindre est précisément
/// celui qu'il faudrait arrêter. Tout ce qui peut être vérifié avant que la
/// première commande parte doit l'être avant.
///
/// UN CONTRÔLE BLOQUANT EN ÉCHEC INTERDIT L'EXÉCUTION. Il n'existe aucun
/// mécanisme pour le contourner : c'est la seule façon qu'un contrôle bloquant
/// reste bloquant le jour où quelqu'un est pressé.
///
/// Les contrôles non bloquants, eux, informent sans empêcher. La nuance
/// compte : tout déclarer bloquant finirait par entraîner à tout contourner.
/// </summary>
public sealed class PreflightService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    ConnectorTargetFactory targetFactory,
    IN4Connector connector,
    EnvironmentLockService locks,
    SequenceValidator sequenceValidator,
    Supervision.SupervisionService supervision,
    CenterContinuityService continuity,
    Security.EnvironmentAccessService acces,
    ILogger<PreflightService> logger)
{
    /// <summary>
    /// Contrôles préalables, avec vérification de l'habilitation du demandeur
    /// sur l'environnement visé (audit SEC-A1).
    ///
    /// LE REFUS TOMBE ICI, ET PAS SEULEMENT DANS LES ÉCRANS. Un contrôle posé
    /// uniquement sur les pages se contourne en appelant directement par
    /// identifiant. Le pré-check est le passage obligé de toute exécution : il
    /// est le bon endroit pour refuser.
    /// </summary>
    public async Task<PreflightReport> RunAsync(
        Guid executionId, System.Security.Claims.ClaimsPrincipal? demandeur,
        CancellationToken ct = default)
    {
        var rapport = await RunInterneAsync(executionId, ct);

        if (demandeur is null || rapport.Error is not null) return rapport;

        await using var lecture = await dbFactory.CreateDbContextAsync(ct);
        var cible = await lecture.Executions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == executionId, ct);

        if (cible is null) return rapport;

        // SEC-001, item 4 du plan : le second facteur est EXIGE pour agir en
        // Production, quel que soit le reglage global.
        //
        // Le controle ne peut pas vivre dans une politique d'autorisation :
        // une politique ne connait pas l'environnement vise, elle ne voit que
        // des roles. Le pre-check, lui, sait sur quoi porte l'operation - c'est
        // donc ici, et nulle part ailleurs, que la distinction est exprimable.
        //
        // La simulation en est dispensee : elle n'emet aucune commande.
        if (!cible.IsSimulation)
        {
            var environnement = await lecture.Environments.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == cible.EnvironmentId, ct);

            if (environnement?.IsProduction == true && !ASecondFacteur(demandeur))
            {
                logger.LogWarning(
                    "Exécution {Correlation} refusée : second facteur absent pour une action en Production.",
                    cible.CorrelationId);

                rapport.Checks.Insert(0, PreflightCheck.Echec(
                    "Second facteur en Production",
                    "Cette session n'a pas été ouverte avec un second facteur, et l'opération vise la "
                    + $"Production (« {cible.EnvironmentCode} »).",
                    "Activez l'authentificateur depuis votre profil, puis reconnectez-vous. "
                    + "Un mot de passe seul ne suffit pas à autoriser l'arrêt d'un terminal.",
                    bloquant: true));

                return rapport;
            }
        }

        // Une simulation n'emet aucune commande : la consultation suffit.
        var decision = cible.IsSimulation
            ? await acces.CanViewAsync(demandeur, cible.EnvironmentId, ct)
            : await acces.CanActAsync(demandeur, cible.EnvironmentId, ct);

        if (decision.Allowed)
        {
            rapport.Checks.Insert(0, PreflightCheck.Reussi(
                "Habilitation sur l'environnement",
                $"Le demandeur est habilité à agir sur « {cible.EnvironmentCode} »."));

            return rapport;
        }

        logger.LogWarning(
            "Exécution {Correlation} refusée : {Motif}", cible.CorrelationId, decision.Reason);

        rapport.Checks.Insert(0, PreflightCheck.Echec(
            "Habilitation sur l'environnement",
            decision.Reason ?? "Habilitation refusée.",
            "Un rôle dit ce que vous savez faire ; l'habilitation dit sur quel environnement. "
            + "Les deux sont nécessaires.",
            bloquant: true));

        return rapport;
    }

    /// <summary>Surcharge sans porteur : conservée pour les appels internes et les tests.</summary>
    public Task<PreflightReport> RunAsync(Guid executionId, CancellationToken ct = default) =>
        RunInterneAsync(executionId, ct);

    /// <summary>
    /// Vrai si la session courante a été ouverte avec un second facteur.
    ///
    /// La revendication <c>amr = mfa</c> est posée par Identity au moment de la
    /// connexion à deux facteurs. Elle prouve que CETTE session a franchi le
    /// second facteur — ce qui est plus fort que de constater que le compte
    /// l'a activé quelque part.
    /// </summary>
    public static bool ASecondFacteur(System.Security.Claims.ClaimsPrincipal utilisateur) =>
        utilisateur.FindAll("amr").Any(c =>
            string.Equals(c.Value, "mfa", StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.Value, "otp", StringComparison.OrdinalIgnoreCase));

    private async Task<PreflightReport> RunInterneAsync(Guid executionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var execution = await db.Executions
            .AsNoTracking()
            .Include(x => x.Steps)
            .Include(x => x.Environment)
            .FirstOrDefaultAsync(x => x.Id == executionId, ct);

        if (execution is null)
            return PreflightReport.Impossible("Exécution introuvable.");

        var controles = new List<PreflightCheck>();

        await ControlerVerrouAsync(execution, controles, ct);

        var composants = await ChargerComposantsAsync(db, execution, ct);

        ControlerFenetreIntervention(execution, controles);
        ControlerTicketProduction(execution, controles);
        ControlerPilotabilite(execution, composants, controles);
        ControlerMarqueurs(execution, composants, controles);
        await ControlerPrerequisAsync(db, execution, composants, controles, ct);
        await ControlerSequenceAsync(execution, controles, ct);
        await ControlerServeursAsync(composants, controles, execution.IsSimulation, ct);
        await ControlerEtatInitialDemarrageCompletAsync(execution, composants, controles, ct);
        await ControlerContinuiteCenterAsync(execution, controles, ct);
        await ControlerContinuiteBridgeAsync(execution, composants, controles, ct);
        ControlerImpact(execution, composants, controles);

        var rapport = new PreflightReport
        {
            ExecutionId = executionId,
            RunAt = DateTimeOffset.UtcNow,
            Checks = controles
        };

        // Le rapport est conserve avec l'execution : trois mois plus tard, on
        // doit pouvoir dire ce qui avait ete verifie avant de lancer, pas
        // seulement ce qui s'est passe pendant.
        await using var ecriture = await dbFactory.CreateDbContextAsync(ct);
        var suivi = await ecriture.Executions.FirstOrDefaultAsync(x => x.Id == executionId, ct);
        if (suivi is not null)
        {
            suivi.PreflightJson = JsonSerializer.Serialize(controles);
            suivi.PreflightAt = rapport.RunAt;
            suivi.PreflightBlocked = rapport.HasBlockingFailure;
            await ecriture.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Pré-check de {Correlation} : {Total} contrôle(s), {Echecs} échec(s) bloquant(s), {Reserves} réserve(s).",
            execution.CorrelationId, controles.Count, rapport.BlockingFailures, rapport.Warnings);

        return rapport;
    }

    // -----------------------------------------------------------------------
    // Contrôles
    // -----------------------------------------------------------------------
    private async Task ControlerVerrouAsync(
        WorkflowExecution execution, List<PreflightCheck> controles, CancellationToken ct)
    {
        if (execution.IsSimulation)
        {
            controles.Add(PreflightCheck.NonApplicable(
                "Verrou d'environnement",
                "Une simulation n'émet aucune commande : elle ne prend pas le verrou et n'empêche personne de travailler."));
            return;
        }

        var verrou = await locks.GetAsync(execution.EnvironmentId, ct);

        if (verrou is null || verrou.IsExpired || verrou.ExecutionId == execution.Id)
        {
            controles.Add(PreflightCheck.Reussi(
                "Verrou d'environnement",
                "Aucune autre opération mutative n'est en cours sur cet environnement."));
            return;
        }

        controles.Add(PreflightCheck.Echec(
            "Verrou d'environnement",
            $"Une opération est déjà en cours : « {verrou.Reason} », lancée par {verrou.HeldBy} "
            + $"à {verrou.AcquiredAt.ToLocalTime():HH:mm}.",
            "Attendez qu'elle se termine, ou demandez à son auteur de l'annuler.",
            bloquant: true));
    }

    private static void ControlerFenetreIntervention(WorkflowExecution execution, List<PreflightCheck> controles)
    {
        if (execution.StartWindow is null && execution.EndWindow is null)
        {
            controles.Add(PreflightCheck.NonApplicable(
                "Fenêtre d'intervention",
                "Aucune fenêtre d'intervention n'a été spécifiée pour cette opération."));
            return;
        }

        var maintenant = DateTimeOffset.UtcNow;

        if (execution.StartWindow is not null && maintenant < execution.StartWindow)
        {
            controles.Add(PreflightCheck.Echec(
                "Fenêtre d'intervention",
                $"L'opération ne peut pas commencer avant {execution.StartWindow.Value.ToLocalTime():g}.",
                "Attendez le début de la fenêtre d'intervention.",
                bloquant: true));
            return;
        }

        if (execution.EndWindow is not null && maintenant > execution.EndWindow)
        {
            controles.Add(PreflightCheck.Echec(
                "Fenêtre d'intervention",
                $"La fenêtre d'intervention autorisée s'est terminée à {execution.EndWindow.Value.ToLocalTime():g}.",
                "L'opération est hors délai. Demandez une nouvelle fenêtre.",
                bloquant: true));
            return;
        }

        controles.Add(PreflightCheck.Reussi(
            "Fenêtre d'intervention",
            "L'heure actuelle respecte la fenêtre d'intervention autorisée."));
    }

    private static void ControlerTicketProduction(WorkflowExecution execution, List<PreflightCheck> controles)
    {
        if (execution.Environment is null || !execution.Environment.IsProduction)
        {
            controles.Add(PreflightCheck.NonApplicable(
                "Ticket ITSM (Production)",
                "Ce contrôle s'applique uniquement aux environnements de Production."));
            return;
        }

        if (string.IsNullOrWhiteSpace(execution.TicketReference))
        {
            controles.Add(PreflightCheck.Echec(
                "Ticket ITSM (Production)",
                "Une référence de ticket ITSM est obligatoire pour toute opération en Production (FR-012).",
                "Renseignez la référence du ticket d'incident ou de changement approuvé.",
                bloquant: true));
            return;
        }

        controles.Add(PreflightCheck.Reussi(
            "Ticket ITSM (Production)",
            $"Référence de ticket renseignée : {execution.TicketReference}."));
    }

    private static void ControlerPilotabilite(
        WorkflowExecution execution, Dictionary<Guid, N4Component> composants, List<PreflightCheck> controles)
    {
        var mutatives = execution.Steps
            .Where(s => s.Action is StepAction.Demarrer or StepAction.Arreter or StepAction.Redemarrer)
            .ToList();

        if (mutatives.Count == 0)
        {
            controles.Add(PreflightCheck.NonApplicable(
                "Composants pilotables",
                "Cette opération ne comporte aucune action mutative."));
            return;
        }

        var refus = new List<string>();

        foreach (var etape in mutatives)
        {
            if (etape.ComponentId is null)
            {
                refus.Add($"« {etape.Name} » agit sur un composant mais n'en désigne aucun");
                continue;
            }

            if (!composants.TryGetValue(etape.ComponentId.Value, out var composant))
            {
                refus.Add($"« {etape.Name} » vise un composant qui n'existe plus au référentiel");
                continue;
            }

            if (!composant.CanBeControlled)
                refus.Add($"« {composant.LogicalName} » n'est pas pilotable "
                        + $"(mode {composant.ControlMode}, état {composant.Status})");

            if (string.IsNullOrWhiteSpace(composant.WindowsServiceName))
                refus.Add($"« {composant.LogicalName} » n'a aucun nom de service Windows renseigné");
        }

        if (refus.Count == 0)
        {
            controles.Add(PreflightCheck.Reussi(
                "Composants pilotables",
                $"Les {mutatives.Count} action(s) mutative(s) portent sur des composants déclarés pilotables et validés."));
            return;
        }

        controles.Add(PreflightCheck.Echec(
            "Composants pilotables",
            string.Join(" ; ", refus) + ".",
            "Le référentiel doit être complet et le composant déclaré pilotable ET validé. "
            + "Aucune commande n'est envoyée à un composant en brouillon, quelle que soit l'urgence.",
            bloquant: true));
    }

    private static void ControlerMarqueurs(
        WorkflowExecution execution, Dictionary<Guid, N4Component> composants, List<PreflightCheck> controles)
    {
        var demarrages = execution.Steps
            .Where(s => s.Action is StepAction.Demarrer or StepAction.Redemarrer && s.ComponentId is not null)
            .Select(s => s.ComponentId!.Value)
            .Distinct()
            .ToList();

        if (demarrages.Count == 0) return;

        var sansPreuve = demarrages
            .Where(id => composants.TryGetValue(id, out var c) && !c.Readiness.IsProvable)
            .Select(id => composants[id].LogicalName)
            .ToList();

        if (sansPreuve.Count == 0)
        {
            controles.Add(PreflightCheck.Reussi(
                "Preuve de démarrage",
                $"Les {demarrages.Count} composant(s) à démarrer portent un marqueur de journal exploitable."));
            return;
        }

        // NON BLOQUANT, et c'est un choix. Interdire une operation faute de
        // marqueur reviendrait a rendre l'outil inutilisable sur un site qui
        // n'a pas encore fait son releve - alors qu'il peut deja rendre
        // service. Mais la reserve doit etre dite, et elle se retrouvera dans
        // le rapport d'execution.
        controles.Add(PreflightCheck.Avertissement(
            "Preuve de démarrage",
            $"{sansPreuve.Count} composant(s) sans marqueur de journal : {string.Join(", ", sansPreuve)}. "
            + "Leur démarrage applicatif ne sera PAS prouvé — l'opération se terminera avec réserve, "
            + "et l'état de ces composants restera « à confirmer ».",
            "L'écran Marqueurs relève le marqueur sur un démarrage réel et lève définitivement cette réserve."));
    }

    /// <summary>
    /// Vérifie que ce dont dépendent les composants à démarrer est soit
    /// démarré par la séquence elle-même, soit déjà opérationnel.
    /// </summary>
    private async Task ControlerPrerequisAsync(
        N4SentinelDbContext db, WorkflowExecution execution,
        Dictionary<Guid, N4Component> composants, List<PreflightCheck> controles, CancellationToken ct)
    {
        var demarres = execution.Steps
            .Where(s => s.Action is StepAction.Demarrer or StepAction.Redemarrer && s.ComponentId is not null)
            .Select(s => s.ComponentId!.Value)
            .ToHashSet();

        if (demarres.Count == 0) return;

        var dependances = await db.ComponentDependencies
            .AsNoTracking()
            .Include(d => d.DependsOnComponent)
            .Where(d => demarres.Contains(d.ComponentId) && d.Kind != DependencyKind.Informative)
            .ToListAsync(ct);

        var externes = dependances
            .Where(d => !demarres.Contains(d.DependsOnComponentId))
            .ToList();

        if (externes.Count == 0)
        {
            controles.Add(PreflightCheck.Reussi(
                "Prérequis de dépendance",
                "Tous les prérequis des composants à démarrer sont eux-mêmes démarrés par cette séquence."));
            return;
        }

        var noms = externes
            .Select(d => d.DependsOnComponent?.LogicalName ?? "composant inconnu")
            .Distinct()
            .ToList();

        controles.Add(PreflightCheck.Avertissement(
            "Prérequis de dépendance",
            $"{noms.Count} prérequis ne sont pas démarrés par cette séquence : {string.Join(", ", noms)}. "
            + "S'ils ne sont pas déjà opérationnels, les composants qui en dépendent échoueront.",
            "Ajoutez une étape de contrôle en tête de séquence, ou vérifiez leur état sur l'écran de supervision."));
    }

    /// <summary>
    /// Rejoue le garde-fou anti-séquence-invalide (FR-044) sur les étapes
    /// RÉELLEMENT préparées pour cette exécution — pas seulement au moment où
    /// le workflow a été édité. Une dépendance ajoutée ou modifiée après la
    /// dernière validation du workflow ne doit pas pouvoir se glisser dans une
    /// exécution réelle sans être revue.
    /// </summary>
    private async Task ControlerSequenceAsync(
        WorkflowExecution execution, List<PreflightCheck> controles, CancellationToken ct)
    {
        var violations = await sequenceValidator.ValidateAsync(
            execution.EnvironmentId, execution.Steps.ToList(), ct);

        if (violations.Count == 0)
        {
            controles.Add(PreflightCheck.Reussi(
                "Cohérence de la séquence",
                "Aucune violation du graphe de dépendances détectée."));
            return;
        }

        var bloquantes = violations.Where(v => v.Blocking).ToList();
        var texte = string.Join(" ; ", violations.Select(v => v.Message));

        controles.Add(bloquantes.Count > 0
            ? PreflightCheck.Echec(
                "Cohérence de la séquence",
                texte,
                "Cette séquence contredit le graphe de dépendances déclaré au référentiel — "
                + "typiquement XPS avant Bridge, ou Center avant les Cluster Nodes. "
                + "Elle ne peut être lancée telle quelle.",
                bloquant: true)
            : PreflightCheck.Avertissement(
                "Cohérence de la séquence", texte,
                "Ces réserves ne bloquent pas le lancement, mais méritent un contrôle avant de continuer."));
    }

    /// <summary>
    /// FR-036/AC-16 : un démarrage complet ne peut commencer que si tous les
    /// composants ciblés sont confirmés DOWN. En laisser un déjà actif expose
    /// à une double commande de démarrage — inoffensive au mieux, source
    /// d'état incohérent au pire.
    /// </summary>
    private async Task ControlerEtatInitialDemarrageCompletAsync(
        WorkflowExecution execution, Dictionary<Guid, N4Component> composants,
        List<PreflightCheck> controles, CancellationToken ct)
    {
        if (execution.Kind != WorkflowKind.DemarrageComplet) return;

        var cibles = execution.Steps
            .Where(s => s.Action is StepAction.Demarrer or StepAction.Redemarrer && s.ComponentId is not null)
            .Select(s => s.ComponentId!.Value)
            .Distinct()
            .ToList();

        if (cibles.Count == 0) return;

        var encoreActifs = new List<(string Nom, ComponentRole Role)>();

        foreach (var id in cibles)
        {
            ct.ThrowIfCancellationRequested();

            var etat = await supervision.EvaluateComponentAsync(id, ct);
            var down = etat.State is ComponentState.Arret or ComponentState.Indisponible
                                    or ComponentState.NonSupervise or ComponentState.Inconnu;

            if (!down && composants.TryGetValue(id, out var composant))
                encoreActifs.Add((composant.LogicalName, composant.Role));
        }

        if (encoreActifs.Count == 0)
        {
            controles.Add(PreflightCheck.Reussi(
                "Composants tous à l'arrêt",
                $"Les {cibles.Count} composant(s) ciblé(s) sont confirmés DOWN avant ce démarrage complet."));
            return;
        }

        // L'ordre d'arret propose reprend EXACTEMENT la table de RangArret
        // (WorkflowService) : c'est la meme sequence que celle utilisee pour
        // construire un vrai workflow d'arret, pas une improvisation.
        var ordre = encoreActifs
            .OrderBy(c => WorkflowService.RangArret(c.Role))
            .Select(c => c.Nom)
            .ToList();

        controles.Add(PreflightCheck.Echec(
            "Composants tous à l'arrêt",
            $"{encoreActifs.Count} composant(s) ciblé(s) par ce démarrage complet sont encore actifs : "
            + $"{string.Join(", ", ordre)}.",
            "Un démarrage complet exige que tous les composants ciblés soient DOWN au préalable. "
            + $"Arrêtez-les dans cet ordre avant de relancer : {string.Join(" → ", ordre)}.",
            bloquant: true));
    }

    /// <summary>
    /// FR-046/047 : une action d'arrêt ou de redémarrage sur le Center exige
    /// un choix explicite de continuité, et une bascule choisie exige un
    /// Standby réellement apte à prendre le relais — jamais supposé.
    /// </summary>
    private async Task ControlerContinuiteCenterAsync(
        WorkflowExecution execution, List<PreflightCheck> controles, CancellationToken ct)
    {
        if (!execution.ContinuityChoiceRequired)
        {
            controles.Add(PreflightCheck.NonApplicable(
                "Continuité Center",
                "Cette opération n'arrête ni ne redémarre le Center."));
            return;
        }

        // FR-047 : un split-brain deja en cours interdit toute nouvelle
        // action sur le Center, quel que soit le choix de continuite - y
        // ajouter une operation orchestree aggraverait une situation deja
        // incoherente plutot que de la resoudre.
        var etatActuel = await continuity.AssessAsync(execution.EnvironmentId, ct);
        if (etatActuel.BothHoldActiveRole)
        {
            controles.Add(PreflightCheck.Echec(
                "Continuité Center",
                $"Le Center « {etatActuel.Center?.LogicalName} » et le Standby « {etatActuel.Standby?.LogicalName} » "
                + "tiennent actuellement TOUS LES DEUX le rôle actif (split-brain FR-032/033/047).",
                "Résolvez le conflit de rôle manuellement (avec le support Navis si nécessaire) avant toute "
                + "opération orchestrée sur le Center : agir dessus maintenant aggraverait l'incohérence.",
                bloquant: true));
            return;
        }

        if (execution.ContinuityChoice is null)
        {
            controles.Add(PreflightCheck.Echec(
                "Continuité Center",
                "Cette opération arrête ou redémarre le Center, sans qu'un choix de continuité ait été fait.",
                "Précisez, depuis l'écran de l'opération, si le Center doit rester le nœud actif ou si le "
                + "rôle actif doit basculer vers le Standby avant de poursuivre.",
                bloquant: true));
            return;
        }

        if (execution.ContinuityChoice == CenterContinuityChoice.ResterActif)
        {
            controles.Add(PreflightCheck.Avertissement(
                "Continuité Center",
                "Choix fait : le Center reste le nœud actif pendant cette opération.",
                "Ne démarrez ni ne validez le Standby pendant cette fenêtre : deux nœuds actifs "
                + "simultanément est un split-brain (FR-032/033)."));
            return;
        }

        // Basculer : le Standby doit etre reellement apte, pas suppose l'etre.
        var evaluation = await continuity.AssessAsync(execution.EnvironmentId, ct);

        if (!evaluation.StandbyIsCapable)
        {
            controles.Add(PreflightCheck.Echec(
                "Continuité Center",
                $"Bascule choisie, mais {evaluation.StandbyUnavailableReason}",
                "Rétablissez le Standby, ou choisissez de garder le Center actif, avant de relancer.",
                bloquant: true));
            return;
        }

        controles.Add(PreflightCheck.Reussi(
            "Continuité Center",
            $"Bascule choisie : le Standby « {evaluation.Standby!.LogicalName} » est disponible et apte "
            + "à prendre le rôle actif."));
    }

    /// <summary>
    /// FR-045 : XPS a sa propre exigence de continuité, distincte de celle du
    /// Center/Standby et du Cluster — le Bridge Daemon traverse WAITING puis
    /// LOADING avant ACTIVE (module 1.6, N4 IT Administrator Day 1), et XPS
    /// NE DOIT PAS être démarré avant que ce marqueur ait été prouvé. Un
    /// composant simplement « Running » côté service Windows ne le garantit
    /// pas — c'est la preuve applicative qui compte, comme partout ailleurs.
    ///
    /// NON BLOQUANT ICI, à dessein : l'état du Bridge peut changer entre la
    /// préparation et le lancement, et c'est <see cref="StepExecutor.VerifierPrerequisAsync"/>,
    /// revérifié juste avant que la commande XPS ne parte, qui fait
    /// effectivement barrage (FR-044). Ce contrôle donne seulement à
    /// l'opérateur une visibilité anticipée, avant qu'il ne s'engage.
    /// </summary>
    private async Task ControlerContinuiteBridgeAsync(
        WorkflowExecution execution, Dictionary<Guid, N4Component> composants,
        List<PreflightCheck> controles, CancellationToken ct)
    {
        var etapeXps = execution.Steps
            .Where(s => s.Action is StepAction.Demarrer or StepAction.Redemarrer
                && s.ComponentId is { } id && composants.TryGetValue(id, out var c) && c.Role == ComponentRole.Xps)
            .OrderBy(s => s.Order)
            .FirstOrDefault();

        if (etapeXps is null)
        {
            controles.Add(PreflightCheck.NonApplicable(
                "Continuité Bridge/XPS",
                "Cette opération ne démarre ni ne redémarre XPS."));
            return;
        }

        // Si CETTE opération démarre aussi le Bridge avant XPS, le garde-fou
        // d'exécution (StepExecutor.VerifierPrerequisAsync, FR-044) revérifie
        // déjà l'état réel juste avant l'étape XPS — un contrôle ici, sur
        // l'état d'AVANT lancement, bloquerait à tort un démarrage à froid
        // classique (Bridge puis XPS dans la même séquence).
        var bridgeDemarreAvant = execution.Steps.Any(s =>
            s.Order < etapeXps.Order
            && s.Action is StepAction.Demarrer or StepAction.Redemarrer
            && s.ComponentId is { } id && composants.TryGetValue(id, out var c) && c.Role == ComponentRole.BridgeDaemon);

        if (bridgeDemarreAvant)
        {
            controles.Add(PreflightCheck.NonApplicable(
                "Continuité Bridge/XPS",
                "Le Bridge est démarré par cette même opération avant XPS : la preuve ACTIVE est revérifiée "
                + "juste avant l'étape XPS, pas ici."));
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var bridge = await db.Components.AsNoTracking()
            .FirstOrDefaultAsync(c => c.EnvironmentId == execution.EnvironmentId && c.Role == ComponentRole.BridgeDaemon, ct);

        if (bridge is null)
        {
            controles.Add(PreflightCheck.Avertissement(
                "Continuité Bridge/XPS",
                "XPS va être démarré, mais aucun XPS Bridge Daemon n'est déclaré dans cet environnement : "
                + "XPS ne fonctionne pas sans lui.",
                "Déclarez le Bridge Daemon dans le référentiel."));
            return;
        }

        var sante = await supervision.EvaluateComponentAsync(bridge.Id, ct);

        if (sante.State != ComponentState.Disponible || sante.LogProofStatus != Supervision.LogProofState.Proved)
        {
            controles.Add(PreflightCheck.Avertissement(
                "Continuité Bridge/XPS",
                $"Le Bridge Daemon « {bridge.LogicalName} » n'est pas encore prouvé ACTIVE ({sante.Verdict}). "
                + "Il traverse WAITING (réclame les données conteneurs au Center) puis LOADING (les reçoit) "
                + "avant ACTIVE : démarrer XPS avant cet état expose à un traitement silencieusement incomplet. "
                + "Le lancement réel revérifiera cet état juste avant la commande.",
                "Vérifiez que le Bridge a atteint ACTIVE avant de valider le lancement."));
            return;
        }

        controles.Add(PreflightCheck.Reussi(
            "Continuité Bridge/XPS",
            $"Le Bridge Daemon « {bridge.LogicalName} » est confirmé ACTIVE : XPS peut être démarré."));
    }

    private async Task ControlerServeursAsync(
        Dictionary<Guid, N4Component> composants, List<PreflightCheck> controles,
        bool simulation, CancellationToken ct)
    {
        var serveurs = composants.Values
            .Where(c => c.Server is not null)
            .Select(c => c.Server!)
            .DistinctBy(s => s.Id)
            .ToList();

        if (serveurs.Count == 0) return;

        var injoignables = new List<string>();
        var reserves = new List<string>();

        foreach (var serveur in serveurs)
        {
            ct.ThrowIfCancellationRequested();

            var resolution = await targetFactory.CreateAsync(serveur, ct);
            if (!resolution.Succeeded)
            {
                injoignables.Add($"{serveur.HostName} : {resolution.Error}");
                continue;
            }

            var ping = await connector.PingAsync(resolution.Target!, ct);
            if (!ping.Succeeded)
            {
                injoignables.Add($"{serveur.HostName} : {ping.Error}");
                continue;
            }

            var systeme = await connector.GetSystemAsync(resolution.Target!, ct);
            if (!systeme.Succeeded || systeme.Value is null) continue;

            // L'ecart d'horloge est une cause documentee de statuts
            // DISCONNECTED trompeurs sur N4, et une cause frequente d'incident
            // majeur. Le signaler AVANT evite de chercher la panne ailleurs.
            if (systeme.Value.ClockSkewSeconds is { } ecart && Math.Abs(ecart) > 5)
                reserves.Add($"{serveur.HostName} : écart d'horloge de {ecart:0.0} s avec le serveur Sentinel");

            foreach (var disque in systeme.Value.Disks.Where(d => d.FreePercent < 10))
                reserves.Add($"{serveur.HostName} : disque {disque.Drive} à {disque.FreePercent:0.0} % libre");
        }

        if (injoignables.Count > 0)
        {
            controles.Add(PreflightCheck.Echec(
                "Joignabilité des serveurs",
                string.Join(" ; ", injoignables) + ".",
                "Un serveur injoignable découvert en cours de séquence laisse l'écosystème dans un état "
                + "intermédiaire. Corrigez l'accès avant de lancer.",
                // Une simulation n'emet rien : l'injoignabilite est une
                // information utile, pas un motif de refus.
                bloquant: !simulation));
        }
        else
        {
            controles.Add(PreflightCheck.Reussi(
                "Joignabilité des serveurs",
                $"Les {serveurs.Count} serveur(s) concerné(s) répondent et acceptent l'exécution distante."));
        }

        if (reserves.Count > 0)
            controles.Add(PreflightCheck.Avertissement(
                "Ressources et horloge",
                string.Join(" ; ", reserves) + ".",
                "Un écart d'horloge supérieur à cinq secondes produit des statuts N4 trompeurs. "
                + "Un disque presque plein empêche l'écriture des journaux — donc la preuve de démarrage."));
    }

    /// <summary>
    /// Énonce ce qui tombera avec les composants arrêtés, même s'ils ne sont
    /// pas dans la séquence. L'opérateur doit le savoir avant, pas le découvrir
    /// à l'appel d'un client.
    /// </summary>
    private static void ControlerImpact(
        WorkflowExecution execution, Dictionary<Guid, N4Component> composants, List<PreflightCheck> controles)
    {
        var arretes = execution.Steps
            .Where(s => s.Action is StepAction.Arreter or StepAction.Redemarrer && s.ComponentId is not null)
            .Select(s => s.ComponentId!.Value)
            .ToHashSet();

        if (arretes.Count == 0) return;

        var noms = arretes
            .Where(composants.ContainsKey)
            .Select(id => composants[id].LogicalName)
            .ToList();

        var critiques = arretes
            .Where(id => composants.TryGetValue(id, out var c) && c.Criticality >= CriticalityLevel.Elevee)
            .Select(id => composants[id].LogicalName)
            .ToList();

        var detail = $"{noms.Count} composant(s) seront arrêtés : {string.Join(", ", noms)}.";
        if (critiques.Count > 0)
            detail += $" Dont {critiques.Count} de criticité élevée : {string.Join(", ", critiques)}.";

        controles.Add(PreflightCheck.Avertissement("Impact de l'arrêt", detail,
            "Les étapes déjà exécutées ne sont pas défaites en cas d'annulation : "
            + "une séquence d'arrêt interrompue laisse l'écosystème à moitié éteint."));
    }

    // -----------------------------------------------------------------------
    private static async Task<Dictionary<Guid, N4Component>> ChargerComposantsAsync(
        N4SentinelDbContext db, WorkflowExecution execution, CancellationToken ct)
    {
        var ids = execution.Steps
            .Where(s => s.ComponentId is not null)
            .Select(s => s.ComponentId!.Value)
            .Distinct()
            .ToList();

        if (ids.Count == 0) return [];

        return await db.Components
            .AsNoTracking()
            .Include(c => c.Server)
            .Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);
    }

    /// <summary>Relit le rapport conservé avec une exécution.</summary>
    public static List<PreflightCheck> Relire(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<PreflightCheck>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}

public enum PreflightOutcome
{
    Reussi = 0,
    Avertissement = 1,
    Echec = 2,
    NonApplicable = 3
}

public sealed record PreflightCheck
{
    public string Name { get; init; } = string.Empty;
    public PreflightOutcome Outcome { get; init; }
    public string Detail { get; init; } = string.Empty;
    public string? Remedy { get; init; }

    /// <summary>
    /// Un contrôle bloquant en échec interdit l'exécution, sans contournement
    /// possible. C'est la seule façon qu'il reste bloquant le jour où quelqu'un
    /// est pressé.
    /// </summary>
    public bool IsBlocking { get; init; }

    public static PreflightCheck Reussi(string nom, string detail) =>
        new() { Name = nom, Outcome = PreflightOutcome.Reussi, Detail = detail };

    public static PreflightCheck Avertissement(string nom, string detail, string? conseil = null) =>
        new() { Name = nom, Outcome = PreflightOutcome.Avertissement, Detail = detail, Remedy = conseil };

    public static PreflightCheck Echec(string nom, string detail, string? conseil, bool bloquant) =>
        new() { Name = nom, Outcome = PreflightOutcome.Echec, Detail = detail, Remedy = conseil, IsBlocking = bloquant };

    public static PreflightCheck NonApplicable(string nom, string detail) =>
        new() { Name = nom, Outcome = PreflightOutcome.NonApplicable, Detail = detail };
}

public sealed record PreflightReport
{
    public Guid ExecutionId { get; init; }
    public DateTimeOffset RunAt { get; init; }
    public List<PreflightCheck> Checks { get; init; } = [];
    public string? Error { get; init; }

    public int BlockingFailures =>
        Checks.Count(c => c.Outcome == PreflightOutcome.Echec && c.IsBlocking);

    public int Warnings => Checks.Count(c => c.Outcome == PreflightOutcome.Avertissement);

    public bool HasBlockingFailure => BlockingFailures > 0;

    /// <summary>Vrai si l'exécution peut être lancée.</summary>
    public bool Cleared => Error is null && !HasBlockingFailure;

    public static PreflightReport Impossible(string error) => new() { Error = error };
}
