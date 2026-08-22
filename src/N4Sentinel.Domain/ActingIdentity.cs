namespace N4Sentinel.Domain;

/// <summary>
/// Redaction unique de l'identite agissante : QUI a agi, et PAR QUEL VECTEUR.
///
/// POURQUOI CETTE CLASSE EXISTE. Le journal de securite d'un serveur N4 dit une
/// chose vraie mais partielle : le compte qui s'est authentifie. Il ne dit pas
/// que l'ordre est venu de N4 Sentinel plutot que d'une console ouverte a la
/// main. Notre propre tracabilite dit l'inverse : elle sait que l'application a
/// agi, mais nommait jusqu'ici un identifiant applicatif, pas le compte Windows
/// reellement employe.
///
/// La forme retenue - « N4Sentinel · DOMAINE\utilisateur » - porte les deux
/// bouts, et se lit de la meme facon partout : rapport d'execution, preuve
/// d'etape, ecran de suivi, marqueur depose sur le serveur cible. Un lecteur
/// n'a pas a se demander si deux formulations designent la meme chose.
///
/// CE QUE CETTE CLASSE N'EST PAS. Un nom de compte. Windows enregistre le
/// principal qui s'authentifie reellement, et aucune API ne permet de maquiller
/// ce champ - c'est precisement ce qui rend l'evenement 4624 opposable. La
/// chaine produite ici est une ETIQUETTE de tracabilite, jamais une identite
/// presentee a une authentification.
/// </summary>
public static class ActingIdentity
{
    /// <summary>Vecteur, tel qu'il apparait dans toutes les traces.</summary>
    public const string Vecteur = "N4Sentinel";

    /// <summary>
    /// « N4Sentinel · DOMAINE\utilisateur », ou le compte est connu.
    ///
    /// Sans compte Windows - identite du processus, ou aucun compte declare -
    /// la formulation le dit plutot que de laisser croire a une attribution
    /// qui n'existe pas.
    /// </summary>
    public static string Format(string? windowsAccount, string? personne = null)
    {
        var compte = string.IsNullOrWhiteSpace(windowsAccount)
            ? "identité du processus"
            : windowsAccount.Trim();

        var etiquette = $"{Vecteur} · {compte}";

        return string.IsNullOrWhiteSpace(personne)
            ? etiquette
            : $"{etiquette} ({personne.Trim()})";
    }
}
