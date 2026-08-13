<#
.SYNOPSIS
    Resout un incident de type Fiche A (SOP-2) : services N4 qui ne demarrent
    pas suite a une corruption du dossier partage amq (fichier db.data).

.DESCRIPTION
    Reproduit la procedure documentee : arret propre des services, sauvegarde
    puis suppression de db.data, redemarrage dans l'ordre.
    Il s'agit d'une action DESTRUCTIVE sur un fichier de persistance : elle
    exige une confirmation explicite et ne doit jamais etre lancee sans avoir
    confirme la cause.

    CE QUI A CHANGE PAR RAPPORT A LA VERSION PRECEDENTE
    ---------------------------------------------------
    L'ancienne version appelait Stop-N4Sequence.ps1 puis supprimait db.data
    immediatement apres, sans verifier que les services etaient reellement
    arretes. Or la sequence d'arret peut se terminer en signalant qu'un
    service n'a pas confirme son arret. Supprimer le fichier de persistance
    pendant qu'une JVM le tient encore ouvert, c'est ajouter une corruption
    a une corruption.
    Trois portillons ont ete ajoutes avant toute suppression :
      1. tous les services N4 confirmes Stopped sur tous les serveurs ;
      2. le fichier db.data n'est plus verrouille par aucun processus ;
      3. la sauvegarde est verifiee (existence + taille identique) AVANT
         que l'original ne soit supprime.

.NOTES
    Copyright : (c) KMKernel

    PREREQUIS
      - Executer depuis un compte Administrateur ayant acces en ecriture au
        dossier partage (UNC) defini dans Navis-Config.json (SharedFolder/amq).
      - Ne PAS executer sans avoir confirme la cause : logs contenant
        IOException ou NegativeArraySizeException (voir SOP-2, Fiche A).
      - Verifier qu'une sauvegarde exploitable de l'environnement existe.

.EXAMPLE
    .\Fix-N4-AMQCorruption.ps1
#>

[CmdletBinding()]
param(
    [System.Management.Automation.PSCredential]$Credential
)

Import-Module "$PSScriptRoot\Navis-Common.psm1" -Force
$logFile = New-N4LogSession -ScriptName "Fix-N4-AMQCorruption"

if (-not (Test-N4Integrity -ScriptPath $PSCommandPath)) { return }
$cfg = $Global:N4Config
$icmParams = @{}
if ($Credential) { $icmParams["Credential"] = $Credential }

Write-N4Log -Message "===== FICHE A : Resolution corruption dossier amq =====" -Level ACTION
Write-N4Instruction -Title "CONFIRMER LA CAUSE AVANT D'ALLER PLUS LOIN" -Level WARN -Lines @(
    "Cette procedure supprime un fichier de persistance ActiveMQ. Elle ne se justifie",
    "que si la corruption est etablie, pas seulement suspectee.",
    "A confirmer dans navis-apex.log :",
    "  - IOException sur le dossier amq, ou",
    "  - NegativeArraySizeException au demarrage du Center.",
    "Si vous ne retrouvez PAS ces signatures : arreter ce script et diagnostiquer",
    "d'abord (SOP-2, Fiche A). Supprimer db.data sans cause etablie fait perdre",
    "les messages non consommes pour rien.",
    "En cas de doute, ou si la corruption touche les journaux KahaDB eux-memes :",
    "escalader avant toute correction destructive."
)

$amqPath    = Join-Path $cfg.SharedFolder "amq"
$dbDataPath = Join-Path $amqPath "db.data"
$backupPath = Join-Path $amqPath ("db.data.backup_" + (Get-Date -Format "yyyyMMdd_HHmmss"))

Write-N4Log -Message "Dossier amq cible : $amqPath" -Level INFO

if (-not (Test-Path $dbDataPath)) {
    Write-N4Log -Message "Fichier db.data introuvable a l'emplacement attendu ($dbDataPath)." -Level ERROR
    Write-N4Log -Message "Verifier la valeur de SharedFolder dans Navis-Config.json, et que le compte courant a bien acces a ce partage. Arret du script." -Level ERROR
    return
}

$tailleOrigine = (Get-Item $dbDataPath).Length
Write-N4Log -Message "Fichier cible : $dbDataPath ($([Math]::Round($tailleOrigine / 1MB, 2)) Mo)." -Level INFO

if (-not (Confirm-N4Action -Prompt "Confirmez-vous l'arret complet de N4 puis la suppression de db.data ?")) {
    Write-N4Log -Message "Action annulee par l'utilisateur. Aucun service n'a ete touche." -Level WARN
    return
}

# ---- 1. Arret propre de tous les services ----
Write-N4Log -Message "--- Etape 1/5 : Arret de tous les services N4 ---" -Level ACTION
& "$PSScriptRoot\Stop-N4Sequence.ps1" -Credential $Credential -Unattended
Write-N4Log -Message "Sequence d'arret terminee. Verification independante de l'etat reel des services." -Level INFO

# ---- 2. Portillon : TOUS les services sont-ils reellement arretes ? ----
Write-N4Log -Message "--- Etape 2/5 : Confirmation que plus aucun service ne tourne ---" -Level ACTION
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
    Write-N4Instruction -Title "SUPPRESSION REFUSEE" -Level ERROR -Lines @(
        "Au moins un service N4 n'est pas confirme a l'arret.",
        "db.data ne sera PAS touche : supprimer un fichier de persistance pendant",
        "qu'une JVM le tient encore ouvert ajoute une corruption a la corruption.",
        "Traiter manuellement les services concernes, confirmer leur arret,",
        "puis relancer ce script.",
        "Log complet de cette session : $logFile"
    )
    return
}
Write-N4Log -Message "Tous les services sont confirmes a l'arret." -Level OK

# ---- 3. Portillon : le fichier est-il encore verrouille ? ----
Write-N4Log -Message "--- Etape 3/5 : Verification que db.data n'est plus verrouille ---" -Level ACTION
$verrouille = $true
try {
    $fs = [System.IO.File]::Open($dbDataPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $fs.Close()
    $verrouille = $false
    Write-N4Log -Message "Le fichier n'est verrouille par aucun processus : suppression possible." -Level OK
} catch {
    Write-N4Log -Message "Le fichier db.data est encore verrouille : $($_.Exception.Message)" -Level ERROR
}

if ($verrouille) {
    Write-N4Instruction -Title "SUPPRESSION REFUSEE - FICHIER VERROUILLE" -Level ERROR -Lines @(
        "Les services declarent etre arretes mais un processus tient encore db.data ouvert.",
        "Pistes, dans cet ordre :",
        "  1. Un processus java residuel sur le Center ou le Standby (a verifier via",
        "     le Gestionnaire des taches sur les serveurs concernes).",
        "  2. Un antivirus en train de scanner le dossier amq - cause n.1 de corruption",
        "     ET de verrous parasites sur ce dossier.",
        "  3. Une session SMB ouverte sur le partage depuis un autre poste.",
        "Ne pas forcer. Relancer ce script une fois le verrou libere.",
        "Log complet de cette session : $logFile"
    )
    return
}

# ---- 4. Sauvegarde VERIFIEE, puis suppression ----
Write-N4Log -Message "--- Etape 4/5 : Sauvegarde verifiee puis suppression de db.data ---" -Level ACTION
try {
    Copy-Item -Path $dbDataPath -Destination $backupPath -ErrorAction Stop

    if (-not (Test-Path $backupPath)) { throw "La sauvegarde $backupPath n'existe pas apres la copie." }
    $tailleBackup = (Get-Item $backupPath).Length
    if ($tailleBackup -ne $tailleOrigine) {
        throw "La sauvegarde est incomplete : $tailleBackup octets copies pour $tailleOrigine attendus."
    }
    Write-N4Log -Message "Sauvegarde creee et verifiee : $backupPath ($([Math]::Round($tailleBackup / 1MB, 2)) Mo, taille identique a l'original)." -Level OK

    Remove-Item -Path $dbDataPath -ErrorAction Stop
    Write-N4Log -Message "Fichier db.data supprime. Il sera reconstruit automatiquement au prochain demarrage." -Level OK
} catch {
    Write-N4Instruction -Title "ECHEC - AUCUNE SUPPRESSION EFFECTUEE" -Level ERROR -Lines @(
        "Motif : $($_.Exception.Message)",
        "L'original n'a pas ete supprime tant que la sauvegarde n'etait pas verifiee.",
        "Verifier l'espace disponible sur le partage et les droits en ecriture du compte courant.",
        "NE PAS redemarrer les services tant que ce point n'est pas resolu.",
        "Log complet de cette session : $logFile"
    )
    return
}

# ---- 5. Redemarrage dans l'ordre ----
Write-N4Log -Message "--- Etape 5/5 : Redemarrage complet dans l'ordre ---" -Level ACTION
Write-N4Log -Message "Rappel : verifier que $($cfg.SharedFolder) et les dossiers Program Data\Navis de chaque serveur sont exclus des scans antivirus - c'est la cause n.1 de recidive." -Level WARN
& "$PSScriptRoot\Start-N4Sequence.ps1" -Credential $Credential

Write-N4Log -Message "===== FIN FICHE A =====" -Level ACTION
Write-N4Instruction -Title "APRES LA REMISE EN SERVICE" -Level WARN -Lines @(
    "1. Cluster Services : tous les noeuds ACTIVE.",
    "2. Files bridge.* : ConsumerCount > 0, QueueSize qui ne s'accumule pas.",
    "3. Synchronisation N4/XPS reverifiee par un test de propagation.",
    "4. Sauvegarde conservee : $backupPath",
    "   Ne pas la supprimer avant confirmation que l'environnement est stable.",
    "5. Traiter la cause racine (exclusions antivirus, espace disque, stabilite du partage),",
    "   faute de quoi l'incident reviendra.",
    "Si l'incident persiste, ouvrir un ticket Navis avec ce log : $logFile"
)
