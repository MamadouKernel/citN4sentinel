<#
.SYNOPSIS
    Regenere Navis-Integrity.json (les empreintes de reference de tous les
    scripts) apres une modification LEGITIME du code.

.DESCRIPTION
    Necessite le code PIN valide - sans quoi n'importe qui pourrait modifier
    un script puis regenerer le manifeste pour faire disparaitre l'alerte
    d'integrite, ce qui viderait la protection de son sens.
    A executer par le proprietaire du code (KMKernel) apres toute mise a
    jour volontaire d'un script.

.NOTES
    Copyright : (c) KMKernel

.EXAMPLE
    .\New-N4IntegrityManifest.ps1
#>

[CmdletBinding()]
param()

Import-Module "$PSScriptRoot\Navis-Common.psm1" -Force
$logFile = New-N4LogSession -ScriptName "New-N4IntegrityManifest"

if (-not (Test-N4Integrity -ScriptPath $PSCommandPath)) { return }

$protectionPath = Join-Path $PSScriptRoot "Navis-Protection.json"
$manifestPath   = Join-Path $PSScriptRoot "Navis-Integrity.json"

if (-not (Test-Path $protectionPath)) {
    Write-N4Log -Message "Fichier Navis-Protection.json introuvable. Arret du script." -Level ERROR
    return
}
$protection = Get-Content -Path $protectionPath -Raw -Encoding UTF8 | ConvertFrom-Json

Write-N4Log -Message "===== Regeneration du manifeste d'integrite =====" -Level ACTION
Write-N4Log -Message "Cette operation doit etre reservee au proprietaire du code, apres une modification volontaire et verifiee." -Level WARN

$securePin = Read-Host -Prompt "Code PIN (6 chiffres) pour confirmer la regeneration" -AsSecureString
$enteredHash = Get-N4PinHash -SecurePin $securePin

if ($enteredHash -ine $protection.CopyrightPinHash) {
    Write-N4Log -Message "Code PIN incorrect. ARRET - manifeste non modifie." -Level ERROR
    return
}
Write-N4Log -Message "Code PIN valide." -Level OK

# Liste des fichiers proteges (scripts logiques uniquement - PAS les .json
# de configuration, qui restent volontairement libres d'edition)
$protectedFiles = @(Get-ChildItem -Path $PSScriptRoot -Filter "*.ps1")
$protectedFiles += @(Get-ChildItem -Path $PSScriptRoot -Filter "*.psm1")

$hashes = @{}
foreach ($f in $protectedFiles) {
    $h = (Get-FileHash -Path $f.FullName -Algorithm SHA256).Hash
    $hashes[$f.Name] = $h
    Write-N4Log -Message "Empreinte calculee : $($f.Name)" -Level INFO
}

$manifest = @{
    Files          = $hashes
    GeneratedAt    = (Get-Date -Format "yyyy-MM-ddTHH:mm:ss")
    GeneratedBy    = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
}

$manifest | ConvertTo-Json | Set-Content -Path $manifestPath -Encoding UTF8

Write-N4Log -Message "Manifeste regenere avec succes : $($hashes.Count) fichier(s). $manifestPath" -Level OK
Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
