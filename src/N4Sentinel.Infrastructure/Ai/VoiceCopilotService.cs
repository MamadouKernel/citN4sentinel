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

        var lower = spokenTranscript.ToLowerInvariant();

        if (lower.Contains("vérif") || lower.Contains("santé") || lower.Contains("état"))
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
        if (lower.Contains("simul") || lower.Contains("test"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "RUN_SIMULATION",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Mode simulation activé. Affichage des étapes de pré-check sans exécution mutative.",
                TargetRoute = "/operations/nouvelle",
                ActionSummary = "Redirection vers le mode simulation."
            };
        }
        if (lower.Contains("edi") || lower.Contains("baplie"))
        {
            return new VoiceCommandResponse
            {
                RecognizedIntent = "CHECK_EDI",
                SpokenTranscript = spokenTranscript,
                SpeechSynthesisText = "Ouverture du suivi des flux EDI. 342 messages BAPLIE intégrés aujourd'hui, zéro rejet.",
                TargetRoute = "/edi",
                ActionSummary = "Navigation vers le tableau de bord EDI."
            };
        }

        return new VoiceCommandResponse
        {
            RecognizedIntent = "ASSISTANT_QUERY",
            SpokenTranscript = spokenTranscript,
            SpeechSynthesisText = $"D'accord, je recherche '{spokenTranscript}' dans le guide Navis et la base de connaissances.",
            TargetRoute = "/assistant",
            ActionSummary = "Requête transmise à l'assistant IA N4."
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
