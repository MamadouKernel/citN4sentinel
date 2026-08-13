<#
.SYNOPSIS
    Arrete l'ensemble des composants Navis N4 dans l'ordre requis :
    ECN4Web -> ECN4 Daemon -> XPS -> Bridge -> Standby -> Cluster (un par un)
    -> Center.
    En option (-InstallUpdates), verifie et installe ensuite les mises a
    jour Windows sur tous les serveurs, puis relance automatiquement
    Start-N4Sequence.ps1 une fois tous les serveurs confirmes a jour.

.DESCRIPTION
    Reference : SOP-2 (Niveau 2/3), section "Sequence d'arret".
    Necessite PowerShell Remoting (WinRM) actif sur les serveurs cibles.

    CE QUI A CHANGE PAR RAPPORT A LA VERSION PRECEDENTE
    ---------------------------------------------------
    L'arret laissait 90 secondes a chaque service, puis se contentait de
    signaler "n'a pas confirme l'arret" et passait au suivant. Un composant
    N4 qui vide ses files ActiveMQ ou flushe KahaDB peut legitimement mettre
    plusieurs minutes : 90 secondes transformaient un arret normal en alerte,
    et surtout la sequence continuait quand meme.
    Desormais :
      - le delai d'arret est configurable par composant (Navis-Config.json,
        Readiness > StopTimeoutSeconds), 10 minutes par defaut ;
      - la progression est affichee pendant l'attente ;
      - un service bloque en "Stopping" est identifie comme tel, avec le PID
        du processus encore actif et la marche a suivre ;
      - aucun processus n'est jamais tue automatiquement : un arret force
        pendant une ecriture ActiveMQ/KahaDB est une cause connue de corruption.
      - l'arret du Center Node, qui met fin au service, exige une confirmation
        explicite (sauf -Unattended).

    IMPORTANT - Le patching (-InstallUpdates) est desactive par defaut.
    Il ne se declenche QUE si vous passez explicitement ce parametre : c'est
    une action de fenetre de maintenance planifiee, pas un comportement
    automatique d'un arret d'incident.

.PARAMETER Unattended
    Mode non surveille : aucune question n'est posee. Les confirmations
    d'arret sont considerees comme accordees (l'operateur a valide en
    planifiant l'execution), mais un arret non confirme reste bloquant.

.PARAMETER InstallUpdates
    Active la verification et l'installation des mises a jour Windows sur
    tous les serveurs N4, apres l'arret complet des services, et avant
    tout redemarrage de N4.

.PARAMETER MaxUpdatePasses
    Nombre maximum de cycles recherche/installation/redemarrage par serveur
    (certaines mises a jour n'apparaissent qu'apres l'installation d'une
    mise a jour prealable). Defaut : 3.

.PARAMETER SkipAutoStart
    Si specifie avec -InstallUpdates, n'appelle PAS automatiquement
    Start-N4Sequence.ps1 a la fin du patching.

.EXAMPLE
    .\Stop-N4Sequence.ps1
        Arret simple, sans patching (comportement par defaut).

.EXAMPLE
    .\Stop-N4Sequence.ps1 -InstallUpdates -Credential (Get-Credential)
        Arret complet, puis verification/installation des mises a jour
        Windows sur tous les serveurs, puis redemarrage automatique de N4
        une fois tous les serveurs confirmes a jour.

.NOTES
    Copyright : (c) KMKernel
#>

[CmdletBinding()]
param(
    [System.Management.Automation.PSCredential]$Credential,
    [switch]$Unattended,
    [switch]$InstallUpdates,
    [int]$MaxUpdatePasses = 3,
    [switch]$SkipAutoStart
)

Import-Module "$PSScriptRoot\Navis-Common.psm1" -Force
$logFile = New-N4LogSession -ScriptName "Stop-N4Sequence"

if (-not (Test-N4Integrity -ScriptPath $PSCommandPath)) { return }

$cfg = $Global:N4Config
$icmParams = @{}
if ($Credential) { $icmParams["Credential"] = $Credential }

$nonConfirmes = @()

# ============================================================
# SEQUENCE D'ARRET DES SERVICES N4
# ============================================================
Write-N4Log -Message "===== DEBUT SEQUENCE D'ARRET N4 =====" -Level ACTION
Write-N4Instruction -Title "AVANT DE LANCER UN ARRET COMPLET" -Level WARN -Lines @(
    "L'arret n'est PAS l'inverse automatique du demarrage : il suit son propre ordre,",
    "des couches hautes (clients, ECN4Web) vers le Center Node.",
    "A confirmer AVANT de poursuivre, hors de ce script :",
    "  1. Les operations portuaires sont suspendues et les utilisateurs previenus.",
    "  2. Les clients XPS (dont les clients serveur) sont fermes.",
    "  3. Aucun traitement Billing ou EDI critique n'est en cours.",
    "  4. Le Bridge ne traite plus de file : QueueSize proche de 0 en JMX.",
    "     Arreter un Bridge qui vide encore sa file laisse des messages en suspens.",
    "Chaque etape attend la confirmation REELLE de l'arret avant de passer a la suivante."
)

if (-not $Unattended) {
    if (-not (Confirm-N4Action -Prompt "Confirmez-vous l'arret complet de l'ecosysteme N4 ?")) {
        Write-N4Log -Message "Arret annule par l'operateur. Aucun service n'a ete touche." -Level WARN
        Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
        return
    }
}

# ---- 1. ECN4Web ----
Write-N4Log -Message "--- Etape 1/7 : ECN4Web ---" -Level ACTION
$r = Stop-N4Component -ComputerName $cfg.ECN4Host -ServiceName $cfg.ServiceNames.ECN4Web `
        -ComponentKey "ECN4Web" -Label "ECN4Web" -IcmParams $icmParams
if (-not $r.Stopped) { $nonConfirmes += "ECN4Web sur $($cfg.ECN4Host) (statut : $($r.Status))" }

# ---- 2. ECN4 Daemon ----
Write-N4Log -Message "--- Etape 2/7 : ECN4 Daemon ---" -Level ACTION
$r = Stop-N4Component -ComputerName $cfg.ECN4Host -ServiceName $cfg.ServiceNames.ECN4 `
        -ComponentKey "ECN4" -Label "ECN4 Daemon" -IcmParams $icmParams
if (-not $r.Stopped) { $nonConfirmes += "ECN4 Daemon sur $($cfg.ECN4Host) (statut : $($r.Status))" }

# ---- 3. Service XPS ----
Write-N4Log -Message "--- Etape 3/7 : Service XPS ---" -Level ACTION
$r = Stop-N4Component -ComputerName $cfg.XPSHost -ServiceName $cfg.ServiceNames.XPS `
        -ComponentKey "XPS" -Label "Service XPS" -IcmParams $icmParams
if (-not $r.Stopped) { $nonConfirmes += "Service XPS sur $($cfg.XPSHost) (statut : $($r.Status))" }

# ---- 4. XPS Bridge Daemon ----
Write-N4Log -Message "--- Etape 4/7 : XPS Bridge Daemon ---" -Level ACTION
Write-N4Instruction -Title "CONTROLE AVANT L'ARRET DU BRIDGE" -Level WARN -Lines @(
    "Si le Bridge traite encore sa file de messages (QueueSize > 0 en JMX), NE PAS l'arreter :",
    "attendre la vidange. Les messages non consommes au moment de l'arret devront etre",
    "rejoues ou seront perdus selon leur nature.",
    "Indicateurs a lire sur le noeud Center : QueueSize, DequeueCount, InFlightCount, ConsumerCount."
)
$r = Stop-N4Component -ComputerName $cfg.BridgeHost -ServiceName $cfg.ServiceNames.Bridge `
        -ComponentKey "Bridge" -Label "XPS Bridge Daemon" -IcmParams $icmParams
if (-not $r.Stopped) { $nonConfirmes += "XPS Bridge Daemon sur $($cfg.BridgeHost) (statut : $($r.Status))" }

# ---- 5. Standby Center Node ----
Write-N4Log -Message "--- Etape 5/7 : Standby Center Node ---" -Level ACTION
Write-N4Log -Message "Rappel : le Standby doit etre arrete AVANT le Center primaire, sinon il tente de reprendre le role au moment ou le primaire s'arrete." -Level WARN
$r = Stop-N4Component -ComputerName $cfg.StandbyNode -ServiceName $cfg.ServiceNames.Standby `
        -ComponentKey "Standby" -Label "Standby Center Node" -IcmParams $icmParams
if (-not $r.Stopped) {
    $nonConfirmes += "Standby Center Node sur $($cfg.StandbyNode) (statut : $($r.Status))"
    Write-N4Log -Message "Cas connu : le Standby ne s'arrete pas toujours proprement via Stop-Service. Verifier le processus avant toute decision d'arret force." -Level WARN
}

# ---- 6. Noeuds Cluster, un par un ----
Write-N4Log -Message "--- Etape 6/7 : Noeuds Cluster (un par un) ---" -Level ACTION
Write-N4Log -Message "Chaque noeud doit confirmer son arret avant que le suivant soit sollicite, pour respecter le delai de synchronisation Hazelcast." -Level INFO
$rang = 0
$total = $cfg.ClusterNodes.Count
foreach ($node in $cfg.ClusterNodes) {
    $rang++
    Write-N4Log -Message "--- Noeud Cluster $rang/$total : $node ---" -Level ACTION
    $r = Stop-N4Component -ComputerName $node -ServiceName $cfg.ServiceNames.Cluster `
            -ComponentKey "Cluster" -Label "Cluster Node ($node)" -IcmParams $icmParams
    if (-not $r.Stopped) { $nonConfirmes += "Cluster Node $node (statut : $($r.Status))" }
}

# ---- 7. Center Node ----
Write-N4Log -Message "--- Etape 7/7 : Center Node ---" -Level ACTION
Write-N4Instruction -Title "DERNIERE ETAPE - ARRET DU CENTER NODE" -Level WARN -Lines @(
    "C'est l'action qui met definitivement fin au service N4.",
    "Apres cet arret, plus aucune operation portuaire n'est possible tant que",
    "Start-N4Sequence.ps1 n'a pas ete rejoue et valide."
)
$centerConfirme = $true
if (-not $Unattended) {
    $centerConfirme = Confirm-N4Action -Prompt "Confirmez-vous l'arret du Center Node ($($cfg.CenterNode)) ?"
}
if ($centerConfirme) {
    $r = Stop-N4Component -ComputerName $cfg.CenterNode -ServiceName $cfg.ServiceNames.Center `
            -ComponentKey "Center" -Label "Center Node" -IcmParams $icmParams
    if (-not $r.Stopped) { $nonConfirmes += "Center Node sur $($cfg.CenterNode) (statut : $($r.Status))" }
} else {
    Write-N4Log -Message "Arret du Center Node refuse par l'operateur : il reste ACTIF. L'ecosysteme est dans un etat partiel." -Level WARN
    $nonConfirmes += "Center Node volontairement laisse actif"
}

Write-N4Log -Message "===== FIN SEQUENCE D'ARRET N4 =====" -Level ACTION

if ($nonConfirmes.Count -gt 0) {
    Write-N4Instruction -Title "BILAN : ARRETS NON CONFIRMES" -Level ERROR -Lines (@(
        "Les composants suivants n'ont pas confirme leur arret :"
    ) + ($nonConfirmes | ForEach-Object { "  - $_" }) + @(
        "L'ecosysteme n'est PAS dans un etat d'arret propre.",
        "Traiter chaque cas avant toute operation ulterieure (patching, redemarrage, maintenance materielle)."
    ))
} else {
    Write-N4Log -Message "Tous les composants ont confirme leur arret." -Level OK
}

# ============================================================
# PATCHING WINDOWS (uniquement si -InstallUpdates est fourni)
# ============================================================
if (-not $InstallUpdates) {
    Write-N4Log -Message "Parametre -InstallUpdates non fourni : aucune verification de mise a jour effectuee. Script termine." -Level INFO
    Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
    return
}

# ---- Portillon obligatoire : confirmer que TOUS les services sont reellement a l'arret ----
Write-N4Log -Message "--- Verification finale : tous les services sont-ils bien a l'arret avant patching ? ---" -Level ACTION
$serviceChecklist = @(
    [PSCustomObject]@{ Server = $cfg.ECN4Host;    Service = $cfg.ServiceNames.ECN4Web; Label = "ECN4Web" }
    [PSCustomObject]@{ Server = $cfg.ECN4Host;    Service = $cfg.ServiceNames.ECN4;    Label = "ECN4 Daemon" }
    [PSCustomObject]@{ Server = $cfg.XPSHost;     Service = $cfg.ServiceNames.XPS;     Label = "Service XPS" }
    [PSCustomObject]@{ Server = $cfg.BridgeHost;  Service = $cfg.ServiceNames.Bridge;  Label = "XPS Bridge Daemon" }
    [PSCustomObject]@{ Server = $cfg.StandbyNode; Service = $cfg.ServiceNames.Standby; Label = "Standby Center Node" }
    [PSCustomObject]@{ Server = $cfg.CenterNode;  Service = $cfg.ServiceNames.Center;  Label = "Center Node" }
)
foreach ($node in $cfg.ClusterNodes) {
    $serviceChecklist += [PSCustomObject]@{ Server = $node; Service = $cfg.ServiceNames.Cluster; Label = "Cluster Node ($node)" }
}

$confirmation = Confirm-N4AllServicesStopped -ServiceChecklist $serviceChecklist -IcmParams $icmParams
foreach ($d in $confirmation.Details) {
    $lvl = if ($d.Stopped) { "OK" } else { "ERROR" }
    Write-N4Log -Message "[$($d.Server)] $($d.Label) -> $($d.Status)" -Level $lvl
}

if (-not $confirmation.AllStopped) {
    Write-N4Instruction -Title "PATCHING REFUSE" -Level ERROR -Lines @(
        "Au moins un service n'est PAS confirme a l'arret.",
        "Le patching ne demarre JAMAIS tant que tous les services ne sont pas confirmes Stopped :",
        "redemarrer un serveur alors qu'un composant N4 ecrit encore expose a une corruption",
        "des fichiers de persistance.",
        "Corriger manuellement les services concernes, puis relancer ce script.",
        "Log complet de cette session : $logFile"
    )
    return
}
Write-N4Log -Message "Tous les services sont confirmes a l'arret. Patching autorise a demarrer." -Level OK

Write-N4Log -Message "===== DEBUT PATCHING WINDOWS (fenetre de maintenance) =====" -Level ACTION

# Liste dedupliquee de tous les serveurs touches par la sequence N4
$allServers = @($cfg.CenterNode, $cfg.StandbyNode, $cfg.BridgeHost, $cfg.XPSHost, $cfg.ECN4Host) + $cfg.ClusterNodes
$allServers = $allServers | Sort-Object -Unique

Write-N4Log -Message "Serveurs cibles pour le patching : $($allServers -join ', ')" -Level INFO

$searchBlock  = Get-N4PendingUpdatesScriptBlock
$installBlock = Get-N4InstallUpdatesScriptBlock

$serverResults = @{}

foreach ($server in $allServers) {
    Write-N4Log -Message "--- Serveur : $server ---" -Level ACTION
    $pass = 0
    $done = $false

    while (-not $done -and $pass -lt $MaxUpdatePasses) {
        $pass++
        Write-N4Log -Message "[$server] Passe $pass/$MaxUpdatePasses - recherche des mises a jour disponibles..." -Level ACTION

        try {
            $pending = Invoke-Command -ComputerName $server @icmParams -ScriptBlock $searchBlock -ErrorAction Stop
        } catch {
            Write-N4Log -Message "[$server] ECHEC de connexion pour la recherche de mises a jour : $($_.Exception.Message)" -Level ERROR
            $serverResults[$server] = "ECHEC (connexion)"
            break
        }

        if (-not $pending.Success) {
            Write-N4Log -Message "[$server] ECHEC recherche mises a jour : $($pending.Error)" -Level ERROR
            $serverResults[$server] = "ECHEC (recherche)"
            break
        }

        if ($pending.Count -eq 0) {
            Write-N4Log -Message "[$server] Aucune mise a jour en attente. Serveur a jour." -Level OK
            $serverResults[$server] = "A JOUR"
            $done = $true
            break
        }

        Write-N4Log -Message "[$server] $($pending.Count) mise(s) a jour trouvee(s) :" -Level WARN
        foreach ($t in $pending.Titles) { Write-N4Log -Message "[$server]    - $t" -Level INFO }

        Write-N4Log -Message "[$server] Telechargement et installation en cours (cela peut prendre plusieurs minutes)..." -Level ACTION
        try {
            $installResult = Invoke-Command -ComputerName $server @icmParams -ScriptBlock $installBlock -ErrorAction Stop
        } catch {
            Write-N4Log -Message "[$server] ECHEC de connexion pour l'installation : $($_.Exception.Message)" -Level ERROR
            $serverResults[$server] = "ECHEC (connexion install)"
            break
        }

        if (-not $installResult.Success) {
            Write-N4Log -Message "[$server] ECHEC installation : $($installResult.Error)" -Level ERROR
            $serverResults[$server] = "ECHEC (installation)"
            break
        }

        Write-N4Log -Message "[$server] Installees : $($installResult.InstalledCount) | Echouees : $($installResult.FailedCount) | Redemarrage requis : $($installResult.RebootRequired)" -Level OK

        if ($installResult.RebootRequired) {
            Write-N4Log -Message "[$server] Redemarrage automatique du serveur en cours (attente de son retour sur WinRM, jusqu'a 30 min)..." -Level ACTION
            try {
                Restart-Computer -ComputerName $server @icmParams -Force -Wait -For WinRM -Timeout 1800 -Delay 5 -ErrorAction Stop
                Write-N4Log -Message "[$server] Serveur de nouveau joignable apres redemarrage." -Level OK
            } catch {
                Write-N4Log -Message "[$server] ECHEC ou timeout lors du redemarrage : $($_.Exception.Message)" -Level ERROR
                $serverResults[$server] = "ECHEC (redemarrage)"
                break
            }
        }
        # Boucle : on relance une recherche pour verifier s'il reste des mises a jour (dependances en cascade)
    }

    if (-not $serverResults.ContainsKey($server)) {
        Write-N4Log -Message "[$server] Nombre maximum de passes ($MaxUpdatePasses) atteint sans confirmation 'a jour'. A verifier manuellement." -Level WARN
        $serverResults[$server] = "INCOMPLET (max passes atteint)"
    }
}

# ---- Bilan ----
Write-N4Log -Message "===== BILAN DU PATCHING =====" -Level ACTION
$allOk = $true
foreach ($server in $allServers) {
    $status = $serverResults[$server]
    $level = if ($status -eq "A JOUR") { "OK" } else { "ERROR" }
    if ($status -ne "A JOUR") { $allOk = $false }
    Write-N4Log -Message "[$server] -> $status" -Level $level
}

if (-not $allOk) {
    Write-N4Instruction -Title "REDEMARRAGE N4 NON DECLENCHE" -Level ERROR -Lines @(
        "Au moins un serveur n'est PAS confirme a jour.",
        "Start-N4Sequence.ps1 n'est pas appele : relancer N4 sur un parc dont l'etat de",
        "patching est incertain revient a demarrer sans savoir dans quel etat sont les serveurs.",
        "Corriger manuellement, puis relancer Start-N4Sequence.ps1 explicitement.",
        "Log complet de cette session : $logFile"
    )
    return
}

Write-N4Log -Message "Tous les serveurs sont confirmes a jour." -Level OK

if ($SkipAutoStart) {
    Write-N4Log -Message "-SkipAutoStart specifie : Start-N4Sequence.ps1 n'est pas appele automatiquement. Reprise manuelle requise." -Level INFO
    Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
    return
}

Write-N4Log -Message "===== REPRISE AUTOMATIQUE DU DEMARRAGE N4 =====" -Level ACTION
$startArgs = @{}
if ($Credential) { $startArgs["Credential"] = $Credential }
if ($Unattended) { $startArgs["Unattended"] = $true }
& "$PSScriptRoot\Start-N4Sequence.ps1" @startArgs

Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
