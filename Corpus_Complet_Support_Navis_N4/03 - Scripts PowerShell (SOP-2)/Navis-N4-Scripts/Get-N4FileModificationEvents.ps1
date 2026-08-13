<#
.SYNOPSIS
    Consulte le Journal des evenements Windows > Securite pour lister QUI a
    modifie les scripts N4, QUAND, et DEPUIS QUELLE IP (si determinable).

.DESCRIPTION
    Necessite qu'Enable-N4FileAuditing.ps1 ait ete execute au prealable -
    sans audit actif, aucun evenement n'existe a lire (Windows n'enregistre
    rien par defaut).
    Lit les evenements Object Access (ID 4663) filtres sur le dossier des
    scripts, et tente de retrouver l'adresse IP source en correlant avec
    l'evenement de connexion (ID 4624) via le meme identifiant de session
    (Logon ID).

.NOTES
    Copyright : (c) KMKernel
    LIMITE HONNETE : pour un acces LOCAL (console/RDP direct sur la
    machine), le champ IP de l'evenement 4624 correspondant est souvent
    "-", "127.0.0.1" ou "::1" - c'est normal, pas une erreur du script :
    une session locale n'a pas d'adresse IP source distante par definition.
    L'IP n'est significative que pour un acces via partage reseau (SMB).

.PARAMETER Days
    Nombre de jours en arriere a consulter (defaut : 30).
.PARAMETER TargetFolder
    Dossier surveille. Par defaut, le dossier de ce script.

.EXAMPLE
    .\Get-N4FileModificationEvents.ps1
    .\Get-N4FileModificationEvents.ps1 -Days 7
#>

[CmdletBinding()]
param(
    [int]$Days = 30,
    [string]$TargetFolder = $PSScriptRoot
)

Import-Module "$PSScriptRoot\Navis-Common.psm1" -Force
$logFile = New-N4LogSession -ScriptName "Get-N4FileModificationEvents"

if (-not (Test-N4Integrity -ScriptPath $PSCommandPath)) { return }

Write-N4Log -Message "===== Consultation des evenements de modification de fichiers =====" -Level ACTION
Write-N4Log -Message "Dossier surveille : $TargetFolder | Periode : $Days derniers jours" -Level INFO

$startTime = (Get-Date).AddDays(-$Days)

# ---- Recuperation des evenements d'acces objet (4663) ----
try {
    $accessEvents = Get-WinEvent -FilterHashtable @{ LogName = "Security"; Id = 4663; StartTime = $startTime } -ErrorAction Stop
} catch {
    Write-N4Log -Message "Aucun evenement 4663 trouve, ou audit non actif. Avez-vous execute Enable-N4FileAuditing.ps1 au prealable ? ($($_.Exception.Message))" -Level WARN
    Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
    return
}

# Filtrer sur le dossier cible et exclure les evenements de liste de repertoire (bruit)
$relevant = @()
foreach ($evt in $accessEvents) {
    $xml = [xml]$evt.ToXml()
    $data = @{}
    foreach ($d in $xml.Event.EventData.Data) { $data[$d.Name] = $d.'#text' }

    if ($data.ObjectName -like "$TargetFolder*") {
        $relevant += [PSCustomObject]@{
            TimeCreated = $evt.TimeCreated
            User        = "$($data.SubjectDomainName)\$($data.SubjectUserName)"
            LogonId     = $data.SubjectLogonId
            File        = $data.ObjectName
            AccessMask  = $data.AccessMask
        }
    }
}

if ($relevant.Count -eq 0) {
    Write-N4Log -Message "Aucune modification enregistree sur $TargetFolder au cours des $Days derniers jours." -Level OK
    Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
    return
}

Write-N4Log -Message "$($relevant.Count) evenement(s) d'acces trouve(s). Correlation avec les evenements de connexion (4624) pour retrouver l'IP..." -Level ACTION

# ---- Recuperation des evenements de connexion (4624) pour correlation IP ----
$logonEvents = @{}
try {
    $rawLogons = Get-WinEvent -FilterHashtable @{ LogName = "Security"; Id = 4624; StartTime = $startTime } -ErrorAction Stop
    foreach ($evt in $rawLogons) {
        $xml = [xml]$evt.ToXml()
        $data = @{}
        foreach ($d in $xml.Event.EventData.Data) { $data[$d.Name] = $d.'#text' }
        if ($data.TargetLogonId) {
            $logonEvents[$data.TargetLogonId] = $data.IpAddress
        }
    }
} catch {
    Write-N4Log -Message "Impossible de recuperer les evenements de connexion (4624) pour correlation IP : $($_.Exception.Message)" -Level WARN
}

# ---- Fusion et affichage ----
Write-N4Log -Message "===== RESULTATS =====" -Level ACTION
foreach ($r in $relevant) {
    $ip = $logonEvents[$r.LogonId]
    if (-not $ip -or $ip -eq "-") { $ip = "Non disponible (acces local ou correlation impossible)" }
    elseif ($ip -eq "127.0.0.1" -or $ip -eq "::1") { $ip = "Local (127.0.0.1 / ::1 - session sur la machine elle-meme)" }

    $line = "$($r.TimeCreated) | Utilisateur: $($r.User) | Fichier: $($r.File) | IP: $ip"
    Write-N4Log -Message $line -Level WARN
}

Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
