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
}
