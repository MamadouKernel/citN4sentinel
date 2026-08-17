namespace N4Sentinel.Domain;

/// <summary>
/// Politique de rétention (FR-079, SEC-009), distincte pour les journaux de
/// diagnostic, l'historique d'exécution (source des rapports) et le journal
/// d'audit. Ligne unique — voir <see cref="Infrastructure.Retention.RetentionPolicyService"/>.
///
/// L'AUDIT N'EST JAMAIS PURGÉ PAR CETTE POLITIQUE. Le journal d'audit est
/// immuable par construction (déclencheur SQL INSTEAD OF UPDATE, DELETE —
/// SEC-008) : <see cref="AuditRetentionDays"/> n'est qu'une référence de
/// conformité affichée à l'exploitant, jamais un déclencheur de suppression.
/// </summary>
public class RetentionPolicy : AuditableEntity
{
    /// <summary>
    /// Journaux de diagnostic (LogSource/LogFinding) : purge les extraits
    /// masqués des sessions CLÔTURÉES plus anciennes que ce délai. La session
    /// elle-même (titre, verdict, phases) est conservée : seuls les extraits
    /// de journal le sont pas.
    /// </summary>
    public int LogsRetentionDays { get; set; } = 180;

    /// <summary>
    /// Historique d'exécution (WorkflowExecution), source des rapports
    /// d'opération : purge les exécutions TERMINÉES plus anciennes que ce
    /// délai. Une exécution en cours n'est jamais purgée.
    /// </summary>
    public int ReportsRetentionDays { get; set; } = 365;

    /// <summary>Référence de conformité affichée, sans effet de purge (voir ci-dessus).</summary>
    public int AuditRetentionDays { get; set; } = 1825;

    /// <summary>
    /// Relevés de supervision (ComponentSignal, §3.18) : un par composant et
    /// par passage (~30 s), volume élevé — délai plus court par défaut que
    /// les journaux de diagnostic.
    /// </summary>
    public int SignalsRetentionDays { get; set; } = 30;
}
