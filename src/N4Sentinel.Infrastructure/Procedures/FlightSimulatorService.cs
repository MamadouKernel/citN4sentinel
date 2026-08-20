using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace N4Sentinel.Infrastructure.Procedures;

/// <summary>
/// Moteur d'entraînement aux situations de crise N4, pour les équipes N1/N2.
///
/// CE QUE CE SERVICE EST, ET CE QU'IL N'EST PAS. Les scénarios sont écrits à
/// l'avance et c'est voulu : un outil d'entraînement doit poser des situations
/// choisies. Il ne décrit jamais l'état réel de l'écosystème et ne prétend pas
/// le faire — ses journaux portent la mention « [Simulateur] ».
///
/// Ce qui était faux, en revanche, c'était la mécanique :
///
///   — la notation se faisait par sous-chaîne (<c>Contains("Vérifier")</c>),
///     indépendamment du scénario ET de l'étape : n'importe quelle action
///     contenant « Vérifier » valait dix points ;
///   — le message d'échec accusait toujours le Standby, même dans le scénario
///     de corruption KahaDB qui n'en parle pas ;
///   — <c>ExecuteAction</c> recevait un identifiant de session qu'il n'utilisait
///     jamais : aucune session n'était conservée, le score ne s'accumulait
///     nulle part, la session était une fiction.
///
/// Un outil censé former des opérateurs qui félicite sur une correspondance de
/// sous-chaîne enseigne par coïncidence. Chaque étape porte désormais ses
/// propres propositions, sa bonne réponse et l'explication de ce qui a été
/// manqué — et l'état de session est réellement tenu.
/// </summary>
public sealed class FlightSimulatorService(ILogger<FlightSimulatorService> logger)
{
    private readonly ConcurrentDictionary<Guid, SimulatorSession> _sessions = new();

    public List<SimulatorScenario> GetAvailableScenarios() => Catalogue.Values.ToList();

    public SimulatorScenario? GetScenario(string scenarioId)
        => Catalogue.TryGetValue(scenarioId, out var s) ? s : null;

    /// <summary>
    /// Ouvre une session. Retourne <c>null</c> si le scénario n'existe pas —
    /// auparavant, n'importe quel identifiant produisait une session valide.
    /// </summary>
    public SimulatorSession? StartSession(string scenarioId, string traineeName)
    {
        if (!Catalogue.TryGetValue(scenarioId, out var scenario))
        {
            logger.LogWarning("[Simulateur] Scénario inconnu : {ScenarioId}.", scenarioId);
            return null;
        }

        var session = new SimulatorSession
        {
            SessionId = Guid.NewGuid(),
            ScenarioId = scenarioId,
            TraineeName = traineeName,
            StartedAt = DateTimeOffset.UtcNow,
            ScorePercent = 100.0,
            StressLevelPercent = 25.0,
            CurrentStepIndex = 1,
            TotalSteps = scenario.Steps.Count,
            CurrentStepInstructions = scenario.Steps[0].Instruction,
            CurrentStepActions = scenario.Steps[0].ProposedActions,
            LogEvents =
            [
                $"{DateTime.Now:HH:mm:ss} — [Simulateur] Scénario « {scenario.Title} » initié par {traineeName}.",
                $"{DateTime.Now:HH:mm:ss} — [Simulateur] Environnement bac à sable. Aucune commande n'est émise."
            ]
        };

        _sessions[session.SessionId] = session;
        logger.LogInformation("[Simulateur] Session {SessionId} ouverte sur {ScenarioId}.", session.SessionId, scenarioId);
        return session;
    }

    /// <summary>
    /// Évalue une action CONTRE L'ÉTAPE COURANTE du scénario courant, met à
    /// jour la session et la fait avancer.
    /// </summary>
    public SimulatorStepResult ExecuteAction(Guid sessionId, string chosenAction)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            // Une session perdue doit se dire. La reprendre silencieusement
            // sur des valeurs par défaut redonnerait un score inventé.
            return new SimulatorStepResult
            {
                IsSuccess = false,
                ScoreDelta = 0,
                FeedbackMessage = "Session d'entraînement introuvable ou expirée. Relancez le scénario.",
                UpdatedStressPercent = 0,
                SessionEnded = true
            };
        }

        var scenario = Catalogue[session.ScenarioId];

        if (session.IsFinished)
        {
            return new SimulatorStepResult
            {
                IsSuccess = false,
                ScoreDelta = 0,
                FeedbackMessage = "Ce scénario est terminé. Relancez-en un pour continuer l'entraînement.",
                UpdatedStressPercent = session.StressLevelPercent,
                SessionEnded = true
            };
        }

        var etape = scenario.Steps[session.CurrentStepIndex - 1];
        var correcte = string.Equals(chosenAction, etape.CorrectAction, StringComparison.Ordinal);

        // Le coût d'une erreur est proportionnel au nombre d'étapes : une faute
        // sur trois étapes pèse plus qu'une faute sur dix. Le score se lit
        // « part de la procédure exécutée correctement ».
        var penalite = correcte ? 0 : (int)Math.Round(100.0 / scenario.Steps.Count);

        session.ScorePercent = Math.Max(0, session.ScorePercent - penalite);
        session.StressLevelPercent = Math.Clamp(
            session.StressLevelPercent + (correcte ? -10 : 25), 0, 100);

        session.LogEvents.Add(
            $"{DateTime.Now:HH:mm:ss} — [Simulateur] Étape {session.CurrentStepIndex}/{session.TotalSteps} : "
            + $"« {chosenAction} » → {(correcte ? "conforme" : "NON conforme")}.");

        var message = correcte
            ? $"Conforme. {etape.WhyItMatters}"
            : $"Non conforme. {etape.WhatWasMissed}";

        // Avancer, ou clore.
        if (session.CurrentStepIndex < session.TotalSteps)
        {
            session.CurrentStepIndex++;
            var suivante = scenario.Steps[session.CurrentStepIndex - 1];
            session.CurrentStepInstructions = suivante.Instruction;
            session.CurrentStepActions = suivante.ProposedActions;
        }
        else
        {
            session.IsFinished = true;
            session.CurrentStepInstructions =
                $"Scénario terminé. Score : {session.ScorePercent:0} %. Objectif visé : {scenario.TargetObjective}";
            session.CurrentStepActions = [];
            session.LogEvents.Add(
                $"{DateTime.Now:HH:mm:ss} — [Simulateur] Scénario clos. Score final : {session.ScorePercent:0} %.");
        }

        return new SimulatorStepResult
        {
            IsSuccess = correcte,
            ScoreDelta = -penalite,
            FeedbackMessage = message,
            UpdatedStressPercent = session.StressLevelPercent,
            SessionEnded = session.IsFinished
        };
    }

    /// <summary>Ferme une session et libère son état.</summary>
    public void EndSession(Guid sessionId) => _sessions.TryRemove(sessionId, out _);

    // =======================================================================
    // Catalogue de scénarios
    // =======================================================================
    //
    // Les situations sont tirées de modes de panne N4 réels documentés par le
    // corpus éditeur : perte du primaire SQL, corruption du journal KahaDB
    // d'ActiveMQ, perte de heartbeat du Center Node avec risque de split-brain.

    private static readonly Dictionary<string, SimulatorScenario> Catalogue = new()
    {
        ["SIM-CRASH-DB"] = new SimulatorScenario
        {
            Id = "SIM-CRASH-DB",
            Title = "Crash imprévu du SQL Server primaire",
            Difficulty = "EXPERT",
            EstimatedDurationMinutes = 15,
            Description = "La base de données primaire subit une rupture de liaison. Vous devez basculer vers le Standby en préservant l'intégrité des transactions N4.",
            InitialSymptoms = "Listener injoignable, délais d'attente sur les nœuds N4, files ActiveMQ figées.",
            TargetObjective = "Basculer la base, vérifier la reprise Hazelcast et confirmer le retour de XPS.",
            Steps =
            [
                new SimulatorStep
                {
                    Instruction = "Étape 1 — Le primaire ne répond plus. Quelle est la première action ?",
                    ProposedActions =
                    [
                        "Vérifier l'état du Standby avant toute bascule",
                        "Basculer immédiatement sans contrôle",
                        "Redémarrer les nœuds N4 pour forcer la reconnexion"
                    ],
                    CorrectAction = "Vérifier l'état du Standby avant toute bascule",
                    WhyItMatters = "Basculer vers un Standby non synchronisé perdrait les transactions non répliquées.",
                    WhatWasMissed = "L'état du Standby n'a pas été vérifié. Une bascule vers un Standby en retard perd des transactions, et redémarrer les nœuds ne répare pas une base absente."
                },
                new SimulatorStep
                {
                    Instruction = "Étape 2 — Le Standby est synchronisé. Comment procédez-vous à la bascule ?",
                    ProposedActions =
                    [
                        "Basculer le rôle, puis contrôler la reprise Hazelcast des nœuds",
                        "Basculer le rôle et déclarer l'incident clos",
                        "Laisser les nœuds se reconnecter d'eux-mêmes"
                    ],
                    CorrectAction = "Basculer le rôle, puis contrôler la reprise Hazelcast des nœuds",
                    WhyItMatters = "La bascule n'est prouvée que lorsque les nœuds ont effectivement repris leur session.",
                    WhatWasMissed = "La bascule a été déclarée réussie sans preuve. Tant que la reprise Hazelcast n'est pas constatée, rien ne dit que les nœuds ont retrouvé la base."
                },
                new SimulatorStep
                {
                    Instruction = "Étape 3 — Les nœuds ont repris. Comment clôturez-vous ?",
                    ProposedActions =
                    [
                        "Contrôler le retour de XPS et consigner l'incident",
                        "Clore immédiatement, les nœuds sont revenus",
                        "Relancer une bascule de contrôle"
                    ],
                    CorrectAction = "Contrôler le retour de XPS et consigner l'incident",
                    WhyItMatters = "XPS revient après les nœuds : sans ce contrôle, l'exploitation reste dégradée sans que personne ne le sache.",
                    WhatWasMissed = "XPS n'a pas été contrôlé. Les nœuds peuvent être revenus alors que XPS est encore hors service, et une seconde bascule de contrôle ne ferait qu'ajouter du risque."
                }
            ]
        },

        ["SIM-KAHADB-CORRUPT"] = new SimulatorScenario
        {
            Id = "SIM-KAHADB-CORRUPT",
            Title = "Corruption KahaDB et reconstitution du dossier partagé",
            Difficulty = "AVANCÉ",
            EstimatedDurationMinutes = 12,
            Description = "Le journal KahaDB d'ActiveMQ présente une structure corrompue après une coupure sur le SAN.",
            InitialSymptoms = "Échec au démarrage du broker, exception de journal KahaDB corrompu.",
            TargetObjective = "Reconstituer le journal en préservant une copie de l'état corrompu.",
            Steps =
            [
                new SimulatorStep
                {
                    Instruction = "Étape 1 — Le broker refuse de démarrer sur un journal corrompu. Que faites-vous d'abord ?",
                    ProposedActions =
                    [
                        "Sauvegarder le dossier KahaDB en l'état avant toute action",
                        "Supprimer le journal corrompu pour permettre le démarrage",
                        "Relancer le broker plusieurs fois"
                    ],
                    CorrectAction = "Sauvegarder le dossier KahaDB en l'état avant toute action",
                    WhyItMatters = "La copie corrompue est la seule trace des messages non consommés ; elle est irremplaçable pour l'analyse.",
                    WhatWasMissed = "Aucune copie n'a été faite. Supprimer le journal débloque le broker mais détruit définitivement les messages non consommés, et relancer ne répare pas un fichier corrompu."
                },
                new SimulatorStep
                {
                    Instruction = "Étape 2 — La copie est faite. Comment reconstituez-vous ?",
                    ProposedActions =
                    [
                        "Appliquer la procédure SOP de reconstitution documentée",
                        "Recréer un dossier vide à la main",
                        "Restaurer une sauvegarde de la veille sans vérifier son âge"
                    ],
                    CorrectAction = "Appliquer la procédure SOP de reconstitution documentée",
                    WhyItMatters = "La procédure fixe l'ordre des opérations et les contrôles ; l'improviser sur un broker en production produit des états partiels.",
                    WhatWasMissed = "La procédure documentée n'a pas été suivie. Un dossier recréé à la main ou une sauvegarde dont on ignore l'âge laissent un état dont personne ne peut dire ce qu'il contient."
                },
                new SimulatorStep
                {
                    Instruction = "Étape 3 — Le broker redémarre. Sur quoi vous appuyez-vous pour conclure ?",
                    ProposedActions =
                    [
                        "Attendre le marqueur de démarrage dans le journal applicatif",
                        "Constater que le service Windows est en cours d'exécution",
                        "Conclure dès que le processus apparaît"
                    ],
                    CorrectAction = "Attendre le marqueur de démarrage dans le journal applicatif",
                    WhyItMatters = "C'est le principe même de N4 Sentinel : seul le marqueur dans le journal prouve qu'un composant est opérationnel.",
                    WhatWasMissed = "Le service en cours d'exécution ou la présence du processus ne prouvent rien : ils disent que la commande est passée, pas que le broker fonctionne."
                }
            ]
        },

        ["SIM-CENTER-FAILOVER"] = new SimulatorScenario
        {
            Id = "SIM-CENTER-FAILOVER",
            Title = "Bascule d'urgence du Center Node vers le Standby",
            Difficulty = "INTERMÉDIAIRE",
            EstimatedDurationMinutes = 8,
            Description = "Le Center Node principal ne répond plus aux battements de cœur. Basculez les rôles en évitant un split-brain.",
            InitialSymptoms = "Battement de cœur du Center perdu, Standby prêt.",
            TargetObjective = "Basculer le rôle actif sans jamais autoriser deux Center simultanés.",
            Steps =
            [
                new SimulatorStep
                {
                    Instruction = "Étape 1 — Le Center ne répond plus. Quelle précaution avant de basculer ?",
                    ProposedActions =
                    [
                        "S'assurer que l'ancien Center est réellement arrêté",
                        "Activer le Standby sans attendre",
                        "Redémarrer les deux Center simultanément"
                    ],
                    CorrectAction = "S'assurer que l'ancien Center est réellement arrêté",
                    WhyItMatters = "Deux Center actifs en même temps produisent un split-brain, dont la réparation coûte bien plus que l'attente.",
                    WhatWasMissed = "L'arrêt effectif de l'ancien Center n'a pas été établi. Un Center qui ne répond plus aux battements de cœur peut être encore actif : l'activer en face crée deux Center simultanés."
                },
                new SimulatorStep
                {
                    Instruction = "Étape 2 — L'ancien Center est arrêté. Comment activez-vous le Standby ?",
                    ProposedActions =
                    [
                        "Activer le Standby et attendre la preuve de prise de rôle",
                        "Activer le Standby et passer à la suite",
                        "Activer le Standby et relancer l'ancien Center en parallèle"
                    ],
                    CorrectAction = "Activer le Standby et attendre la preuve de prise de rôle",
                    WhyItMatters = "Sans preuve de prise de rôle, on ignore lequel des deux nœuds porte réellement le rôle actif.",
                    WhatWasMissed = "La prise de rôle n'a pas été prouvée. Relancer l'ancien Center en parallèle est précisément ce qui provoque le split-brain qu'on cherche à éviter."
                }
            ]
        }
    };
}

public sealed class SimulatorScenario
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Difficulty { get; init; }
    public int EstimatedDurationMinutes { get; init; }
    public required string Description { get; init; }
    public required string InitialSymptoms { get; init; }
    public required string TargetObjective { get; init; }

    /// <summary>Étapes du scénario, dans l'ordre. Chacune porte ses propres propositions.</summary>
    public required IReadOnlyList<SimulatorStep> Steps { get; init; }
}

/// <summary>
/// Une étape : ce qu'on demande, ce qu'on propose, ce qui est correct, et
/// POURQUOI. Le « pourquoi » est le contenu pédagogique réel : sans lui,
/// l'exercice apprend à cliquer au bon endroit, pas à raisonner.
/// </summary>
public sealed class SimulatorStep
{
    public required string Instruction { get; init; }
    public required IReadOnlyList<string> ProposedActions { get; init; }
    public required string CorrectAction { get; init; }
    public required string WhyItMatters { get; init; }
    public required string WhatWasMissed { get; init; }
}

public sealed class SimulatorSession
{
    public Guid SessionId { get; init; }
    public required string ScenarioId { get; init; }
    public required string TraineeName { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public double ScorePercent { get; set; }
    public double StressLevelPercent { get; set; }
    public int CurrentStepIndex { get; set; }
    public int TotalSteps { get; init; }
    public required string CurrentStepInstructions { get; set; }

    /// <summary>Propositions de l'étape courante. Vide quand le scénario est clos.</summary>
    public IReadOnlyList<string> CurrentStepActions { get; set; } = [];

    public bool IsFinished { get; set; }
    public List<string> LogEvents { get; init; } = [];
}

public sealed class SimulatorStepResult
{
    public bool IsSuccess { get; init; }
    public int ScoreDelta { get; init; }
    public required string FeedbackMessage { get; init; }
    public double UpdatedStressPercent { get; init; }

    /// <summary>Vrai quand le scénario est clos ou la session perdue.</summary>
    public bool SessionEnded { get; init; }
}
