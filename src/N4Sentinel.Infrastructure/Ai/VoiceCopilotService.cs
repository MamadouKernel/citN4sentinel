using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N4Sentinel.Domain;
using N4Sentinel.Infrastructure.Persistence;
using N4Sentinel.Infrastructure.Supervision;

namespace N4Sentinel.Infrastructure.Ai;

/// <summary>
/// Interprétation des commandes vocales pour N4 Sentinel Copilot.
///
/// RÈGLE DE CE SERVICE : il n'énonce aucun chiffre qu'il n'a pas lu, et
/// n'énonce jamais un chiffre sans dire de quand il date.
///
/// Deux réponses affirmaient auparavant des faits écrits en dur dans ce
/// fichier — « 4 nœuds sur 4 sont actifs », « 342 messages BAPLIE intégrés
/// aujourd'hui ». Le service n'avait alors aucune dépendance de données : il
/// ne pouvait rien lire, par construction. Sur une base vide, il affirmait
/// donc à voix haute que tout était opérationnel.
///
/// C'est la contradiction la plus directe possible avec le principe fondateur
/// du produit, et l'oral l'aggrave : rien ne reste à relire, et un opérateur
/// en incident agit sur ce qu'il vient d'entendre.
/// </summary>
public sealed class VoiceCopilotService(
    SupervisionStateCache supervision,
    IDbContextFactory<N4SentinelDbContext> dbFactory,
    ILogger<VoiceCopilotService> logger)
{
    public async Task<VoiceCommandResponse> ProcessVoiceCommandAsync(
        string spokenTranscript, CancellationToken ct = default)
    {
        logger.LogInformation("[Voice Copilot] Traitement de la commande vocale : '{Transcript}'", spokenTranscript);

        var lower = spokenTranscript.ToLowerInvariant().Trim();

        // 1. Tableau de bord (accueil)
        if (lower.Contains("tableau de bord") || lower.Contains("accueil") || lower.Contains("vue d'ensemble") || lower.Contains("page d'accueil"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "HOME",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Ouverture du tableau de bord.",
                TargetRoute = "/",
                ActionSummary = "Navigation vers le Tableau de bord."
            };
        }

        // 2. Flight Simulator & Simulation
        if (lower.Contains("simul") || lower.Contains("test") || lower.Contains("flight") || lower.Contains("exercice") || lower.Contains("crise"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "RUN_SIMULATION",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Ouverture du Flight Simulator N4. Affichage des scénarios de crise et d'entraînement.",
                TargetRoute = "/flight-simulator",
                ActionSummary = "Navigation vers le Flight Simulator N4."
            };
        }

        // 3. Supervision & Santé Cluster
        if (lower.Contains("supervis") || lower.Contains("santé") || lower.Contains("sante") || lower.Contains("état") || lower.Contains("etat") || lower.Contains("vérif") || lower.Contains("verif") || lower.Contains("nœud") || lower.Contains("noeud") || lower.Contains("serveur") || lower.Contains("cluster"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "CHECK_HEALTH",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = EnoncerEtatSupervision(),
                TargetRoute = "/supervision",
                ActionSummary = "État de supervision énoncé à la voix, d'après le dernier relevé."
            };
        }

        // 4. Vitalité hardware & OS
        if (lower.Contains("vital") || lower.Contains("cpu") || lower.Contains("ram") || lower.Contains("mémoire") || lower.Contains("memoire") || lower.Contains("disque") || lower.Contains("ntp"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "VITALITY",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Affichage de la vitalité matérielle des serveurs : CPU, RAM, disques et horloge NTP.",
                TargetRoute = "/vitalite",
                ActionSummary = "Navigation vers la Vitalité des Nœuds."
            };
        }

        // 5a. Démarrage d'un service ou d'une opération
        // Ne déclenche jamais l'action elle-même : le pilotage d'exploitation exige de
        // choisir l'environnement puis de confirmer explicitement (verrou d'environnement,
        // matrice d'approbation). La voix ouvre l'écran et guide, elle n'exécute pas.
        if (lower.Contains("démarr") || lower.Contains("demarr"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "START_SERVICE",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Ouverture du pilotage des opérations pour démarrer un service. Choisissez l'environnement, puis le workflow à lancer, et confirmez.",
                TargetRoute = "/operations",
                ActionSummary = "Navigation vers Opérations pour démarrage d'un service (confirmation requise)."
            };
        }

        // 5b. Arrêt d'un service ou d'une opération
        if (lower.Contains("arrêt") || lower.Contains("arret") || lower.Contains("stopp") || lower.Contains("coupe"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "STOP_SERVICE",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Ouverture du pilotage des opérations pour arrêter un service. Choisissez l'environnement et l'opération en cours, puis confirmez l'arrêt.",
                TargetRoute = "/operations",
                ActionSummary = "Navigation vers Opérations pour arrêt d'un service (confirmation requise)."
            };
        }

        // 5c. Opérations & Pilotage (général)
        if (lower.Contains("opérat") || lower.Contains("operat") || lower.Contains("pilotage") || lower.Contains("bascule"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "OPERATIONS",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Ouverture du centre de pilotage des opérations.",
                TargetRoute = "/operations",
                ActionSummary = "Navigation vers Opérations & Pilotage."
            };
        }

        // 6. Workflows (référentiel)
        if (lower.Contains("workflow"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "ADMIN_WORKFLOWS",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Ouverture du référentiel des workflows d'orchestration.",
                TargetRoute = "/admin/workflows",
                ActionSummary = "Navigation vers l'administration des Workflows."
            };
        }

        // 7. EDI & BAPLIE
        if (lower.Contains("edi") || lower.Contains("baplie") || lower.Contains("codeco") || lower.Contains("edifact"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "CHECK_EDI",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = await EnoncerFluxEdiAsync(ct),
                TargetRoute = "/edi",
                ActionSummary = "Suivi EDI du jour énoncé à la voix."
            };
        }

        // 8. Alertes
        if (lower.Contains("alert") || lower.Contains("seuil"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "ALERTS",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Ouverture du centre d'alertes N4 Sentinel.",
                TargetRoute = "/alertes",
                ActionSummary = "Navigation vers le centre d'alertes."
            };
        }

        // 9. SOP (Procédures Réflexes)
        if (lower.Contains("sop") || lower.Contains("procédure") || lower.Contains("procedure") || lower.Contains("consigne"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "SOP",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Consultation des procédures SOP et fiches réflexes d'exploitation.",
                TargetRoute = "/sop",
                ActionSummary = "Navigation vers les SOP."
            };
        }

        // 10. Diagnostics
        if (lower.Contains("diagnostic"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "DIAGNOSTICS",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Ouverture des diagnostics d'exploitation.",
                TargetRoute = "/diagnostics",
                ActionSummary = "Navigation vers les Diagnostics."
            };
        }

        // 11. Signatures d'anomalie
        if (lower.Contains("signature"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "ANOMALY_SIGNATURES",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Ouverture du catalogue des signatures d'anomalie.",
                TargetRoute = "/admin/signatures",
                ActionSummary = "Navigation vers les Signatures d'anomalie."
            };
        }

        // 12. Matrice de criticité / approbation
        if (lower.Contains("matrice") || lower.Contains("approbation") || lower.Contains("criticité") || lower.Contains("criticite"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "APPROVAL_MATRIX",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Ouverture de la matrice de criticité et d'approbation.",
                TargetRoute = "/admin/matrice-approbation",
                ActionSummary = "Navigation vers la Matrice de criticité."
            };
        }

        // 13. Historique & escalade
        if (lower.Contains("historique") || lower.Contains("escalade"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "HISTORY",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Ouverture de l'historique et de l'escalade.",
                TargetRoute = "/historique",
                ActionSummary = "Navigation vers l'Historique & escalade."
            };
        }

        // 14. Journal d'audit
        if (lower.Contains("audit"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "AUDIT",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Ouverture du journal d'audit.",
                TargetRoute = "/admin/audit",
                ActionSummary = "Navigation vers le Journal d'audit."
            };
        }

        // 15. Comptes & droits
        if (lower.Contains("utilisateur") || lower.Contains("comptes") || lower.Contains("droits"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "USERS",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Ouverture des comptes et droits utilisateurs.",
                TargetRoute = "/admin/utilisateurs",
                ActionSummary = "Navigation vers Comptes & droits."
            };
        }

        // 16. Azure AD / SSO
        if (lower.Contains("azure") || lower.Contains("sso") || lower.Contains("active directory"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "AZURE_AD",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Ouverture de la configuration Azure AD et SSO.",
                TargetRoute = "/admin/azure-ad",
                ActionSummary = "Navigation vers Azure AD / SSO."
            };
        }

        // 17. Sauvegarde
        if (lower.Contains("sauvegarde") || lower.Contains("backup"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "BACKUP",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Ouverture de la gestion des sauvegardes.",
                TargetRoute = "/admin/sauvegarde",
                ActionSummary = "Navigation vers la Sauvegarde."
            };
        }

        // 18. Environnements
        if (lower.Contains("environnement"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "ENVIRONMENTS",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Ouverture des environnements N4.",
                TargetRoute = "/admin/environnements",
                ActionSummary = "Navigation vers les Environnements."
            };
        }

        // 19. Rapports & SLA
        if (lower.Contains("rapport") || lower.Contains("sla"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "REPORTS",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Ouverture des rapports et indicateurs SLA.",
                TargetRoute = "/admin/rapports",
                ActionSummary = "Navigation vers Rapports & SLA."
            };
        }

        // 20. Guide & Documentation (Uniquement si explicitement demandé)
        if (lower.Contains("guide") || lower.Contains("doc") || lower.Contains("manuel") || lower.Contains("aide"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "DOCUMENTATION",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = $"Recherche de la documentation Navis N4 pour '{spokenTranscript}'.",
                TargetRoute = "/documentation",
                ActionSummary = "Navigation vers la base documentaire."
            };
        }

        // 21. Commande non reconnue : Ne navigue PAS vers le guide ! Reste sur la page courante.
        return new VoiceCommandResponse
        {
            RecognizedIntent = "UNKNOWN_COMMAND",
            SpokenTranscript = spokenTranscript,
            SpeechSynthesisText = $"J'ai bien entendu '{spokenTranscript}'. Dites par exemple : Supervision, Opérations, Diagnostics, SOP, ou EDI.",
            TargetRoute = "", // Reste sur la page courante sans redirection intempestive !
            ActionSummary = "Commande vocale non reconnue (sans redirection)."
        };
    }

    // =======================================================================
    // Énoncés construits sur des données réelles
    // =======================================================================

    /// <summary>
    /// État de supervision, lu dans le MÊME cache que l'écran Supervision —
    /// pour que la voix et l'écran ne puissent pas se contredire.
    ///
    /// Aucun verdict global n'est prononcé. « Opérationnel » est une
    /// conclusion, pas une mesure : on énonce ce qui a été relevé, l'opérateur
    /// conclut. Les composants dont l'état est inconnu sont cités
    /// explicitement — les taire reviendrait à les compter comme sains.
    /// </summary>
    private string EnoncerEtatSupervision()
    {
        var resume = supervision.GetSummary();

        if (resume.Total == 0)
        {
            return "Je n'ai aucun relevé de supervision. Soit aucun composant n'est déclaré, "
                 + "soit la collecte n'a pas encore tourné. Je ne peux pas vous dire dans quel "
                 + "état est l'écosystème.";
        }

        var morceaux = new List<string>();
        if (resume.Disponible > 0) morceaux.Add($"{resume.Disponible} disponible{Pluriel(resume.Disponible)}");
        if (resume.Degrade > 0) morceaux.Add($"{resume.Degrade} dégradé{Pluriel(resume.Degrade)}");
        if (resume.Indisponible > 0) morceaux.Add($"{resume.Indisponible} indisponible{Pluriel(resume.Indisponible)}");
        if (resume.Demarrage > 0) morceaux.Add($"{resume.Demarrage} en démarrage");
        if (resume.Arret > 0) morceaux.Add($"{resume.Arret} en arrêt");
        if (resume.Inconnu > 0) morceaux.Add($"{resume.Inconnu} d'état inconnu");
        if (resume.NonSupervise > 0) morceaux.Add($"{resume.NonSupervise} non supervisé{Pluriel(resume.NonSupervise)}");

        var phrase = $"Sur {resume.Total} composant{Pluriel(resume.Total)} : {string.Join(", ", morceaux)}.";

        // L'AGE DU RELEVE fait partie de l'information, pas du confort. Un
        // « tout est disponible » vieux d'une heure ne dit rien de maintenant.
        var dernier = supervision.GetAllSnapshots()
            .Select(s => s.EvaluatedAt)
            .DefaultIfEmpty()
            .Max();

        phrase += dernier == default
            ? " Je ne sais pas de quand date ce relevé."
            : $" Relevé {Anciennete(DateTimeOffset.UtcNow - dernier)}.";

        if (resume.Inconnu > 0 || resume.NonSupervise > 0)
            phrase += " Un état inconnu n'est pas un état sain : consultez l'écran.";

        return phrase;
    }

    /// <summary>
    /// Flux EDI du jour, comptés en base. « Aujourd'hui » se calcule sur
    /// l'heure locale du poste : c'est la journée d'exploitation telle que
    /// l'opérateur la vit, pas une journée UTC qui bascule à contretemps.
    /// </summary>
    private async Task<string> EnoncerFluxEdiAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var debutDuJour = new DateTimeOffset(DateTime.Today, DateTimeOffset.Now.Offset);

            var duJour = await db.EdiFiles.AsNoTracking()
                .Where(f => f.LastSeenAt >= debutDuJour)
                .GroupBy(f => f.Status)
                .Select(g => new { Statut = g.Key, Nombre = g.Count() })
                .ToListAsync(ct);

            if (duJour.Count == 0)
            {
                var suiviTotal = await db.EdiFiles.AsNoTracking().AnyAsync(ct);

                // Distinguer « rien reçu aujourd'hui » de « rien n'est suivi » :
                // le premier peut être normal, le second est une configuration
                // absente. Les confondre masquerait un partage jamais déclaré.
                return suiviTotal
                    ? "Aucun fichier EDI relevé aujourd'hui. Les relevés précédents restent consultables à l'écran."
                    : "Aucun fichier EDI n'est suivi. Vérifiez qu'un dossier partagé EDI est déclaré au référentiel.";
            }

            int Compter(EdiFileStatus s) => duJour.FirstOrDefault(x => x.Statut == s)?.Nombre ?? 0;

            var integres = Compter(EdiFileStatus.Integre);
            var attente = Compter(EdiFileStatus.EnAttente);
            var rejetes = Compter(EdiFileStatus.Rejete);
            var total = integres + attente + rejetes;

            var phrase = $"{total} fichier{Pluriel(total)} EDI relevé{Pluriel(total)} aujourd'hui : "
                       + $"{integres} intégré{Pluriel(integres)}, {attente} en attente, "
                       + $"{rejetes} rejeté{Pluriel(rejetes)}.";

            if (rejetes > 0) phrase += " Les rejets demandent une reprise manuelle.";

            return phrase;
        }
        catch (Exception ex)
        {
            // Un copilote qui ne peut pas lire doit le DIRE. Se rabattre sur
            // une phrase rassurante serait exactement la faute qu'on corrige.
            logger.LogWarning(ex, "[Voice Copilot] Lecture des flux EDI impossible.");
            return "Je n'ai pas pu lire le suivi EDI. Consultez l'écran : je ne peux rien affirmer.";
        }
    }

    private static string Pluriel(int n) => n > 1 ? "s" : string.Empty;

    /// <summary>Ancienneté en clair, pour être entendue et non lue.</summary>
    private static string Anciennete(TimeSpan age)
    {
        if (age < TimeSpan.Zero) return "à l'instant";
        if (age.TotalSeconds < 90) return $"il y a {Math.Max(1, (int)age.TotalSeconds)} secondes";
        if (age.TotalMinutes < 90) return $"il y a {(int)age.TotalMinutes} minutes";
        if (age.TotalHours < 36) return $"il y a {(int)age.TotalHours} heures";
        return $"il y a {(int)age.TotalDays} jours";
    }
}

public sealed class VoiceCommandResponse
{
    public required string RecognizedIntent { get; init; }
    public required string SpokenTranscript { get; init; }
    public required string SpeechSynthesisText { get; init; }
    public required string TargetRoute { get; init; }
    public required string ActionSummary { get; init; }
}
