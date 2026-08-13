using Microsoft.EntityFrameworkCore;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Referential;

/// <summary>
/// Calcule l'avancement de la mise en service d'un environnement.
///
/// L'application se deploie une fois par site, puis chaque environnement est
/// amene a l'etat operationnel par une suite d'etapes. Sans vue d'ensemble,
/// l'exploitant ne sait pas ou il en est ni ce qui lui manque : les ecrans
/// existent, mais rien ne dit lequel ouvrir ensuite.
///
/// L'avancement est DEDUIT du referentiel, jamais stocke. Une case a cocher
/// qu'on remplit soi-meme finit toujours par mentir ; un etat recalcule a
/// chaque affichage ne le peut pas.
/// </summary>
public sealed class CommissioningStatus(IDbContextFactory<N4SentinelDbContext> dbFactory)
{
    public async Task<CommissioningReport> EvaluateAsync(Guid environmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var env = await db.Environments.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == environmentId, ct);

        if (env is null)
            return new CommissioningReport { EnvironmentCode = "?" };

        var serveurs = await db.Servers.AsNoTracking()
            .Where(s => s.EnvironmentId == environmentId).ToListAsync(ct);

        var composants = await db.Components.AsNoTracking()
            .Where(c => c.EnvironmentId == environmentId).ToListAsync(ct);

        var comptes = await db.Credentials.AsNoTracking()
            .Where(c => c.EnvironmentId == environmentId).ToListAsync(ct);

        var rapport = new CommissioningReport
        {
            EnvironmentId = environmentId,
            EnvironmentCode = env.Code,
            IsProduction = env.IsProduction,
            Status = env.Status
        };

        // --- 1. Environnement declare -------------------------------------
        rapport.Steps.Add(new CommissioningStep
        {
            Number = 1,
            Title = "Déclarer l'environnement",
            State = StepState.Fait,
            Detail = $"{env.Code} — {env.Name}, type {env.Kind}",
            Link = $"/admin/environnements/{environmentId}"
        });

        // --- 2. Serveurs ---------------------------------------------------
        var serveursSansHote = serveurs.Count(s => string.IsNullOrWhiteSpace(s.HostName));
        rapport.Steps.Add(new CommissioningStep
        {
            Number = 2,
            Title = "Déclarer les serveurs",
            State = serveurs.Count == 0 ? StepState.AFaire
                  : serveursSansHote > 0 ? StepState.Bloquant
                  : StepState.Fait,
            Detail = serveurs.Count == 0
                ? "Aucun serveur déclaré. Rien ne peut être interrogé tant qu'il n'y en a pas."
                : serveursSansHote > 0
                    ? $"{serveursSansHote} serveur(s) sans nom d'hôte."
                    : $"{serveurs.Count} serveur(s) déclaré(s).",
            Link = $"/admin/environnements/{environmentId}"
        });

        // --- 3. Compte d'acces --------------------------------------------
        // L'absence de compte n'est PAS un manque : c'est le mode recommande,
        // ou l'application se connecte sous sa propre identite. On le dit
        // plutot que de laisser croire a une etape oubliee.
        var comptesInutilisables = comptes.Count(c => !c.IsUsable);
        rapport.Steps.Add(new CommissioningStep
        {
            Number = 3,
            Title = "Définir le compte d'accès",
            State = comptesInutilisables > 0 ? StepState.Bloquant : StepState.Fait,
            Detail = comptes.Count == 0
                ? "Aucun compte déclaré : connexion sous l'identité du processus applicatif. C'est le mode recommandé."
                : comptesInutilisables > 0
                    ? $"{comptesInutilisables} compte(s) incomplet(s) : mot de passe non renseigné."
                    : $"{comptes.Count} compte(s) technique(s) défini(s).",
            Link = $"/admin/environnements/{environmentId}"
        });

        // --- 4. Composants -------------------------------------------------
        var pilotables = composants.Where(c => c.ControlMode == ControlMode.Pilotable).ToList();
        var sansService = pilotables.Count(c => string.IsNullOrWhiteSpace(c.WindowsServiceName));
        var sansServeur = composants.Count(c => c.ServerId is null && c.Role != ComponentRole.SystemeExterne);

        rapport.Steps.Add(new CommissioningStep
        {
            Number = 4,
            Title = "Renseigner les composants",
            State = composants.Count == 0 ? StepState.AFaire
                  : sansService > 0 ? StepState.Bloquant
                  : sansServeur > 0 ? StepState.Avertissement
                  : StepState.Fait,
            Detail = composants.Count == 0
                ? "Aucun composant déclaré. Importez Navis-Config.json ou saisissez-les."
                : sansService > 0
                    ? $"{sansService} composant(s) pilotable(s) sans nom de service Windows : ils ne pourront jamais être pilotés."
                    : sansServeur > 0
                        ? $"{sansServeur} composant(s) sans serveur associé."
                        : $"{composants.Count} composant(s), dont {pilotables.Count} pilotable(s).",
            Link = $"/admin/environnements/{environmentId}"
        });

        // --- 5. Marqueurs de demarrage -------------------------------------
        // L'etape la plus importante, et la seule qu'aucun import ne peut
        // faire a la place de l'exploitant.
        var sansPreuve = pilotables.Where(c => !c.Readiness.IsProvable).ToList();
        rapport.Steps.Add(new CommissioningStep
        {
            Number = 5,
            Title = "Relever les marqueurs de démarrage",
            State = pilotables.Count == 0 ? StepState.AFaire
                  : sansPreuve.Count == pilotables.Count ? StepState.AFaire
                  : sansPreuve.Count > 0 ? StepState.Avertissement
                  : StepState.Fait,
            Detail = pilotables.Count == 0
                ? "Aucun composant pilotable déclaré."
                : sansPreuve.Count == 0
                    ? $"Les {pilotables.Count} composants pilotables ont un marqueur configuré."
                    : $"{sansPreuve.Count} composant(s) sans preuve : {string.Join(", ", sansPreuve.Take(4).Select(c => c.LogicalName))}"
                      + (sansPreuve.Count > 4 ? "…" : ""),
            Hint = sansPreuve.Count > 0
                ? "Sans marqueur, l'état de ces composants restera « à confirmer » et aucune séquence ne pourra "
                  + "conclure. Utilisez l'assistant de relevé sur un démarrage réussi."
                : null,
            Link = sansPreuve.Count > 0
                ? $"/admin/composants/{sansPreuve[0].Id}/marqueurs"
                : $"/admin/environnements/{environmentId}"
        });

        // --- 6. Dependances et ordre ---------------------------------------
        var sansOrdre = pilotables.Count(c => c.StartOrder <= 0);
        rapport.Steps.Add(new CommissioningStep
        {
            Number = 6,
            Title = "Vérifier l'ordre et les dépendances",
            State = pilotables.Count == 0 ? StepState.AFaire
                  : sansOrdre > 0 ? StepState.Avertissement
                  : StepState.Fait,
            Detail = sansOrdre > 0
                ? $"{sansOrdre} composant(s) pilotable(s) sans ordre de démarrage."
                : "Tous les composants pilotables portent un ordre de démarrage.",
            Hint = sansOrdre > 0
                ? "L'ordre conditionne les séquences : nœuds Cluster un par un, puis Center, puis Bridge, puis XPS."
                : null,
            Link = $"/admin/environnements/{environmentId}"
        });

        // --- 7. Test de configuration --------------------------------------
        // Deliberement toujours "a faire" : le resultat d'un test n'est pas
        // persiste, et un test passe hier ne dit rien de la configuration
        // modifiee ce matin.
        rapport.Steps.Add(new CommissioningStep
        {
            Number = 7,
            Title = "Lancer le test de configuration",
            State = StepState.AFaire,
            Detail = "Contrôle complet sans action mutative : DNS, joignabilité, ports, services, journaux.",
            Hint = "À rejouer après toute modification du référentiel — son résultat n'est pas conservé.",
            Link = $"/admin/environnements/{environmentId}"
        });

        // --- 8. Validation et activation -----------------------------------
        rapport.Steps.Add(new CommissioningStep
        {
            Number = 8,
            Title = "Valider et activer",
            State = env.Status == LifecycleStatus.Actif ? StepState.Fait
                  : env.Status == LifecycleStatus.Desactive ? StepState.Bloquant
                  : StepState.AFaire,
            Detail = $"Statut actuel : {env.Status}.",
            Hint = env.IsProduction && env.Status != LifecycleStatus.Actif
                ? "En Production, le lancement et l'approbation d'une opération critique doivent être le fait "
                  + "de deux personnes distinctes."
                : null,
            Link = $"/admin/environnements/{environmentId}"
        });

        return rapport;
    }
}

public enum StepState
{
    AFaire = 0,
    Avertissement = 1,
    Bloquant = 2,
    Fait = 3
}

public sealed class CommissioningStep
{
    public required int Number { get; init; }
    public required string Title { get; init; }
    public required StepState State { get; init; }
    public required string Detail { get; init; }
    public string? Hint { get; init; }
    public string? Link { get; init; }
}

public sealed class CommissioningReport
{
    public Guid EnvironmentId { get; init; }
    public required string EnvironmentCode { get; init; }
    public bool IsProduction { get; init; }
    public LifecycleStatus Status { get; init; }
    public List<CommissioningStep> Steps { get; } = [];

    public int Done => Steps.Count(s => s.State == StepState.Fait);
    public int Total => Steps.Count;
    public int Blocking => Steps.Count(s => s.State == StepState.Bloquant);

    public int PercentComplete => Total == 0 ? 0 : (int)Math.Round(Done * 100.0 / Total);

    /// <summary>
    /// Premiere etape non terminee : c'est celle a proposer a l'exploitant.
    /// Les etapes bloquantes passent devant, quel que soit leur rang.
    /// </summary>
    public CommissioningStep? NextStep =>
        Steps.FirstOrDefault(s => s.State == StepState.Bloquant)
        ?? Steps.FirstOrDefault(s => s.State != StepState.Fait);
}
