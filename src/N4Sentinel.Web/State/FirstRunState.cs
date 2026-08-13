using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Web.State;

/// <summary>
/// Sait si l'application a déjà un compte, et donc si le parcours de premier
/// démarrage doit s'ouvrir.
///
/// N4 Sentinel se déploie site par site. Exiger d'un exploitant qu'il définisse
/// une variable d'environnement pour créer son premier compte est une barrière
/// inutile : le produit doit savoir s'amorcer lui-même.
///
/// Le résultat est mis en cache et ne redevient jamais « aucun compte » une
/// fois qu'un compte a été vu. Sans cela, chaque requête interrogerait la base,
/// et surtout une suppression accidentelle de tous les comptes rouvrirait
/// silencieusement une page de création d'administrateur accessible sans
/// authentification.
/// </summary>
/// <remarks>
/// Ce service est un singleton, alors que la fabrique de contexte est à portée
/// de requête — l'intercepteur d'audit ayant besoin de connaître l'utilisateur
/// courant. Un singleton ne peut donc pas la recevoir directement : il ouvre sa
/// propre portée le temps de l'interrogation.
/// </remarks>
public sealed class FirstRunState(IServiceScopeFactory scopeFactory)
{
    private volatile bool _accountSeen;

    /// <summary>
    /// Vrai tant qu'aucun compte n'existe. Une fois faux, le reste vrai pour
    /// la durée de vie du processus.
    /// </summary>
    public async Task<bool> NeedsSetupAsync(CancellationToken ct = default)
    {
        if (_accountSeen) return false;

        using var scope = scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<N4SentinelDbContext>>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var exists = await db.Users.AnyAsync(ct);

        if (exists) _accountSeen = true;
        return !exists;
    }

    /// <summary>À appeler dès qu'un compte vient d'être créé.</summary>
    public void MarkAccountCreated() => _accountSeen = true;
}
