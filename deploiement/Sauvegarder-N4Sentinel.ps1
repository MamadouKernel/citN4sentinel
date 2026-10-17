<#
.SYNOPSIS
    Sauvegarde coherente de N4 Sentinel en mode SQLite (base + trousseau de cles).

.DESCRIPTION
    Produit une archive datee contenant la base SQLite ET le dossier des cles
    DataProtection : les deux sont INDISSOCIABLES. Sans le trousseau, une base
    restauree ailleurs rend les mots de passe des comptes techniques illisibles.

    Coherence : par defaut le service est arrete le temps de la copie, puis
    redemarre. SQLite ecrit en continu (fichiers -wal/-shm) ; copier la base a
    chaud peut produire une archive incoherente. L'arret dure quelques secondes.
    Pour une sauvegarde SANS interruption, l'application expose /admin/sauvegarde
    (VACUUM INTO a chaud) ; ce script en est le pendant planifiable.

    Rotation : ne conserve que les N dernieres archives (30 par defaut).

.PARAMETER DataDir
    Dossier de donnees (base + trousseau). C:\ProgramData\N4Sentinel par defaut.

.PARAMETER Destination
    Dossier ou deposer les archives. Idealement un partage reseau distant
    (ex. \\SAUVEGARDE\N4Sentinel), pas le meme disque que la base.

.PARAMETER NomService
    Nom du service Windows. 'N4Sentinel' par defaut.

.PARAMETER Retention
    Nombre d'archives a conserver. 30 par defaut.

.PARAMETER SansArret
    Copie la base SANS arreter le service (inclut -wal/-shm). Plus rapide, mais
    la coherence n'est PAS garantie ; a reserver aux environnements de test.

.PARAMETER Planifier
    Au lieu de sauvegarder maintenant, cree (ou remplace) une tache planifiee
    quotidienne qui lancera CE script chaque jour, sous le compte SYSTEM. Les
    options -Destination, -NomService, -Retention et -SansArret sont reportees
    dans la tache. A lancer en PowerShell ELEVE, depuis l'emplacement definitif
    du script (la tache memorise son chemin).

.PARAMETER HeurePlanifiee
    Heure de la sauvegarde quotidienne (format HH:mm). 02:00 par defaut.

.PARAMETER NomTache
    Nom de la tache planifiee. 'N4Sentinel-Sauvegarde' par defaut.

.EXAMPLE
    .\Sauvegarder-N4Sentinel.ps1 -Destination \\SAUVEGARDE\N4Sentinel

.EXAMPLE
    # Installe la sauvegarde quotidienne automatique (02h00, compte SYSTEM) :
    .\Sauvegarder-N4Sentinel.ps1 -Destination \\SAUVEGARDE\N4Sentinel -Planifier
#>
[CmdletBinding()]
param(
    [string]$DataDir = 'C:\ProgramData\N4Sentinel',
    [Parameter(Mandatory)]
    [string]$Destination,
    [string]$NomService = 'N4Sentinel',
    [int]$Retention = 30,
    [switch]$SansArret,
    [switch]$Planifier,
    [string]$HeurePlanifiee = '02:00',
    [string]$NomTache = 'N4Sentinel-Sauvegarde'
)

$ErrorActionPreference = 'Stop'

# --- Mode planification : installe la tache et sort -------------------------
if ($Planifier) {
    $estAdmin = ([Security.Principal.WindowsPrincipal] `
        [Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $estAdmin) {
        throw "La creation d'une tache planifiee exige une console PowerShell ELEVEE (Administrateur)."
    }

    # Chemin definitif de CE script : la tache l'appellera a cet emplacement.
    $moi = $PSCommandPath

    # Arguments reportes dans la tache. Les chemins sont entre guillemets pour
    # tolerer les espaces.
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Destination "{1}" -NomService "{2}" -Retention {3}' `
        -f $moi, $Destination, $NomService, $Retention
    if ($SansArret) { $arguments += ' -SansArret' }

    $action    = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments
    $trigger   = New-ScheduledTaskTrigger -Daily -At $HeurePlanifiee
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $parametres = New-ScheduledTaskSettingsSet -StartWhenAvailable -DontStopOnIdleEnd

    Register-ScheduledTask -TaskName $NomTache -Action $action -Trigger $trigger `
        -Principal $principal -Settings $parametres -Force | Out-Null

    Write-Host "Tache planifiee '$NomTache' creee : chaque jour a $HeurePlanifiee, sous SYSTEM." -ForegroundColor Green
    Write-Host "  Script    : $moi"
    Write-Host "  Vers      : $Destination"
    Write-Host "  Retention : $Retention archives"
    Write-Host "Le compte SYSTEM doit pouvoir ecrire sur la destination (sinon, editez la tache sous un compte habilite)." -ForegroundColor Yellow
    return
}

function Journal($texte) {
    $ligne = '{0}  {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $texte
    Write-Host $ligne
    try { Add-Content -Path (Join-Path $Destination 'sauvegarde.log') -Value $ligne -Encoding UTF8 } catch { }
}

$base = Join-Path $DataDir 'n4sentinel.db'
$cles = Join-Path $DataDir 'cles-protection'

if (-not (Test-Path $base)) { throw "Base introuvable : $base. Verifiez -DataDir." }
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$horodatage = Get-Date -Format 'yyyyMMdd-HHmmss'
$staging    = Join-Path $env:TEMP "n4sentinel-backup-$horodatage"
$archive    = Join-Path $Destination "N4Sentinel-backup-$horodatage.zip"

Journal "Debut de sauvegarde vers $archive"

# --- Arret du service (coherence), sauf si -SansArret ------------------------
$service = Get-Service -Name $NomService -ErrorAction SilentlyContinue
$aRedemarrer = $false
if (-not $SansArret -and $service -and $service.Status -eq 'Running') {
    Journal "Arret du service $NomService pour copie coherente..."
    Stop-Service -Name $NomService -Force
    $service.WaitForStatus('Stopped', '00:02:00')
    $aRedemarrer = $true
}

try {
    New-Item -ItemType Directory -Force -Path $staging | Out-Null

    # La base, plus les journaux WAL/SHM s'ils existent (mode WAL).
    Copy-Item $base $staging -Force
    foreach ($suffixe in '-wal', '-shm') {
        $f = "$base$suffixe"
        if (Test-Path $f) { Copy-Item $f $staging -Force }
    }

    # Le trousseau de cles, INDISSOCIABLE de la base.
    if (Test-Path $cles) {
        Copy-Item $cles (Join-Path $staging 'cles-protection') -Recurse -Force
    }
    else {
        Journal "ATTENTION : trousseau introuvable ($cles) ; archive SANS les cles."
    }
}
finally {
    # Redemarrage AVANT la compression : minimise l'indisponibilite.
    if ($aRedemarrer) {
        Start-Service -Name $NomService
        (Get-Service -Name $NomService).WaitForStatus('Running', '00:02:00')
        Journal "Service $NomService redemarre."
    }
}

# --- Compression -------------------------------------------------------------
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $archive -CompressionLevel Optimal
Remove-Item $staging -Recurse -Force

$tailleMo = [math]::Round((Get-Item $archive).Length / 1MB, 1)
Journal "Archive creee : $archive ($tailleMo Mo)"

# --- Rotation : ne garder que les N dernieres --------------------------------
$archives = Get-ChildItem $Destination -Filter 'N4Sentinel-backup-*.zip' |
    Sort-Object LastWriteTime -Descending
if ($archives.Count -gt $Retention) {
    $archives | Select-Object -Skip $Retention | ForEach-Object {
        Remove-Item $_.FullName -Force
        Journal "Rotation : ancienne archive supprimee ($($_.Name))"
    }
}

Journal "Sauvegarde terminee."
