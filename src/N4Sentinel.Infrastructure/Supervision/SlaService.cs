using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Supervision;

public class SlaReportDto
{
    public Guid EnvironmentId { get; set; }
    public string EnvironmentCode { get; set; } = string.Empty;
    public string EnvironmentName { get; set; } = string.Empty;
    public bool IsProduction { get; set; }
    public double TargetSlaPercentage { get; set; } = 99.9;
    public double ActualSlaPercentage { get; set; }
    public double TotalDowntimeMinutes { get; set; }
    public int TotalIncidents { get; set; }
    public double MttrMinutes { get; set; }
    public double MtbfHours { get; set; }
    public int TotalExecutions { get; set; }
    public int SuccessfulExecutions { get; set; }
    public int FailedExecutions { get; set; }
    public List<ComponentSlaMetricDto> ComponentMetrics { get; set; } = new();

    /// <summary>FR-094 : taux de réussite PAR OPÉRATION (workflow), pas seulement toutes opérations confondues.</summary>
    public List<OperationSuccessRateDto> SuccessRateByOperation { get; set; } = new();

    /// <summary>FR-094 : étapes dont la durée réelle a nettement dépassé la durée attendue.</summary>
    public List<SlowStepDto> SlowSteps { get; set; } = new();

    /// <summary>FR-094 : causes d'échec qui se répètent, classées par fréquence — jamais une seule occurrence isolée.</summary>
    public List<RecurringCauseDto> RecurringCauses { get; set; } = new();

    /// <summary>
    /// FR-094 : temps moyen, en minutes, entre l'ouverture d'une session de
    /// diagnostic et son analyse. **Null quand aucune session n'a été analysée
    /// sur la période** — et non zéro : « aucune donnée » et « diagnostic
    /// instantané » ne doivent pas s'afficher pareil.
    /// </summary>
    public double? AverageDiagnosticMinutes { get; set; }

    /// <summary>FR-094 : nombre de sessions ayant réellement servi au calcul ci-dessus.</summary>
    public int DiagnosticSessionsAnalysed { get; set; }
}

public class OperationSuccessRateDto
{
    public string WorkflowName { get; set; } = string.Empty;
    public int TotalExecutions { get; set; }
    public int SuccessfulExecutions { get; set; }
    public double SuccessRatePercentage { get; set; }
}

public class SlowStepDto
{
    public string StepName { get; set; } = string.Empty;
    public string? ComponentName { get; set; }
    public int ExpectedSeconds { get; set; }
    public double ActualSeconds { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public class RecurringCauseDto
{
    public string Cause { get; set; } = string.Empty;
    public StepErrorType? ErrorType { get; set; }
    public int OccurrenceCount { get; set; }
}

public class ComponentSlaMetricDto
{
    public Guid ComponentId { get; set; }
    public string LogicalName { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public double SlaPercentage { get; set; }
    public int IncidentCount { get; set; }
    public double DowntimeMinutes { get; set; }
}

/// <summary>
/// Service de calcul du SLA, métriques de disponibilité et synthèses analytiques (Sprint 5).
/// </summary>
public class SlaService
{
    private readonly IDbContextFactory<N4SentinelDbContext> _dbFactory;

    public SlaService(IDbContextFactory<N4SentinelDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<SlaReportDto> GenerateReportAsync(Guid environmentId, TimeSpan window)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var env = await db.Environments
            .Include(e => e.Servers)
            .ThenInclude(s => s.Components)
            .FirstOrDefaultAsync(e => e.Id == environmentId);

        if (env is null)
        {
            return new SlaReportDto
            {
                EnvironmentCode = "N/A",
                EnvironmentName = "Environnement introuvable",
                TargetSlaPercentage = 99.9,
                ActualSlaPercentage = 0,
                TotalDowntimeMinutes = 0,
                TotalIncidents = 0,
                MttrMinutes = 0,
                MtbfHours = 0,
                ComponentMetrics = new()
            };
        }

        var depuis = DateTimeOffset.UtcNow - window;

        // Récupérer les alertes sur la période
        var alerts = await db.Alerts
            .Where(a => a.EnvironmentId == environmentId && a.FirstOccurredAt >= depuis)
            .ToListAsync();

        var criticalAlerts = alerts.Where(a => a.Severity == AlertSeverity.Critique).ToList();

        // Récupérer les exécutions de workflows sur la période
        var executions = await db.Executions
            .Include(w => w.Steps)
            .Where(w => w.EnvironmentId == environmentId && w.StartedAt >= depuis.DateTime)
            .ToListAsync();

        int totalIncidents = criticalAlerts.Count;
        double totalDowntimeMinutes = criticalAlerts.Sum(a =>
        {
            var end = a.ResolvedAt ?? DateTimeOffset.UtcNow;
            return (end - a.FirstOccurredAt).TotalMinutes;
        });

        double totalHoursInPeriod = window.TotalHours;
        double totalMinutesInPeriod = window.TotalMinutes;

        double actualSla = totalMinutesInPeriod > 0
            ? Math.Max(0, Math.Min(100, ((totalMinutesInPeriod - totalDowntimeMinutes) / totalMinutesInPeriod) * 100))
            : 100.0;

        double mttr = totalIncidents > 0 ? totalDowntimeMinutes / totalIncidents : 0;
        double mtbf = totalIncidents > 0 ? (totalHoursInPeriod / totalIncidents) : totalHoursInPeriod;

        var allComponents = env.Servers.SelectMany(s => s.Components).ToList();
        var componentMetrics = new List<ComponentSlaMetricDto>();

        foreach (var c in allComponents)
        {
            var compAlerts = alerts.Where(a => a.ComponentId == c.Id).ToList();
            var compDowntime = compAlerts
                .Where(a => a.Severity == AlertSeverity.Critique)
                .Sum(a => ((a.ResolvedAt ?? DateTimeOffset.UtcNow) - a.FirstOccurredAt).TotalMinutes);

            double compSla = totalMinutesInPeriod > 0
                ? Math.Max(0, Math.Min(100, ((totalMinutesInPeriod - compDowntime) / totalMinutesInPeriod) * 100))
                : 100.0;

            componentMetrics.Add(new ComponentSlaMetricDto
            {
                ComponentId = c.Id,
                LogicalName = c.LogicalName,
                HostName = c.Server?.HostName ?? "N/A",
                Role = c.Role.ToString(),
                ServiceName = c.WindowsServiceName ?? "N/A",
                SlaPercentage = Math.Round(compSla, 2),
                IncidentCount = compAlerts.Count,
                DowntimeMinutes = Math.Round(compDowntime, 1)
            });
        }

        // FR-094 : taux de réussite par opération, distinct du taux global —
        // une opération peu fiable ne doit pas se noyer dans la moyenne des autres.
        var tauxParOperation = executions
            .Where(e => e.IsFinished)
            .GroupBy(e => e.WorkflowName)
            .Select(g => new OperationSuccessRateDto
            {
                WorkflowName = g.Key,
                TotalExecutions = g.Count(),
                SuccessfulExecutions = g.Count(e => e.Status == ExecutionStatus.TermineSucces),
                SuccessRatePercentage = Math.Round(100.0 * g.Count(e => e.Status == ExecutionStatus.TermineSucces) / g.Count(), 1)
            })
            .OrderBy(o => o.SuccessRatePercentage)
            .ToList();

        // FR-094 : une étape "lente" dépasse son seuil d'avertissement DÉCLARÉ
        // (WarningThresholdSeconds), pas une estimation arbitraire — c'est le
        // même seuil qui déclenche déjà l'avertissement pendant l'exécution.
        var etapesLentes = executions
            .SelectMany(e => e.Steps)
            .Where(s => s.StartedAt is not null && s.EndedAt is not null && s.WarningThresholdSeconds > 0
                     && (s.EndedAt.Value - s.StartedAt!.Value).TotalSeconds > s.WarningThresholdSeconds)
            .Select(s => new SlowStepDto
            {
                StepName = s.Name,
                ComponentName = s.ComponentName,
                ExpectedSeconds = s.ExpectedSeconds,
                ActualSeconds = Math.Round((s.EndedAt!.Value - s.StartedAt!.Value).TotalSeconds, 1),
                OccurredAt = s.StartedAt!.Value
            })
            .OrderByDescending(s => s.ActualSeconds)
            .Take(20)
            .ToList();

        // FR-094 : cause récurrente = classée (ErrorType si connu, sinon le
        // message brut) puis comptée — une seule occurrence n'est jamais
        // présentée comme une récurrence.
        var causesRecurrentes = executions
            .SelectMany(e => e.Steps)
            .Where(s => s.State == ExecutionStepState.Echec && s.Error is { Length: > 0 })
            .GroupBy(s => s.ErrorType is { } t ? (Cause: LibelleTypeErreur(t), Type: (StepErrorType?)t) : (Cause: s.Error!, Type: (StepErrorType?)null))
            .Where(g => g.Count() > 1)
            .Select(g => new RecurringCauseDto { Cause = g.Key.Cause, ErrorType = g.Key.Type, OccurrenceCount = g.Count() })
            .OrderByDescending(c => c.OccurrenceCount)
            .ToList();

        // FR-094 : temps de diagnostic moyen — de l'ouverture de la session a
        // l'analyse. Seules les sessions REELLEMENT analysees comptent : y
        // inclure les sessions encore ouvertes ferait baisser la moyenne a
        // mesure qu'elles s'accumulent, c'est-a-dire que l'indicateur
        // s'ameliorerait quand la situation empire.
        var sessionsAnalysees = await db.Sessions.AsNoTracking()
            .Where(s => s.EnvironmentId == environmentId
                     && s.AnalysedAt != null
                     && s.AnalysedAt >= depuis)
            .Select(s => new { s.CreatedAt, s.AnalysedAt })
            .ToListAsync();

        var dureesDiagnostic = sessionsAnalysees
            .Select(s => (s.AnalysedAt!.Value - s.CreatedAt).TotalMinutes)
            .Where(m => m >= 0)
            .ToList();

        return new SlaReportDto
        {
            DiagnosticSessionsAnalysed = dureesDiagnostic.Count,
            AverageDiagnosticMinutes = dureesDiagnostic.Count == 0
                ? null
                : Math.Round(dureesDiagnostic.Average(), 1),

            EnvironmentId = env.Id,
            EnvironmentCode = env.Code,
            EnvironmentName = env.Name,
            IsProduction = env.IsProduction,
            TargetSlaPercentage = 99.9,
            ActualSlaPercentage = Math.Round(actualSla, 2),
            TotalDowntimeMinutes = Math.Round(totalDowntimeMinutes, 1),
            TotalIncidents = totalIncidents,
            MttrMinutes = Math.Round(mttr, 1),
            MtbfHours = Math.Round(mtbf, 1),
            TotalExecutions = executions.Count,
            SuccessfulExecutions = executions.Count(e => e.Status == ExecutionStatus.TermineSucces),
            FailedExecutions = executions.Count(e => e.Status == ExecutionStatus.Echec),
            ComponentMetrics = componentMetrics.OrderBy(c => c.SlaPercentage).ToList(),
            SuccessRateByOperation = tauxParOperation,
            SlowSteps = etapesLentes,
            RecurringCauses = causesRecurrentes
        };
    }

    private static string LibelleTypeErreur(StepErrorType t) => t switch
    {
        StepErrorType.CommandeRefusee => "Commande refusée",
        StepErrorType.TimeoutAttente => "Délai dépassé",
        StepErrorType.ComportementConnuStopPending => "StopPending connu",
        StepErrorType.ComposantNonConfigure => "Référentiel incomplet",
        StepErrorType.PrerequisNonSatisfait => "Prérequis non satisfait",
        _ => "Non classé"
    };
}
