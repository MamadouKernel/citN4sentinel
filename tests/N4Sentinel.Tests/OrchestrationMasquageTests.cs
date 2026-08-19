using Xunit;
using N4Sentinel.Infrastructure.Orchestration;

namespace N4Sentinel.Tests;

/// <summary>
/// Masquage des secrets côté Orchestration — SEC-005 / FR-021, recette AC-11.
///
/// L'audit du 15/08/2026 a confirmé que <c>SecretMasker</c> protégeait le
/// module Diagnostic mais n'était jamais appelé depuis
/// <c>StepExecutor</c>/<c>OrchestrationEngine</c> : un mot de passe apparu
/// dans un journal N4 lu en direct pendant une exécution pouvait donc être
/// stocké en clair dans <c>ExecutionStep.Evidence</c>/<c>Error</c>. Ces tests
/// vérifient le correctif au seul point qui compte : la construction d'un
/// <see cref="StepOutcome"/>, par lequel passe tout message produit par une
/// étape d'exécution.
/// </summary>
public sealed class OrchestrationMasquageTests
{
    [SkippableFact]
    public void Un_Succes_Masque_Un_Secret_Dans_La_Preuve()
    {
        var issue = StepOutcome.Succeeded(
            "Marqueur reconnu : « password=Prod2026Secret dans navis-apex.log »");

        Assert.DoesNotContain("Prod2026Secret", issue.Message);
        Assert.Contains("***MASQUÉ***", issue.Message);
    }

    [SkippableFact]
    public void Un_Echec_Masque_Un_Secret_Dans_La_Cause()
    {
        var issue = StepOutcome.Failed(
            "Signature d'échec caractérisée : jdbc:sqlserver://srv01;user=n4;password=EciProd42");

        Assert.DoesNotContain("EciProd42", issue.Message);
        Assert.Contains("***MASQUÉ***", issue.Message);
    }

    [SkippableFact]
    public void Un_Avertissement_Masque_Un_Secret_Dans_La_Reserve()
    {
        var issue = StepOutcome.Warned(
            "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.secret.token trouvé dans la sortie de commande.");

        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9.secret.token", issue.Message);
        Assert.Contains("***MASQUÉ***", issue.Message);
    }

    [SkippableFact]
    public void Une_Attente_Operateur_Masque_Un_Secret_Dans_La_Consigne()
    {
        var issue = StepOutcome.AttenteOperateur(
            "Confirmez après avoir vérifié <password>ClearText42</password> dans la config Mule.");

        Assert.DoesNotContain("ClearText42", issue.Message);
        Assert.Contains("***MASQUÉ***", issue.Message);
    }

    [SkippableFact]
    public void Un_Message_Sans_Secret_Traverse_Sans_Modification()
    {
        const string message = "Service « N4-Bridge » démarré sur SRV-BRIDGE-01.";
        var issue = StepOutcome.Succeeded(message);

        Assert.Equal(message, issue.Message);
    }

    // =======================================================================
    // FR-028 — la commande réellement émise, conservée SOUS FORME MASQUÉE
    // =======================================================================

    [SkippableFact]
    public void La_Commande_Emise_Est_Masquee_Comme_Le_Message()
    {
        // FR-028 exige la commande « sous forme masquée ». Elle était
        // conservée en clair : le masquage vivait dans les fabriques et ne
        // portait que sur le message.
        var issue = StepOutcome.Succeeded(
            "Service démarré.",
            "Start-Service -Name N4Bridge -Credential (svc_n4;password=Prod2026Secret)");

        Assert.DoesNotContain("Prod2026Secret", issue.ExecutedCommand!);
        Assert.Contains("***MASQUÉ***", issue.ExecutedCommand!);
    }

    [SkippableFact]
    public void La_Commande_Reste_Masquee_Quand_Elle_Est_Posee_Par_Une_Expression_With()
    {
        // Le redémarrage et l'attente de marqueur reposent sur
        // « with { ExecutedCommand = … } » : masquer dans les fabriques
        // seulement aurait laissé ces chemins écrire en clair.
        var issue = StepOutcome.Succeeded("Redémarrage terminé.")
            with { ExecutedCommand = "Restart-Service -ArgumentList 'apikey=AbCdEf123456'" };

        Assert.DoesNotContain("AbCdEf123456", issue.ExecutedCommand!);
        Assert.Contains("***MASQUÉ***", issue.ExecutedCommand!);
    }

    [SkippableFact]
    public void Une_Commande_Sans_Secret_Reste_Lisible()
    {
        // Le masquage ne doit pas rendre le rapport inutilisable : sans
        // secret, la commande se lit telle quelle.
        const string commande = "Stop-Service -Name N4Bridge -Force";
        var issue = StepOutcome.Succeeded("Service arrêté.", commande);

        Assert.Equal(commande, issue.ExecutedCommand);
    }

    [SkippableFact]
    public void Une_Commande_Absente_Reste_Absente()
    {
        var issue = StepOutcome.Succeeded("Étape sans commande.");

        Assert.Null(issue.ExecutedCommand);
    }
}
