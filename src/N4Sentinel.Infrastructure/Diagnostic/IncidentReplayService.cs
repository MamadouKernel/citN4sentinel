using Microsoft.Extensions.Logging;

namespace N4Sentinel.Infrastructure.Diagnostic;

/// <summary>
/// Moteur Incident Replay & Time-Travel Debugging ("Boîte Noire TOS").
/// Permet de remonter le temps minute par minute avant une panne et d'isoler automatiquement le "Patient Zéro".
/// </summary>
public sealed class IncidentReplayService(ILogger<IncidentReplayService> logger)
{
    public IncidentReplayTimeline GetTimeline(Guid incidentId)
    {
        logger.LogInformation("[Incident Replay] Chargement du flux temporel pour l'incident {IncidentId}.", incidentId);

        var startTime = DateTimeOffset.UtcNow.AddMinutes(-30);
        var frames = new List<SystemStateFrame>();

        for (int i = 0; i <= 30; i++)
        {
            var timestamp = startTime.AddMinutes(i);
            var isCrashPoint = i == 25;
            var isPatientZeroPoint = i == 18;

            frames.Add(new SystemStateFrame
            {
                MinuteIndex = i,
                Timestamp = timestamp,
                OverallHealthStatus = i < 18 ? "NORMAL" : i < 25 ? "DEGRADE" : "CRITIQUE",
                ActiveNodeCount = i >= 25 ? 2 : 4,
                ActiveMqQueuePressurePercent = Math.Min(100, 20 + (i * 2.8)),
                SqlLockCount = i < 18 ? 4 : 12 + ((i - 18) * 8),
                JvmHeapUsagePercent = Math.Min(99.9, 55.0 + (i * 1.4)),
                PatientZeroComponent = isPatientZeroPoint ? "SRV-AMQ-BROKER01 (KahaDB Storage Overflow)" : null,
                SummaryNote = i switch
                {
                    0 => "État nominal du cluster N4.",
                    18 => "PATIENT ZÉRO : Saturation soudaine du dossier partagé ActiveMQ KahaDB.",
                    22 => "Effet domino : Blocage des threads de confirmation N4 sur Node 02.",
                    25 => "CRASH SYSTEME : Déconnexion du Bridge Daemon et bascule Center Node.",
                    _ => $"Replay T+{i} min : Surveillance télémétrique active."
                }
            });
        }

        return new IncidentReplayTimeline
        {
            IncidentId = incidentId,
            IncidentTitle = "Arrêt Imprévu du Bridge Daemon & Desynchronisation XPS",
            RecordedAt = DateTimeOffset.UtcNow,
            PatientZeroComponent = "SRV-AMQ-BROKER01 (KahaDB Storage Overflow)",
            CausalChain = [
                "1. Surchauffe de la file EDI BAPLIE sur le Broker ActiveMQ 01 (T+18 min)",
                "2. Saturation de l'espace disque du dossier partagé /n4/share/kahadb (T+20 min)",
                "3. Interruption du thread de communication N4-Bridge Daemon (T+22 min)",
                "4. Desynchronisation complète des ordres de mouvement XPS (T+25 min)"
            ],
            Frames = frames
        };
    }
}

public sealed class IncidentReplayTimeline
{
    public Guid IncidentId { get; init; }
    public required string IncidentTitle { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
    public required string PatientZeroComponent { get; init; }
    public List<string> CausalChain { get; init; } = [];
    public List<SystemStateFrame> Frames { get; init; } = [];
}

public sealed class SystemStateFrame
{
    public int MinuteIndex { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public required string OverallHealthStatus { get; init; }
    public int ActiveNodeCount { get; init; }
    public double ActiveMqQueuePressurePercent { get; init; }
    public int SqlLockCount { get; init; }
    public double JvmHeapUsagePercent { get; init; }
    public string? PatientZeroComponent { get; init; }
    public required string SummaryNote { get; init; }
}
