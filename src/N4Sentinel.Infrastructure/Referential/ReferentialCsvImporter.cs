using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Security;

namespace N4Sentinel.Infrastructure.Referential;

/// <summary>
/// Import en masse de serveurs et composants depuis un tableur exporté en CSV.
///
/// L'APERÇU ET L'ÉCRITURE PASSENT PAR LE MÊME CODE. C'est la seule façon
/// qu'un aperçu ne mente pas : s'il était calculé par un chemin distinct, il
/// finirait tôt ou tard par annoncer autre chose que ce qui est écrit, et on
/// aurait fabriqué un faux sentiment de contrôle.
///
/// Rien n'est écrit tant que <c>appliquer</c> vaut faux, et une ligne refusée
/// dit POURQUOI : « rôle inconnu » n'aide personne si l'on ne dit pas quels
/// rôles existent.
/// </summary>
public sealed class ReferentialCsvImporter(
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    IAuditWriter auditWriter,
    ILogger<ReferentialCsvImporter> logger)
{
    /// <summary>En-têtes acceptés, dans n'importe quel ordre. Casse et accents indifférents.</summary>
    public static readonly string[] ColonnesReconnues =
        ["hote", "composant", "role", "service", "ordre", "chemin_journal", "marqueur"];

    public enum Issue { Cree, Ignore, Refuse }

    public sealed record Ligne(int Numero, string Contenu, Issue Issue, string Explication);

    public sealed class Rapport
    {
        public bool Applique { get; init; }
        public List<Ligne> Lignes { get; } = [];
        public string? ErreurGlobale { get; set; }

        public int Crees => Lignes.Count(l => l.Issue == Issue.Cree);
        public int Ignores => Lignes.Count(l => l.Issue == Issue.Ignore);
        public int Refuses => Lignes.Count(l => l.Issue == Issue.Refuse);
        public int ServeursCrees { get; set; }

        public bool Succeeded => ErreurGlobale is null;
    }

    public Task<Rapport> ApercuAsync(Guid environmentId, string csv, CancellationToken ct = default)
        => TraiterAsync(environmentId, csv, appliquer: false, actor: null, ct);

    public Task<Rapport> ImporterAsync(Guid environmentId, string csv, string actor, CancellationToken ct = default)
        => TraiterAsync(environmentId, csv, appliquer: true, actor, ct);

    private async Task<Rapport> TraiterAsync(
        Guid environmentId, string csv, bool appliquer, string? actor, CancellationToken ct)
    {
        var rapport = new Rapport { Applique = appliquer };

        if (string.IsNullOrWhiteSpace(csv))
        {
            rapport.ErreurGlobale = "Le fichier est vide.";
            return rapport;
        }

        var lignes = csv.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.TrimEnd())
            .ToList();

        var indexEntete = lignes.FindIndex(l => !string.IsNullOrWhiteSpace(l));
        if (indexEntete < 0)
        {
            rapport.ErreurGlobale = "Le fichier ne contient aucune ligne.";
            return rapport;
        }

        // Séparateur déduit de l'en-tête : Excel francophone écrit des
        // points-virgules, les exports anglophones des virgules. Imposer l'un
        // des deux ferait échouer la moitié des fichiers sans rien expliquer.
        var entete = lignes[indexEntete];
        var separateur = entete.Count(c => c == ';') >= entete.Count(c => c == ',') ? ';' : ',';

        var colonnes = entete.Split(separateur)
            .Select((c, i) => (Nom: Normaliser(c), Index: i))
            .ToDictionary(x => x.Nom, x => x.Index);

        foreach (var obligatoire in new[] { "hote", "composant", "role" })
        {
            if (!colonnes.ContainsKey(obligatoire))
            {
                rapport.ErreurGlobale =
                    $"Colonne « {obligatoire} » absente. Colonnes attendues : "
                    + string.Join(", ", ColonnesReconnues) + ".";
                return rapport;
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await db.Environments.AnyAsync(e => e.Id == environmentId, ct))
        {
            rapport.ErreurGlobale = "Environnement introuvable.";
            return rapport;
        }

        var nomsExistants = await db.Components.AsNoTracking()
            .Where(c => c.EnvironmentId == environmentId)
            .Select(c => c.LogicalName)
            .ToListAsync(ct);

        var deja = new HashSet<string>(nomsExistants, StringComparer.OrdinalIgnoreCase);

        var serveurs = await db.Servers
            .Where(s => s.EnvironmentId == environmentId)
            .ToDictionaryAsync(s => s.HostName, s => s, StringComparer.OrdinalIgnoreCase, ct);

        var serveursCrees = 0;

        for (var i = indexEntete + 1; i < lignes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var brute = lignes[i];
            var numero = i + 1;

            if (string.IsNullOrWhiteSpace(brute)) continue;

            var cellules = brute.Split(separateur);
            string Cellule(string nom) =>
                colonnes.TryGetValue(nom, out var idx) && idx < cellules.Length
                    ? cellules[idx].Trim()
                    : string.Empty;

            var hote = Cellule("hote");
            var nomComposant = Cellule("composant");
            var roleTexte = Cellule("role");

            if (string.IsNullOrWhiteSpace(hote) || string.IsNullOrWhiteSpace(nomComposant))
            {
                rapport.Lignes.Add(new Ligne(numero, brute, Issue.Refuse,
                    "Nom d'hôte et nom de composant sont tous deux obligatoires."));
                continue;
            }

            if (!Enum.TryParse<ComponentRole>(roleTexte, ignoreCase: true, out var role))
            {
                rapport.Lignes.Add(new Ligne(numero, brute, Issue.Refuse,
                    $"Rôle « {roleTexte} » inconnu. Valeurs acceptées : "
                    + string.Join(", ", Enum.GetNames<ComponentRole>()) + "."));
                continue;
            }

            if (deja.Contains(nomComposant))
            {
                // Ignoré et non refusé : réimporter le même fichier après
                // correction d'une ligne ne doit pas être une faute.
                rapport.Lignes.Add(new Ligne(numero, brute, Issue.Ignore,
                    $"Un composant « {nomComposant} » existe déjà dans cet environnement."));
                continue;
            }

            var ordre = 0;
            var ordreTexte = Cellule("ordre");
            if (!string.IsNullOrWhiteSpace(ordreTexte) && !int.TryParse(ordreTexte, out ordre))
            {
                rapport.Lignes.Add(new Ligne(numero, brute, Issue.Refuse,
                    $"Ordre de démarrage « {ordreTexte} » n'est pas un nombre entier."));
                continue;
            }

            // À partir d'ici la ligne est acceptée. En aperçu on s'arrête là :
            // même validation, aucune écriture.
            deja.Add(nomComposant);

            if (!appliquer)
            {
                rapport.Lignes.Add(new Ligne(numero, brute, Issue.Cree,
                    $"{nomComposant} ({role}) sur {hote}."));
                continue;
            }

            if (!serveurs.TryGetValue(hote, out var serveur))
            {
                serveur = new N4Server
                {
                    EnvironmentId = environmentId,
                    HostName = hote,
                    Status = LifecycleStatus.Brouillon,
                    Description = "Créé par import CSV. À compléter et valider."
                };
                db.Servers.Add(serveur);
                await db.SaveChangesAsync(ct);
                serveurs[hote] = serveur;
                serveursCrees++;
            }

            var cheminJournal = Cellule("chemin_journal");
            var marqueur = Cellule("marqueur");

            var composant = new N4Component
            {
                EnvironmentId = environmentId,
                ServerId = serveur.Id,
                LogicalName = nomComposant,
                Role = role,
                WindowsServiceName = Cellule("service") is { Length: > 0 } sn ? sn : null,
                StartOrder = ordre,
                ControlMode = ControlMode.SuperviseSeulement,
                Status = LifecycleStatus.Brouillon,
                Description = "Créé par import CSV. À compléter et valider avant toute opération."
            };

            // Le profil de démarrage n'est renseigné que si le fichier apporte
            // quelque chose : un profil vide vaut mieux qu'un profil inventé.
            if (!string.IsNullOrWhiteSpace(cheminJournal) || !string.IsNullOrWhiteSpace(marqueur))
            {
                composant.Readiness = new ReadinessProfile
                {
                    LogPath = string.IsNullOrWhiteSpace(cheminJournal) ? null : cheminJournal,
                    ReadyPatterns = string.IsNullOrWhiteSpace(marqueur) ? [] : [marqueur]
                };
            }

            db.Components.Add(composant);
            await db.SaveChangesAsync(ct);

            rapport.Lignes.Add(new Ligne(numero, brute, Issue.Cree,
                $"{nomComposant} ({role}) sur {hote}."));
        }

        rapport.ServeursCrees = serveursCrees;

        if (appliquer && rapport.Crees > 0)
        {
            await auditWriter.WriteAsync(
                AuditAction.Creation, AuditOutcome.Succes, actor ?? "inconnu",
                entityType: nameof(N4Component), entityId: environmentId.ToString(),
                entityLabel: "Import CSV du référentiel",
                environmentId: environmentId,
                reason: $"{rapport.Crees} composant(s) et {serveursCrees} serveur(s) créés, "
                      + $"{rapport.Ignores} ignoré(s), {rapport.Refuses} refusé(s).", ct: ct);

            logger.LogInformation(
                "Import CSV : {Crees} composant(s), {Serveurs} serveur(s) créés dans {Env}.",
                rapport.Crees, serveursCrees, environmentId);
        }

        return rapport;
    }

    /// <summary>Normalise un en-tête : minuscules, sans accents, espaces en tirets bas.</summary>
    private static string Normaliser(string entete)
    {
        var s = entete.Trim().ToLowerInvariant()
            .Replace('é', 'e').Replace('è', 'e').Replace('ê', 'e')
            .Replace('à', 'a').Replace('ô', 'o').Replace('î', 'i')
            .Replace('û', 'u').Replace('ç', 'c')
            .Replace(' ', '_').Replace('-', '_');

        return s.Trim('"', '﻿');
    }
}
