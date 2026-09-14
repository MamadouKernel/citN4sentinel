using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Capitalisation;

/// <summary>
/// P3 MERISE — Calcule & persiste les métriques business ENT_METBUS
/// (BusinessSuccessMetrics) par période (Hebdomadaire S = ISO semaine,
/// Mensuel M = Mois-YYYY-MM, Journalier J = Jour-YYYY-MM-DD).
///
/// Métriques calculées :
///   MTTR_MIN (Moyenne min)           — moyenne TotalDurationMinutes.
///   P1_EVITES_APR_COUNT (Somme)      — nb APR Résolues pendant la période.
///   WF_SUCCESS_PCT (Pourcentage)     — Executions TerminéSucces ÷ Terminaux ×100.
///   RG_COMPLIANCE_PCT (Pourcentage)  — Incidents où famille cause racine est
///                                       bien renseignée par capitalisation auto
///                                       ÷ Incidents terminés ×100.
///   INCIDENTS_P1_COUNT (Somme)       — Incidents CriticalityLevel ≥ Élevée
///                                       (CriticalityLevel.Elevee + Critique).
/// Pattern : scellé Scoped, retour string? null = OK.
/// </summary>
public sealed class BusinessMetricsService(
    IDbContextFactory<N4SentinelDbContext> dbFactory)
{
    public enum Periode { Jour, Semaine, Mois }

    public async Task<string?> CalculerPourTousLesEnvAsync(
        Periode periode,
        DateTimeOffset dateDuJour,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var envIds = await db.Environments.AsNoTracking()
            .Where(e => !e.DeletedAt.HasValue)
            .Select(e => e.Id)
            .ToListAsync(ct);

        var toutEnv = new List<Guid?>(envIds.Count + 1) { null };
        foreach (var e in envIds) toutEnv.Add(e);

        foreach (var env in toutEnv)
        {
            var libellePeriode = PeriodeVersCle(periode, dateDuJour);
            var debutFin = PeriodeVersBornes(periode, dateDuJour);

            await CalculerEtEnregistrerUneMetriqueAsync(db, env, libellePeriode, "MTTR_MIN",
                "Temps moyen de résolution (MTTR) en minutes",
                BusinessMetricAggregation.Moyenne, targetFixedValue: 60m,
                calcul: async () => await ValeurMttrMoyAsync(db, env, debutFin.Debut, debutFin.Fin, ct),
                periode: periode, ct: ct);

            await CalculerEtEnregistrerUneMetriqueAsync(db, env, libellePeriode, "WF_SUCCESS_PCT",
                "Taux de succès des exécutions Workflow terminales (%)",
                BusinessMetricAggregation.Pourcentage, targetFixedValue: 95m,
                calcul: async () => await ValeurWfSuccessPctAsync(db, env, debutFin.Debut, debutFin.Fin, ct),
                periode: periode, ct: ct);

            await CalculerEtEnregistrerUneMetriqueAsync(db, env, libellePeriode, "P1_EVITES_APR_COUNT",
                "Nombre d'APR Résolues (incidents P1 évités par action préventive)",
                BusinessMetricAggregation.Somme, targetFixedValue: 5m,
                calcul: async () => await ValeurP1EvitesAprAsync(db, env, debutFin.Debut, debutFin.Fin, ct),
                periode: periode, ct: ct, plusHautEstMieux: true);

            await CalculerEtEnregistrerUneMetriqueAsync(db, env, libellePeriode, "INCIDENTS_P1_COUNT",
                "Nombre d'incidents P1 (gravité Élevée ou Critique) capitalisés",
                BusinessMetricAggregation.Somme, targetFixedValue: 0m,
                calcul: async () => await ValeurIncidentsP1Async(db, env, debutFin.Debut, debutFin.Fin, ct),
                periode: periode, ct: ct);

            await CalculerEtEnregistrerUneMetriqueAsync(db, env, libellePeriode, "RG_COMPLIANCE_PCT",
                "Taux incidents dont la cause racine est classée par capitalisation auto (%)",
                BusinessMetricAggregation.Pourcentage, targetFixedValue: 90m,
                calcul: async () => await ValeurRgCompliancePctAsync(db, env, debutFin.Debut, debutFin.Fin, ct),
                periode: periode, ct: ct, plusHautEstMieux: true);
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Calculs valeurs individuelles
    // -----------------------------------------------------------------------
    private static async Task<(decimal? Valeur, long TailleEchantillon)> ValeurMttrMoyAsync(
        N4SentinelDbContext db, Guid? envId, DateTimeOffset debut, DateTimeOffset fin, CancellationToken ct)
    {
        var query = db.CapitalizedIncidents.AsNoTracking()
            .Where(i => i.ClosedAt.HasValue && i.ClosedAt.Value >= debut && i.ClosedAt.Value < fin)
            .Where(i => i.TotalDurationMinutes.HasValue);
        if (envId.HasValue) query = query.Where(i => i.EnvironmentId == envId.Value);

        var durees = await query.Select(i => i.TotalDurationMinutes!.Value).ToListAsync(ct);
        if (durees.Count == 0) return (null, 0);
        long n = durees.Count;
        decimal avg = (decimal)durees.Average();
        return (Math.Round(avg, 2), n);
    }

    private static async Task<(decimal? Valeur, long TailleEchantillon)> ValeurWfSuccessPctAsync(
        N4SentinelDbContext db, Guid? envId, DateTimeOffset debut, DateTimeOffset fin, CancellationToken ct)
    {
        var query = db.Executions.AsNoTracking()
            .Where(e => e.EndedAt.HasValue && e.EndedAt.Value >= debut && e.EndedAt.Value < fin)
            .Where(e => e.Status == ExecutionStatus.TermineSucces
                || e.Status == ExecutionStatus.TermineAvecAvertissements
                || e.Status == ExecutionStatus.Echec
                || e.Status == ExecutionStatus.Annule);
        if (envId.HasValue) query = query.Where(e => e.EnvironmentId == envId.Value);

        var tous = await query.Select(e => e.Status).ToListAsync(ct);
        if (tous.Count == 0) return (null, 0);
        var succes = tous.Count(s => s == ExecutionStatus.TermineSucces);
        decimal pct = Math.Round(100m * succes / tous.Count, 2);
        return (pct, tous.Count);
    }

    private static async Task<(decimal? Valeur, long TailleEchantillon)> ValeurP1EvitesAprAsync(
        N4SentinelDbContext db, Guid? envId, DateTimeOffset debut, DateTimeOffset fin, CancellationToken ct)
    {
        var query = db.PreventiveRecommendedActions.AsNoTracking()
            .Where(a => a.APRStatus == AprStatus.Resolue)
            .Where(a => a.ModifiedAt >= debut && a.ModifiedAt < fin);
        if (envId.HasValue) query = query.Where(a => a.EnvironmentId == envId.Value);

        var nb = await query.CountAsync(ct);
        return (nb, nb);
    }

    private static async Task<(decimal? Valeur, long TailleEchantillon)> ValeurIncidentsP1Async(
        N4SentinelDbContext db, Guid? envId, DateTimeOffset debut, DateTimeOffset fin, CancellationToken ct)
    {
        var query = db.CapitalizedIncidents.AsNoTracking()
            .Where(i => i.ClosedAt.HasValue && i.ClosedAt.Value >= debut && i.ClosedAt.Value < fin)
            .Where(i => i.DeclaredSeverity == CriticalityLevel.Elevee
                     || i.DeclaredSeverity == CriticalityLevel.Critique);
        if (envId.HasValue) query = query.Where(i => i.EnvironmentId == envId.Value);

        var nb = await query.CountAsync(ct);
        return (nb, nb);
    }

    private static async Task<(decimal? Valeur, long TailleEchantillon)> ValeurRgCompliancePctAsync(
        N4SentinelDbContext db, Guid? envId, DateTimeOffset debut, DateTimeOffset fin, CancellationToken ct)
    {
        var query = db.CapitalizedIncidents.AsNoTracking()
            .Where(i => i.ClosedAt.HasValue && i.ClosedAt.Value >= debut && i.ClosedAt.Value < fin);
        if (envId.HasValue) query = query.Where(i => i.EnvironmentId == envId.Value);

        var tous = await query.Select(i => i.MainRootCauseCode).ToListAsync(ct);
        if (tous.Count == 0) return (null, 0);
        var bienClasses = tous.Count(c => !string.IsNullOrWhiteSpace(c) && c != WorkflowPreCheckCategory.Autre.ToString());
        decimal pct = Math.Round(100m * bienClasses / tous.Count, 2);
        return (pct, tous.Count);
    }

    // -----------------------------------------------------------------------
    // Persistance Upsert par (ReportingPeriod, EnvId, MetricCode) + tendance
    // -----------------------------------------------------------------------
    private static async Task CalculerEtEnregistrerUneMetriqueAsync(
        N4SentinelDbContext db,
        Guid? envId,
        string libellePeriode,
        string code,
        string label,
        BusinessMetricAggregation agregation,
        decimal? targetFixedValue,
        Func<Task<(decimal? Valeur, long TailleEchantillon)>> calcul,
        Periode periode,
        bool plusHautEstMieux = false,
        CancellationToken ct = default)
    {
        var (valeur, sample) = await calcul();
        var maintenant = DateTimeOffset.UtcNow;

        var existant = await db.BusinessSuccessMetrics
            .FirstOrDefaultAsync(m =>
                m.ReportingPeriod == libellePeriode
                && m.MetricCode == code
                && ((envId.HasValue && m.EnvironmentId == envId.Value)
                    || (!envId.HasValue && m.EnvironmentId == null)),
                ct);

        decimal? tendance = null;
        if (valeur.HasValue && existant?.NumericValueAvgOrSum is decimal ancienne
            && ancienne != 0)
        {
            tendance = Math.Round(100m * (valeur.Value - ancienne) / Math.Abs(ancienne), 2);
        }

        bool targetRatee;
        if (!valeur.HasValue || !targetFixedValue.HasValue) targetRatee = false;
        else
        {
            targetRatee = plusHautEstMieux
                ? valeur.Value < targetFixedValue.Value
                : valeur.Value > targetFixedValue.Value;
        }

        if (existant is null)
        {
            db.BusinessSuccessMetrics.Add(new BusinessSuccessMetric
            {
                Id = Guid.NewGuid(),
                ReportingPeriod = libellePeriode,
                EnvironmentId = envId,
                MetricCode = code,
                MetricLabel = label,
                NumericValueAvgOrSum = valeur,
                TargetFixedValue = targetFixedValue,
                AggregationKind = agregation,
                SampleSizeCount = sample,
                CalculatedAt = maintenant,
                IsTargetMissed = targetRatee,
                TrendVsPreviousPeriodPct = tendance,
                CreatedAt = maintenant,
            });
        }
        else
        {
            existant.NumericValueAvgOrSum = valeur;
            existant.TargetFixedValue = targetFixedValue;
            existant.AggregationKind = agregation;
            existant.SampleSizeCount = sample;
            existant.CalculatedAt = maintenant;
            existant.IsTargetMissed = targetRatee;
            existant.TrendVsPreviousPeriodPct = tendance;
            existant.ModifiedAt = maintenant;
        }
        _ = periode;
        await db.SaveChangesAsync(ct);
    }

    // -----------------------------------------------------------------------
    // Périodes / Bornes ISO
    // -----------------------------------------------------------------------
    public static string PeriodeVersCle(Periode p, DateTimeOffset dt)
    {
        var d = dt.Date;
        return p switch
        {
            Periode.Jour => $"Jour-{d:yyyy-MM-dd}",
            Periode.Semaine => $"Sem-{IsoSemaine(d)}",
            Periode.Mois => $"Mois-{d:yyyy-MM}",
            _ => $"Periode-{d:yyyy-MM-dd}",
        };
    }

    public static (DateTimeOffset Debut, DateTimeOffset Fin) PeriodeVersBornes(Periode p, DateTimeOffset dt)
    {
        var d = dt.Date;
        return p switch
        {
            Periode.Jour => (d.Date, d.Date.AddDays(1)),
            Periode.Semaine => BornesIsoSemaine(d),
            Periode.Mois => (new DateTime(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc),
                             new DateTime(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1)),
            _ => (d.Date, d.Date.AddDays(1)),
        };
    }

    private static string IsoSemaine(DateTime d)
    {
        // ISO 8601 — semaine commence lundi ; semaine 1 = 1er jeudi de l'année.
        var day = System.Globalization.CultureInfo.InvariantCulture.Calendar
            .GetWeekOfYear(d, System.Globalization.CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
        int annee = d.Year;
        if (day >= 52 && d.Month == 1) annee--;
        else if (day == 1 && d.Month == 12) annee++;
        return $"{annee}-W{day:D2}";
    }

    private static (DateTimeOffset Debut, DateTimeOffset Fin) BornesIsoSemaine(DateTime d)
    {
        var diff = (7 + (d.DayOfWeek - DayOfWeek.Monday)) % 7;
        var lundi = d.AddDays(-1 * diff).Date;
        return (new DateTime(lundi.Year, lundi.Month, lundi.Day, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(lundi.Year, lundi.Month, lundi.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(7));
    }
}
