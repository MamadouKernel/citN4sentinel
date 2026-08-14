using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;

namespace N4Sentinel.Infrastructure.Knowledge;

/// <summary>
/// Base documentaire : indexation, recherche et réponse citée
/// (FR-080 à FR-086, recette AC-10).
///
/// DEUX RÈGLES GOUVERNENT CE SERVICE.
///
/// 1. AUCUNE RÉPONSE SANS CITATION. Si aucun passage ne correspond, le service
///    le dit. Il ne compose jamais une réponse à partir de ce qu'il « sait » :
///    il n'a pas de connaissance propre, seulement des documents. Une réponse
///    plausible mais non sourcée est pire qu'une absence de réponse — elle sera
///    crue, et personne ne saura d'où elle vient.
///
/// 2. LA DOCUMENTATION CONSEILLE, ELLE N'AGIT PAS (FR-084, FR-085). Rien de ce
///    qui sort d'ici ne peut déclencher une opération. Un texte trouvé dans un
///    guide reste un texte : c'est l'opérateur qui décide, et l'orchestrateur
///    qui exécute, sous ses propres garde-fous.
/// </summary>
public sealed class KnowledgeService(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    ILogger<KnowledgeService> logger)
{
    /// <summary>Longueur d'un extrait présenté dans une réponse.</summary>
    private const int LongueurExtrait = 420;

    // -----------------------------------------------------------------------
    // Lecture
    // -----------------------------------------------------------------------
    public async Task<List<KnowledgeDocument>> GetDocumentsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Documents
            .AsNoTracking()
            .OrderBy(d => d.Kind).ThenBy(d => d.Reference)
            .ToListAsync(ct);
    }

    public async Task<KnowledgeDocument?> GetDocumentAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Documents
            .AsNoTracking()
            .Include(d => d.Sections.OrderBy(s => s.Ordinal))
            .FirstOrDefaultAsync(d => d.Id == id, ct);
    }

    // -----------------------------------------------------------------------
    // Indexation
    // -----------------------------------------------------------------------
    /// <summary>
    /// Indexe un document déjà découpé en fragments.
    ///
    /// Le document est créé en BROUILLON : il ne répondra à aucune question
    /// tant que quelqu'un ne l'aura pas relu et validé.
    /// </summary>
    public async Task<IndexResult> IndexAsync(
        KnowledgeDocument document, IReadOnlyList<ExtractedSection> fragments,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(document.Title))
            return IndexResult.Failed("Le titre du document est obligatoire.");

        if (string.IsNullOrWhiteSpace(document.Reference))
            return IndexResult.Failed(
                "La référence est obligatoire : c'est elle qui sera citée dans les réponses. "
                + "Sans elle, une citation ne désigne rien.");

        var retenus = fragments
            .Where(f => !string.IsNullOrWhiteSpace(f.Content) && f.Content.Trim().Length >= 40)
            .ToList();

        if (retenus.Count == 0)
            return IndexResult.Failed(
                "Aucun contenu exploitable n'a été extrait. S'il s'agit d'un PDF, il est peut-être "
                + "constitué d'images numérisées : son texte n'est alors pas extractible sans "
                + "reconnaissance de caractères.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var doublon = await db.Documents.AnyAsync(d => d.Reference == document.Reference, ct);
        if (doublon)
            return IndexResult.Failed($"La référence « {document.Reference} » est déjà utilisée.");

        document.Status = LifecycleStatus.Brouillon;
        document.SectionCount = retenus.Count;
        document.PageCount = retenus.Count == 0 ? 0 : retenus.Max(f => f.PageNumber);

        db.Documents.Add(document);
        await db.SaveChangesAsync(ct);

        var ordinal = 1;
        foreach (var fragment in retenus)
        {
            var contenu = fragment.Content.Trim();

            db.DocumentSections.Add(new DocumentSection
            {
                DocumentId = document.Id,
                Ordinal = ordinal++,
                PageNumber = fragment.PageNumber,
                Heading = Tronquer(fragment.Heading, 300),
                Content = Tronquer(contenu, 8000)!,
                SearchText = Normaliser(contenu)
            });
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Document « {Reference} » indexé : {Sections} section(s), {Pages} page(s). "
            + "En brouillon jusqu'à validation.",
            document.Reference, retenus.Count, document.PageCount);

        return IndexResult.Ok(document.Id, retenus.Count, document.PageCount);
    }

    public async Task<string?> ValidateAsync(Guid documentId, string actor, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (document is null) return "Document introuvable.";

        if (document.SectionCount == 0)
            return "Ce document ne contient aucune section indexée : il ne peut rien citer.";

        document.Status = LifecycleStatus.Valide;
        document.ValidatedBy = actor;
        document.ValidatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Document « {Reference} » validé par {Acteur}.", document.Reference, actor);
        return null;
    }

    public async Task<string?> RevokeAsync(Guid documentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (document is null) return "Document introuvable.";

        // Desactive plutot que supprime : un rapport passe peut citer ce
        // document, et la citation doit rester verifiable.
        document.Status = LifecycleStatus.Desactive;
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task DeleteAsync(Guid documentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (document is null) return;

        db.Documents.Remove(document);
        await db.SaveChangesAsync(ct);
    }

    // -----------------------------------------------------------------------
    // Recherche
    // -----------------------------------------------------------------------
    /// <summary>
    /// Cherche les passages répondant à une question et renvoie une réponse
    /// CITÉE, ou l'aveu qu'aucun passage ne correspond.
    /// </summary>
    public async Task<KnowledgeAnswer> AskAsync(
        string question, int maxPassages = 5, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Trim().Length < 3)
            return KnowledgeAnswer.SansReponse(
                question, "La question est trop courte pour être recherchée.");

        var termes = Termes(question);
        if (termes.Count == 0)
            return KnowledgeAnswer.SansReponse(
                question, "La question ne contient aucun terme exploitable.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Seuls les documents CITABLES sont interrogés. Un brouillon peut être
        // consulté sur sa fiche, jamais servir de réponse.
        var candidats = await db.DocumentSections
            .AsNoTracking()
            .Include(s => s.Document)
            .Where(s => s.Document!.Status == LifecycleStatus.Valide
                        || s.Document.Status == LifecycleStatus.Actif)
            .ToListAsync(ct);

        var corpusVide = candidats.Count == 0;

        var scores = candidats
            .Select(s => new { Section = s, Score = Noter(s.SearchText, termes) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Section.Document!.Reference)
            .ThenBy(x => x.Section.PageNumber)
            .Take(maxPassages)
            .ToList();

        if (scores.Count == 0)
        {
            var motif = corpusVide
                ? "Aucun document validé n'est indexé. Versez la documentation, puis validez-la : "
                  + "tant qu'aucun document n'est validé, la base ne peut rien citer."
                : $"Aucun passage de la documentation validée ne traite de « {question.Trim()} ». "
                  + "L'application ne compose pas de réponse à partir d'autre chose que de vos documents : "
                  + "elle n'a pas de connaissance propre.";

            return KnowledgeAnswer.SansReponse(question, motif);
        }

        var passages = scores.Select(x => new KnowledgePassage
        {
            DocumentId = x.Section.DocumentId,
            DocumentReference = x.Section.Document!.Reference,
            DocumentTitle = x.Section.Document.Title,
            DocumentKind = x.Section.Document.Kind,
            AppliesToVersion = x.Section.Document.AppliesToVersion,
            PageNumber = x.Section.PageNumber,
            Heading = x.Section.Heading,
            Excerpt = Extraire(x.Section.Content, termes),
            FullContent = x.Section.Content,
            Score = x.Score,
            Citation = x.Section.Citation
        }).ToList();

        return new KnowledgeAnswer
        {
            Question = question.Trim(),
            Passages = passages,
            Answered = true
        };
    }

    /// <summary>
    /// Note un passage.
    ///
    /// Un terme rare pèse plus qu'un terme courant, et un passage qui contient
    /// TOUS les termes de la question passe devant celui qui n'en contient
    /// qu'un, même répété. Sans cette règle, une page qui répète vingt fois
    /// « service » remonterait devant celle qui répond vraiment.
    /// </summary>
    private static int Noter(string texte, List<string> termes)
    {
        if (texte.Length == 0) return 0;

        var score = 0;
        var trouves = 0;

        foreach (var terme in termes)
        {
            var occurrences = Compter(texte, terme);
            if (occurrences == 0) continue;

            trouves++;

            // Les occurrences au-dela de trois n'apportent plus rien : elles
            // signalent une repetition, pas une pertinence superieure.
            score += Math.Min(occurrences, 3) * (terme.Length >= 7 ? 3 : 1);
        }

        if (trouves == 0) return 0;

        // Prime de couverture : proportionnelle a la part des termes trouves.
        return score + trouves * 5 * trouves / Math.Max(1, termes.Count);
    }

    private static int Compter(string texte, string terme)
    {
        var compte = 0;
        var index = 0;

        while ((index = texte.IndexOf(terme, index, StringComparison.Ordinal)) >= 0)
        {
            compte++;
            index += terme.Length;
        }

        return compte;
    }

    /// <summary>Extrait le passage autour de la première occurrence utile.</summary>
    private static string Extraire(string contenu, List<string> termes)
    {
        if (contenu.Length <= LongueurExtrait) return contenu;

        var normalise = Normaliser(contenu);

        var position = termes
            .Select(t => normalise.IndexOf(t, StringComparison.Ordinal))
            .Where(i => i >= 0)
            .DefaultIfEmpty(-1)
            .Min();

        if (position < 0) return contenu[..LongueurExtrait] + "…";

        // La normalisation preserve la longueur caractere pour caractere : les
        // accents sont remplaces, jamais retires. La position vaut donc dans
        // les deux textes.
        var debut = Math.Max(0, position - LongueurExtrait / 3);
        var longueur = Math.Min(LongueurExtrait, contenu.Length - debut);

        var extrait = contenu.Substring(debut, longueur);

        return (debut > 0 ? "…" : string.Empty)
             + extrait
             + (debut + longueur < contenu.Length ? "…" : string.Empty);
    }

    // -----------------------------------------------------------------------
    // Normalisation
    // -----------------------------------------------------------------------
    private static readonly Regex MotifNonAlphanumerique =
        new(@"[^a-z0-9]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Mots trop courants pour discriminer. Les retenir ferait remonter
    /// n'importe quelle page.
    /// </summary>
    private static readonly HashSet<string> MotsVides = new(StringComparer.Ordinal)
    {
        "le", "la", "les", "un", "une", "des", "du", "de", "et", "ou", "que", "qui", "quoi",
        "est", "sont", "pour", "par", "sur", "dans", "avec", "sans", "au", "aux", "ce", "cet",
        "cette", "ces", "il", "elle", "on", "nous", "vous", "ils", "elles", "en", "y", "a",
        "the", "and", "or", "of", "to", "in", "is", "are", "for", "on", "with", "as", "at",
        "comment", "pourquoi", "quand", "quel", "quelle", "quels", "quelles", "faire", "faut"
    };

    /// <summary>
    /// Minuscules, sans accents, ponctuation remplacée par des espaces.
    ///
    /// LA LONGUEUR EST PRÉSERVÉE caractère pour caractère : chaque symbole est
    /// remplacé, jamais supprimé. C'est ce qui permet d'utiliser une position
    /// trouvée dans le texte normalisé pour extraire le passage du texte
    /// d'origine, accents compris.
    /// </summary>
    public static string Normaliser(string texte)
    {
        if (string.IsNullOrEmpty(texte)) return string.Empty;

        var decompose = texte.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(texte.Length);

        foreach (var c in decompose)
        {
            var categorie = CharUnicodeInfo.GetUnicodeCategory(c);

            // Les diacritiques sont ecartes du resultat mais la lettre de base
            // les remplace : "é" (1 caractere) devient "e" (1 caractere).
            if (categorie == UnicodeCategory.NonSpacingMark) continue;

            sb.Append(c);
        }

        var aplati = sb.ToString();

        // Recomposition impossible ici sans risquer de changer la longueur :
        // on remplace directement ce qui n'est ni lettre ni chiffre.
        return MotifNonAlphanumerique.Replace(aplati, " ");
    }

    /// <summary>Termes retenus d'une question, dédupliqués et sans mots vides.</summary>
    public static List<string> Termes(string question) =>
        Normaliser(question)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(m => m.Length >= 3 && !MotsVides.Contains(m))
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToList();

    private static string? Tronquer(string? texte, int max) =>
        texte is null ? null : texte.Length <= max ? texte : texte[..(max - 1)] + "…";
}

/// <summary>Fragment extrait d'un fichier, avant indexation.</summary>
public sealed record ExtractedSection
{
    public int PageNumber { get; init; }
    public string? Heading { get; init; }
    public string Content { get; init; } = string.Empty;
}

public sealed record IndexResult
{
    public bool Succeeded { get; init; }
    public Guid DocumentId { get; init; }
    public int SectionCount { get; init; }
    public int PageCount { get; init; }
    public string? Error { get; init; }

    public static IndexResult Ok(Guid id, int sections, int pages) =>
        new() { Succeeded = true, DocumentId = id, SectionCount = sections, PageCount = pages };

    public static IndexResult Failed(string error) => new() { Succeeded = false, Error = error };
}

/// <summary>Passage cité, avec de quoi le retrouver dans le document d'origine.</summary>
public sealed record KnowledgePassage
{
    public Guid DocumentId { get; init; }
    public string DocumentReference { get; init; } = string.Empty;
    public string DocumentTitle { get; init; } = string.Empty;
    public DocumentKind DocumentKind { get; init; }
    public string? AppliesToVersion { get; init; }

    public int PageNumber { get; init; }
    public string? Heading { get; init; }

    public string Excerpt { get; init; } = string.Empty;
    public string FullContent { get; init; } = string.Empty;

    public int Score { get; init; }
    public string Citation { get; init; } = string.Empty;
}

/// <summary>
/// Réponse de la base documentaire.
///
/// Elle est soit CITÉE, soit ABSENTE. Il n'y a pas de troisième forme : le
/// service ne rédige rien qui ne provienne d'un document versé.
/// </summary>
public sealed record KnowledgeAnswer
{
    public string Question { get; init; } = string.Empty;
    public bool Answered { get; init; }
    public List<KnowledgePassage> Passages { get; init; } = [];

    /// <summary>Pourquoi il n'y a pas de réponse. Renseigné uniquement dans ce cas.</summary>
    public string? Explanation { get; init; }

    /// <summary>
    /// Rappel affiché avec toute réponse : la documentation conseille, elle
    /// n'exécute pas (FR-084, FR-085).
    /// </summary>
    public const string AvertissementConseil =
        "Ces éléments proviennent de la documentation versée. Ils informent une décision, "
        + "ils ne la prennent pas : aucune action n'est déclenchée depuis cet écran. "
        + "Toute opération passe par un workflow validé, avec ses propres contrôles préalables.";

    public static KnowledgeAnswer SansReponse(string question, string motif) =>
        new() { Question = question.Trim(), Answered = false, Explanation = motif };
}
