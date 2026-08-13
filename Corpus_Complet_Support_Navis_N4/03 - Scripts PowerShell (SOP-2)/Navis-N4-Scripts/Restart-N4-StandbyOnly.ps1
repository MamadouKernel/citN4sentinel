<#
.SYNOPSIS
    Redemarre uniquement le noeud Standby Center, pour l'incident
    "Center et Standby actifs en meme temps" (manuel de diagnostic 5.7).

.DESCRIPTION
    N'affecte aucun autre composant. A utiliser apres avoir verifie les
    exclusions antivirus sur les dossiers Navis (cause frequente).

    CE QUI A CHANGE PAR RAPPORT A LA VERSION PRECEDENTE
    ---------------------------------------------------
    L'ancienne version se terminait sur "Commande de demarrage envoyee", sans
    verifier quoi que ce soit. Le script confirme maintenant l'arret reel,
    puis attend une preuve dans le log applicatif.

    PARTICULARITE DU STANDBY - A LIRE
    ---------------------------------
    Un Standby en attente n'ecrit PAS les memes marqueurs qu'un Center actif :
    l'absence de "Web tier servlet initialized" est le comportement NORMAL
    d'une instance en veille, pas un symptome. Le marqueur configure pour
    'Standby' dans Navis-Config.json doit donc etre celui du mode veille.
    Si le Standby affiche les marqueurs d'un Center actif alors que le
    primaire est actif lui aussi, il ne s'agit pas d'un demarrage reussi mais
    d'un CONFLIT DE ROLE - c'est precisement l'incident 5.7.

.PARAMETER Unattended
    Ne pose aucune question.

.EXAMPLE
    .\Restart-N4-StandbyOnly.ps1

.NOTES
    Copyright : (c) KMKernel
#>

[CmdletBinding()]
param(
    [System.Management.Automation.PSCredential]$Credential,
    [switch]$Unattended
)

Import-Module "$PSScriptRoot\Navis-Common.psm1" -Force
$logFile = New-N4LogSession -ScriptName "Restart-N4-StandbyOnly"

if (-not (Test-N4Integrity -ScriptPath $PSCommandPath)) { return }
$cfg = $Global:N4Config
$icmParams = @{}
if ($Credential) { $icmParams["Credential"] = $Credential }

Write-N4Log -Message "===== Redemarrage cible : Standby Center Node ($($cfg.StandbyNode)) =====" -Level ACTION
Write-N4Instruction -Title "CAUSES A ECARTER AVANT DE REDEMARRER" -Level WARN -Lines @(
    "Un conflit Center/Standby vient rarement du service lui-meme. Verifier d'abord :",
    "  1. Antivirus : les dossiers Navis (Program Data\Navis, dossier partage, amq)",
    "     sont-ils exclus des scans sur TOUS les serveurs ? C'est la cause n.1.",
    "  2. VMware : DRS, HA et vMotion sont-ils desactives sur ce noeud ?",
    "     Un vMotion peut geler l'instance assez longtemps pour lui faire perdre son verrou.",
    "  3. Horloges : moins d'1 seconde d'ecart entre Center et Standby ?",
    "  4. Dossier partage : accessible et stable depuis les deux instances ?",
    "Redemarrer sans avoir traite ces points fait revenir l'incident.",
    "Test-N4IncidentPreCheck.ps1 couvre les points 3 et 4."
)

if (-not $Unattended) {
    if (-not (Confirm-N4Action -Prompt "Confirmez-vous le redemarrage du Standby Center Node ($($cfg.StandbyNode)) ?")) {
        Write-N4Log -Message "Action annulee par l'utilisateur." -Level WARN
        return
    }
}

$stop = Stop-N4Component -ComputerName $cfg.StandbyNode -ServiceName $cfg.ServiceNames.Standby `
            -ComponentKey "Standby" -Label "Standby Center Node" -IcmParams $icmParams

if (-not $stop.Stopped) {
    Write-N4Instruction -Title "REDEMARRAGE NON POURSUIVI" -Level ERROR -Lines @(
        "Le Standby n'a pas confirme son arret (statut : $($stop.Status)).",
        "Cas connu et documente : le Standby ne s'arrete pas toujours proprement via",
        "Stop-Service. Ce script ne force jamais l'arret automatiquement.",
        "Se connecter a $($cfg.StandbyNode), verifier le processus (consommation CPU :",
        "occupe ou reellement fige ?), puis decider - et tracer la decision.",
        "Log complet de cette session : $logFile"
    )
    return
}

$res = Start-N4Component -ComputerName $cfg.StandbyNode -ServiceName $cfg.ServiceNames.Standby `
            -ComponentKey "Standby" -Label "Standby Center Node" -IcmParams $icmParams

Write-N4Log -Message "===== FIN =====" -Level ACTION
Write-N4Instruction -Title "VERIFICATION DU ROLE - ETAPE OBLIGATOIRE" -Level WARN -Lines @(
    "Ouvrir Node Info Desk sur le Standby et confirmer qu'il N'AFFICHE PAS",
    "'Web tier servlet initialized' : c'est le comportement attendu d'une instance en veille.",
    "S'il l'affiche alors que le Center primaire est actif, le conflit de role persiste :",
    "ne pas relancer le script en boucle, escalader avec les logs des deux instances.",
    "Confirmer enfin dans Cluster Services qu'UN SEUL Center est actif."
)
Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
