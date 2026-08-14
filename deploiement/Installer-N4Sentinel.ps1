<#
.SYNOPSIS
    Installe N4 Sentinel comme service Windows sur un serveur.

.DESCRIPTION
    Copie l'application, prépare le dossier du trousseau de clés, enregistre le
    service Windows et vérifie que la configuration est exploitable.

    LE SCRIPT REFUSE D'INSTALLER UNE CONFIGURATION INCOMPLETE. Un service
    enregistré qui ne démarre pas est pire qu'une installation interrompue :
    il donne l'illusion que le travail est fait, et le défaut se découvre au
    premier besoin réel.

    IL NE DEMANDE JAMAIS DE MOT DE PASSE. Le compte de service et la chaîne de
    connexion se renseignent à part — le premier par la console des services ou
    par une stratégie de groupe, la seconde dans appsettings.Production.json.
    Un mot de passe saisi ici se retrouverait dans l'historique PowerShell.

.PARAMETER Source
    Dossier 'application' du paquet.

.PARAMETER Destination
    Dossier d'installation sur le serveur.

.PARAMETER Port
    Port d'écoute HTTP. 8443 par défaut.

.PARAMETER NomService
    Nom du service Windows.

.EXAMPLE
    .\Installer-N4Sentinel.ps1 -Source .\application -Destination C:\N4Sentinel
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Source,
    [Parameter(Mandatory)] [string]$Destination,
    [int]$Port = 8443,
    [string]$NomService = 'N4Sentinel',
    [switch]$ForcerRemplacement
)

$ErrorActionPreference = 'Stop'

function Ecrire($texte, $couleur = 'Gray') { Write-Host "  $texte" -ForegroundColor $couleur }

Write-Host ""
Write-Host "  N4 SENTINEL - INSTALLATION" -ForegroundColor Cyan
Write-Host ""

# --- Contrôles préalables ----------------------------------------------------
# Les memes principes que dans l'application : ce qui peut etre verifie avant
# doit l'etre avant. Un echec a mi-parcours laisse un serveur dans un etat
# intermediaire que personne ne sait decrire.

$estAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $estAdmin) {
    throw "Cette installation enregistre un service Windows : une console elevee est requise."
}

if (-not (Test-Path (Join-Path $Source 'N4Sentinel.Web.dll'))) {
    throw "N4Sentinel.Web.dll est introuvable dans '$Source'. Indiquez le dossier 'application' du paquet."
}

$configuration = Join-Path $Source 'appsettings.Production.json'
if (-not (Test-Path $configuration)) {
    throw "appsettings.Production.json est absent du paquet."
}

$contenu = Get-Content $configuration -Raw

if ($contenu -match 'MOT_DE_PASSE_A_DEFINIR') {
    throw @"
La chaine de connexion porte encore le mot de passe du gabarit.

Renseignez-la AVANT d'installer, dans :
  $configuration

Le service demarrerait sinon, echouerait a joindre la base, et s'arreterait
sans que rien n'indique pourquoi.
"@
}

if ($contenu -match 'NOM_DU_SERVEUR_SQL') {
    throw "Le nom du serveur SQL est encore celui du gabarit dans $configuration."
}

$service = Get-Service -Name $NomService -ErrorAction SilentlyContinue
if ($service -and -not $ForcerRemplacement) {
    throw "Le service '$NomService' existe deja. Relancez avec -ForcerRemplacement pour le remplacer."
}

Ecrire "Controles prealables : reussis." Green

# --- Arrêt du service existant ----------------------------------------------
if ($service) {
    Ecrire "Arret du service existant..." Yellow
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $NomService -Force
        $service.WaitForStatus('Stopped', '00:01:00')
    }
    & sc.exe delete $NomService | Out-Null
    Start-Sleep -Seconds 2
    Ecrire "Service precedent supprime." Green
}

# --- Copie -------------------------------------------------------------------
Ecrire "Copie de l'application vers $Destination..."

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

# Le trousseau de cles n'est JAMAIS ecrase : il contient de quoi dechiffrer les
# mots de passe deja enregistres. L'effacer lors d'une mise a jour rendrait
# tous les comptes techniques illisibles, sans message d'erreur.
$clesExistantes = Join-Path $Destination 'cles-protection'
$sauvegardeCles = $null

if (Test-Path $clesExistantes) {
    $sauvegardeCles = Join-Path $env:TEMP "n4-cles-$(Get-Random)"
    Copy-Item $clesExistantes $sauvegardeCles -Recurse -Force
    Ecrire "Trousseau existant mis de cote avant copie." Yellow
}

Copy-Item (Join-Path $Source '*') $Destination -Recurse -Force

if ($sauvegardeCles) {
    Copy-Item (Join-Path $sauvegardeCles '*') $clesExistantes -Recurse -Force
    Remove-Item $sauvegardeCles -Recurse -Force
    Ecrire "Trousseau existant restaure." Green
}

Ecrire "Application copiee." Green

# --- Dossier du trousseau ----------------------------------------------------
$cheminCles = 'C:\ProgramData\N4Sentinel\cles-protection'
if ($contenu -match '"KeyPath"\s*:\s*"([^"]+)"') {
    $cheminCles = $Matches[1] -replace '\\\\', '\'
}

if (-not (Test-Path $cheminCles)) {
    New-Item -ItemType Directory -Force -Path $cheminCles | Out-Null
    Ecrire "Dossier du trousseau cree : $cheminCles" Green
}
else {
    $nb = (Get-ChildItem $cheminCles -Filter *.xml -ErrorAction SilentlyContinue).Count
    Ecrire "Trousseau existant conserve : $nb fichier(s) dans $cheminCles" Green
}

# --- Journaux ----------------------------------------------------------------
$journaux = Join-Path $Destination 'journaux'
New-Item -ItemType Directory -Force -Path $journaux | Out-Null

# --- Enregistrement du service ----------------------------------------------
Ecrire "Enregistrement du service Windows '$NomService'..."

$executable = Join-Path $Destination 'N4Sentinel.Web.exe'
if (-not (Test-Path $executable)) {
    throw "N4Sentinel.Web.exe est introuvable apres copie : la publication n'a pas produit d'executable."
}

& sc.exe create $NomService `
    binPath= "`"$executable`" --urls http://+:$Port" `
    start= auto `
    DisplayName= "N4 Sentinel - supervision Navis N4" | Out-Null

if ($LASTEXITCODE -ne 0) { throw "L'enregistrement du service a echoue (code $LASTEXITCODE)." }

& sc.exe description $NomService `
    "Supervision, orchestration et diagnostic de l'ecosysteme Navis N4." | Out-Null

# Redemarrage automatique apres incident : 1re et 2e defaillance apres 60 s,
# suivantes apres 120 s. Le compteur se reinitialise au bout d'une journee.
& sc.exe failure $NomService reset= 86400 actions= restart/60000/restart/60000/restart/120000 | Out-Null

Ecrire "Service enregistre." Green

# --- Pare-feu ----------------------------------------------------------------
$regle = "N4 Sentinel (TCP $Port)"
if (-not (Get-NetFirewallRule -DisplayName $regle -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $regle -Direction Inbound -Protocol TCP `
        -LocalPort $Port -Action Allow -Profile Domain | Out-Null
    Ecrire "Regle de pare-feu ajoutee sur le profil Domaine." Green
}
else {
    Ecrire "Regle de pare-feu deja presente." Green
}

# --- Fin ---------------------------------------------------------------------
Write-Host ""
Write-Host "  INSTALLATION TERMINEE" -ForegroundColor Green
Write-Host ""
Write-Host "  IL RESTE DEUX CHOSES A FAIRE, ET ELLES NE SONT PAS AUTOMATISABLES." -ForegroundColor Yellow
Write-Host ""
Write-Host "  1. COMPTE DE SERVICE" -ForegroundColor Cyan
Write-Host "     Le service tourne sous LocalSystem, ce qui ne convient pas :"
Write-Host "     il doit se connecter a SQL Server et interroger les serveurs N4."
Write-Host ""
Write-Host "     Ouvrez services.msc, proprietes de '$NomService', onglet Connexion,"
Write-Host "     et designez le compte de service du domaine."
Write-Host ""
Write-Host "     Ce script ne le fait pas : un mot de passe passe en parametre se"
Write-Host "     retrouve dans l'historique PowerShell et dans les journaux."
Write-Host ""
Write-Host "  2. PREMIER DEMARRAGE" -ForegroundColor Cyan
Write-Host "     Start-Service $NomService"
Write-Host "     puis http://$env:COMPUTERNAME`:$Port"
Write-Host ""
Write-Host "     Le premier ecran cree l'administrateur initial. Aucun compte"
Write-Host "     n'existe avant : il n'y a pas de mot de passe par defaut a changer."
Write-Host ""
Write-Host "  Dossier du trousseau a SAUVEGARDER avec la base :" -ForegroundColor Yellow
Write-Host "     $cheminCles"
Write-Host ""
