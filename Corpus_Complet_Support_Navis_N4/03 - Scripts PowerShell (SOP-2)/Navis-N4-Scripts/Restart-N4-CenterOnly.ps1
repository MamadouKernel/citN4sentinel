<#
.SYNOPSIS
    Redemarre uniquement le service N4 du noeud Center, pour l'incident
    "Le noeud Center n'est pas actif" (manuel de diagnostic 5.6).

.DESCRIPTION
    Peut se faire sans toucher aux autres noeuds. En cas de bascule, le
    Standby prend le relais automatiquement (verrouillage via base de donnees
    ou verrou fichier ActiveMQ).

    CE QUI A CHANGE PAR RAPPORT A LA VERSION PRECEDENTE
    ---------------------------------------------------
    L'ancienne version envoyait la commande de demarrage puis s'arretait la :
    le log final disait "Commande de demarrage envoyee", ce qui ne dit rien
    de l'etat reel du Center. Le script attend maintenant la confirmation
    d'arret, puis la preuve de reinitialisation dans le log applicatif
    (timeout configurable, 30 min par defaut).

    CE QUE CE SCRIPT NE FAIT PAS
    ----------------------------
    Il ne determine pas quelle instance detient le role ACTIF. Le log prouve
    que le service a fini son initialisation ; seule la vue N4 (Cluster
    Services) dit qui est reellement actif. Cette verification reste manuelle.

.PARAMETER Unattended
    Ne pose aucune question. A eviter sur ce script : l'arret du Center a un
    impact direct sur la Production.

.EXAMPLE
    .\Restart-N4-CenterOnly.ps1

.NOTES
    Copyright : (c) KMKernel
#>

[CmdletBinding()]
param(
    [System.Management.Automation.PSCredential]$Credential,
    [switch]$Unattended
)

Import-Module "$PSScriptRoot\Navis-Common.psm1" -Force
$logFile = New-N4LogSession -ScriptName "Restart-N4-CenterOnly"

if (-not (Test-N4Integrity -ScriptPath $PSCommandPath)) { return }
$cfg = $Global:N4Config
$icmParams = @{}
if ($Credential) { $icmParams["Credential"] = $Credential }

Write-N4Log -Message "===== Redemarrage cible : Center Node ($($cfg.CenterNode)) =====" -Level ACTION
Write-N4Instruction -Title "IMPACT ET PREREQUIS" -Level WARN -Lines @(
    "Le Center Node porte les communications entre les noeuds N4 et les composants",
    "d'integration. Son redemarrage interrompt temporairement ces echanges.",
    "",
    "A decider AVANT de lancer :",
    "  - Le role actif doit-il rester sur ce Center, ou acceptez-vous une bascule",
    "    vers le Standby ? Si vous refusez la bascule, arretez d'abord le Standby.",
    "  - Le Standby est-il apte a reprendre le role si la bascule se produit ?",
    "  - Les files ActiveMQ sont-elles en train de se vider ? (JMX : QueueSize)",
    "",
    "Apres redemarrage, penser aux composants dependants qui peuvent necessiter",
    "un relance : EDI, scripts Groovy, taches de fond (manuel 5.6)."
)

if (-not $Unattended) {
    if (-not (Confirm-N4Action -Prompt "Confirmez-vous le redemarrage du Center Node ($($cfg.CenterNode)) ?")) {
        Write-N4Log -Message "Action annulee par l'utilisateur." -Level WARN
        return
    }
}

# Arret confirme, puis redemarrage avec preuve par le log.
$stop = Stop-N4Component -ComputerName $cfg.CenterNode -ServiceName $cfg.ServiceNames.Center `
            -ComponentKey "Center" -Label "Center Node" -IcmParams $icmParams

if (-not $stop.Stopped) {
    Write-N4Instruction -Title "REDEMARRAGE NON POURSUIVI" -Level ERROR -Lines @(
        "Le Center n'a pas confirme son arret (statut : $($stop.Status)).",
        "Le script ne lance PAS le demarrage : envoyer un Start-Service a un service",
        "encore en cours d'arret produit un etat indetermine, et peut laisser deux",
        "instances se disputer le verrou ActiveMQ partage.",
        "Traiter d'abord l'arret (voir les recommandations ci-dessus), puis relancer ce script.",
        "Log complet de cette session : $logFile"
    )
    return
}

$res = Start-N4Component -ComputerName $cfg.CenterNode -ServiceName $cfg.ServiceNames.Center `
            -ComponentKey "Center" -Label "Center Node" -IcmParams $icmParams

Write-N4Log -Message "===== FIN =====" -Level ACTION

if ($res.Status -eq "Ready") {
    Write-N4Instruction -Title "VERIFICATIONS MANUELLES RESTANTES" -Level WARN -Lines @(
        "Le log confirme que le service a fini son initialisation. Il ne dit pas qui detient le role actif.",
        "  1. Cluster Services : CenterNode est-il repasse ACTIVE ?",
        "  2. Un seul Center est-il actif ? (deux Center actifs = incident, manuel 5.7)",
        "  3. Les files bridge.* ont-elles retrouve un ConsumerCount > 0 ?",
        "  4. Faut-il relancer EDI, Groovy ou les taches de fond ? (manuel 5.6)"
    )
} else {
    Write-N4Log -Message "Etat du Center non prouve : ne pas considerer l'incident comme resolu." -Level ERROR
}
Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
