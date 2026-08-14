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

        return new SlaReportDto
        {
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
            ComponentMetrics = componentMetrics.OrderBy(c => c.SlaPercentage).ToList()
        };
    }
}
