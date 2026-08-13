<#
.SYNOPSIS
    Premier reflexe a executer des qu'un incident Navis N4 est signale,
    AVANT de plonger dans la lecture des logs applicatifs.

.DESCRIPTION
    Automatise l'Etape 0 documentee dans SOP-0 / le manuel de diagnostic :
      - Reseau : chaque serveur repond-il (ping + PowerShell Remoting) ?
      - Horloges : sont-elles synchronisees a moins d'1 seconde d'ecart
        (cause frequente de faux statuts DISCONNECTED et de P1) ?
      - Espace disque : de la marge sur chaque serveur ?
      - Base de donnees : le serveur/port repond-il (test de connectivite
        reseau uniquement, aucun identifiant utilise) ?

    Ce script NE remplace PAS le diagnostic detaille des fiches SOP-1/SOP-2 :
    il sert a eliminer rapidement les causes transverses avant de s'engager
    dans une investigation plus longue (lecture de logs, JMX, etc.).

.EXAMPLE
    .\Test-N4IncidentPreCheck.ps1
    .\Test-N4IncidentPreCheck.ps1 -Credential (Get-Credential)
.NOTES
    Copyright : (c) KMKernel

#>

[CmdletBinding()]
param(
    [System.Management.Automation.PSCredential]$Credential
)

Import-Module "$PSScriptRoot\Navis-Common.psm1" -Force
$logFile = New-N4LogSession -ScriptName "Test-N4IncidentPreCheck"

if (-not (Test-N4Integrity -ScriptPath $PSCommandPath)) { return }

$cfg = $Global:N4Config
$icmParams = @{}
if ($Credential) { $icmParams["Credential"] = $Credential }

$allServers = @($cfg.CenterNode, $cfg.StandbyNode, $cfg.BridgeHost, $cfg.XPSHost, $cfg.ECN4Host) + $cfg.ClusterNodes
$allServers = $allServers | Sort-Object -Unique

$issues = @()

Write-N4Log -Message "===== PRE-CHECK INCIDENT : reseau / horloges / disque / base de donnees =====" -Level ACTION
Write-N4Log -Message "A executer AVANT la lecture des logs applicatifs (Etape 0, SOP-0)." -Level INFO

# ============================================================
# 1. RESEAU
# ============================================================
Write-N4Log -Message "--- 1/4 : Reseau (ping + WinRM) ---" -Level ACTION
foreach ($server in $allServers) {
    $test = Test-N4ServerReachable -ComputerName $server -IcmParams $icmParams
    if ($test.Reachable) {
        $lat = if ($test.LatencyMs) { "$($test.LatencyMs) ms" } else { "n/d" }
        Write-N4Log -Message "$server : OK (ping=$($test.PingOk), latence=$lat, WinRM=$($test.WinRMOk))" -Level OK
    } else {
        Write-N4Log -Message "$server : PROBLEME RESEAU (ping=$($test.PingOk), WinRM=$($test.WinRMOk))" -Level ERROR
        $issues += "$server injoignable (reseau/WinRM)"
    }
}

# ============================================================
# 2. SYNCHRONISATION DES HORLOGES
# ============================================================
Write-N4Log -Message "--- 2/4 : Synchronisation des horloges (seuil : moins d'1 seconde d'ecart) ---" -Level ACTION
$localTime = Get-Date
foreach ($server in $allServers) {
    try {
        $remoteTime = Invoke-Command -ComputerName $server @icmParams -ScriptBlock { Get-Date } -ErrorAction Stop
        $diffSec = [Math]::Abs(($remoteTime - $localTime).TotalSeconds)
        if ($diffSec -lt 1) {
            Write-N4Log -Message "$server : horloge synchronisee (ecart ${diffSec}s)." -Level OK
        } else {
            Write-N4Log -Message "$server : ECART D'HORLOGE de $([Math]::Round($diffSec,2))s - cause frequente de statuts DISCONNECTED trompeurs et de P1." -Level WARN
            $issues += "$server ecart horloge ${diffSec}s"
        }
    } catch {
        Write-N4Log -Message "$server : impossible de verifier l'horloge ($($_.Exception.Message))." -Level ERROR
        $issues += "$server horloge non verifiable"
    }
}

# ============================================================
# 3. ESPACE DISQUE
# ============================================================
Write-N4Log -Message "--- 3/4 : Espace disque (lecteur systeme C:) ---" -Level ACTION
foreach ($server in $allServers) {
    try {
        $disk = Invoke-Command -ComputerName $server @icmParams -ScriptBlock {
            Get-CimInstance -ClassName Win32_LogicalDisk -Filter "DeviceID='C:'" |
                Select-Object -Property @{N = "FreeGB"; E = { [Math]::Round($_.FreeSpace / 1GB, 1) } },
                                          @{N = "TotalGB"; E = { [Math]::Round($_.Size / 1GB, 1) } }
        } -ErrorAction Stop

        $pctFree = if ($disk.TotalGB -gt 0) { [Math]::Round(($disk.FreeGB / $disk.TotalGB) * 100, 0) } else { 0 }
        if ($pctFree -lt 10) {
            Write-N4Log -Message "$server : ESPACE DISQUE FAIBLE - $($disk.FreeGB) Go libres / $($disk.TotalGB) Go ($pctFree%)." -Level ERROR
            $issues += "$server disque a $pctFree% libre"
        } elseif ($pctFree -lt 20) {
            Write-N4Log -Message "$server : espace disque a surveiller - $($disk.FreeGB) Go libres / $($disk.TotalGB) Go ($pctFree%)." -Level WARN
        } else {
            Write-N4Log -Message "$server : espace disque OK - $($disk.FreeGB) Go libres / $($disk.TotalGB) Go ($pctFree%)." -Level OK
        }
    } catch {
        Write-N4Log -Message "$server : impossible de verifier l'espace disque ($($_.Exception.Message))." -Level ERROR
        $issues += "$server disque non verifiable"
    }
}

# ============================================================
# 4. BASE DE DONNEES (test de connectivite reseau uniquement)
# ============================================================
Write-N4Log -Message "--- 4/4 : Base de donnees ($($cfg.DatabaseEngine) sur $($cfg.DatabaseHost):$($cfg.DatabasePort)) ---" -Level ACTION
$dbTest = Test-N4TcpPort -ComputerName $cfg.DatabaseHost -Port $cfg.DatabasePort -TimeoutMs 3000
if ($dbTest.Success) {
    Write-N4Log -Message "Base de donnees $($cfg.DatabaseHost):$($cfg.DatabasePort) : port ouvert, serveur joignable." -Level OK
} else {
    Write-N4Log -Message "Base de donnees $($cfg.DatabaseHost):$($cfg.DatabasePort) : INJOIGNABLE ($($dbTest.Error))." -Level ERROR
    $issues += "Base de donnees injoignable ($($cfg.DatabaseHost):$($cfg.DatabasePort))"
}
Write-N4Log -Message "Rappel : ce test verifie uniquement que le port reseau repond, pas que les identifiants ou le pool de connexions N4 fonctionnent (voir SOP-2, Fiche E pour aller plus loin)." -Level INFO

# ============================================================
# BILAN
# ============================================================
Write-N4Log -Message "===== BILAN DU PRE-CHECK =====" -Level ACTION
if ($issues.Count -eq 0) {
    Write-N4Log -Message "Aucune anomalie transverse detectee (reseau, horloges, disque, base de donnees). La cause est probablement plus specifique : passer a la lecture des logs applicatifs (SOP-1/SOP-2)." -Level OK
} else {
    Write-N4Log -Message "$($issues.Count) anomalie(s) detectee(s) AVANT meme de lire les logs :" -Level WARN
    foreach ($i in $issues) { Write-N4Log -Message "  - $i" -Level WARN }
    Write-N4Log -Message "Traiter ces points en premier : ils peuvent expliquer directement l'incident signale (voir SOP-0, Etape 0 et Regles generales d'escalade)." -Level WARN
}

Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
