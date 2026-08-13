<#
.SYNOPSIS
    Change le code PIN de protection (utilise pour autoriser l'execution
    d'un script dont l'integrite ne correspond plus a Navis-Integrity.json).

.DESCRIPTION
    Demande l'ancien PIN (verifie contre Navis-Protection.json), puis le
    nouveau PIN (saisi deux fois), et met a jour le fichier de protection.
    Le PIN n'est jamais stocke en clair, uniquement son empreinte SHA-256.

.NOTES
    Copyright : (c) KMKernel
    IMPORTANT : ce mecanisme protege contre la modification ACCIDENTELLE
    ou non autorisee des scripts. Il ne remplace pas un controle d'acces
    au niveau du systeme de fichiers (permissions NTFS) pour une securite
    renforcee.

.EXAMPLE
    .\Set-N4CopyrightPin.ps1
#>

[CmdletBinding()]
param()

Import-Module "$PSScriptRoot\Navis-Common.psm1" -Force
$logFile = New-N4LogSession -ScriptName "Set-N4CopyrightPin"

if (-not (Test-N4Integrity -ScriptPath $PSCommandPath)) { return }

$protectionPath = Join-Path $PSScriptRoot "Navis-Protection.json"

if (-not (Test-Path $protectionPath)) {
    Write-N4Log -Message "Fichier Navis-Protection.json introuvable. Impossible de changer le PIN." -Level ERROR
    return
}

$protection = Get-Content -Path $protectionPath -Raw -Encoding UTF8 | ConvertFrom-Json

Write-N4Log -Message "===== Changement du code PIN de protection =====" -Level ACTION

# ---- Verification de l'ancien PIN ----
$oldSecure = Read-Host -Prompt "Code PIN actuel (6 chiffres)" -AsSecureString
$oldHash = Get-N4PinHash -SecurePin $oldSecure

if ($oldHash -ine $protection.CopyrightPinHash) {
    Write-N4Log -Message "Code PIN actuel incorrect. Arret du script." -Level ERROR
    return
}
Write-N4Log -Message "Code PIN actuel valide." -Level OK

# ---- Saisie et validation du nouveau PIN ----
$newSecure1 = Read-Host -Prompt "Nouveau code PIN (exactement 6 chiffres)" -AsSecureString
$newSecure2 = Read-Host -Prompt "Confirmer le nouveau code PIN" -AsSecureString

$bstr1 = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($newSecure1)
$plain1 = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr1)
[System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr1)

$bstr2 = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($newSecure2)
$plain2 = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr2)
[System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr2)

if ($plain1 -ne $plain2) {
    Write-N4Log -Message "Les deux saisies ne correspondent pas. Arret du script sans modification." -Level ERROR
    return
}

if ($plain1 -notmatch '^\d{6}$') {
    Write-N4Log -Message "Le code PIN doit contenir exactement 6 chiffres. Arret du script sans modification." -Level ERROR
    return
}

$newHash = Get-N4PinHash -SecurePin $newSecure1

$protection.CopyrightPinHash = $newHash
$protection.MaxAttempts = if ($protection.MaxAttempts) { $protection.MaxAttempts } else { 3 }
$protection.LastChanged = (Get-Date -Format "yyyy-MM-ddTHH:mm:ss")
$protection.ChangedBy = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name

$protection | ConvertTo-Json | Set-Content -Path $protectionPath -Encoding UTF8

Write-N4Log -Message "Code PIN change avec succes par $($protection.ChangedBy)." -Level OK
Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
