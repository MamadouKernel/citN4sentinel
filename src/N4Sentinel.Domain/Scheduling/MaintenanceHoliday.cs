namespace N4Sentinel.Domain.Scheduling;

/// <summary>
/// P6 — Jour exclu du calendrier de maintenance pour un environnement.
///
/// Utilisé par le planificateur pour SKIP les maintenances planifiées les
/// jours fériés, périodes de solde, pics d'activité, etc. Une date est
/// considérée comme valide par environnement (pas global) — une fermeture
/// terminale d'un port spécifique n'empêche pas les autres environnements
/// de lancer leurs routines habituelles.
/// </summary>
public class MaintenanceHoliday : AuditableEntity
{
    public Guid EnvironmentId { get; set; }
    public N4Environment? Environment { get; set; }

    /// <summary>Date entière (horaire ignoré) à exclure, en temps LOCAL.</summary>
    public DateOnly Date { get; set; }

    /// <summary>Raison affichable : « Férié 14 juillet », « Pics activité Noël », « Découverte environnements »…</summary>
    public string Reason { get; set; } = string.Empty;
}
