<#
.SYNOPSIS
    Resout un incident de type Fiche C (SOP-2) : desynchronisation N4/XPS
    persistante malgre le retablissement du reseau. Redemarre uniquement
    la chaine Bridge -> XPS -> ECN4, sans toucher a Center/Cluster.

.DESCRIPTION
    A utiliser en DERNIER RECOURS, apres avoir :
      - verifie les indicateurs JMX (QueueSize, DequeueCount, InFlightCount,
        ConsumerCount) ;
      - confirme que le reseau entre Center et Bridge est retabli ;
      - laisse une chance aux files de se vider naturellement.
    Reference : SOP-2, Fiche C.

    CE QUI A CHANGE PAR RAPPORT A LA VERSION PRECEDENTE
    ---------------------------------------------------
    L'ancienne version faisait Restart-Service puis "Start-Sleep 30" avant de
    passer a XPS. Trente secondes forfaitaires ne prouvent rien : le Bridge
    pouvait etre encore en train d'etablir sa connexion au Center quand XPS
    demarrait, ce qui reproduit exactement la desynchronisation qu'on cherche
    a corriger.
    Desormais, XPS n'est lance que lorsque la connexion du Bridge est
    confirmee dans son log (timeout configurable, 15 min par defaut).

.PARAMETER Unattended
    Ne pose aucune question. Un etat non prouve arrete la sequence.

.EXAMPLE
    .\Restart-N4-BridgeChain.ps1

.NOTES
    Copyright : (c) KMKernel
#>

[CmdletBinding()]
param(
    [System.Management.Automation.PSCredential]$Credential,
    [switch]$Unattended
)

Import-Module "$PSScriptRoot\Navis-Common.psm1" -Force
$logFile = New-N4LogSession -ScriptName "Restart-N4-BridgeChain"

if (-not (Test-N4Integrity -ScriptPath $PSCommandPath)) { return }
$cfg = $Global:N4Config
$icmParams = @{}
if ($Credential) { $icmParams["Credential"] = $Credential }

Write-N4Log -Message "===== FICHE C : Redemarrage cible Bridge -> XPS -> ECN4 =====" -Level ACTION
Write-N4Instruction -Title "A VERIFIER AVANT CE REDEMARRAGE" -Level WARN -Lines @(
    "1. Indicateurs JMX sur le noeud Center, files bridge.* :",
    "     QueueSize     -> stable et proche de 0 ? ou en accumulation ?",
    "     DequeueCount  -> progresse-t-il encore ?",
    "     InFlightCount -> revient-il a 0 ?",
    "     ConsumerCount -> est-il > 0 ? (a zero = plus aucun consommateur)",
    "2. Le reseau entre Center et Bridge est-il confirme retabli ?",
    "3. Avez-vous laisse aux files le temps de se vider naturellement ?",
    "",
    "Redemarrer la chaine alors que des messages sont encore en vol ne les rejoue pas :",
    "cela peut au contraire aggraver l'ecart entre N4 et XPS.",
    "Ce script ne touche NI au Center NI aux noeuds Cluster."
)

if (-not $Unattended) {
    if (-not (Confirm-N4Action -Prompt "Confirmez-vous le redemarrage de Bridge, XPS et ECN4 ?")) {
        Write-N4Log -Message "Action annulee par l'utilisateur." -Level WARN
        return
    }
}

# ---- 1. Bridge : la connexion au Center doit etre PROUVEE ----
Write-N4Log -Message "--- Etape 1/3 : XPS Bridge Daemon ---" -Level ACTION
$res = Start-N4Component -ComputerName $cfg.BridgeHost -ServiceName $cfg.ServiceNames.Bridge `
            -ComponentKey "Bridge" -Label "XPS Bridge Daemon" -IcmParams $icmParams -Restart

if ($res.Status -eq "Failed") {
    Write-N4Instruction -Title "SEQUENCE INTERROMPUE - XPS NON REDEMARRE" -Level ERROR -Lines @(
        "Le Bridge n'est pas reparti : $($res.Reason)",
        "XPS ne sera pas redemarre : le lancer sans Bridge operationnel recree",
        "immediatement la desynchronisation que cette procedure vise a corriger.",
        "Verifier le log du Bridge, la connectivite vers le Center et les files ActiveMQ.",
        "Log complet de cette session : $logFile"
    )
    return
}

if ($res.Status -ne "Ready") {
    if (-not (Confirm-N4ContinueOnUnknown -Result $res -Unattended:$Unattended)) {
        Write-N4Log -Message "Sequence interrompue : XPS et ECN4 n'ont pas ete touches." -Level WARN
        Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
        return
    }
}

# ---- 2. XPS ----
Write-N4Log -Message "--- Etape 2/3 : Service XPS ---" -Level ACTION
$res = Start-N4Component -ComputerName $cfg.XPSHost -ServiceName $cfg.ServiceNames.XPS `
            -ComponentKey "XPS" -Label "Service XPS" -IcmParams $icmParams -Restart

if ($res.Status -eq "Failed") {
    Write-N4Log -Message "XPS n'est pas reparti : $($res.Reason). ECN4 n'est pas redemarre." -Level ERROR
    Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
    return
}

# ---- 3. ECN4 ----
Write-N4Log -Message "--- Etape 3/3 : ECN4 Daemon ---" -Level ACTION
$res = Start-N4Component -ComputerName $cfg.ECN4Host -ServiceName $cfg.ServiceNames.ECN4 `
            -ComponentKey "ECN4" -Label "ECN4 Daemon" -IcmParams $icmParams -Restart

Write-N4Log -Message "===== FIN FICHE C =====" -Level ACTION
Write-N4Instruction -Title "TEST DE VALIDATION OBLIGATOIRE" -Level WARN -Lines @(
    "Le redemarrage ne vaut pas resolution tant que la propagation n'est pas reverifiee :",
    "  1. Modifier un champ simple sur un conteneur dans N4.",
    "  2. Chronometrer son apparition cote XPS.",
    "  3. Comparer ce delai a celui d'une periode saine connue.",
    "Verifier aussi les files bridge.* : ConsumerCount > 0 et QueueSize qui redescend.",
    "Si l'ecart persiste, la cause n'est pas dans la chaine Bridge/XPS :",
    "reprendre le diagnostic cote base ECI, reseau ou charge des noeuds."
)
Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
