namespace N4Sentinel.Domain;

/// <summary>
/// Fournit l'identite de l'acteur courant aux couches basses, sans les
/// coupler a ASP.NET Core. Implemente cote Web a partir du HttpContext.
///
/// Sert au journal d'audit : toute ecriture est rattachee a son auteur et,
/// quand elle est connue, a son adresse IP d'origine.
/// </summary>
public interface ICurrentUserContext
{
    /// <summary>Identifiant de l'acteur, ou "systeme" pour une tache de fond.</summary>
    string Actor { get; }

    /// <summary>Adresse IP d'origine, si l'action provient d'une requete HTTP.</summary>
    string? IpAddress { get; }

    /// <summary>Identifiant de correlation de l'operation en cours, s'il y en a une.</summary>
    string? CorrelationId { get; }
}

/// <summary>
/// Contexte par defaut : utilise par les taches de fond et les migrations,
/// qui n'ont pas d'utilisateur connecte.
/// </summary>
public sealed class SystemUserContext : ICurrentUserContext
{
    public string Actor => "systeme";
    public string? IpAddress => null;
    public string? CorrelationId => null;
}
