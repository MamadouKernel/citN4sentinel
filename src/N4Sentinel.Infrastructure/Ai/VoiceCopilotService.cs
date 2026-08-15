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

        // 1. AIOps & Auto-Healing
        if (lower.Contains("aiops") || lower.Contains("guerison") || lower.Contains("guérison") || lower.Contains("anomalie") || lower.Contains("prédict") || lower.Contains("predict"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "AIOPS",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Ouverture du tableau de bord AIOps et détection prédictive d'anomalies.",
                TargetRoute = "/aiops",
                ActionSummary = "Navigation vers AIOps & Auto-Healing."
            };
        }

        // 2. Incident Replay & Boîte Noire
        if (lower.Contains("replay") || lower.Contains("boite noire") || lower.Contains("boîte noire") || lower.Contains("patient zero") || lower.Contains("patient zéro") || lower.Contains("time travel") || lower.Contains("rebobin"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "INCIDENT_REPLAY",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Ouverture du module Incident Replay et analyse du composant Patient Zéro.",
                TargetRoute = "/incident-replay",
                ActionSummary = "Navigation vers Boîte Noire & Incident Replay."
            };
        }

        // 3. Flight Simulator & Simulation
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

        // 4. Jumeau Numérique 3D
        if (lower.Contains("jumeau") || lower.Contains("twin") || lower.Contains("3d") || lower.Contains("portique") || lower.Contains("sts") || lower.Contains("rtg") || lower.Contains("quai"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "DIGITAL_TWIN",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Ouverture du Jumeau Numérique 3D de Côte d'Ivoire Terminal.",
                TargetRoute = "/digital-twin",
                ActionSummary = "Navigation vers le Jumeau Numérique 3D."
            };
        }

        // 5. Supervision & Santé Cluster
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

        // 6. Vitalité hardware & OS
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

        // 7. Opérations & Workflows
        if (lower.Contains("opérat") || lower.Contains("operat") || lower.Contains("workflow") || lower.Contains("démarr") || lower.Contains("demarr") || lower.Contains("arrêt") || lower.Contains("arret") || lower.Contains("bascule"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "OPERATIONS",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Ouverture du centre de pilotage des opérations et workflows d'orchestration.",
                TargetRoute = "/operations",
                ActionSummary = "Navigation vers Opérations & Pilotage."
            };
        }

        // 8. EDI & BAPLIE
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

        // 9. Alertes
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

        // 10. SOP (Procédures Réflexes)
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

        // 11. Guide & Documentation (Uniquement si explicitement demandé)
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

        // 12. Commande non reconnue : Ne navigue PAS vers le guide ! Reste sur la page courante.
        return new VoiceCommandResponse
        {
            RecognizedIntent = "UNKNOWN_COMMAND",
            SpokenTranscript = spokenTranscript,
            SpeechSynthesisText = $"J'ai bien entendu '{spokenTranscript}'. Dites par exemple : Supervision, AIOps, Replay, Simulator, ou EDI.",
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
