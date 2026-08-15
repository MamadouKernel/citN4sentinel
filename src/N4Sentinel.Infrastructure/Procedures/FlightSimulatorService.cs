using Microsoft.Extensions.Logging;

namespace N4Sentinel.Infrastructure.Procedures;

/// <summary>
/// Moteur Flight Simulator pour l'entraînement gamifié des équipes IT N1/N2 aux situations de crise.
/// </summary>
public sealed class FlightSimulatorService(ILogger<FlightSimulatorService> logger)
{
    public List<SimulatorScenario> GetAvailableScenarios()
    {
        return [
            new()
            {
                Id = "SIM-CRASH-DB",
                Title = "Crash Imprévu SQL Server Primary",
                Difficulty = "EXPERT",
                EstimatedDurationMinutes = 15,
                Description = "La base de données primaire subit une rupture de liaison. Vous devez effectuer la bascule vers le serveur Standby tout en préservant l'intégrité des transactions N4.",
                InitialSymptoms = "DB Listener unreachability, N4 Node timeouts, ActiveMQ queues stalling.",
                TargetObjective = "Activer le Failover DB, vérifier la reprise Hazelcast et valider le retour de XPS en moins de 10 min."
            },
            new()
            {
                Id = "SIM-KAHADB-CORRUPT",
                Title = "Corruption KahaDB & Reconstitution Shared Folder",
                Difficulty = "AVANCÉ",
                EstimatedDurationMinutes = 12,
                Description = "Le fichier de journalisation KahaDB d'ActiveMQ présente une structure corrompue suite à une coupure électrique sur le SAN.",
                InitialSymptoms = "ActiveMQ Broker Startup Error, Exception KahaDB Journal corrupt.",
                TargetObjective = "Exécuter la procédure SOP-004 de reconstitution sécurisée avec backup préalable."
            },
            new()
            {
                Id = "SIM-CENTER-FAILOVER",
                Title = "Bascule d'Urgence Center Node vers Standby",
                Difficulty = "INTERMÉDIAIRE",
                EstimatedDurationMinutes = 8,
                Description = "Le Center Node principal ne répond plus aux heartbeats. Basculez les rôles tout en évitant un Split-Brain.",
                InitialSymptoms = "Center Node Heartbeat lost, Standby Ready.",
                TargetObjective = "Basculer le rôle actif sur Standby sans autoriser deux Center simultanés."
            }
        ];
    }

    public SimulatorSession StartSession(string scenarioId, string traineeName)
    {
        logger.LogInformation("[Flight Simulator] Nouvelle session {ScenarioId} démarrée par {Trainee}.", scenarioId, traineeName);

        return new SimulatorSession
        {
            SessionId = Guid.NewGuid(),
            ScenarioId = scenarioId,
            TraineeName = traineeName,
            StartedAt = DateTimeOffset.UtcNow,
            ScorePercent = 100.0,
            StressLevelPercent = 25.0,
            CurrentStepIndex = 1,
            TotalSteps = 4,
            CurrentStepInstructions = "Étape 1/4 : Exécuter le pré-check de sécurité pour vérifier l'état du Standby avant d'interrompre le nœud défaillant.",
            LogEvents = [
                $"{DateTime.Now:HH:mm:ss} — [Simulateur] Scénario initié par {traineeName}.",
                $"{DateTime.Now:HH:mm:ss} — [Injecteur d'anomalies] Simulation active sur l'environnement bac à sable."
            ]
        };
    }

    public SimulatorStepResult ExecuteAction(Guid sessionId, string chosenAction)
    {
        logger.LogInformation("[Flight Simulator] Action '{Action}' exécutée sur session {SessionId}.", chosenAction, sessionId);

        var isCorrectAction = chosenAction.Contains("Preflight") || chosenAction.Contains("SOP") || chosenAction.Contains("Vérifier");

        return new SimulatorStepResult
        {
            IsSuccess = isCorrectAction,
            ScoreDelta = isCorrectAction ? 10 : -15,
            FeedbackMessage = isCorrectAction
                ? "Excellente décision ! Conforme aux prérequis du Cahier des Charges CIT."
                : "Avertissement : Vous avez omis la vérification du Standby. Risque de Split-Brain !",
            UpdatedStressPercent = isCorrectAction ? 20.0 : 65.0
        };
    }
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
    public List<string> LogEvents { get; init; } = [];
}

public sealed class SimulatorStepResult
{
    public bool IsSuccess { get; init; }
    public int ScoreDelta { get; init; }
    public required string FeedbackMessage { get; init; }
    public double UpdatedStressPercent { get; init; }
}
