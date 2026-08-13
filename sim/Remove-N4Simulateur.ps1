<#
.SYNOPSIS
    Supprime l'environnement N4 simule : journaux, configuration et faux
    services Windows.

.DESCRIPTION
    Ne touche qu'a ce que New-N4Simulateur.ps1 a cree. Les services simules
    portent tous le prefixe "N4Sim " : aucun service reel ne peut etre atteint
    par ce script, meme si un vrai N4 etait installe sur la machine.

.PARAMETER Racine
    Racine du simulateur. Defaut : C:\N4Simulateur

.PARAMETER ConserverJournaux
    Supprime les services mais garde l'arborescence des journaux.

.EXAMPLE
    .\Remove-N4Simulateur.ps1

.NOTES
    Copyright : (c) KMKernel
#>

[CmdletBinding()]
param(
    [string]$Racine = "C:\N4Simulateur",
    [switch]$ConserverJournaux
)

$ErrorActionPreference = "Stop"

Write-Host "`n=== Suppression du simulateur ===" -ForegroundColor Cyan

# --- Services -------------------------------------------------------------
# Le filtre sur le prefixe est le garde-fou : il rend impossible la
# suppression d'un service Navis reel par mauvaise manipulation.
$services = Get-Service -Name "N4Sim *" -ErrorAction SilentlyContinue

if ($services) {
    $estAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
                ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

    if (-not $estAdmin) {
        Write-Host "  !    $($services.Count) service(s) simule(s) presents, mais la suppression exige" -ForegroundColor Yellow
        Write-Host "       une console ADMINISTRATEUR. Les journaux peuvent etre supprimes sans privilege." -ForegroundColor Yellow
    } else {
        foreach ($s in $services) {
            if ($s.Status -ne 'Stopped') {
                Stop-Service -Name $s.Name -Force -ErrorAction SilentlyContinue
            }
            Remove-Service -Name $s.Name -ErrorAction SilentlyContinue
            Write-Host "  OK   service supprime : $($s.Name)" -ForegroundColor Green
        }
    }
} else {
    Write-Host "  ...  aucun service simule" -ForegroundColor Gray
}

# --- Journaux et configuration --------------------------------------------
if ($ConserverJournaux) {
    Write-Host "  ...  journaux conserves ($Racine)" -ForegroundColor Gray
} elseif (Test-Path $Racine) {
    Remove-Item -Path $Racine -Recurse -Force
    Write-Host "  OK   arborescence supprimee : $Racine" -ForegroundColor Green
} else {
    Write-Host "  ...  aucune arborescence a supprimer" -ForegroundColor Gray
}

Write-Host "`nTermine.`n" -ForegroundColor Cyan
