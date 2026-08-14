using System.Text.RegularExpressions;

namespace N4Sentinel.Infrastructure.Diagnostic;

/// <summary>
/// Masquage des secrets dans les journaux (FR-078, recette AC-11).
///
/// LE MASQUAGE INTERVIENT AVANT L'ENREGISTREMENT, PAS AVANT L'AFFICHAGE.
/// C'est toute la différence : masquer à l'affichage laisserait le secret en
/// clair dans la base de N4 Sentinel, dans ses sauvegardes, et dans tout export
/// SQL qu'on en ferait. La base deviendrait alors un second endroit où traînent
/// les mots de passe de production — exactement ce que le cahier des charges
/// interdit. Un secret qui n'est jamais entré ne peut pas fuir.
///
/// LE MASQUAGE EST DÉLIBÉRÉMENT LARGE. Masquer par excès rend une ligne moins
/// lisible ; ne pas masquer par défaut expose un mot de passe de production
/// dans un rapport transmis à un tiers. Le déséquilibre entre ces deux erreurs
/// ne laisse pas le choix.
///
/// Ce masquage ne remplace pas l'habilitation : il réduit la portée d'une
/// fuite, il ne la rend pas impossible. Un journal reste une donnée sensible.
/// </summary>
public static class SecretMasker
{
    public const string Remplacement = "***MASQUÉ***";

    private static readonly RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly TimeSpan Delai = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Les motifs capturent la VALEUR dans le groupe nommé « secret ». Tout ce
    /// qui l'entoure est conservé : « password=*** » reste lisible comme un
    /// mot de passe masqué, ce qui vaut mieux qu'une ligne effacée dont on ne
    /// sait plus ce qu'elle contenait.
    /// </summary>
    private static readonly Regex[] Motifs =
    [
        // password=..., pwd=..., pass: ..., motdepasse=...
        new(@"\b(?:password|passwd|pwd|pass|motdepasse|mot_de_passe)\s*[:=]\s*(?<secret>[^\s;,""'&<>\]}]+)", Options, Delai),

        // "password" : "..." dans du JSON, avec ou sans espaces
        new(@"""(?:password|passwd|pwd|secret|token|apikey|api_key|accesskey|access_key)""\s*:\s*""(?<secret>[^""]*)""", Options, Delai),

        // <password>...</password> dans du XML de configuration N4
        new(@"<(?<balise>password|passwd|pwd|secret|token|credential)\b[^>]*>(?<secret>[^<]*)</\k<balise>>", Options, Delai),

        // ATTRIBUT XML : password="..." — la forme que prend la configuration
        // Mule/ESB deversee dans navis-apex.log. Le guide 3.8.25 documente
        // explicitement que passer com.navis.control ou com.navis.control.esb
        // en DEBUG sur le Center Node fait ecrire LE MOT DE PASSE DE LA BASE
        // ECI EN CLAIR dans le journal. Sans ce motif, ce mot de passe de
        // production entrerait tel quel dans la base de N4 Sentinel.
        new(@"\b(?:password|passwd|pwd|secret|token|apikey|api[_-]?key|credential)\s*=\s*""(?<secret>[^""]*)""", Options, Delai),

        // Meme forme, guillemets simples.
        new(@"\b(?:password|passwd|pwd|secret|token|apikey|api[_-]?key|credential)\s*=\s*'(?<secret>[^']*)'", Options, Delai),

        // Chaine de connexion JDBC : jdbc:sqlserver://hote;user=x;password=y
        new(@"(?<=jdbc:[^\s]{0,200}?)(?:password|pwd)\s*=\s*(?<secret>[^;\s""']+)", Options, Delai),

        // Authorization: Bearer ..., Authorization: Basic ...
        new(@"\bauthorization\s*:\s*(?:bearer|basic|digest|negotiate|ntlm)\s+(?<secret>[^\s""']+)", Options, Delai),

        // Jeton, cle d'API, secret client sous toutes leurs formes usuelles
        new(@"\b(?:api[_-]?key|apikey|access[_-]?token|refresh[_-]?token|client[_-]?secret|secret[_-]?key|auth[_-]?token|sessionid|jsessionid)\s*[:=]\s*(?<secret>[^\s;,""'&<>\]}]+)", Options, Delai),

        // URL portant des identifiants : protocole://utilisateur:motdepasse@hote
        new(@"(?<=://)[^\s:/@]+:(?<secret>[^\s@/]+)(?=@)", Options, Delai),

        // Cle privee au format PEM : on remplace tout le bloc
        new(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----(?<secret>[\s\S]*?)-----END", Options, Delai),

        // Connection string .NET : Password=...;  User ID=... reste visible
        new(@"\b(?:password|pwd)\s*=\s*(?<secret>[^;""'\r\n]+)(?=\s*;|\s*$|\s*"")", Options, Delai)
    ];

    /// <summary>
    /// Masque un texte et retourne le nombre de secrets remplacés.
    ///
    /// Le compte est rendu à l'opérateur : il doit savoir que ce qu'il lit a
    /// été transformé, sans quoi il chercherait en vain une valeur qui a
    /// disparu de la ligne.
    /// </summary>
    public static (string Texte, int Masques) Masquer(string? texte)
    {
        if (string.IsNullOrEmpty(texte)) return (texte ?? string.Empty, 0);

        var compte = 0;
        var resultat = texte;

        foreach (var motif in Motifs)
        {
            try
            {
                resultat = motif.Replace(resultat, correspondance =>
                {
                    var groupe = correspondance.Groups["secret"];
                    if (!groupe.Success) return correspondance.Value;

                    // Une valeur vide, ou un masquage deja applique, ne compte
                    // pas : sinon le total gonflerait a chaque passe.
                    if (groupe.Value.Length == 0 || groupe.Value.Contains(Remplacement))
                        return correspondance.Value;

                    compte++;

                    var debut = groupe.Index - correspondance.Index;
                    return correspondance.Value[..debut]
                         + Remplacement
                         + correspondance.Value[(debut + groupe.Length)..];
                });
            }
            catch (RegexMatchTimeoutException)
            {
                // Une ligne pathologique ne doit pas faire echouer la collecte.
                // Mais elle ne doit pas passer non plus : dans le doute, on
                // remplace la ligne entiere plutot que de la laisser en clair.
                return (Remplacement + " (ligne illisible, masquée par précaution)", compte + 1);
            }
        }

        return (resultat, compte);
    }

    /// <summary>
    /// Vrai si le texte contient encore quelque chose qui ressemble à un
    /// secret. Sert de filet de sécurité aux tests et avant tout export : si
    /// cette méthode répond vrai sur du contenu déjà masqué, c'est qu'un motif
    /// manque au masquage.
    /// </summary>
    public static bool ContientUnSecretApparent(string? texte)
    {
        if (string.IsNullOrEmpty(texte)) return false;

        foreach (var motif in Motifs)
        {
            try
            {
                foreach (Match m in motif.Matches(texte))
                {
                    var groupe = m.Groups["secret"];
                    if (groupe.Success && groupe.Value.Length > 0 && !groupe.Value.Contains(Remplacement))
                        return true;
                }
            }
            catch (RegexMatchTimeoutException) { return true; }
        }

        return false;
    }
}
