using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Referential;

/// <summary>
/// Operations du referentiel technique : environnements, serveurs, composants.
///
/// Porte les regles du cycle de validation (FR-006) : le passage d'un statut a
/// l'autre n'est pas libre, et une regression volontaire vers Brouillon reste
/// possible tant que rien n'a ete execute sur l'objet.
/// </summary>
public sealed class ReferentialService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    ILogger<ReferentialService> logger)
{
    // -----------------------------------------------------------------------
    // Lecture
    // -----------------------------------------------------------------------
    public async Task<List<N4Environment>> GetEnvironmentsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Environments
            .AsNoTracking()
            .OrderBy(e => e.Kind).ThenBy(e => e.Code)
            .ToListAsync(ct);
    }

    public async Task<N4Environment?> GetEnvironmentAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Environments.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
    }

    public async Task<List<N4Server>> GetServersAsync(Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Servers
            .AsNoTracking()
            .Where(s => s.EnvironmentId == environmentId)
            .OrderBy(s => s.HostName)
            .ToListAsync(ct);
    }

    public async Task<N4Server?> GetServerAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Servers
            .Include(s => s.Environment)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
    }

    /// <summary>
    /// Composants heberges par un serveur. Sert a montrer, avant suppression,
    /// ce qui deviendrait orphelin.
    /// </summary>
    public async Task<List<N4Component>> GetComponentsOnServerAsync(Guid serverId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Components
            .AsNoTracking()
            .Where(c => c.ServerId == serverId)
            .OrderBy(c => c.StartOrder).ThenBy(c => c.LogicalName)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Suppression d'un serveur. Refusee tant qu'il porte des composants :
    /// les detacher en silence produirait des composants sans hote, donc
    /// injoignables, sans que personne ne l'ait decide.
    /// </summary>
    public async Task<string?> DeleteServerAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Sans IgnoreQueryFilters, une purge ne trouverait jamais sa cible :
        // un objet a purger est, par construction, deja retire du service et
        // donc invisible pour toute requete ordinaire.
        var portes = await db.Components.IgnoreQueryFilters()
            .Where(c => c.ServerId == id)
            .Select(c => c.LogicalName)
            .ToListAsync(ct);

        if (portes.Count > 0)
            return $"Purge refusée : ce serveur héberge {string.Join(", ", portes)}. " +
                   "Purgez ou réaffectez ces composants d'abord.";

        var serveur = await db.Servers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == id, ct);
        if (serveur is null) return "Serveur introuvable.";

        db.Servers.Remove(serveur);
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<List<N4Component>> GetComponentsAsync(Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Components
            .AsNoTracking()
            .Include(c => c.Server)
            .Where(c => c.EnvironmentId == environmentId)
            .OrderBy(c => c.StartOrder).ThenBy(c => c.LogicalName)
            .ToListAsync(ct);
    }

    public async Task<N4Component?> GetComponentAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Components
            .Include(c => c.Server)
            .Include(c => c.Environment)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<int> CountComponentsAsync(Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Components.CountAsync(c => c.EnvironmentId == environmentId, ct);
    }

    // -----------------------------------------------------------------------
    // Dependances (FR-002, FR-044)
    // -----------------------------------------------------------------------
    /// <summary>Dependances declarees par ce composant : ce dont il a besoin.</summary>
    public async Task<List<ComponentDependency>> GetDependenciesAsync(
        Guid componentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ComponentDependencies
            .AsNoTracking()
            .Include(d => d.DependsOnComponent)
            .Where(d => d.ComponentId == componentId)
            .OrderBy(d => d.Kind)
            .ToListAsync(ct);
    }

    /// <summary>
    /// FR-050 : toutes les dependances d'un environnement en UNE requete,
    /// indexees par composant dependant. La carte de topologie les affiche pour
    /// chaque noeud : les demander composant par composant ouvrirait autant de
    /// connexions qu'il y a de composants, a chaque changement d'environnement.
    /// </summary>
    public async Task<Dictionary<Guid, List<ComponentDependency>>> GetDependenciesByEnvironmentAsync(
        Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var liens = await db.ComponentDependencies
            .AsNoTracking()
            .Include(d => d.DependsOnComponent)
            .Where(d => d.Component!.EnvironmentId == environmentId)
            .OrderBy(d => d.Kind)
            .ToListAsync(ct);

        return liens.GroupBy(d => d.ComponentId)
                    .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>
    /// Composants qui dependent de celui-ci. C'est la question a laquelle il
    /// faut repondre AVANT d'arreter quoi que ce soit : qui va tomber avec lui.
    /// </summary>
    public async Task<List<ComponentDependency>> GetDependentsAsync(
        Guid componentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ComponentDependencies
            .AsNoTracking()
            .Include(d => d.Component)
            .Where(d => d.DependsOnComponentId == componentId)
            .OrderBy(d => d.Kind)
            .ToListAsync(ct);
    }

    public async Task<string?> AddDependencyAsync(
        Guid componentId, Guid dependsOnId, DependencyKind kind, string? notes,
        CancellationToken ct = default)
    {
        if (componentId == dependsOnId)
            return "Un composant ne peut pas dependre de lui-meme.";

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var doublon = await db.ComponentDependencies.AnyAsync(
            d => d.ComponentId == componentId && d.DependsOnComponentId == dependsOnId, ct);

        if (doublon) return "Cette dependance est deja declaree.";

        db.ComponentDependencies.Add(new ComponentDependency
        {
            ComponentId = componentId,
            DependsOnComponentId = dependsOnId,
            Kind = kind,
            Notes = notes
        });

        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task RemoveDependencyAsync(Guid dependencyId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var dependance = await db.ComponentDependencies.FirstOrDefaultAsync(d => d.Id == dependencyId, ct);
        if (dependance is null) return;

        db.ComponentDependencies.Remove(dependance);
        await db.SaveChangesAsync(ct);
    }

    // -----------------------------------------------------------------------
    // Ecriture
    // -----------------------------------------------------------------------
    // NOTE IMPORTANTE SUR LA MISE A JOUR
    // On recharge l'entite existante et on lui applique les valeurs, plutot
    // que d'appeler Update() sur un objet detache. Update() marque TOUTES les
    // proprietes comme modifiees, meme celles qui n'ont pas bouge : le journal
    // d'audit enregistrerait alors l'objet entier a chaque enregistrement,
    // et deviendrait illisible. Avec SetValues, EF ne retient que les
    // differences reelles.

    public async Task SaveEnvironmentAsync(N4Environment environment, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Environments.FirstOrDefaultAsync(e => e.Id == environment.Id, ct);

        if (existing is null) db.Environments.Add(environment);
        else db.Entry(existing).CurrentValues.SetValues(environment);

        await db.SaveChangesAsync(ct);
    }

    public async Task SaveServerAsync(N4Server server, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Servers.FirstOrDefaultAsync(s => s.Id == server.Id, ct);

        if (existing is null) db.Servers.Add(server);
        else db.Entry(existing).CurrentValues.SetValues(server);

        await db.SaveChangesAsync(ct);
    }

    public async Task SaveComponentAsync(N4Component component, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Components.FirstOrDefaultAsync(c => c.Id == component.Id, ct);

        if (existing is null)
        {
            db.Components.Add(component);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(component);

            // Le profil de demarrage est un type possede : ses valeurs se
            // reportent separement, sinon les marqueurs et delais edites a
            // l'ecran seraient silencieusement perdus.
            var readiness = db.Entry(existing).Reference(c => c.Readiness).TargetEntry;
            if (readiness is not null)
                readiness.CurrentValues.SetValues(component.Readiness);

            // Meme raison que Readiness ci-dessus : type possede, sa mise a
            // jour ne se propage pas via SetValues(component) seul.
            var sharedFolder = db.Entry(existing).Reference(c => c.SharedFolder).TargetEntry;
            if (sharedFolder is not null)
                sharedFolder.CurrentValues.SetValues(component.SharedFolder);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Suppression d'un composant.
    ///
    /// PRINCIPE, LE MEME PARTOUT ICI : ce qui n'a jamais servi se supprime, ce
    /// qui porte une histoire se DESACTIVE. Un composant cite par un workflow,
    /// une SOP, une execution ou un diagnostic a laisse une trace que la
    /// suppression rendrait incomprehensible - on refuse, en disant quoi.
    ///
    /// Les releves de supervision et les alertes, eux, partent avec le
    /// composant : ce sont des mesures, deja soumises a la purge de retention,
    /// et sans objet une fois l'objet mesure disparu. Ce qui subsiste dans tous
    /// les cas, c'est la piste d'audit - elle ne porte aucune cle etrangere et
    /// survit donc a tout ce qu'elle relate.
    /// </summary>
    public async Task<string?> DeleteComponentAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var component = await db.Components.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (component is null) return "Composant introuvable.";

        var dependents = await db.ComponentDependencies
            .Where(d => d.DependsOnComponentId == id)
            .Select(d => d.Component!.LogicalName)
            .ToListAsync(ct);

        if (dependents.Count > 0)
            return $"Suppression refusée : {string.Join(", ", dependents)} dépend(ent) de ce composant. "
                 + "Retirez ces dépendances d'abord.";

        var obstacles = new List<string>();

        var etapes = await db.WorkflowSteps.CountAsync(s => s.ComponentId == id, ct);
        if (etapes > 0) obstacles.Add($"{etapes} étape(s) de workflow le pilotent");

        var etapesSop = await db.SopSteps.CountAsync(s => s.ComponentId == id, ct);
        if (etapesSop > 0) obstacles.Add($"{etapesSop} étape(s) de SOP le citent");

        var executions = await db.ExecutionSteps.CountAsync(x => x.ComponentId == id, ct);
        if (executions > 0) obstacles.Add($"{executions} étape(s) d'exécution l'ont piloté");

        var journaux = await db.Sources.CountAsync(s => s.ComponentId == id, ct);
        if (journaux > 0) obstacles.Add($"{journaux} source(s) de journal lui sont rattachée(s)");

        if (obstacles.Count > 0)
            return $"Suppression refusée : {string.Join(", ", obstacles)}. "
                 + "Supprimer « " + component.LogicalName + " » rendrait cet historique incompréhensible. "
                 + "Passez-le en Désactivé : il cesse d'être supervisé et piloté, sans effacer ce qui a eu lieu.";

        // Mesures et alertes suivent leur objet : elles n'ont plus de sens sans
        // lui, et la purge de retention les aurait effacees de toute facon.
        await db.ComponentSignals.Where(s => s.ComponentId == id).ExecuteDeleteAsync(ct);
        await db.Alerts.Where(a => a.ComponentId == id).ExecuteDeleteAsync(ct);

        db.Components.Remove(component);
        await db.SaveChangesAsync(ct);
        return null;
    }

    // -----------------------------------------------------------------------
    // Retrait du service (suppression douce) et retablissement
    // -----------------------------------------------------------------------
    /// <summary>
    /// Retire un environnement du service, avec ses serveurs et ses composants.
    ///
    /// Rien n'est efface : les lignes demeurent, les executions passees
    /// continuent de les designer, et le filtre global du contexte les rend
    /// invisibles a tout le reste de l'application - y compris au moteur
    /// d'orchestration et a la supervision de fond.
    ///
    /// Un seul refus, et il ne se contourne pas : une operation en cours. On
    /// n'arrache pas le referentiel sous les pieds d'une sequence qui pilote
    /// des services N4 en ce moment meme.
    /// </summary>
    public async Task<string?> SoftDeleteEnvironmentAsync(
        Guid id, string confirmation, string actor, string? motif = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var environnement = await db.Environments.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (environnement is null) return "Environnement introuvable.";

        if (!string.Equals(confirmation?.Trim(), environnement.Code, StringComparison.Ordinal))
            return $"Confirmation incorrecte : retapez exactement le code « {environnement.Code} ».";

        var barrageEnv = await OperationEnCoursAsync(db, id, ct);
        if (barrageEnv is not null) return barrageEnv;

        var lot = Guid.NewGuid();
        var quand = DateTimeOffset.UtcNow;

        Marquer(environnement, lot, quand, actor, motif);

        foreach (var serveur in await db.Servers.Where(s => s.EnvironmentId == id).ToListAsync(ct))
            Marquer(serveur, lot, quand, actor, motif);

        foreach (var composant in await db.Components.Where(c => c.EnvironmentId == id).ToListAsync(ct))
            Marquer(composant, lot, quand, actor, motif);

        await db.SaveChangesAsync(ct);

        logger.LogWarning(
            "Environnement {Code} retire du service par {Acteur} (lot {Lot}). Rien n'a ete efface.",
            environnement.Code, actor, lot);

        return null;
    }

    /// <summary>Retire un serveur et les composants qu'il heberge.</summary>
    public async Task<string?> SoftDeleteServerAsync(
        Guid id, string actor, string? motif = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var serveur = await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (serveur is null) return "Serveur introuvable.";

        var barrage = await OperationEnCoursAsync(db, serveur.EnvironmentId, ct);
        if (barrage is not null) return barrage;

        var lot = Guid.NewGuid();
        var quand = DateTimeOffset.UtcNow;

        Marquer(serveur, lot, quand, actor, motif);

        // Les composants suivent leur hote : les laisser visibles sans serveur
        // en ferait des composants injoignables, que la supervision signalerait
        // en echec sans que personne n'ait rien casse.
        foreach (var composant in await db.Components.Where(c => c.ServerId == id).ToListAsync(ct))
            Marquer(composant, lot, quand, actor, motif);

        await db.SaveChangesAsync(ct);
        logger.LogWarning("Serveur {Hote} retire du service par {Acteur}.", serveur.HostName, actor);
        return null;
    }

    /// <summary>Retire un composant.</summary>
    public async Task<string?> SoftDeleteComponentAsync(
        Guid id, string actor, string? motif = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var composant = await db.Components.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (composant is null) return "Composant introuvable.";

        var barrage = await OperationEnCoursAsync(db, composant.EnvironmentId, ct);
        if (barrage is not null) return barrage;

        Marquer(composant, Guid.NewGuid(), DateTimeOffset.UtcNow, actor, motif);
        await db.SaveChangesAsync(ct);

        logger.LogWarning(
            "Composant {Nom} retire du service par {Acteur}.", composant.LogicalName, actor);
        return null;
    }

    /// <summary>
    /// Retablit ce qu'un retrait avait emporte - ni plus, ni moins.
    ///
    /// Le lot est ce qui rend l'operation exacte : retablir un environnement ne
    /// doit pas ressusciter un serveur retire separement trois semaines plus
    /// tot, et volontairement.
    /// </summary>
    public async Task<string?> RestoreAsync(Guid batchId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var environnements = await db.Environments.IgnoreQueryFilters()
            .Where(x => x.DeletionBatchId == batchId).ToListAsync(ct);
        var serveurs = await db.Servers.IgnoreQueryFilters()
            .Where(x => x.DeletionBatchId == batchId).ToListAsync(ct);
        var composants = await db.Components.IgnoreQueryFilters()
            .Where(x => x.DeletionBatchId == batchId).ToListAsync(ct);

        if (environnements.Count == 0 && serveurs.Count == 0 && composants.Count == 0)
            return "Ce retrait est introuvable : il a peut-être déjà été rétabli ou purgé.";

        // Un code libere puis repris par un nouvel environnement empeche le
        // retablissement : mieux vaut le dire que de laisser la base refuser
        // avec une violation d'index incomprehensible.
        foreach (var env in environnements)
        {
            var occupe = await db.Environments.AnyAsync(x => x.Code == env.Code && x.Id != env.Id, ct);
            if (occupe)
                return $"Rétablissement impossible : le code « {env.Code} » a été repris par un autre "
                     + "environnement depuis ce retrait. Renommez-le d'abord.";
        }

        foreach (var x in environnements) Demarquer(x);
        foreach (var x in serveurs) Demarquer(x);
        foreach (var x in composants) Demarquer(x);

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Retrait {Lot} retabli.", batchId);
        return null;
    }

    /// <summary>Ce qui a ete retire, groupe par geste de retrait.</summary>
    public async Task<List<DeletionBatch>> GetDeletionBatchesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var environnements = await db.Environments.IgnoreQueryFilters()
            .Where(x => x.DeletedAt != null).AsNoTracking().ToListAsync(ct);
        var serveurs = await db.Servers.IgnoreQueryFilters()
            .Where(x => x.DeletedAt != null).AsNoTracking().ToListAsync(ct);
        var composants = await db.Components.IgnoreQueryFilters()
            .Where(x => x.DeletedAt != null).AsNoTracking().ToListAsync(ct);

        var lots = environnements.Select(x => (ISoftDeletable)x)
            .Concat(serveurs)
            .Concat(composants)
            .Where(x => x.DeletionBatchId is not null)
            .GroupBy(x => x.DeletionBatchId!.Value);

        return lots.Select(g => new DeletionBatch
        {
            BatchId = g.Key,
            DeletedAt = g.Max(x => x.DeletedAt!.Value),
            DeletedBy = g.First().DeletedBy ?? "inconnu",
            Reason = g.First().DeletionReason,
            Environments = environnements.Where(x => x.DeletionBatchId == g.Key)
                                         .Select(x => $"{x.Code} — {x.Name}").ToList(),
            Servers = serveurs.Where(x => x.DeletionBatchId == g.Key)
                              .Select(x => x.HostName).ToList(),
            Components = composants.Where(x => x.DeletionBatchId == g.Key)
                                   .Select(x => x.LogicalName).ToList()
        })
        .OrderByDescending(b => b.DeletedAt)
        .ToList();
    }

    // -----------------------------------------------------------------------
    private static void Marquer(
        ISoftDeletable cible, Guid lot, DateTimeOffset quand, string actor, string? motif)
    {
        // Deja retire : on ne reecrit pas son lot, sans quoi un retrait
        // d'environnement absorberait un retrait anterieur et le
        // retablissement rendrait ce que personne n'avait demande de rendre.
        if (cible.DeletedAt is not null) return;

        cible.DeletedAt = quand;
        cible.DeletedBy = actor;
        cible.DeletionBatchId = lot;
        cible.DeletionReason = motif;
    }

    private static void Demarquer(ISoftDeletable cible)
    {
        cible.DeletedAt = null;
        cible.DeletedBy = null;
        cible.DeletionBatchId = null;
        cible.DeletionReason = null;
    }

    /// <summary>
    /// Etats d'execution qui interdisent de retirer le referentiel sous eux.
    ///
    /// Les operations EN COURS pour la raison evidente : on n'arrache pas le
    /// referentiel sous les pieds d'une sequence qui pilote des services N4.
    ///
    /// Les operations PREPAREES ou EN ATTENTE D'APPROBATION pour une raison
    /// moins visible, et c'est celle qui compte. Le pre-check lit
    /// <c>execution.Environment</c> pour savoir s'il s'agit de la Production ;
    /// un environnement retire rend cette navigation nulle, et le controle du
    /// ticket ITSM se declarerait alors « non applicable » au lieu d'exiger le
    /// ticket. Un garde-fou qui s'eteint sans rien dire est pire que pas de
    /// garde-fou : on continue de compter dessus.
    /// </summary>
    private static async Task<string?> OperationEnCoursAsync(
        N4SentinelDbContext db, Guid environmentId, CancellationToken ct)
    {
        var bloquantes = await db.Executions
            .Where(x => x.EnvironmentId == environmentId
                        && (x.Status == ExecutionStatus.EnCours
                            || x.Status == ExecutionStatus.EnPause
                            || x.Status == ExecutionStatus.AnnulationDemandee
                            || x.Status == ExecutionStatus.EnPreparation
                            || x.Status == ExecutionStatus.EnAttenteApprobation))
            .Select(x => x.Status)
            .ToListAsync(ct);

        if (bloquantes.Count == 0) return null;

        var actives = bloquantes.Count(s => s is ExecutionStatus.EnCours
                                              or ExecutionStatus.EnPause
                                              or ExecutionStatus.AnnulationDemandee);

        return actives > 0
            ? "Retrait refusé : une opération est en cours sur cet environnement. "
              + "Attendez qu'elle se termine — on ne retire pas le référentiel sous les pieds "
              + "d'une séquence qui pilote des services N4."
            : $"Retrait refusé : {bloquantes.Count} opération(s) y sont préparées ou attendent une "
              + "approbation. Lancez-les ou annulez-les d'abord — retirer l'environnement maintenant "
              + "désactiverait silencieusement leurs contrôles préalables.";
    }

    /// <summary>
    /// Suppression d'un environnement, avec tout ce qu'il configure.
    ///
    /// L'ACTION LA PLUS DESTRUCTRICE DE L'APPLICATION : elle emporte en cascade
    /// les serveurs, les composants, les comptes techniques, les workflows et
    /// les SOP de cet environnement. D'ou trois verrous, et non un seul.
    ///
    /// 1. Un environnement Valide ou Actif ne se supprime jamais. Il se
    ///    desactive. Supprimer la Production parce qu'une liste deroulante
    ///    etait mal positionnee n'est pas un scenario acceptable.
    /// 2. Un environnement qui porte une histoire d'exploitation - executions,
    ///    SOP deroulees - est refuse : cette histoire doit rester lisible, et
    ///    le modele de donnees la protege deja (ces liens sont en NoAction).
    /// 3. Le code de l'environnement doit etre retape. Un clic ne suffit pas
    ///    pour un geste irreversible.
    ///
    /// La piste d'audit, elle, ne porte aucune cle etrangere vers
    /// l'environnement : elle survit a sa suppression, et garde trace de qui
    /// l'a supprime.
    /// </summary>
    public async Task<string?> DeleteEnvironmentAsync(
        Guid id, string confirmation, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var environnement = await db.Environments.IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.Id == id, ct);
        if (environnement is null) return "Environnement introuvable.";

        // On ne purge que ce qui a d'abord ete retire du service. Le detour par
        // la corbeille n'est pas une formalite : il laisse le temps de
        // s'apercevoir d'une erreur, et c'est la seule protection qui reste
        // devant un geste sans retour.
        if (environnement.DeletedAt is null)
            return $"« {environnement.Code} » est encore en service. Retirez-le d'abord : "
                 + "la purge définitive ne s'applique qu'à ce qui est dans la corbeille.";

        if (!string.Equals(confirmation?.Trim(), environnement.Code, StringComparison.Ordinal))
            return $"Confirmation incorrecte : retapez exactement le code « {environnement.Code} » "
                 + "pour confirmer une purge irréversible.";

        var obstacles = new List<string>();

        var executions = await db.Executions.CountAsync(x => x.EnvironmentId == id, ct);
        if (executions > 0) obstacles.Add($"{executions} exécution(s) d'opération");

        var sops = await db.SopExecutions.CountAsync(x => x.EnvironmentId == id, ct);
        if (sops > 0) obstacles.Add($"{sops} SOP déroulée(s)");

        if (obstacles.Count > 0)
            return $"Purge refusée : {string.Join(" et ", obstacles)} s'y rattache(nt). "
                 + "Cet historique doit rester lisible, et le purger le rendrait incompréhensible. "
                 + "L'environnement reste dans la corbeille : il n'apparaît plus nulle part dans "
                 + "l'exploitation, et rien n'est perdu.";

        var serveurs = await db.Servers.IgnoreQueryFilters().CountAsync(s => s.EnvironmentId == id, ct);
        var composants = await db.Components.IgnoreQueryFilters().CountAsync(c => c.EnvironmentId == id, ct);

        // Les liens en NoAction du modele peuvent encore refuser la suppression
        // pour un cas non anticipe ici. Mieux vaut le dire lisiblement qu'exposer
        // une violation de cle etrangere a l'operateur.
        try
        {
            db.Environments.Remove(environnement);
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            return "Purge refusée par la base : des enregistrements y font encore référence. "
                 + "L'environnement reste dans la corbeille, rien n'est perdu. "
                 + $"Détail technique : {ex.InnerException?.Message ?? ex.Message}";
        }

        logger.LogWarning(
            "Environnement {Code} PURGE definitivement, avec {Serveurs} serveur(s) et {Composants} composant(s).",
            environnement.Code, serveurs, composants);

        return null;
    }

    /// <summary>
    /// Purge definitivement tout ce qu'un retrait avait emporte.
    ///
    /// Tout ou rien : si un seul element porte un historique, la purge est
    /// refusee entiere. Purger la moitie d'un lot laisserait un referentiel
    /// a moitie coherent, et une corbeille dont le contenu ne correspond plus
    /// a ce qu'elle annonce.
    /// </summary>
    public async Task<string?> PurgeBatchAsync(
        Guid batchId, string confirmation, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var environnements = await db.Environments.IgnoreQueryFilters()
            .Where(x => x.DeletionBatchId == batchId).ToListAsync(ct);

        // L'ordre compte : composants, puis serveurs, puis environnements. Un
        // serveur qui porte encore un composant est refuse, et un environnement
        // ne se purge qu'une fois vide.
        var composants = await db.Components.IgnoreQueryFilters()
            .Where(x => x.DeletionBatchId == batchId).Select(x => x.Id).ToListAsync(ct);
        foreach (var id in composants)
        {
            var erreur = await DeleteComponentAsync(id, ct);
            if (erreur is not null) return erreur;
        }

        var serveurs = await db.Servers.IgnoreQueryFilters()
            .Where(x => x.DeletionBatchId == batchId).Select(x => x.Id).ToListAsync(ct);
        foreach (var id in serveurs)
        {
            var erreur = await DeleteServerAsync(id, ct);
            if (erreur is not null) return erreur;
        }

        foreach (var env in environnements)
        {
            var erreur = await DeleteEnvironmentAsync(env.Id, confirmation, ct);
            if (erreur is not null) return erreur;
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Cycle de validation (FR-006)
    // -----------------------------------------------------------------------
    /// <summary>
    /// Transitions autorisees. Le cycle n'est pas lineaire : on peut renvoyer
    /// un objet en Brouillon pour le corriger, et desactiver depuis n'importe
    /// quel statut valide.
    /// </summary>
    /// <summary>Un geste de retrait, et ce qu'il a emporte.</summary>
    public sealed record DeletionBatch
    {
        public Guid BatchId { get; init; }
        public DateTimeOffset DeletedAt { get; init; }
        public string DeletedBy { get; init; } = string.Empty;
        public string? Reason { get; init; }
        public List<string> Environments { get; init; } = [];
        public List<string> Servers { get; init; } = [];
        public List<string> Components { get; init; } = [];

        public int Total => Environments.Count + Servers.Count + Components.Count;
    }

    public static IReadOnlyList<LifecycleStatus> AllowedTransitions(LifecycleStatus current) => current switch
    {
        LifecycleStatus.Brouillon => [LifecycleStatus.AValider],
        LifecycleStatus.AValider => [LifecycleStatus.Valide, LifecycleStatus.Brouillon],
        LifecycleStatus.Valide => [LifecycleStatus.Actif, LifecycleStatus.Brouillon, LifecycleStatus.Desactive],
        LifecycleStatus.Actif => [LifecycleStatus.Desactive],
        LifecycleStatus.Desactive => [LifecycleStatus.Valide],
        _ => []
    };

    /// <summary>
    /// Fait evoluer le statut d'un environnement, en verifiant que la
    /// transition est permise ET que l'activation repose sur un referentiel
    /// reellement renseigne.
    /// </summary>
    public async Task<string?> ChangeEnvironmentStatusAsync(
        Guid id, LifecycleStatus target, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var environment = await db.Environments.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (environment is null) return "Environnement introuvable.";

        if (!AllowedTransitions(environment.Status).Contains(target))
            return $"Transition refusee : {environment.Status} ne peut pas passer directement a {target}.";

        if (target == LifecycleStatus.Actif)
        {
            var componentCount = await db.Components.CountAsync(c => c.EnvironmentId == id, ct);
            if (componentCount == 0)
                return "Activation refusee : aucun composant declare dans cet environnement.";

            var orphans = await db.Components
                .Where(c => c.EnvironmentId == id
                         && c.ControlMode == ControlMode.Pilotable
                         && (c.WindowsServiceName == null || c.WindowsServiceName == ""))
                .Select(c => c.LogicalName)
                .ToListAsync(ct);

            if (orphans.Count > 0)
                return $"Activation refusee : composant(s) pilotable(s) sans nom de service Windows - {string.Join(", ", orphans)}.";
        }

        environment.Status = target;
        await db.SaveChangesAsync(ct);
        return null;
    }

    // -----------------------------------------------------------------------
    // Audit
    // -----------------------------------------------------------------------
    public async Task<List<AuditEntry>> GetAuditEntriesAsync(
        Guid? environmentId = null, int take = 200, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = db.AuditEntries.AsNoTracking();
        if (environmentId.HasValue)
            query = query.Where(a => a.EnvironmentId == environmentId.Value);

        return await query
            .OrderByDescending(a => a.OccurredAt)
            .Take(take)
            .ToListAsync(ct);
    }

    public async Task<int> CountAuditEntriesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AuditEntries.CountAsync(ct);
    }

    public async Task SeedDefaultTopologyForEnvironmentAsync(Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var env = await db.Environments.FirstOrDefaultAsync(e => e.Id == environmentId, ct);
        if (env is null) return;

        if (await db.Components.AnyAsync(c => c.EnvironmentId == environmentId, ct)) return;

        var prefix = env.Code.Replace("-", "").ToUpperInvariant();
        var srvApp1 = new N4Server { EnvironmentId = env.Id, HostName = $"{prefix}-APP01", IpAddress = "10.150.10.11", OperatingSystem = "Windows Server 2022 Datacenter", Status = LifecycleStatus.Actif };
        var srvApp2 = new N4Server { EnvironmentId = env.Id, HostName = $"{prefix}-APP02", IpAddress = "10.150.10.12", OperatingSystem = "Windows Server 2022 Datacenter", Status = LifecycleStatus.Actif };
        var srvDb1 = new N4Server { EnvironmentId = env.Id, HostName = $"{prefix}-DB01", IpAddress = "10.150.10.21", OperatingSystem = "Windows Server 2022 Datacenter", Status = LifecycleStatus.Actif };
        var srvDb2 = new N4Server { EnvironmentId = env.Id, HostName = $"{prefix}-DB02", IpAddress = "10.150.10.22", OperatingSystem = "Windows Server 2022 Datacenter", Status = LifecycleStatus.Actif };
        var srvCenter = new N4Server { EnvironmentId = env.Id, HostName = $"{prefix}-MASTER", IpAddress = "10.150.10.30", OperatingSystem = "Windows Server 2022 Datacenter", Status = LifecycleStatus.Actif };
        var srvXps = new N4Server { EnvironmentId = env.Id, HostName = $"{prefix}-XPS01", IpAddress = "10.150.10.40", OperatingSystem = "Windows Server 2022 Datacenter", Status = LifecycleStatus.Actif };

        db.Servers.AddRange(srvApp1, srvApp2, srvDb1, srvDb2, srvCenter, srvXps);
        await db.SaveChangesAsync(ct);

        var components = new List<N4Component>
        {
            // 1. CLUSTER NODES
            new() { EnvironmentId = env.Id, ServerId = srvApp1.Id, LogicalName = "Cluster Node 1", WindowsServiceName = "NavisN4Node1", Role = ComponentRole.ClusterNode, StartOrder = 1, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvApp1.Id, LogicalName = "Cluster Node 2", WindowsServiceName = "NavisN4Node2", Role = ComponentRole.ClusterNode, StartOrder = 2, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvApp2.Id, LogicalName = "Cluster Node 3", WindowsServiceName = "NavisN4Node3", Role = ComponentRole.ClusterNode, StartOrder = 3, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvApp2.Id, LogicalName = "Cluster Node 4", WindowsServiceName = "NavisN4Node4", Role = ComponentRole.ClusterNode, StartOrder = 4, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvApp1.Id, LogicalName = "Gate Node", WindowsServiceName = "NavisGateService", Role = ComponentRole.ClusterNode, StartOrder = 5, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvApp2.Id, LogicalName = "EDI Node", WindowsServiceName = "NavisEdiService", Role = ComponentRole.InterfaceEdi, StartOrder = 6, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvApp1.Id, LogicalName = "SmartAccess Node", WindowsServiceName = "NavisSmartAccess", Role = ComponentRole.ClusterNode, StartOrder = 7, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvApp2.Id, LogicalName = "Partenaire Externe", WindowsServiceName = "NavisExternalPartner", Role = ComponentRole.ClusterNode, StartOrder = 8, ControlMode = ControlMode.SuperviseSeulement },

            // 2. CLUSTER DATABASE
            new() { EnvironmentId = env.Id, ServerId = srvDb1.Id, LogicalName = "Database Host", WindowsServiceName = "MSSQLSERVER", Role = ComponentRole.BaseDeDonnees, StartOrder = 0, ControlMode = ControlMode.SuperviseSeulement },
            new() { EnvironmentId = env.Id, ServerId = srvDb2.Id, LogicalName = "Database Host (Replicated)", WindowsServiceName = "MSSQLSERVER", Role = ComponentRole.BaseDeDonnees, StartOrder = 0, ControlMode = ControlMode.SuperviseSeulement },

            // 3. MASTER / STANDBY NODE
            new() { EnvironmentId = env.Id, ServerId = srvCenter.Id, LogicalName = "Master Node", WindowsServiceName = "NavisCenterMaster", Role = ComponentRole.CenterNode, StartOrder = 1, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvCenter.Id, LogicalName = "Standby Node", WindowsServiceName = "NavisCenterStandby", Role = ComponentRole.StandbyCenterNode, StartOrder = 2, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvCenter.Id, LogicalName = "Share Folder", WindowsServiceName = "LanmanServer", Role = ComponentRole.DossierPartage, StartOrder = 0, ControlMode = ControlMode.SuperviseSeulement },

            // 4. MODULES XPS / ECN
            new() { EnvironmentId = env.Id, ServerId = srvXps.Id, LogicalName = "XPS Server", WindowsServiceName = "XpsServerEngine", Role = ComponentRole.Xps, StartOrder = 10, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvXps.Id, LogicalName = "Docing", WindowsServiceName = "XpsDocingService", Role = ComponentRole.Xps, StartOrder = 11, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvXps.Id, LogicalName = "Dispatcher", WindowsServiceName = "XpsDispatcherService", Role = ComponentRole.Xps, StartOrder = 12, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvXps.Id, LogicalName = "ECN4", WindowsServiceName = "ECN4Service", Role = ComponentRole.Ecn4, StartOrder = 13, ControlMode = ControlMode.Pilotable },
            new() { EnvironmentId = env.Id, ServerId = srvXps.Id, LogicalName = "ECN4 Web", WindowsServiceName = "W3SVC", Role = ComponentRole.Ecn4Web, StartOrder = 14, ControlMode = ControlMode.Pilotable },

            // 5. INTERFACES & ÉCOSYSTÈME EXTERNE
            new() { EnvironmentId = env.Id, ServerId = srvApp1.Id, LogicalName = "Gate Operating System (GOS)", WindowsServiceName = "GosConnector", Role = ComponentRole.SystemeExterne, StartOrder = 20, ControlMode = ControlMode.SuperviseSeulement },
            new() { EnvironmentId = env.Id, ServerId = srvApp2.Id, LogicalName = "Server SFTP (EDI)", WindowsServiceName = "SftpEdiService", Role = ComponentRole.SystemeExterne, StartOrder = 21, ControlMode = ControlMode.SuperviseSeulement },
            new() { EnvironmentId = env.Id, ServerId = srvApp1.Id, LogicalName = "Billing System (IKOS)", WindowsServiceName = "IkosConnector", Role = ComponentRole.SystemeExterne, StartOrder = 22, ControlMode = ControlMode.SuperviseSeulement },
            new() { EnvironmentId = env.Id, ServerId = srvApp2.Id, LogicalName = "Vehicle Booking system", WindowsServiceName = "VbsConnector", Role = ComponentRole.SystemeExterne, StartOrder = 23, ControlMode = ControlMode.SuperviseSeulement },
            new() { EnvironmentId = env.Id, ServerId = srvApp1.Id, LogicalName = "Hyperion", WindowsServiceName = "HyperionConnector", Role = ComponentRole.SystemeExterne, StartOrder = 24, ControlMode = ControlMode.SuperviseSeulement },
            new() { EnvironmentId = env.Id, ServerId = srvApp2.Id, LogicalName = "Reefer Runner", WindowsServiceName = "ReeferConnector", Role = ComponentRole.SystemeExterne, StartOrder = 25, ControlMode = ControlMode.SuperviseSeulement }
        };

        db.Components.AddRange(components);
        await db.SaveChangesAsync(ct);
    }
}
