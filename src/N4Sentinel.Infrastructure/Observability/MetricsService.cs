using System.Collections.Concurrent;
using N4Sentinel.Domain;

namespace N4Sentinel.Infrastructure.Observability;

/// <summary>
/// NFR-008 : métriques d'exploitation AU-DELÀ DES LOGS — des compteurs
/// consultables sans grep un fichier, exposés à un outil d'APM externe.
///
/// Volontairement en mémoire, pas dans la base : une métrique perdue au
/// redémarrage n'est pas un incident, contrairement à une entrée d'audit ou
/// un relevé de supervision — la distinction est délibérée.
/// </summary>
public sealed class MetricsService
{
    private long _supervisionPolls;
    private long _supervisionPollFailures;
    private long _supervisionPollTotalMs;

    private readonly ConcurrentDictionary<ExecutionStepState, long> _stepOutcomes = new();
    private readonly ConcurrentDictionary<DiagnosticVerdict, long> _diagnosticVerdicts = new();
    private readonly ConcurrentDictionary<ResolutionSkillLevel, long> _incidentResolutionLevels = new();
    private long _incidentMttrSumMinutes;
    private long _incidentCount;

    public void RecordSupervisionPoll(TimeSpan duree, bool succes)
    {
        Interlocked.Increment(ref _supervisionPolls);
        Interlocked.Add(ref _supervisionPollTotalMs, (long)duree.TotalMilliseconds);
        if (!succes) Interlocked.Increment(ref _supervisionPollFailures);
    }

    public void RecordStepOutcome(ExecutionStepState state) =>
        _stepOutcomes.AddOrUpdate(state, 1, (_, n) => n + 1);

    public void RecordDiagnosticVerdict(DiagnosticVerdict verdict) =>
        _diagnosticVerdicts.AddOrUpdate(verdict, 1, (_, n) => n + 1);

    /// <summary>
    /// NFR-008 : métriques de capitalisation d'incident après diagnostic.
    /// Permet de suivre l'évolution du MTTR moyen et la répartition des
    /// niveaux de résolution N1/N2/Expert, ainsi que la distribution des
    /// verdicts pour les incidents capitalisés.
    /// </summary>
    public void RecordIncidentCapitalized(ResolutionSkillLevel niveau, long mttrMinutes, DiagnosticVerdict verdict)
    {
        _incidentResolutionLevels.AddOrUpdate(niveau, 1, (_, n) => n + 1);
        Interlocked.Add(ref _incidentMttrSumMinutes, mttrMinutes);
        Interlocked.Increment(ref _incidentCount);
        RecordDiagnosticVerdict(verdict);
    }

    public MetricsSnapshot GetSnapshot()
    {
        var polls = Interlocked.Read(ref _supervisionPolls);
        var incidents = Interlocked.Read(ref _incidentCount);
        return new MetricsSnapshot
        {
            SupervisionPollCount = polls,
            SupervisionPollFailureCount = Interlocked.Read(ref _supervisionPollFailures),
            SupervisionPollAverageMs = polls > 0 ? (double)Interlocked.Read(ref _supervisionPollTotalMs) / polls : 0,
            StepOutcomes = _stepOutcomes.ToDictionary(kv => kv.Key, kv => kv.Value),
            DiagnosticVerdicts = _diagnosticVerdicts.ToDictionary(kv => kv.Key, kv => kv.Value),
            CapitalizedIncidentCount = incidents,
            CapitalizedIncidentAverageMttrMinutes = incidents > 0
                ? (double)Interlocked.Read(ref _incidentMttrSumMinutes) / incidents : null,
            IncidentResolutionLevels = _incidentResolutionLevels.ToDictionary(kv => kv.Key, kv => kv.Value)
        };
    }
}

public sealed record MetricsSnapshot
{
    public long SupervisionPollCount { get; init; }
    public long SupervisionPollFailureCount { get; init; }
    public double SupervisionPollAverageMs { get; init; }
    public Dictionary<ExecutionStepState, long> StepOutcomes { get; init; } = new();
    public Dictionary<DiagnosticVerdict, long> DiagnosticVerdicts { get; init; } = new();

    public long CapitalizedIncidentCount { get; init; }
    public double? CapitalizedIncidentAverageMttrMinutes { get; init; }
    public Dictionary<ResolutionSkillLevel, long> IncidentResolutionLevels { get; init; } = new();
}
