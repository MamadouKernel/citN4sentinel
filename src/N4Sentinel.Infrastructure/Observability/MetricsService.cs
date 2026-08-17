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

    public MetricsSnapshot GetSnapshot()
    {
        var polls = Interlocked.Read(ref _supervisionPolls);
        return new MetricsSnapshot
        {
            SupervisionPollCount = polls,
            SupervisionPollFailureCount = Interlocked.Read(ref _supervisionPollFailures),
            SupervisionPollAverageMs = polls > 0 ? (double)Interlocked.Read(ref _supervisionPollTotalMs) / polls : 0,
            StepOutcomes = _stepOutcomes.ToDictionary(kv => kv.Key, kv => kv.Value),
            DiagnosticVerdicts = _diagnosticVerdicts.ToDictionary(kv => kv.Key, kv => kv.Value)
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
}
