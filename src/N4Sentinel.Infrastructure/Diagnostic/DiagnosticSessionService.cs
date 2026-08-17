using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Diagnostic;

/// <summary>
/// Cycle de vie d'une session de diagnostic (FR-062 à FR-069).
///
/// UNE SESSION EST UNE INVESTIGATION, PAS UNE LECTURE. Elle porte ce que l'on
/// cherche, ce qui a été regardé, ce qui a été trouvé, et ce qui n'a pas pu
/// l'être. C'est cette dernière partie qui distingue un diagnostic d'une simple
/// consultation de journal.
/// </summary>
public sealed class DiagnosticSessionService(
    IDbContextFactory<N4SentinelDbContext> dbFactory, LogAnalysisService logAnalysis, IAuditWriter auditWriter)
{
    /// <summary>Au-delà, une référence est jugée trop ancienne pour être utilisée sans avertissement (FR-066).</summary>
    private static readonly TimeSpan AgeMaximalReference = TimeSpan.FromDays(90);


    public async Task<Guid> CreateAsync(
        Guid environmentId, string title, string? reason, string? ticket, string requestedBy,
        DateTimeOffset? windowStart, DateTimeOffset? windowEnd, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var environnement = await db.Environments.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == environmentId, ct);

        var session = new DiagnosticSession
        {
            EnvironmentId = environmentId,
            EnvironmentCode = environnement?.Code ?? "INCONNU",
            Title = string.IsNullOrWhiteSpace(title) ? "Diagnostic" : title.Trim(),
            Reason = reason,
            TicketReference = ticket,
            RequestedBy = requestedBy,
            WindowStart = windowStart,
            WindowEnd = windowEnd
        };

        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session.Id;
    }

    /// <summary>
    /// FR-068 : ouvre une session directement depuis une alerte, en
    /// identifiant le composant concerné — ou, pour une alerte de portée
    /// environnement, les composants candidats — et en lançant la collecte
    /// aussitôt. Ne remplace pas la sélection manuelle : elle reste possible
    /// depuis l'écran de diagnostic, notamment quand aucun candidat ne peut
    /// être déterminé.
    /// </summary>
    public async Task<CreateFromAlertResult> CreateFromAlertAsync(
        Guid alertId, string requestedBy, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var alert = await db.Alerts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == alertId, ct);
        if (alert is null) return CreateFromAlertResult.Failed("Alerte introuvable.");

        var environnement = await db.Environments.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == alert.EnvironmentId, ct);

        var session = new DiagnosticSession
        {
            EnvironmentId = alert.EnvironmentId,
            EnvironmentCode = environnement?.Code ?? "INCONNU",
            Title = $"Diagnostic — {alert.Title}",
            Reason = $"Ouvert automatiquement depuis l'alerte « {alert.Title} » (FR-068). {alert.Detail}",
            RequestedBy = requestedBy,
            // Marge avant le premier signalement : la cause precede
            // generalement l'alerte elle-meme, pas l'inverse.
            WindowStart = alert.FirstOccurredAt.AddMinutes(-30),
            WindowEnd = DateTimeOffset.UtcNow,
            SourceAlertId = alert.Id
        };

        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);

        var candidats = await IdentifierComposantsCandidatsAsync(db, alert, ct);

        foreach (var composantId in candidats)
            await logAnalysis.CollectFromServerAsync(session.Id, composantId, ct);

        if (candidats.Count > 0)
            await logAnalysis.ConcludeAsync(session.Id, ct);

        return CreateFromAlertResult.Ok(session.Id, candidats.Count);
    }

    /// <summary>
    /// FR-059J : ouvre une session de diagnostic rattachée à UN fichier EDI
    /// précis, pas seulement au composant qui l'héberge. Le nom du fichier,
    /// son partenaire et son état constatent la cible dans le motif de la
    /// session — c'est ce lien-là qui manquait : un clic « Diagnostiquer »
    /// depuis une alerte EDI ouvrait un diagnostic générique du composant,
    /// sans jamais dire pour quel fichier.
    /// </summary>
    public async Task<CreateFromAlertResult> CreateFromEdiFileAsync(
        EdiFile fichier, Guid componentId, string requestedBy, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var composant = await db.Components.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == componentId, ct);
        if (composant is null) return CreateFromAlertResult.Failed("Composant introuvable.");

        var session = new DiagnosticSession
        {
            EnvironmentId = composant.EnvironmentId,
            EnvironmentCode = (await db.Environments.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == composant.EnvironmentId, ct))?.Code ?? "INCONNU",
            Title = $"Diagnostic EDI — {fichier.FileName}",
            Reason = $"Fichier « {fichier.FileName} » (partenaire : {fichier.Partner ?? "non classé"}, "
                   + $"statut : {fichier.Status}, âge : {Formater(fichier.Age)}"
                   + (fichier.ConsecutiveRejections > 0
                        ? $", {fichier.ConsecutiveRejections} échec(s) consécutif(s)"
                        : string.Empty)
                   + $") sur « {composant.LogicalName} ».",
            RequestedBy = requestedBy,
            WindowStart = fichier.FirstSeenAt.AddMinutes(-30),
            WindowEnd = DateTimeOffset.UtcNow
        };

        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);

        await logAnalysis.CollectFromServerAsync(session.Id, componentId, ct);
        await logAnalysis.ConcludeAsync(session.Id, ct);

        return CreateFromAlertResult.Ok(session.Id, 1);
    }

    private static string Formater(TimeSpan d) =>
        d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min" : $"{(int)d.TotalHours} h";

    /// <summary>
    /// Une alerte de composant designe directement sa cible. Une alerte de
    /// portee environnement (ex. conflit de role Center/Standby) n'en
    /// designe aucune a elle seule : les candidats sont deduits de sa
    /// nature, jamais devines au hasard — une liste vide est un resultat
    /// honnete quand aucune regle ne s'applique, pas une erreur a masquer.
    /// </summary>
    private static async Task<List<Guid>> IdentifierComposantsCandidatsAsync(
        N4SentinelDbContext db, Alert alert, CancellationToken ct)
    {
        if (alert.ComponentId is { } id) return [id];

        if (alert.Kind == AlertKind.ConflitRoleActifCenter)
        {
            return await db.Components.AsNoTracking()
                .Where(c => c.EnvironmentId == alert.EnvironmentId
                            && (c.Role == ComponentRole.CenterNode || c.Role == ComponentRole.StandbyCenterNode))
                .Select(c => c.Id)
                .ToListAsync(ct);
        }

        return [];
    }

    public async Task<DiagnosticSession?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Sessions
            .AsNoTracking()
            .Include(s => s.Sources)
            .Include(s => s.Findings)
            .Include(s => s.Hypotheses.OrderBy(h => h.Rank))
            .Include(s => s.PhaseTransitions.OrderBy(t => t.EnteredAt))
            .Include(s => s.ExternalActions.OrderBy(a => a.OccurredAt))
            .FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    /// <summary>
    /// §3.10.1/§3.19 : déclare une action manuelle effectuée HORS de
    /// N4 Sentinel pendant le traitement — l'application ne peut pas la
    /// détecter, mais le texte accepte explicitement la déclaration comme
    /// mécanisme équivalent. Intégrée à la chronologie et auditée.
    /// </summary>
    public async Task<string?> DeclareExternalActionAsync(
        Guid sessionId, string description, DateTimeOffset occurredAt, string declaredBy,
        Guid? componentId, string? componentName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(description)) return "Décrivez ce qui a été fait.";

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return "Session introuvable.";

        db.ExternalActionDeclarations.Add(new ExternalActionDeclaration
        {
            EnvironmentId = session.EnvironmentId,
            DiagnosticSessionId = sessionId,
            ComponentId = componentId,
            ComponentName = componentName,
            Description = description.Trim(),
            OccurredAt = occurredAt,
            DeclaredBy = declaredBy
        });

        await db.SaveChangesAsync(ct);

        await auditWriter.WriteAsync(
            AuditAction.Creation, AuditOutcome.Succes, declaredBy,
            entityType: nameof(ExternalActionDeclaration), entityId: sessionId.ToString(),
            entityLabel: session.Title, environmentId: session.EnvironmentId,
            detail: $"Action externe déclarée : {description.Trim()}", ct: ct);

        return null;
    }

    /// <summary>
    /// §3.10.1 : fait avancer — ou revenir — la session vers une autre phase du
    /// cycle. Chaque appel AJOUTE une entrée à <see cref="DiagnosticSession.PhaseTransitions"/>,
    /// il n'en écrase aucune : un retour vers une phase déjà visitée (nouvel
    /// élément qui invalide une hypothèse, par exemple) est une reprise
    /// légitime, pas une correction d'erreur à faire disparaître.
    /// </summary>
    public async Task<string?> AdvancePhaseAsync(
        Guid sessionId, DiagnosticPhase phase, string actor, string? note,
        string? escaladeVers = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return "Session introuvable.";
        if (string.IsNullOrWhiteSpace(actor)) return "Acteur manquant.";

        if (phase == DiagnosticPhase.ClotureEtCapitalisation)
        {
            if (string.IsNullOrWhiteSpace(note))
                return "La clôture exige de préciser ce qui a été vérifié pour confirmer le retour à un état stable.";

            // §3.10.1 : un diagnostic ANALYSÉ qui ne conclut à aucune cause
            // établie ne se clôture pas silencieusement — quelqu'un doit
            // avoir été prévenu. Une session jamais analysée (résolue par
            // une action directe, sans passer par le moteur de corrélation)
            // n'est pas concernée : son verdict par défaut ne reflète aucune
            // tentative de diagnostic restée sans réponse.
            if (session.HasBeenAnalysed && session.VerdictEstInconcluant
                && string.IsNullOrWhiteSpace(session.EscalatedTo)
                && string.IsNullOrWhiteSpace(escaladeVers))
                return "Ce diagnostic n'aboutit à aucune cause établie. Précisez à qui il est escaladé "
                     + "(support Navis, équipe Infrastructure...) avant de clôturer, ou complétez d'abord "
                     + "l'analyse jusqu'à une cause établie.";

            if (!string.IsNullOrWhiteSpace(escaladeVers) && string.IsNullOrWhiteSpace(session.EscalatedTo))
            {
                session.EscalatedTo = escaladeVers.Trim();
                session.EscalatedAt = DateTimeOffset.UtcNow;
                session.EscalatedBy = actor;
            }
        }

        session.Phase = phase;

        db.PhaseTransitions.Add(new DiagnosticPhaseTransition
        {
            SessionId = sessionId,
            Phase = phase,
            EnteredBy = actor,
            EnteredAt = DateTimeOffset.UtcNow,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
        });

        await db.SaveChangesAsync(ct);

        await auditWriter.WriteAsync(
            AuditAction.ChangementDeStatut, AuditOutcome.Succes, actor,
            entityType: nameof(DiagnosticSession), entityId: session.Id.ToString(), entityLabel: session.Title,
            environmentId: session.EnvironmentId, detail: $"Phase → {phase}", ct: ct);

        return null;
    }

    public async Task<List<DiagnosticSession>> GetRecentAsync(
        Guid? environmentId, int count = 40, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var requete = db.Sessions.AsNoTracking().AsQueryable();
        if (environmentId is not null) requete = requete.Where(s => s.EnvironmentId == environmentId);

        return await requete.OrderByDescending(s => s.CreatedAt).Take(count).ToListAsync(ct);
    }

    /// <summary>FR-066 : sessions pouvant servir de référence — marquées saines explicitement, jamais devinées.</summary>
    public async Task<List<DiagnosticSession>> GetBaselineCandidatesAsync(
        Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Sessions.AsNoTracking()
            .Where(s => s.EnvironmentId == environmentId && s.IsReferenceBaseline)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// FR-066 : marque ou démarque une session comme référence saine. C'est
    /// une affirmation humaine — l'application ne peut pas déduire seule
    /// qu'une période a été « validée saine ».
    /// </summary>
    public async Task<string?> MarkAsBaselineAsync(Guid sessionId, bool isBaseline, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return "Session introuvable.";

        if (isBaseline && !session.HasBeenAnalysed)
            return "Une session doit être analysée avant de pouvoir servir de référence.";

        session.IsReferenceBaseline = isBaseline;
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>FR-066 : choisit (ou retire) la session de référence utilisée pour la comparaison.</summary>
    public async Task<string?> SetReferenceAsync(
        Guid sessionId, Guid? referenceSessionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return "Session introuvable.";

        if (referenceSessionId is { } refId)
        {
            var reference = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == refId, ct);
            if (reference is null) return "Session de référence introuvable.";
            if (!reference.IsReferenceBaseline)
                return "Cette session n'est pas marquée comme référence saine — marquez-la explicitement avant de vous en servir de comparaison.";
        }

        session.ReferenceSessionId = referenceSessionId;
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// FR-066 : anomalies présentes dans la session courante mais absentes de
    /// la référence, avec l'âge et la complétude de cette dernière rendus
    /// visibles — une référence ancienne ou incomplète ne doit jamais être
    /// utilisée sans avertissement.
    /// </summary>
    public async Task<ReferenceComparison?> CompareToReferenceAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await GetAsync(sessionId, ct);
        if (session?.ReferenceSessionId is not { } refId) return null;

        var reference = await GetAsync(refId, ct);
        if (reference is null) return null;

        var motifsReference = reference.Findings
            .Select(f => f.SignatureCode ?? f.Title)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var nouveaux = session.Findings
            .Where(f => !motifsReference.Contains(f.SignatureCode ?? f.Title))
            .OrderByDescending(f => f.Severity)
            .ToList();

        var age = DateTimeOffset.UtcNow - reference.CreatedAt;

        return new ReferenceComparison(
            Reference: reference,
            NewFindings: nouveaux,
            ReferenceAgeDays: (int)age.TotalDays,
            IsStale: age > AgeMaximalReference,
            ReferenceScopeIncomplete: reference.Sources.Any(s => !s.Succeeded));
    }

    /// <summary>FR-066 : choisit le mode de comparaison actif pour cette session.</summary>
    public async Task<string?> SetReferenceKindAsync(Guid sessionId, ReferenceKind kind, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return "Session introuvable.";

        session.ReferenceKind = kind;
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>FR-066 : exécutions récentes terminées avec succès dans cet environnement — candidates pour le mode ExecutionReussie, jamais choisies automatiquement.</summary>
    public async Task<List<WorkflowExecution>> GetSuccessfulExecutionCandidatesAsync(
        Guid environmentId, int count = 20, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Executions.AsNoTracking()
            .Where(e => e.EnvironmentId == environmentId && e.Status == ExecutionStatus.TermineSucces)
            .OrderByDescending(e => e.EndedAt)
            .Take(count)
            .ToListAsync(ct);
    }

    /// <summary>FR-066 : choisit (ou retire) l'exécution de référence pour le mode ExecutionReussie.</summary>
    public async Task<string?> SetReferenceExecutionAsync(Guid sessionId, Guid? executionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return "Session introuvable.";

        if (executionId is { } id)
        {
            var execution = await db.Executions.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
            if (execution is null) return "Exécution introuvable.";
            if (execution.Status != ExecutionStatus.TermineSucces)
                return "Seule une exécution terminée avec succès peut servir de référence.";
            if (execution.EnvironmentId != session.EnvironmentId)
                return "L'exécution de référence doit appartenir au même environnement que le diagnostic.";
        }

        session.ReferenceExecutionId = executionId;
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// FR-066 : compare l'incident à l'exécution de référence choisie —
    /// composants qu'elle a réellement touchés, et ceux qu'elle partage avec
    /// l'incident en cours. Ne rejoue rien, ne recalcule aucune métrique :
    /// affiche ce que cette exécution a réellement fait.
    /// </summary>
    public async Task<ExecutionReferenceComparison?> CompareToSuccessfulExecutionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await GetAsync(sessionId, ct);
        if (session?.ReferenceExecutionId is not { } execId) return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var execution = await db.Executions.AsNoTracking().Include(e => e.Steps)
            .FirstOrDefaultAsync(e => e.Id == execId, ct);
        if (execution is null) return null;

        var composantsExecution = execution.Steps
            .Where(s => s.ComponentName is not null)
            .Select(s => s.ComponentName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var composantsIncident = session.Sources
            .Where(s => s.ComponentName is not null)
            .Select(s => s.ComponentName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var partages = composantsExecution.Where(composantsIncident.Contains).ToList();

        var reference = execution.EndedAt ?? execution.StartedAt ?? execution.CreatedAt;
        var age = DateTimeOffset.UtcNow - reference;

        return new ExecutionReferenceComparison(
            ExecutionId: execution.Id,
            WorkflowName: execution.WorkflowName,
            WorkflowVersion: execution.WorkflowVersion,
            StartedAt: execution.StartedAt,
            EndedAt: execution.EndedAt,
            Duration: execution.Duration,
            ComponentNames: composantsExecution,
            SharedIncidentComponentNames: partages,
            ReferenceAgeDays: (int)age.TotalDays,
            IsStale: age > AgeMaximalReference);
    }

    /// <summary>FR-066 : composants réellement présents dans cette session (un journal en a été collecté) — candidats pour les modes ValeursHabituellesComposant et NoeudPair.</summary>
    public async Task<List<(Guid ComponentId, string ComponentName)>> GetSessionComponentsAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var lignes = await db.Sources.AsNoTracking()
            .Where(s => s.SessionId == sessionId && s.ComponentId != null)
            .Select(s => new { Id = s.ComponentId!.Value, s.ComponentName })
            .Distinct()
            .ToListAsync(ct);

        return lignes.Select(l => (l.Id, l.ComponentName ?? "—")).ToList();
    }

    /// <summary>FR-066 : autres composants du même rôle, dans le même environnement — candidats « nœud pair », jamais choisis automatiquement.</summary>
    public async Task<List<N4Component>> GetPeerCandidatesAsync(Guid componentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var composant = await db.Components.AsNoTracking().FirstOrDefaultAsync(c => c.Id == componentId, ct);
        if (composant is null) return [];

        return await db.Components.AsNoTracking()
            .Where(c => c.EnvironmentId == composant.EnvironmentId && c.Role == composant.Role && c.Id != componentId)
            .OrderBy(c => c.LogicalName)
            .ToListAsync(ct);
    }

    /// <summary>FR-066 : choisit (ou retire) le composant de référence — son sens dépend de <see cref="ReferenceKind"/>.</summary>
    public async Task<string?> SetReferenceComponentAsync(Guid sessionId, Guid? componentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return "Session introuvable.";

        if (componentId is { } id && !await db.Components.AnyAsync(c => c.Id == id, ct))
            return "Composant introuvable.";

        session.ReferenceComponentId = componentId;
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Fenêtre retenue pour situer l'incident dans le temps quand la session n'en déclare pas explicitement une.</summary>
    private static (DateTimeOffset Start, DateTimeOffset End) FenetreIncident(DiagnosticSession session) =>
        (session.WindowStart ?? session.CreatedAt.AddMinutes(-30), session.WindowEnd ?? session.CreatedAt.AddMinutes(30));

    /// <summary>En dessous, un historique de signaux est jugé trop court pour en tirer une habitude (FR-066).</summary>
    private const int SeuilEchantillonMinimal = 3;

    /// <summary>
    /// FR-066 : compare, pour chaque type de signal, la valeur la plus
    /// fréquemment observée dans <paramref name="reference"/> à la dernière
    /// valeur observée dans <paramref name="observe"/>. Un type de signal
    /// absent de la référence est ignoré — comparer à une habitude
    /// inexistante n'en est pas une.
    /// </summary>
    private static List<SignalBaselineEntry> ComparerSignaux(List<ComponentSignal> reference, List<ComponentSignal> observe)
    {
        var types = reference.Select(s => s.SignalType)
            .Union(observe.Select(s => s.SignalType))
            .Distinct()
            .OrderBy(t => t);

        var resultat = new List<SignalBaselineEntry>();
        foreach (var type in types)
        {
            var ref_ = reference.Where(s => s.SignalType == type).ToList();
            if (ref_.Count == 0) continue;

            var valeurRef = ref_.GroupBy(s => s.Value).OrderByDescending(g => g.Count()).First().Key;
            var dernierObserve = observe.Where(s => s.SignalType == type).OrderByDescending(s => s.CapturedAt).FirstOrDefault();

            resultat.Add(new SignalBaselineEntry(
                SignalType: type,
                ReferenceValue: valeurRef,
                ReferenceSampleCount: ref_.Count,
                ReferenceOldestAt: ref_.Min(s => s.CapturedAt),
                ReferenceNewestAt: ref_.Max(s => s.CapturedAt),
                ObservedValue: dernierObserve?.Value,
                Differs: dernierObserve is not null && !string.Equals(dernierObserve.Value, valeurRef, StringComparison.OrdinalIgnoreCase)));
        }
        return resultat;
    }

    /// <summary>
    /// FR-066 : compare les signaux capturés PENDANT la fenêtre de l'incident
    /// à ce que ce même composant affiche HABITUELLEMENT, hors de cette
    /// fenêtre. La valeur habituelle est la plus fréquente sur l'historique
    /// disponible — jamais inventée quand cet historique est trop court,
    /// <see cref="ComponentSignalComparison.IsIncomplete"/> le signale alors.
    /// </summary>
    public async Task<ComponentSignalComparison?> CompareToUsualValuesAsync(Guid sessionId, Guid componentId, CancellationToken ct = default)
    {
        var session = await GetAsync(sessionId, ct);
        if (session is null) return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var composant = await db.Components.AsNoTracking().FirstOrDefaultAsync(c => c.Id == componentId, ct);
        if (composant is null) return null;

        var (debut, fin) = FenetreIncident(session);

        var historique = await db.ComponentSignals.AsNoTracking()
            .Where(s => s.ComponentId == componentId && (s.CapturedAt < debut || s.CapturedAt > fin))
            .ToListAsync(ct);

        var pendant = await db.ComponentSignals.AsNoTracking()
            .Where(s => s.ComponentId == componentId && s.CapturedAt >= debut && s.CapturedAt <= fin)
            .ToListAsync(ct);

        var signaux = ComparerSignaux(historique, pendant);

        return new ComponentSignalComparison(
            ComponentId: componentId,
            ComponentName: composant.LogicalName,
            ReferenceLabel: "Valeur habituelle (hors incident)",
            Signals: signaux,
            IsIncomplete: signaux.Count == 0 || signaux.All(s => s.ReferenceSampleCount < SeuilEchantillonMinimal));
    }

    /// <summary>
    /// FR-066 : compare, sur la MÊME fenêtre que l'incident, les signaux du
    /// composant en cause à ceux d'un autre nœud du même rôle — si le pair
    /// affiche autre chose au même moment, l'anomalie n'est probablement pas
    /// propre au rôle mais à ce nœud précis.
    /// </summary>
    public async Task<ComponentSignalComparison?> CompareToPeerNodeAsync(
        Guid sessionId, Guid subjectComponentId, Guid peerComponentId, CancellationToken ct = default)
    {
        var session = await GetAsync(sessionId, ct);
        if (session is null) return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var peer = await db.Components.AsNoTracking().FirstOrDefaultAsync(c => c.Id == peerComponentId, ct);
        if (peer is null) return null;

        var (debut, fin) = FenetreIncident(session);

        var signauxPair = await db.ComponentSignals.AsNoTracking()
            .Where(s => s.ComponentId == peerComponentId && s.CapturedAt >= debut && s.CapturedAt <= fin)
            .ToListAsync(ct);

        var signauxSujet = await db.ComponentSignals.AsNoTracking()
            .Where(s => s.ComponentId == subjectComponentId && s.CapturedAt >= debut && s.CapturedAt <= fin)
            .ToListAsync(ct);

        var signaux = ComparerSignaux(signauxPair, signauxSujet);

        return new ComponentSignalComparison(
            ComponentId: peerComponentId,
            ComponentName: peer.LogicalName,
            ReferenceLabel: $"Nœud pair « {peer.LogicalName} » (même fenêtre)",
            Signals: signaux,
            IsIncomplete: signaux.Count == 0 || signaux.All(s => s.ReferenceSampleCount < SeuilEchantillonMinimal));
    }

    /// <summary>
    /// FR-061 : aligne les constats de toutes les sources sur une
    /// chronologie commune, ajustée de l'écart d'horloge mesuré à la
    /// collecte de chacune. Une entrée reste marquée incertaine dès que cet
    /// écart est inconnu ou dépasse le seuil d'une seconde — l'ordre affiché
    /// est alors probable, pas garanti, et le dit.
    /// </summary>
    public static List<TimelineEntry> BuildTimeline(DiagnosticSession session)
    {
        var sourcesParId = session.Sources.ToDictionary(s => s.Id);

        return session.Findings
            .Where(f => f.FirstSeenAt is not null)
            .Select(f =>
            {
                sourcesParId.TryGetValue(f.SourceId, out var source);
                var ecart = source?.ClockSkewSecondsAtCollection;
                var brut = f.FirstSeenAt!.Value;
                var ajuste = ecart is { } e ? brut - TimeSpan.FromSeconds(e) : brut;

                return new TimelineEntry(
                    AdjustedTime: ajuste,
                    RawTime: brut,
                    ComponentName: source?.ComponentName ?? "—",
                    HostName: source?.HostName,
                    Title: f.Title,
                    Severity: f.Severity,
                    ClockSkewSeconds: ecart,
                    IsUncertain: ecart is null || Math.Abs(ecart.Value) > 1.0);
            })
            .OrderBy(t => t.AdjustedTime)
            .ToList();
    }

    /// <summary>
    /// FR-073 : évolution temporelle — répartition par heure des PREMIÈRES
    /// occurrences de chaque constat (jamais de chaque occurrence répétée,
    /// dont l'horodatage individuel n'est pas conservé — grouper 40 fois la
    /// même exception en une seule ligne est un choix assumé, voir <see cref="LogFinding"/>).
    /// Construite sur des données réelles, jamais une répartition inventée.
    /// </summary>
    public static List<(DateTimeOffset Heure, int Nombre)> BuildHourlyHistogram(DiagnosticSession session)
    {
        var sourcesParId = session.Sources.ToDictionary(s => s.Id);

        return session.Findings
            .Where(f => f.FirstSeenAt is not null)
            .Select(f =>
            {
                sourcesParId.TryGetValue(f.SourceId, out var source);
                var ecart = source?.ClockSkewSecondsAtCollection;
                var brut = f.FirstSeenAt!.Value;
                var ajuste = ecart is { } e ? brut - TimeSpan.FromSeconds(e) : brut;
                return new DateTimeOffset(ajuste.Year, ajuste.Month, ajuste.Day, ajuste.Hour, 0, 0, ajuste.Offset);
            })
            .GroupBy(h => h)
            .Select(g => (Heure: g.Key, Nombre: g.Count()))
            .OrderBy(x => x.Heure)
            .ToList();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null) return;

        db.Sessions.Remove(session);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Rapport de diagnostic, destiné à être transmis — à l'éditeur, à une
    /// direction, à un auditeur.
    ///
    /// Il énonce ses limites AVANT ses conclusions. Un rapport qui affirme sans
    /// dire ce qu'il n'a pas regardé se fait croire au-delà de ce qu'il vaut,
    /// et se retourne contre son auteur au premier recoupement.
    /// </summary>
    public async Task<string?> BuildMarkdownAsync(Guid id, CancellationToken ct = default)
    {
        var session = await GetAsync(id, ct);
        if (session is null) return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var seuilEtablie = (await db.DiagnosticSettings.AsNoTracking().FirstOrDefaultAsync(ct))
            ?.HypothesisEstablishedThreshold ?? new DiagnosticSettings().HypothesisEstablishedThreshold;

        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"# Diagnostic — {session.Title}");
        sb.AppendLine();
        sb.AppendLine($"**{LibelleVerdict(session.Verdict)}**");
        sb.AppendLine();

        sb.AppendLine("| | |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Environnement | {session.EnvironmentCode} |");
        sb.AppendLine($"| Demandeur | {session.RequestedBy} |");
        sb.AppendLine($"| Objet | {session.Reason ?? "—"} |");
        sb.AppendLine($"| Ticket | {session.TicketReference ?? "—"} |");
        sb.AppendLine($"| Fenêtre | {Plage(session)} |");
        sb.AppendLine($"| Analysé le | {session.AnalysedAt?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "—"} |");
        sb.AppendLine();

        if (session.VerdictExplanation is { Length: > 0 })
        {
            sb.AppendLine("## Verdict");
            sb.AppendLine();
            sb.AppendLine(session.VerdictExplanation);
            sb.AppendLine();
        }

        // --- Sources ---------------------------------------------------------
        sb.AppendLine("## Journaux examinés");
        sb.AppendLine();
        sb.AppendLine("| Journal | Composant | Origine | Lignes | Secrets masqués | État |");
        sb.AppendLine("|---|---|---|---|---|---|");

        foreach (var s in session.Sources)
            sb.AppendLine($"| {Cellule(s.FileName)} | {Cellule(s.ComponentName ?? "—")} "
                        + $"| {(s.Origin == LogOriginKind.CollecteCiblee ? "collecte" : "import")} "
                        + $"| {s.LineCount} | {s.MaskedSecretCount} "
                        + $"| {(s.Succeeded ? (s.Truncated ? "fin de fichier seulement" : "complet") : "**non collecté**")} |");

        sb.AppendLine();

        if (session.Sources.Any(s => !s.Succeeded))
        {
            sb.AppendLine("> **Sources manquantes.** Certains journaux n'ont pas pu être collectés. "
                        + "Leur contenu n'a pas été examiné, et le verdict ne les couvre donc pas.");
            sb.AppendLine();
            foreach (var s in session.Sources.Where(x => !x.Succeeded))
                sb.AppendLine($"- {s.ComponentName ?? s.FileName} : {s.Error}");
            sb.AppendLine();
        }

        // --- Hypothèses ------------------------------------------------------
        if (session.Hypotheses.Count > 0)
        {
            sb.AppendLine("## Hypothèses");
            sb.AppendLine();
            sb.AppendLine("_Ce sont des hypothèses, présentées avec ce sur quoi elles reposent. "
                        + "Elles peuvent être contestées._");
            sb.AppendLine();

            foreach (var h in session.Hypotheses.OrderBy(x => x.Rank))
            {
                sb.AppendLine($"### {h.Rank}. {LogAnalysisService.Libelle(h.Domain)} — confiance {h.Confidence} %"
                            + (h.EstEtablie(seuilEtablie) ? "" : " _(insuffisante pour conclure)_"));
                sb.AppendLine();
                sb.AppendLine(h.Statement);
                sb.AppendLine();
                sb.AppendLine($"- **Sur quoi elle repose.** {h.Evidence}");
                if (h.Recommendation is { Length: > 0 })
                    sb.AppendLine($"- **À vérifier.** {h.Recommendation}");
                sb.AppendLine();
            }
        }

        // --- Constats --------------------------------------------------------
        sb.AppendLine("## Constats");
        sb.AppendLine();

        if (session.Findings.Count == 0)
        {
            sb.AppendLine("_Aucune anomalie relevée dans ce qui a été analysé._");
            sb.AppendLine();
        }
        else
        {
            foreach (var f in session.Findings.OrderByDescending(x => x.Severity)
                                              .ThenByDescending(x => x.OccurrenceCount))
            {
                sb.AppendLine($"### {f.Title}");
                sb.AppendLine();
                sb.AppendLine($"- **Gravité.** {f.Severity} · **Domaine.** {LogAnalysisService.Libelle(f.Domain)}"
                            + (f.SignatureCode is null ? " · _non répertorié au catalogue_" : $" · signature `{f.SignatureCode}`"));
                sb.AppendLine($"- **Occurrences.** {f.OccurrenceCount}, à partir de la ligne {f.FirstLineNumber}"
                            + (f.FirstSeenAt is null ? "" : $", de {f.FirstSeenAt:HH:mm:ss} à {f.LastSeenAt:HH:mm:ss}"));

                if (f.Meaning is { Length: > 0 })
                    sb.AppendLine($"- **Ce que cela signifie.** {f.Meaning}");

                if (f.Remediation is { Length: > 0 })
                    sb.AppendLine($"- **Conduite à tenir.** {f.Remediation}");

                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(f.Context ?? f.SampleLine);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("_Les secrets ont été masqués avant enregistrement : ce rapport n'a jamais contenu "
                    + "de mot de passe en clair. Le contenu intégral des journaux n'est pas conservé par "
                    + "N4 Sentinel._");
        sb.AppendLine();
        sb.AppendLine($"_N4 Sentinel — rapport produit le {DateTimeOffset.Now:dd/MM/yyyy à HH:mm}._");

        return sb.ToString();
    }

    private static string Plage(DiagnosticSession s) =>
        s.WindowStart is null && s.WindowEnd is null
            ? "tout le contenu analysé"
            : $"{s.WindowStart?.ToLocalTime().ToString("dd/MM HH:mm") ?? "origine"} → "
              + $"{s.WindowEnd?.ToLocalTime().ToString("dd/MM HH:mm") ?? "fin"}";

    private static string Cellule(string t) => t.Replace("|", "\\|");

    public static string LibelleVerdict(DiagnosticVerdict v) => v switch
    {
        DiagnosticVerdict.CauseConfirmee => "Cause confirmée",
        DiagnosticVerdict.CauseCaracterisee => "Cause très probable",
        DiagnosticVerdict.PisteSerieuse => "Plusieurs causes possibles",
        DiagnosticVerdict.AnomaliesSansCause => "Anomalies relevées, cause non identifiée",
        DiagnosticVerdict.RienDeConcluant => "Aucune anomalie détectée sur le périmètre analysé",
        DiagnosticVerdict.InformationsInsuffisantes => "Informations insuffisantes",
        _ => v.ToString()
    };

    /// <summary>§3.10.1 — libellé court pour l'affichage du cycle en 8 phases.</summary>
    public static string LibellePhase(DiagnosticPhase p) => p switch
    {
        DiagnosticPhase.DetectionEtEnregistrement => "Détection et enregistrement",
        DiagnosticPhase.QualificationEtCollecte => "Qualification et collecte",
        DiagnosticPhase.Securisation => "Sécurisation",
        DiagnosticPhase.DiagnosticEtCorrelation => "Diagnostic et corrélation",
        DiagnosticPhase.ChoixDuPlanDAction => "Choix du plan d'action",
        DiagnosticPhase.ValidationEtExecution => "Validation et exécution",
        DiagnosticPhase.RemiseEnServiceEtVerification => "Remise en service et vérification",
        DiagnosticPhase.ClotureEtCapitalisation => "Clôture et capitalisation",
        _ => p.ToString()
    };
}

/// <summary>FR-068.</summary>
public sealed record CreateFromAlertResult
{
    public bool Succeeded { get; init; }
    public Guid SessionId { get; init; }

    /// <summary>Nombre de composants dont la collecte a été lancée automatiquement.</summary>
    public int ComponentsCollected { get; init; }

    public string? Error { get; init; }

    public static CreateFromAlertResult Ok(Guid sessionId, int composants) =>
        new() { Succeeded = true, SessionId = sessionId, ComponentsCollected = composants };

    public static CreateFromAlertResult Failed(string error) => new() { Succeeded = false, Error = error };
}

/// <summary>FR-066.</summary>
public sealed record ReferenceComparison(
    DiagnosticSession Reference,
    List<LogFinding> NewFindings,
    int ReferenceAgeDays,
    bool IsStale,
    bool ReferenceScopeIncomplete);

/// <summary>FR-066 : comparaison à une exécution antérieure réussie (mode ExecutionReussie).</summary>
public sealed record ExecutionReferenceComparison(
    Guid ExecutionId,
    string WorkflowName,
    int WorkflowVersion,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    TimeSpan? Duration,
    List<string> ComponentNames,
    List<string> SharedIncidentComponentNames,
    int ReferenceAgeDays,
    bool IsStale);

/// <summary>FR-066 : un type de signal comparé entre une référence et ce qui a été observé.</summary>
public sealed record SignalBaselineEntry(
    string SignalType,
    string ReferenceValue,
    int ReferenceSampleCount,
    DateTimeOffset ReferenceOldestAt,
    DateTimeOffset ReferenceNewestAt,
    string? ObservedValue,
    bool Differs);

/// <summary>FR-066 : comparaison des signaux d'un composant (modes ValeursHabituellesComposant et NoeudPair).</summary>
public sealed record ComponentSignalComparison(
    Guid ComponentId,
    string ComponentName,
    string ReferenceLabel,
    List<SignalBaselineEntry> Signals,
    bool IsIncomplete);

/// <summary>FR-061 : une entrée de la chronologie multi-sources.</summary>
public sealed record TimelineEntry(
    DateTimeOffset AdjustedTime,
    DateTimeOffset RawTime,
    string ComponentName,
    string? HostName,
    string Title,
    SignatureSeverity Severity,
    double? ClockSkewSeconds,
    bool IsUncertain);
