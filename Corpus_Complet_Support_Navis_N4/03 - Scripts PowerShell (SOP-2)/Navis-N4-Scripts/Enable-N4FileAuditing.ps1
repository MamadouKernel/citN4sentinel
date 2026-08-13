<#
.SYNOPSIS
    Active l'audit de securite Windows sur le dossier des scripts N4, pour
    tracer QUI modifie un fichier, QUAND, et DEPUIS QUELLE IP (si acces reseau).

.DESCRIPTION
    Deux choses sont necessaires pour que Windows enregistre ces evenements,
    et ce script fait les deux :
      1. Activer la sous-categorie d'audit "File System" au niveau de la
         machine (auditpol.exe) - sinon Windows n'enregistre RIEN, meme
         avec une SACL en place.
      2. Poser une SACL (System Access Control List) sur le dossier des
         scripts, qui indique a Windows QUELS acces surveiller (ecriture,
         suppression, modification) et par QUI (Tout le monde, par defaut).

    Une fois actif, chaque modification apparait dans le Journal des
    evenements Windows > Securite, Event ID 4663 (et 4656). Utiliser
    Get-N4FileModificationEvents.ps1 pour les consulter facilement.

.NOTES
    Copyright : (c) KMKernel
    IMPORTANT - Limite honnete sur l'adresse IP :
      - Si le dossier est accede LOCALEMENT (RDP, console) : l'IP ne sera
        pas dans l'evenement 4663 lui-meme, mais peut etre retrouvee en
        correlant avec l'evenement de connexion 4624 (Get-N4FileModification
        Events.ps1 fait cette correlation automatiquement).
      - Si le dossier est accede par PARTAGE RESEAU (SMB, \\serveur\partage) :
        l'IP source de la connexion SMB est generalement disponible.
      - Ceci necessite des droits Administrateur pour s'executer.

.PARAMETER TargetFolder
    Dossier a auditer. Par defaut, le dossier de ce script.

.EXAMPLE
    .\Enable-N4FileAuditing.ps1
#>

[CmdletBinding()]
param(
    [string]$TargetFolder = $PSScriptRoot
)

Import-Module "$PSScriptRoot\Navis-Common.psm1" -Force
$logFile = New-N4LogSession -ScriptName "Enable-N4FileAuditing"

if (-not (Test-N4Integrity -ScriptPath $PSCommandPath)) { return }

Write-N4Log -Message "===== Activation de l'audit de securite Windows =====" -Level ACTION
Write-N4Log -Message "Dossier cible : $TargetFolder" -Level INFO

# ---- Verification des droits Administrateur ----
$currentPrincipal = New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-N4Log -Message "Ce script doit etre execute en tant qu'Administrateur. Relancer une PowerShell elevee." -Level ERROR
    return
}

# ---- Etape 1 : activer la sous-categorie d'audit "File System" ----
Write-N4Log -Message "--- Etape 1/2 : Activation de la sous-categorie d'audit 'File System' ---" -Level ACTION
try {
    $result = auditpol /set /subcategory:"File System" /success:enable /failure:enable 2>&1
    Write-N4Log -Message "auditpol : $result" -Level INFO
    Write-N4Log -Message "Sous-categorie d'audit 'File System' activee (succes + echecs)." -Level OK
} catch {
    Write-N4Log -Message "ECHEC activation auditpol : $($_.Exception.Message)" -Level ERROR
    return
}

# ---- Etape 2 : poser la SACL sur le dossier cible ----
Write-N4Log -Message "--- Etape 2/2 : Configuration de la SACL sur $TargetFolder ---" -Level ACTION
try {
    $acl = Get-Acl -Path $TargetFolder -Audit -ErrorAction Stop

    $auditRule = New-Object System.Security.AccessControl.FileSystemAuditRule(
        "Everyone",
        [System.Security.AccessControl.FileSystemRights]"Write, Delete, Modify, DeleteSubdirectoriesAndFiles",
        [System.Security.AccessControl.InheritanceFlags]"ContainerInherit, ObjectInherit",
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AuditFlags]"Success, Failure"
    )
    $acl.AddAuditRule($auditRule)
    Set-Acl -Path $TargetFolder -AclObject $acl -ErrorAction Stop

    Write-N4Log -Message "SACL posee avec succes sur $TargetFolder (Ecriture/Suppression/Modification, Tout le monde, succes+echec)." -Level OK
} catch {
    Write-N4Log -Message "ECHEC configuration SACL : $($_.Exception.Message)" -Level ERROR
    return
}

Write-N4Log -Message "===== Audit active =====" -Level ACTION
Write-N4Log -Message "Toute modification future de fichiers dans ce dossier sera desormais enregistree dans le Journal des evenements > Securite (Event ID 4663)." -Level OK
Write-N4Log -Message "Utiliser Get-N4FileModificationEvents.ps1 pour consulter ces evenements de maniere lisible." -Level INFO
Write-N4Log -Message "Log complet de cette session : $logFile" -Level INFO
