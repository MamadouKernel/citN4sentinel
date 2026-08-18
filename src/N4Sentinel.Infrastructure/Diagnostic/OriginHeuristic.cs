using System.Text.RegularExpressions;
using N4Sentinel.Domain;

namespace N4Sentinel.Infrastructure.Diagnostic;

/// <summary>
/// FR-071 — devine l'origine d'un journal versé manuellement à partir de son
/// CONTENU, quand le nom de fichier n'a rien donné.
///
/// Le principe fondateur de l'application s'applique ici comme ailleurs : on
/// ne rattache pas, on suggère. Un journal mal attribué est pire qu'un journal
/// non attribué — il envoie la chronologie, la corrélation et in fine
/// l'intervention sur le mauvais serveur, et rien dans l'écran ne dit que
/// c'est une supposition. Cette classe ne renseigne donc jamais
/// <c>ComponentId</c> ; elle remplit les champs <c>Suggested*</c>, que l'écran
/// affiche comme une question posée à l'opérateur.
///
/// Les motifs ne sont pas codés en dur : ils viennent du catalogue de
/// signatures du site (<see cref="DiagnosticSignature.AppliesToRole"/>). Ce
/// que l'exploitation a appris sur ses propres journaux sert donc aussi à les
/// reconnaître.
/// </summary>
public static class OriginHeuristic
{
    /// <summary>Une regex de signature vient du référentiel : elle peut être coûteuse ou mal écrite.</summary>
    private static readonly TimeSpan DelaiMotif = TimeSpan.FromMilliseconds(250);

    private const int LongueurExtrait = 160;

    /// <summary>Force du signal, de la plus fiable à la plus faible.</summary>
    public enum SignalKind
    {
        /// <summary>Nom d'hôte d'un serveur déclaré, écrit dans le journal.</summary>
        NomHote,
        /// <summary>Nom de service Windows ou nom logique d'un composant déclaré.</summary>
        NomComposant,
        /// <summary>Motif du catalogue de signatures rattaché à un rôle.</summary>
        MotifCatalogue
    }

    /// <param name="ComponentId">Composant suggéré, ou null si rien de sûr.</param>
    /// <param name="Ambiguous">Vrai si le contenu désigne plusieurs composants distincts.</param>
    public sealed record OriginGuess(
        Guid? ComponentId,
        string? ComponentName,
        string? Evidence,
        bool Ambiguous)
    {
        public static readonly OriginGuess Aucune = new(null, null, null, false);
    }

    /// <summary>
    /// Analyse un échantillon DÉJÀ MASQUÉ. L'extrait justificatif est stocké
    /// puis affiché : il ne doit jamais transiter en clair.
    /// </summary>
    public static OriginGuess Deviner(
        IReadOnlyList<N4Component> catalogue,
        IReadOnlyList<DiagnosticSignature> signatures,
        string echantillonMasque)
    {
        if (catalogue.Count == 0 || string.IsNullOrWhiteSpace(echantillonMasque))
            return OriginGuess.Aucune;

        // --- Signal 1 : nom d'hôte -------------------------------------------
        // Le plus fiable : un journal qui écrit un nom d'hôte déclaré vient
        // presque toujours de cette machine. Mais un serveur porte souvent
        // plusieurs composants — il ne suffit à conclure que s'il n'en porte
        // qu'un dans cet environnement.
        var parHote = catalogue
            .Where(c => c.Server is not null
                && !string.IsNullOrWhiteSpace(c.Server.HostName)
                && DesigneExplicitement(echantillonMasque, c.Server.HostName))
            .ToList();

        if (parHote.Count > 0)
        {
            var hote = parHote[0].Server!.HostName;
            if (parHote.Count == 1)
                return new OriginGuess(parHote[0].Id, parHote[0].LogicalName,
                    Justifier(SignalKind.NomHote, hote, echantillonMasque), false);

            // Plusieurs composants sur cet hôte : on tranche avec le signal
            // suivant, restreint à ces candidats-là.
            var affine = ParNomComposant(parHote, echantillonMasque);
            if (affine is not null) return affine;

            return new OriginGuess(null, null,
                Justifier(SignalKind.NomHote, hote, echantillonMasque), true);
        }

        // --- Signal 2 : nom de composant --------------------------------------
        var parNom = ParNomComposant(catalogue, echantillonMasque);
        if (parNom is not null) return parNom;

        // --- Signal 3 : motif du catalogue de signatures -----------------------
        // Une signature rattachée à un rôle est un indice de rôle, pas de
        // composant. Elle ne conclut donc que si l'environnement ne compte
        // qu'un seul composant de ce rôle.
        var rolesVus = new Dictionary<ComponentRole, (string Code, string Extrait)>();

        foreach (var signature in signatures)
        {
            if (signature.AppliesToRole is not { } role) continue;
            if (!signature.IsEnabled) continue;
            if (string.IsNullOrWhiteSpace(signature.Pattern)) continue;
            if (rolesVus.ContainsKey(role)) continue;

            var extrait = PremiereCorrespondance(signature.Pattern, echantillonMasque);
            if (extrait is not null) rolesVus[role] = (signature.Code, extrait);
        }

        if (rolesVus.Count == 0) return OriginGuess.Aucune;

        if (rolesVus.Count > 1)
        {
            var codes = string.Join(", ", rolesVus.Select(r => $"{r.Key} ({r.Value.Code})"));
            return new OriginGuess(null, null,
                $"Motifs du catalogue désignant plusieurs rôles : {codes}.", true);
        }

        var (roleUnique, preuve) = (rolesVus.Keys.First(), rolesVus.Values.First());
        var candidats = catalogue.Where(c => c.Role == roleUnique).ToList();

        if (candidats.Count == 1)
            return new OriginGuess(candidats[0].Id, candidats[0].LogicalName,
                $"Signature « {preuve.Code} » (rôle {roleUnique}) : {Tronquer(preuve.Extrait)}", false);

        // Le rôle est reconnu mais l'environnement en compte plusieurs : c'est
        // une information utile, même sans conclusion.
        return new OriginGuess(null, null,
            $"Signature « {preuve.Code} » désigne le rôle {roleUnique}, mais "
            + $"{candidats.Count} composants de ce rôle existent dans cet environnement.",
            candidats.Count > 1);
    }

    private static OriginGuess? ParNomComposant(IReadOnlyList<N4Component> candidats, string echantillon)
    {
        // À égalité, le nom le plus long l'emporte : « ECN4 » est un préfixe
        // d'« ECN4Web », et les deux apparaissent dans le même journal.
        var trouves = candidats
            .Select(c => new
            {
                Composant = c,
                Motif = new[] { c.WindowsServiceName, c.LogicalName }
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Where(n => DesigneExplicitement(echantillon, n!))
                    .OrderByDescending(n => n!.Length)
                    .FirstOrDefault()
            })
            .Where(x => x.Motif is not null)
            .OrderByDescending(x => x.Motif!.Length)
            .ToList();

        if (trouves.Count == 0) return null;

        var distincts = trouves.Select(x => x.Composant.Id).Distinct().Count();
        if (distincts > 1)
        {
            // Deux composants nommés dans le même journal : c'est courant
            // (un composant en cite un autre) et strictement non concluant.
            var noms = string.Join(", ", trouves.Take(4).Select(x => x.Composant.LogicalName));
            return new OriginGuess(null, null,
                $"Plusieurs composants déclarés sont nommés dans ce journal : {noms}.", true);
        }

        var gagnant = trouves[0];
        return new OriginGuess(gagnant.Composant.Id, gagnant.Composant.LogicalName,
            Justifier(SignalKind.NomComposant, gagnant.Motif!, echantillon), false);
    }

    private static string Justifier(SignalKind signal, string motif, string echantillon)
    {
        var libelle = signal switch
        {
            SignalKind.NomHote => "Nom d'hôte déclaré",
            SignalKind.NomComposant => "Nom de composant déclaré",
            _ => "Motif du catalogue"
        };

        var ligne = LigneContenant(echantillon, motif);
        return ligne is null
            ? $"{libelle} « {motif} » présent dans le journal."
            : $"{libelle} « {motif} » : {Tronquer(ligne)}";
    }

    private static string? LigneContenant(string echantillon, string motif)
    {
        foreach (var ligne in echantillon.Split('\n'))
        {
            if (DesigneExplicitement(ligne, motif)) return ligne.Trim();
        }
        return null;
    }

    private static string Tronquer(string texte)
    {
        var propre = texte.Trim();
        return propre.Length <= LongueurExtrait ? propre : propre[..LongueurExtrait] + "…";
    }

    private static string? PremiereCorrespondance(string motif, string echantillon)
    {
        try
        {
            var m = Regex.Match(echantillon, motif,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, DelaiMotif);
            return m.Success ? m.Value : null;
        }
        catch (RegexMatchTimeoutException)
        {
            // Un motif trop coûteux ne doit pas faire échouer un import : la
            // suggestion est un confort, l'import est la fonction.
            return null;
        }
        catch (ArgumentException)
        {
            // Motif invalide saisi au référentiel — même raisonnement.
            return null;
        }
    }

    /// <summary>
    /// Vrai si <paramref name="needle"/> est écrit dans <paramref name="haystack"/>
    /// comme un mot À LUI SEUL.
    ///
    /// Règle plus stricte que <see cref="ContientCommeUnite"/>, et il le faut :
    /// dans un journal, un nom court comme « XPS » apparaît partout SANS
    /// désigner le composant — dans un nom d'hôte (« SRV-N4-XPS »), dans un nom
    /// de paquet Java (« com.navis.xps.Dispatcher »). Les bornes « non
    /// alphanumériques » suffisent pour un nom de fichier ; sur du contenu
    /// elles attribueraient le journal au premier composant dont le nom traîne
    /// quelque part. On exige donc que le jeton, débarrassé de sa ponctuation
    /// de bord, soit exactement le nom.
    ///
    /// Les noms composés de plusieurs mots (« Center Node », « Navis N4 Center
    /// Node ») ne peuvent pas être des jetons : ils sont assez spécifiques
    /// pour se contenter de la règle d'unité.
    /// </summary>
    public static bool DesigneExplicitement(string haystack, string needle)
    {
        if (string.IsNullOrWhiteSpace(needle) || string.IsNullOrEmpty(haystack)) return false;

        if (needle.Any(char.IsWhiteSpace)) return ContientCommeUnite(haystack, needle);

        foreach (var jeton in haystack.Split(
            [' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(RognerPonctuation(jeton), needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Retire la ponctuation de bord : « [SRV-01] » → « SRV-01 », « Bridge: » → « Bridge ».</summary>
    private static string RognerPonctuation(string jeton)
    {
        var debut = 0;
        var fin = jeton.Length - 1;

        while (debut <= fin && !char.IsLetterOrDigit(jeton[debut])) debut++;
        while (fin >= debut && !char.IsLetterOrDigit(jeton[fin])) fin--;

        return debut > fin ? string.Empty : jeton[debut..(fin + 1)];
    }

    /// <summary>
    /// Vrai si <paramref name="needle"/> apparaît dans <paramref name="haystack"/>
    /// sans être collé à un mot plus long — évite qu'« ECN4 » ne s'attribue
    /// « ecn4web ». Toutes les occurrences sont examinées, et pas seulement la
    /// première : dans un journal entier, « ECN4 » apparaît fréquemment
    /// d'abord au sein d'« ECN4Web » puis seul quelques lignes plus bas.
    /// </summary>
    public static bool ContientCommeUnite(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle) || string.IsNullOrEmpty(haystack)) return false;

        var depuis = 0;
        while (depuis <= haystack.Length - needle.Length)
        {
            var idx = haystack.IndexOf(needle, depuis, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;

            var avant = idx > 0 ? haystack[idx - 1] : '\0';
            var finIdx = idx + needle.Length;
            var apres = finIdx < haystack.Length ? haystack[finIdx] : '\0';

            if (!char.IsLetterOrDigit(avant) && !char.IsLetterOrDigit(apres)) return true;

            depuis = idx + 1;
        }

        return false;
    }
}
