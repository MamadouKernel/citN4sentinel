namespace N4Sentinel.Domain.Scheduling;

/// <summary>
/// P6 — Planification récurrente d'un workflow N4.
///
/// Une planification ne pilote JAMAIS les composants elle-même : elle crée une
/// exécution (PrepareAsync) et optionnellement l'approuve. Le moteur
/// d'orchestration existant (<see cref="OrchestrationEngine"/>) prend ensuite
/// le relais — séparer le planificateur de l'exécuteur garantit que la
/// non-disponibilité ponctuelle du premier ne compromet pas la réactivité du
/// second.
///
/// Toutes les heures sont stockées en LOCAL (TimeZoneId de l'environnement) :
/// une maintenance 22h00 doit rester 22h00, hiver comme été, sans
/// être décalée silencieusement d'une heure deux fois par an.
/// TimeZone par défaut si non spécifié : UTC+00 (GMT).
/// </summary>
public class ExecutionSchedule : AuditableEntity
{
    public Guid EnvironmentId { get; set; }
    public N4Environment? Environment { get; set; }

    public Guid WorkflowId { get; set; }
    public Workflow? Workflow { get; set; }

    /// <summary>Code unique PAR ENVIRONNEMENT (ex. « NIGHTLY-RESTART-BRIDGE »).</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public ScheduleKind Kind { get; set; }

    /// <summary>
    /// Heure de déclenchement, en temps LOCAL de l'environnement (pas UTC).
    /// Pour CronExpression, cette propriété n'est utilisée QUE comme repère
    /// d'affichage humain — elle ne pilote pas réellement le déclenchement.
    /// </summary>
    public TimeSpan TimeOfDay { get; set; }

    /// <summary>
    /// Masque jours de semaine pour <see cref="ScheduleKind.HebdoJoursChoisis"/>.
    /// 0x01 = Lundi (DayOfWeek.Monday → 1), 0x02 = Mardi… 0x40 = Dimanche.
    /// DayOfWeek Sunday = 0 → bit 6 = 0x40.
    /// </summary>
    public ushort WeeklyDayOfWeekMask { get; set; } = 0x001F; // lun..ven par défaut

    /// <summary>Pour MensuelJoursPrecis : liste des jours numériques 1..31 (dé-sérialisée en JSON).</summary>
    public List<int> MonthlyDays { get; set; } = [];

    /// <summary>Pour CronExpression : expression standard 5 ou 6 champs (minute heure jour mois jour-semaine [année]).</summary>
    public string? CronExpression { get; set; }

    public ScheduleStatus Status { get; set; } = ScheduleStatus.Actif;

    /// <summary>Identifiant qui sera utilisé comme RequestedBy lors du PrepareAsync automatique.</summary>
    public string RanAs { get; set; } = string.Empty;

    /// <summary>Motif récurrent reproduit comme Reason sur chaque nouvelle exécution.</summary>
    public string? ReasonTemplate { get; set; }

    /// <summary>Préfixe ticket recopié sur chaque exécution (la référence finale peut être post-complétée).</summary>
    public string? TicketTemplate { get; set; }

    /// <summary>
    /// Vrai → Approuve immédiatement après la préparation si le workflow
    /// l'autorise (RequiresApproval=false OU requiert une seule approbation
    /// et que RanAs est distinct du créateur du workflow).
    /// Faux → l'exécution reste EnAttenteApprobation en attente d'un humain.
    /// </summary>
    public bool AutoApprove { get; set; }

    /// <summary>Date de début de validité (incluse). Null = dès maintenant.</summary>
    public DateTimeOffset? ValidFrom { get; set; }

    /// <summary>Date de fin de validité (incluse). Null = jamais expiré.</summary>
    public DateTimeOffset? ValidUntil { get; set; }

    public DateTimeOffset? LastTriggeredAt { get; set; }
    public DateTimeOffset? NextPlannedAt { get; set; }

    /// <summary>Vrai par défaut : si le même workflow est déjà en cours sur cet environnement, SKIP le déclenchement.</summary>
    public bool SkipIfActiveAlready { get; set; } = true;
}
