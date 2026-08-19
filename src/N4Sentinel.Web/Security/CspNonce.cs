namespace N4Sentinel.Web.Security;

/// <summary>
/// Jeton a usage unique de la politique de securite de contenu.
///
/// Genere par requete dans l'intergiciel d'en-tetes, il autorise le SEUL bloc
/// de script en ligne que l'application ne peut pas sortir dans un fichier :
/// le <c>&lt;script type="importmap"&gt;</c> qu'emet Blazor.
///
/// Il ne doit servir a rien d'autre. Un nonce distribue a du script ecrit a la
/// main ramenerait progressivement l'application a 'unsafe-inline', mais sans
/// que la politique le dise — c'est-a-dire au pire endroit possible : une
/// protection qui a l'air stricte et ne l'est plus.
/// </summary>
public static class CspNonce
{
    /// <summary>Cle sous laquelle le jeton est depose dans <c>HttpContext.Items</c>.</summary>
    public const string CleContexte = "csp-nonce";

    /// <summary>
    /// Jeton de la requete en cours, ou <c>null</c> hors requete HTTP.
    /// Retourner null plutot qu'une chaine vide est deliberé : un attribut
    /// <c>nonce=""</c> serait rendu et ne correspondrait a aucune politique.
    /// </summary>
    public static string? Lire(HttpContext? contexte)
        => contexte?.Items.TryGetValue(CleContexte, out var valeur) == true
            ? valeur as string
            : null;
}
