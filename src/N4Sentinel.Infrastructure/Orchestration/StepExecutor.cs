using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Connectors;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Supervision;

namespace N4Sentinel.Infrastructure.Orchestration;

/// <summary>
/// Exécution d'UNE étape, et recherche de la preuve que son résultat est atteint.
///
/// C'est ici que vit le principe fondateur du projet : émettre la commande ne
/// prouve rien, et un service Windows « Running » non plus. La preuve est le
/// marqueur écrit dans le journal applicatif APRÈS le lancement.
///
/// Le point de repère (watchpoint) posé avant d'émettre la commande est ce qui
/// rend la preuve valide : sans lui, on relirait le marqueur du démarrage
/// précédent, encore présent dans le fichier, et on déclarerait opérationnel un
/// composant qui n'a jamais démarré. Cette erreur est exactement celle que les
/// scripts d'exploitation ont appris à éviter.
/// </summary>
public sealed class StepExecutor(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    ConnectorTargetFactory targetFactory,
    IN4Connector connector,
    SupervisionService supervision,
    ILogger<StepExecutor> logger)
{
    public async Task<StepOutcome> ExecuteAsync(
        ExecutionStep step, bool isSimulation, IProgress<string> progress, CancellationToken ct)
    {
        try
        {
            return step.Action switch
            {
                StepAction.Attendre => await AttendreAsync(step, isSimulation, progress, ct),
                StepAction.InterventionManuelle => StepOutcome.AttenteOperateur(
                    step.Instruction ?? "Intervention manuelle attendue."),
                StepAction.Verifier => await VerifierAsync(step, isSimulation, progress, ct),
                StepAction.Demarrer => await DemarrerAsync(step, isSimulation, progress, ct),
                StepAction.Arreter => await ArreterAsync(step, isSimulation, progress, ct),
                StepAction.Redemarrer => await RedemarrerAsync(step, isSimulation, progress, ct),
                _ => StepOutcome.Failed($"Action « {step.Action} » non prise en charge.")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Échec inattendu de l'étape {Etape}", step.Name);
            return StepOutcome.Failed($"Échec inattendu : {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Actions
    // -----------------------------------------------------------------------
    private static async Task<StepOutcome> AttendreAsync(
        ExecutionStep step, bool isSimulation, IProgress<string> progress, CancellationToken ct)
    {
        if (isSimulation)
            return StepOutcome.Succeeded($"[Simulation] Temporisation de {step.ExpectedSeconds} s non appliquée.");

        var fin = DateTimeOffset.UtcNow.AddSeconds(step.ExpectedSeconds);
        while (DateTimeOffset.UtcNow < fin)
        {
            ct.ThrowIfCancellationRequested();
            var restant = (int)(fin - DateTimeOffset.UtcNow).TotalSeconds;
            progress.Report($"Temporisation en cours, {restant} s restantes.");
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, Math.Max(1, restant))), ct);
        }

        return StepOutcome.Succeeded($"Temporisation de {step.ExpectedSeconds} s écoulée.");
    }

    private async Task<StepOutcome> VerifierAsync(
        ExecutionStep step, bool isSimulation, IProgress<string> progress, CancellationToken ct)
    {
        if (step.ComponentId is null)
            return StepOutcome.Succeeded("Contrôle sans composant désigné : rien à vérifier.");

        progress.Report("Contrôle de l'état réel du composant.");

        // Un controle est en LECTURE SEULE : il s'execute meme en simulation,
        // puisqu'il ne modifie rien. C'est meme tout l'interet d'une simulation
        // que de constater l'etat de depart reel.
        var sante = await supervision.EvaluateComponentAsync(step.ComponentId.Value, ct);

        return sante.State switch
        {
            ComponentState.Disponible when sante.LogProofStatus == LogProofState.Proved =>
                StepOutcome.Succeeded($"{sante.Verdict} Marqueur reconnu : « {sante.MatchedPattern} »."),

            // Aucun marqueur configure : on constate, on ne conclut pas.
            ComponentState.Disponible =>
                StepOutcome.Warned(sante.Verdict),

            ComponentState.NonSupervise =>
                StepOutcome.Succeeded(sante.Verdict),

            _ => StepOutcome.Failed(sante.Verdict)
        };
    }

    private async Task<StepOutcome> DemarrerAsync(
        ExecutionStep step, bool isSimulation, IProgress<string> progress, CancellationToken ct)
    {
        var contexte = await ChargerAsync(step, ct);
        if (contexte.Error is not null) return StepOutcome.Failed(contexte.Error);

        var composant = contexte.Component!;
        var cible = contexte.Target!;
        var readiness = composant.Readiness;

        if (string.IsNullOrWhiteSpace(composant.WindowsServiceName))
            return StepOutcome.Failed(
                $"Aucun nom de service Windows n'est renseigné pour « {composant.LogicalName} ». "
                + "Le pilotage est impossible tant que le référentiel est incomplet.");

        // GARDE-FOU DE SEQUENCE (FR-044). Verifie au dernier moment que ce dont
        // ce composant depend est REELLEMENT operationnel. Le pre-check l'a
        // deja regarde, mais l'ecosysteme a pu changer depuis - un prerequis a
        // pu tomber pendant les six etapes precedentes.
        var barrage = await VerifierPrerequisAsync(composant, progress, ct);

        // EN SIMULATION, LE BARRAGE INFORME MAIS N'ARRETE PAS. Une simulation
        // sert justement a derouler une sequence sur un ecosysteme froid, ou
        // aucun prerequis n'est encore demarre : la faire echouer a la
        // deuxieme etape la rendrait inutile. Le constat reste dit.
        if (barrage is not null && !isSimulation) return barrage;

        // POINT DE REPERE, pose AVANT d'emettre la commande. Sans lui, le
        // marqueur du demarrage precedent - toujours present dans le fichier -
        // serait pris pour la preuve du demarrage en cours.
        var repere = await PoserRepereAsync(cible, readiness, ct);

        if (isSimulation)
        {
            var texte =
                $"[Simulation] Aucune commande émise. Le service « {composant.WindowsServiceName} » "
                + $"aurait été démarré sur {cible.HostName}, "
                + (readiness.IsProvable
                    ? $"puis la preuve aurait été cherchée dans « {repere.Path ?? readiness.LogPath} »."
                    : "sans preuve applicative disponible.");

            return barrage is null
                ? StepOutcome.Succeeded(texte)
                : StepOutcome.Warned($"{texte} EN EXÉCUTION RÉELLE, CETTE ÉTAPE SERAIT REFUSÉE : {barrage.Message}");
        }

        progress.Report($"Envoi de la commande de démarrage à {cible.HostName}.");

        var commande = await connector.ControlServiceAsync(
            cible, composant.WindowsServiceName, ServiceControlAction.Demarrer, ct);

        if (!commande.Succeeded)
            return StepOutcome.Failed($"La commande de démarrage a été refusée : {commande.Error}");

        // Phase 1 : le service atteint Running. Preuve faible, mais un service
        // qui n'y arrive pas rend la phase 2 sans objet.
        var running = await AttendreStatutAsync(
            cible, composant.WindowsServiceName, "Running",
            TimeSpan.FromSeconds(readiness.ServiceRunningTimeoutSeconds),
            readiness.PollIntervalSeconds, progress,
            "Attente du passage du service à Running", ct);

        if (!running.Reached)
            return StepOutcome.Failed(
                $"Le service n'a pas atteint Running en {readiness.ServiceRunningTimeoutSeconds} s "
                + $"(dernier état observé : {running.LastStatus}).");

        if (!readiness.IsProvable)
            return StepOutcome.Warned(
                $"Service « {composant.WindowsServiceName} » à l'état Running sur {cible.HostName}. "
                + "AUCUN MARQUEUR DE JOURNAL N'EST CONFIGURÉ pour ce composant : son démarrage applicatif "
                + "n'est donc PAS prouvé. À confirmer par l'opérateur. "
                + "Renseigner le marqueur depuis l'écran Marqueurs lèvera définitivement cette réserve.");

        // Phase 2 : la preuve. C'est elle qui distingue "la commande est passee"
        // de "le composant est operationnel".
        progress.Report("Service Running. Attente du marqueur d'initialisation dans le journal.");

        return await AttendreMarqueurAsync(cible, readiness, repere, progress, ct);
    }

    private async Task<StepOutcome> ArreterAsync(
        ExecutionStep step, bool isSimulation, IProgress<string> progress, CancellationToken ct)
    {
        var contexte = await ChargerAsync(step, ct);
        if (contexte.Error is not null) return StepOutcome.Failed(contexte.Error);

        var composant = contexte.Component!;
        var cible = contexte.Target!;
        var readiness = composant.Readiness;

        if (string.IsNullOrWhiteSpace(composant.WindowsServiceName))
            return StepOutcome.Failed(
                $"Aucun nom de service Windows n'est renseigné pour « {composant.LogicalName} ».");

        if (isSimulation)
            return StepOutcome.Succeeded(
                $"[Simulation] Aucune commande émise. Le service « {composant.WindowsServiceName} » "
                + $"aurait été arrêté sur {cible.HostName}.");

        progress.Report($"Envoi de la commande d'arrêt à {cible.HostName}.");

        var commande = await connector.ControlServiceAsync(
            cible, composant.WindowsServiceName, ServiceControlAction.Arreter, ct);

        if (!commande.Succeeded)
            return StepOutcome.Failed($"La commande d'arrêt a été refusée : {commande.Error}");

        var arrete = await AttendreStatutAsync(
            cible, composant.WindowsServiceName, "Stopped",
            TimeSpan.FromSeconds(readiness.StopTimeoutSeconds),
            readiness.PollIntervalSeconds, progress,
            "Attente de l'arrêt complet", ct);

        if (arrete.Reached)
            return StepOutcome.Succeeded(
                $"Service « {composant.WindowsServiceName} » arrêté sur {cible.HostName}.");

        // Cas connu et documente : le Standby Center Node reste bloque en
        // StopPending. Le dire explicitement evite a l'operateur de chercher
        // une panne la ou il y a un comportement connu.
        if (string.Equals(arrete.LastStatus, "StopPending", StringComparison.OrdinalIgnoreCase))
            return StepOutcome.Failed(
                $"Le service est resté bloqué en StopPending pendant {readiness.StopTimeoutSeconds} s. "
                + "C'est un comportement connu sur certains composants N4 (notamment le Standby Center Node) : "
                + "le processus ne rend pas la main au gestionnaire de services. "
                + "L'arrêt du processus doit être décidé par un opérateur, il n'est pas fait automatiquement — "
                + "un composant qui vide ses files ActiveMQ ou écrit KahaDB est occupé, pas bloqué.");

        return StepOutcome.Failed(
            $"Le service ne s'est pas arrêté en {readiness.StopTimeoutSeconds} s "
            + $"(dernier état observé : {arrete.LastStatus}).");
    }

    /// <summary>
    /// Redémarrage : un arrêt PROUVÉ, puis un démarrage PROUVÉ. Fondre les deux
    /// en une commande unique ferait perdre la preuve intermédiaire, et donc la
    /// capacité à dire lequel des deux a échoué.
    /// </summary>
    private async Task<StepOutcome> RedemarrerAsync(
        ExecutionStep step, bool isSimulation, IProgress<string> progress, CancellationToken ct)
    {
        var arret = await ArreterAsync(step, isSimulation, progress, ct);
        if (arret.State is ExecutionStepState.Echec)
            return StepOutcome.Failed($"Redémarrage interrompu à l'arrêt : {arret.Message}");

        var demarrage = await DemarrerAsync(step, isSimulation, progress, ct);

        return demarrage with { Message = $"Arrêt : {arret.Message} — Démarrage : {demarrage.Message}" };
    }

    // -----------------------------------------------------------------------
    // Garde-fou de séquence
    // -----------------------------------------------------------------------
    /// <summary>
    /// Refuse de démarrer un composant dont un prérequis n'est pas prouvé
    /// opérationnel (FR-044).
    ///
    /// C'est la règle N4 que le cahier des charges cite explicitement : XPS ne
    /// démarre pas tant que le Bridge n'est pas confirmé. Un XPS lancé sur un
    /// Bridge absent démarre en apparence, échoue silencieusement à traiter les
    /// messages, et le problème se manifeste des heures plus tard sous une
    /// forme méconnaissable.
    ///
    /// « Prouvé » veut dire marqueur reconnu dans le journal. Un composant dont
    /// l'état est seulement « à confirmer » ne suffit pas à autoriser la suite —
    /// mais le dire ainsi, plutôt que d'échouer sèchement, permet à l'opérateur
    /// de trancher.
    /// </summary>
    private async Task<StepOutcome?> VerifierPrerequisAsync(
        N4Component composant, IProgress<string> progress, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var prerequis = await db.ComponentDependencies
            .AsNoTracking()
            .Include(d => d.DependsOnComponent)
            .Where(d => d.ComponentId == composant.Id && d.Kind == DependencyKind.RequisAuDemarrage)
            .ToListAsync(ct);

        if (prerequis.Count == 0) return null;

        progress.Report($"Contrôle des {prerequis.Count} prérequis de « {composant.LogicalName} ».");

        foreach (var dependance in prerequis)
        {
            ct.ThrowIfCancellationRequested();

            var cible = dependance.DependsOnComponent;
            if (cible is null) continue;

            // Un composant declare non supervise ne peut pas etre prouve : on
            // ne va pas bloquer la sequence sur une information qu'on a
            // volontairement choisi de ne pas collecter.
            if (cible.ControlMode == ControlMode.NonSupervise) continue;

            var sante = await supervision.EvaluateComponentAsync(dependance.DependsOnComponentId, ct);

            if (sante.State == ComponentState.Disponible && sante.LogProofStatus == LogProofState.Proved)
                continue;

            if (sante.State == ComponentState.Disponible)
                return StepOutcome.Failed(
                    $"« {composant.LogicalName} » dépend de « {cible.LogicalName} », dont le service tourne "
                    + "mais dont le démarrage applicatif n'est PAS prouvé — aucun marqueur de journal "
                    + "n'est configuré pour lui. "
                    + $"Démarrer {composant.LogicalName} maintenant, c'est parier. "
                    + $"Relevez le marqueur de « {cible.LogicalName} », ou contournez cette étape "
                    + "explicitement si vous avez vérifié son état par un autre moyen.");

            return StepOutcome.Failed(
                $"« {composant.LogicalName} » dépend de « {cible.LogicalName} », qui n'est pas opérationnel "
                + $"(état constaté : {sante.State}). {sante.Verdict} "
                + "La séquence s'arrête ici : un composant démarré sur un prérequis absent démarre en "
                + "apparence, puis échoue plus tard sous une forme méconnaissable.");
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Preuve par le journal
    // -----------------------------------------------------------------------
    private async Task<LogWatchpoint> PoserRepereAsync(
        ConnectorTarget cible, ReadinessProfile readiness, CancellationToken ct)
    {
        if (!readiness.IsProvable) return new LogWatchpoint();

        var info = await connector.ResolveLogAsync(cible, readiness.LogPath!, ct);

        // Journal absent : c'est le cas normal pour XPS, dont le fichier est
        // recree a chaque demarrage. On repart de zero.
        if (!info.Succeeded || info.Value is not { Exists: true })
            return new LogWatchpoint { Offset = 0, Path = null };

        return new LogWatchpoint { Offset = info.Value.Length, Path = info.Value.Path };
    }

    private async Task<StepOutcome> AttendreMarqueurAsync(
        ConnectorTarget cible, ReadinessProfile readiness, LogWatchpoint repere,
        IProgress<string> progress, CancellationToken ct)
    {
        var debut = DateTimeOffset.UtcNow;
        var limite = debut.AddSeconds(readiness.LogReadyTimeoutSeconds);
        var dernierMessage = debut;
        var offset = repere.Offset;
        var accumule = string.Empty;

        while (DateTimeOffset.UtcNow < limite)
        {
            ct.ThrowIfCancellationRequested();

            var delta = await connector.ReadLogDeltaAsync(cible, readiness.LogPath!, offset, ct: ct);

            if (delta.Succeeded && delta.Value is { Exists: true } lu)
            {
                // Rotation : le fichier a ete recree ou tronque. Repartir de
                // zero plutot que de rester bloque sur un offset devenu faux.
                if (lu.Rotated)
                {
                    logger.LogInformation("Rotation du journal détectée sur {Hote}, lecture reprise à zéro.", cible.HostName);
                    offset = 0;
                    accumule = string.Empty;
                }
                else
                {
                    offset = lu.NewOffset;
                    accumule += lu.Text;
                }

                var utile = Filtrer(accumule, readiness.IgnorePatterns);

                var erreur = Trouver(utile, readiness.ErrorPatterns);
                if (erreur is not null)
                    return StepOutcome.Failed(
                        $"Signature d'échec caractérisée dans le journal : « {erreur} ». "
                        + "L'attente est interrompue : il est inutile de consommer le délai restant.");

                var pret = Trouver(utile, readiness.ReadyPatterns);
                if (pret is not null)
                {
                    if (readiness.PostReadySettleSeconds > 0)
                    {
                        progress.Report(
                            $"Marqueur reconnu. Observation de {readiness.PostReadySettleSeconds} s "
                            + "pour détecter une erreur survenant juste après.");

                        await Task.Delay(TimeSpan.FromSeconds(readiness.PostReadySettleSeconds), ct);

                        var apres = await connector.ReadLogDeltaAsync(cible, readiness.LogPath!, offset, ct: ct);
                        if (apres.Succeeded && apres.Value is { Exists: true } suite)
                        {
                            var erreurTardive = Trouver(
                                Filtrer(suite.Text, readiness.IgnorePatterns), readiness.ErrorPatterns);

                            if (erreurTardive is not null)
                                return StepOutcome.Failed(
                                    $"Marqueur reconnu, mais une erreur est apparue dans les "
                                    + $"{readiness.PostReadySettleSeconds} s suivantes : « {erreurTardive} ».");
                        }
                    }

                    var duree = DateTimeOffset.UtcNow - debut;
                    return StepOutcome.Succeeded(
                        $"Marqueur d'initialisation reconnu dans « {repere.Path ?? readiness.LogPath} » "
                        + $"après {Formater(duree)} : « {pret} ».");
                }
            }
            else if (!delta.Succeeded)
            {
                progress.Report($"Journal momentanément illisible ({delta.Error}). Nouvelle tentative.");
            }

            if ((DateTimeOffset.UtcNow - dernierMessage).TotalSeconds >= readiness.ProgressEverySeconds)
            {
                dernierMessage = DateTimeOffset.UtcNow;
                var ecoule = DateTimeOffset.UtcNow - debut;
                progress.Report(
                    $"Initialisation applicative en cours depuis {Formater(ecoule)}. "
                    + $"Le marqueur n'est pas encore écrit (délai maximal {Formater(TimeSpan.FromSeconds(readiness.LogReadyTimeoutSeconds))}).");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, readiness.PollIntervalSeconds)), ct);
        }

        // Le delai est ecoule sans preuve. On ne conclut NI au succes NI a
        // l'echec technique : on dit ce qui est constate, et on laisse la
        // decision a l'operateur.
        return StepOutcome.Failed(
            $"Aucun marqueur d'initialisation n'a été trouvé dans « {repere.Path ?? readiness.LogPath} » "
            + $"au bout de {Formater(TimeSpan.FromSeconds(readiness.LogReadyTimeoutSeconds))}. "
            + "Le service Windows tourne, mais le démarrage applicatif n'est PAS prouvé. "
            + "Soit le composant est plus lent que le délai retenu, soit le marqueur configuré ne correspond "
            + "pas à ce qu'écrit réellement cette version — l'écran Marqueurs permet de le vérifier sur pièce.");
    }

    private async Task<StatusWait> AttendreStatutAsync(
        ConnectorTarget cible, string service, string attendu, TimeSpan delai,
        int pollSeconds, IProgress<string> progress, string libelle, CancellationToken ct)
    {
        var debut = DateTimeOffset.UtcNow;
        var limite = debut.Add(delai);
        var dernier = "Inconnu";
        var dernierMessage = debut;

        while (DateTimeOffset.UtcNow < limite)
        {
            ct.ThrowIfCancellationRequested();

            var etat = await connector.GetServiceAsync(cible, service, ct);
            if (etat.Succeeded && etat.Value is not null)
            {
                dernier = etat.Value.Status;
                if (string.Equals(dernier, attendu, StringComparison.OrdinalIgnoreCase))
                    return new StatusWait(true, dernier);
            }
            else
            {
                dernier = $"illisible ({etat.Error})";
            }

            if ((DateTimeOffset.UtcNow - dernierMessage).TotalSeconds >= 15)
            {
                dernierMessage = DateTimeOffset.UtcNow;
                progress.Report($"{libelle} — état actuel : {dernier} ({Formater(DateTimeOffset.UtcNow - debut)}).");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, Math.Min(pollSeconds, 5))), ct);
        }

        return new StatusWait(false, dernier);
    }

    // -----------------------------------------------------------------------
    // Chargement du contexte
    // -----------------------------------------------------------------------
    private async Task<StepContext> ChargerAsync(ExecutionStep step, CancellationToken ct)
    {
        if (step.ComponentId is null)
            return new StepContext { Error = $"L'étape « {step.Name} » agit sur un composant mais n'en désigne aucun." };

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var composant = await db.Components
            .AsNoTracking()
            .Include(c => c.Server)
            .FirstOrDefaultAsync(c => c.Id == step.ComponentId.Value, ct);

        if (composant is null)
            return new StepContext { Error = "Composant introuvable au référentiel." };

        // REGLE NON NEGOCIABLE : aucune action sur un composant qui n'est pas
        // declare pilotable ET valide. Un composant encore en brouillon ne
        // recoit aucune commande, quelle que soit l'urgence.
        if (!composant.CanBeControlled)
            return new StepContext
            {
                Error = $"Le composant « {composant.LogicalName} » n'est pas pilotable "
                        + $"(mode : {composant.ControlMode}, état : {composant.Status}). "
                        + "Aucune commande ne lui sera envoyée."
            };

        if (composant.Server is null)
            return new StepContext { Error = $"Aucun serveur n'est rattaché à « {composant.LogicalName} »." };

        var resolution = await targetFactory.CreateAsync(composant.Server, ct);
        if (!resolution.Succeeded)
            return new StepContext { Error = $"Serveur inaccessible : {resolution.Error}" };

        return new StepContext { Component = composant, Target = resolution.Target };
    }

    // -----------------------------------------------------------------------
    private static string Filtrer(string texte, List<string> ignores)
    {
        if (ignores.Count == 0 || string.IsNullOrEmpty(texte)) return texte;

        var lignes = texte.Split('\n');
        var retenues = lignes.Where(l => !ignores.Any(m => Correspond(l, m)));
        return string.Join('\n', retenues);
    }

    private static string? Trouver(string texte, List<string> motifs)
    {
        foreach (var motif in motifs)
        {
            if (string.IsNullOrWhiteSpace(motif)) continue;
            try
            {
                var m = Regex.Match(texte, motif, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
                if (m.Success) return m.Value.Trim();
            }
            catch (RegexMatchTimeoutException) { }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static bool Correspond(string ligne, string motif)
    {
        if (string.IsNullOrWhiteSpace(motif)) return false;
        try { return Regex.IsMatch(ligne, motif, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)); }
        catch { return false; }
    }

    /// <summary>
    /// Durée lisible. Troncature et non arrondi : afficher « 2 min » pour 90 s
    /// est un mensonge discret, et ce genre de mensonge se paie un jour
    /// d'incident, quand on compare des chronologies.
    /// </summary>
    public static string Formater(TimeSpan d)
    {
        if (d.TotalSeconds < 60) return $"{(int)Math.Floor(d.TotalSeconds)} s";
        if (d.TotalMinutes < 60) return $"{(int)Math.Floor(d.TotalMinutes)} min {(int)Math.Floor((double)d.Seconds)} s";
        return $"{(int)Math.Floor(d.TotalHours)} h {(int)Math.Floor((double)d.Minutes)} min";
    }

    private sealed class StepContext
    {
        public N4Component? Component { get; init; }
        public ConnectorTarget? Target { get; init; }
        public string? Error { get; init; }
    }

    private sealed record LogWatchpoint
    {
        public long Offset { get; init; }
        public string? Path { get; init; }
    }

    private readonly record struct StatusWait(bool Reached, string LastStatus);
}

/// <summary>Issue d'une étape, avec sa preuve ou sa cause d'échec.</summary>
public sealed record StepOutcome
{
    public ExecutionStepState State { get; init; }
    public string Message { get; init; } = string.Empty;

    /// <summary>Vrai si l'étape attend un geste humain avant de pouvoir conclure.</summary>
    public bool WaitsForOperator => State == ExecutionStepState.EnAttente;

    public static StepOutcome Succeeded(string preuve) =>
        new() { State = ExecutionStepState.Reussi, Message = preuve };

    /// <summary>
    /// Résultat atteint, mais non prouvé. Ce n'est pas un succès dégradé qu'on
    /// masque : c'est un « à confirmer » qui reste visible dans le rapport.
    /// </summary>
    public static StepOutcome Warned(string reserve) =>
        new() { State = ExecutionStepState.Avertissement, Message = reserve };

    public static StepOutcome Failed(string cause) =>
        new() { State = ExecutionStepState.Echec, Message = cause };

    public static StepOutcome AttenteOperateur(string consigne) =>
        new() { State = ExecutionStepState.EnAttente, Message = consigne };
}
