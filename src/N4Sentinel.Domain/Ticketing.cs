namespace N4Sentinel.Domain;

/// <summary>
/// Ticket d'incident exploité par l'équipe DSI CIT (S3-09).
///
/// Un ticket est ouvert par l'une des voies suivantes :
///   - Manuellement depuis la page Admin/Ticketing par un ExploitN4.
///   - Automatiquement par le moteur de diagnostic quand une signature du
///     TOP 10 des causes P1 Navis est identifiée (DiagnosticSession clos avec
///     sévérité critique).
///   - Automatiquement par les Actions Préventives Recommandées (APR).
///   - Automatiquement par le moniteur EDI WatchFolder après 3 échecs consécutifs.
///
/// Le Ticket est un objet métier « autonome » : il n'a pas besoin d'une
/// intégration ServiceNow / Jira en V1 — on produit des exports CSV/JSON pour
/// import manuel dans l'outil de la DSI.
/// </summary>
public class Ticket : AuditableEntity
{
    /// <summary>Identifiant métier lisible (ex : TIC-2026-00001).</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Titre résumant l'incident — une ligne maximum.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Description détaillée, contexte, états constatés.</summary>
    public string Description { get; set; } = string.Empty;

    public TicketStatus Status { get; set; } = TicketStatus.Nouveau;
    public TicketPriority Priority { get; set; } = TicketPriority.P3Normal;
    public TicketSeverity Severity { get; set; } = TicketSeverity.Mineur;
    public TicketSource Source { get; set; } = TicketSource.Manuel;

    /// <summary>Environnement Navis concerné par l'incident, le cas échéant.</summary>
    public Guid? EnvironmentId { get; set; }

    /// <summary>Signature diagnostic à l'origine du ticket, si ouvert automatiquement.</summary>
    public Guid? DiagnosticSignatureId { get; set; }

    /// <summary>Exécution de workflow liée, le cas échéant.</summary>
    public string? OperationCorrelationId { get; set; }

    /// <summary>Identifiant de l'APR à l'origine, le cas échéant.</summary>
    public Guid? AprId { get; set; }

    /// <summary>Acteur (personne / file d'attente / équipe) responsable du ticket.</summary>
    public string? AssignedTo { get; set; }

    /// <summary>Identifiant externe dans l'outil exploitant (ServiceNow / Jira / GLPI), synchronisation manuelle V1.</summary>
    public string? ExternalTicketId { get; set; }

    public DateTimeOffset OpenedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt { get; set; }
    public string? ClosureReason { get; set; }

    /// <summary>Auteur à l'ouverture (compte / systeme). Nommé CreatedBy pour l'UI Admin.</summary>
    public string? OpenedBy { get; set; }

    public List<TicketComment> Comments { get; set; } = [];
}

/// <summary>Commentaire ajouté à un ticket par un acteur humain ou le moteur.</summary>
public class TicketComment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TicketId { get; set; }

    /// <summary>Auteur du commentaire (nom affichable / compte).</summary>
    public string WrittenBy { get; set; } = string.Empty;

    /// <summary>Alias compatibilité UI (Author = WrittenBy).</summary>
    public string Author { get => WrittenBy; set => WrittenBy = value; }

    /// <summary>Corps du commentaire.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Alias compatibilité UI (Text = Body).</summary>
    public string Text { get => Body; set => Body = value; }

    public DateTimeOffset WrittenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Si vrai : commentaire privé (équipe DSI uniquement, non diffusé à l'exploitant).</summary>
    public bool IsInternal { get; set; }

    /// <summary>Origine du commentaire (Interne Sentinel / Externe DSI via webhook).</summary>
    public TicketCommentSource Source { get; set; } = TicketCommentSource.Interne;

    public Ticket? Ticket { get; set; }
}

/// <summary>Origine du commentaire de ticket (flux bidirectionnel PB-25).</summary>
public enum TicketCommentSource : byte
{
    Interne = 0,
    Externe = 1
}
