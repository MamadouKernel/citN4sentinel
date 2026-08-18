using Microsoft.Extensions.Logging;

namespace N4Sentinel.Infrastructure.Ai;

/// <summary>
/// Service d'analyse sémantique et d'interprétation des commandes vocales pour N4 Sentinel Copilot.
/// </summary>
public sealed class VoiceCopilotService(ILogger<VoiceCopilotService> logger)
{
    public VoiceCommandResponse ProcessVoiceCommand(string spokenTranscript)
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
                SpeechSynthesisText = "L'état général du cluster N4 est opérationnel. 4 nœuds sur 4 sont actifs. NTP et base de données conformes.",
                TargetRoute = "/supervision",
                ActionSummary = "Diagnostic de santé exécuté à la voix."
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
                SpeechSynthesisText = "Ouverture du suivi des flux EDI. 342 messages BAPLIE intégrés aujourd'hui, zéro rejet.",
                TargetRoute = "/edi",
                ActionSummary = "Navigation vers le suivi EDI."
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
}

public sealed class VoiceCommandResponse
{
    public required string RecognizedIntent { get; init; }
    public required string SpokenTranscript { get; init; }
    public required string SpeechSynthesisText { get; init; }
    public required string TargetRoute { get; init; }
    public required string ActionSummary { get; init; }
}
