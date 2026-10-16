<#
.SYNOPSIS
    Déploie N4 Sentinel de bout en bout sur une VM, en mode SQLite + service
    Windows, en enchaînant toutes les étapes de la VM.

.DESCRIPTION
    Orchestrateur « tout-en-un » à lancer sur la machine cible, depuis la racine
    du paquet (à côté de Installer-N4Sentinel.ps1 et Verifier-N4Sentinel.ps1).
    Il réalise, dans l'ordre :

      1. le dossier de données, HORS du dossier d'application ;
      2. les droits d'écriture du compte de service sur ce dossier (si fourni) ;
      3. l'écriture de appsettings.Production.json pour SQLite (provider, chemin
         de base fichier, trousseau de clés, hôtes autorisés) ;
      4. l'installation du service Windows, en déléguant à Installer-N4Sentinel.ps1 ;
      5. la variable d'environnement ASPNETCORE_ENVIRONMENT=Production du service ;
      6. le démarrage puis la PREUVE de bon fonctionnement (Verifier-N4Sentinel.ps1).

    CE QU'IL NE FAIT PAS, VOLONTAIREMENT : renseigner le compte de service ET son
    mot de passe. Un mot de passe passé en paramètre se retrouverait dans
    l'historique PowerShell et les journaux. Cette étape reste manuelle
    (services.msc), et le script la rappelle à la fin.

.PARAMETER Source
    Dossier 'application' du paquet. Par défaut '.\application'.

.PARAMETER Destination
    Dossier d'installation du service. Par défaut 'C:\N4Sentinel'.

.PARAMETER DataDir
    Dossier de données (base + trousseau), HORS de l'application. Par défaut
    'C:\ProgramData\N4Sentinel'. Une mise à jour ne le touche jamais.

.PARAMETER Port
    Port d'écoute HTTP. 8443 par défaut.

.PARAMETER NomService
    Nom du service Windows. 'N4Sentinel' par défaut.

.PARAMETER AllowedHosts
    En-tête Host autorisé (ex. 'n4sentinel.cit.local'). '*' par défaut.

.PARAMETER CompteService
    Compte de domaine (NetBIOS, ex. 'CIT\svc-n4sentinel') à qui accorder
    l'écriture sur le dossier de données. Facultatif ; sans lui, seul l'accès
    par défaut (dont LocalSystem) est en place.

.PARAMETER ForcerRemplacement
    Réinstalle par-dessus un service existant.

.PARAMETER PasDeDemarrage
    N'effectue ni le démarrage ni la vérification finale (utile pour désigner le
    compte de service avant le tout premier démarrage).

.EXAMPLE
    .\Deployer-N4Sentinel-Sqlite.ps1

.EXAMPLE
    .\Deployer-N4Sentinel-Sqlite.ps1 -Port 8443 -AllowedHosts n4sentinel.cit.local -CompteService 'CIT\svc-n4sentinel'
#>
[CmdletBinding()]
param(
    [string]$Source = '.\application',
    [string]$Destination = 'C:\N4Sentinel',
    [string]$DataDir = 'C:\ProgramData\N4Sentinel',
    [int]$Port = 8443,
    [string]$NomService = 'N4Sentinel',
    [string]$AllowedHosts = '*',
    [string]$CompteService,
    [switch]$ForcerRemplacement,
    [switch]$PasDeDemarrage
)

$ErrorActionPreference = 'Stop'

function Etape($n, $texte) { Write-Host ""; Write-Host "  [$n] $texte" -ForegroundColor Cyan }
function Ok($texte)        { Write-Host "      $texte" -ForegroundColor Green }
function Info($texte)      { Write-Host "      $texte" -ForegroundColor Gray }
function Attention($texte) { Write-Host "      $texte" -ForegroundColor Yellow }

Write-Host ""
Write-Host "  N4 SENTINEL - DEPLOIEMENT SQLITE + SERVICE WINDOWS" -ForegroundColor Cyan
Write-Host "  =================================================" -ForegroundColor Cyan

# --- Contrôles préalables ----------------------------------------------------
$estAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $estAdmin) {
    throw "Ce deploiement enregistre un service Windows : lancez une console PowerShell ELEVEE (Administrateur)."
}

$racinePaquet = $PSScriptRoot
$installer = Join-Path $racinePaquet 'Installer-N4Sentinel.ps1'
$verifier  = Join-Path $racinePaquet 'Verifier-N4Sentinel.ps1'
foreach ($s in @($installer, $verifier)) {
    if (-not (Test-Path $s)) {
        throw "Script attendu introuvable a cote de cet orchestrateur : $s. Lancez ce script depuis la racine du paquet."
    }
}

$Source = (Resolve-Path $Source -ErrorAction SilentlyContinue).Path
if (-not $Source -or -not (Test-Path (Join-Path $Source 'N4Sentinel.Web.dll'))) {
    throw "N4Sentinel.Web.dll est introuvable dans '$Source'. Indiquez le dossier 'application' du paquet via -Source."
}

$config = Join-Path $Source 'appsettings.Production.json'
if (-not (Test-Path $config)) {
    throw "appsettings.Production.json est absent du dossier application : $config"
}

# --- 1. Dossier de données ---------------------------------------------------
Etape 1 "Dossier de donnees (base + trousseau), hors de l'application"
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
Ok "Present : $DataDir"

# --- 2. Droits du compte de service -----------------------------------------
# Un compte introuvable n'interrompt PAS le deploiement : on poursuit sous
# LocalSystem (repli documente au chapitre C.4). Le grant se refera quand le
# compte existera, au moment de l'affecter dans services.msc.
Etape 2 "Droits d'ecriture du compte de service"
if ($CompteService) {
    $compteExiste = $false
    try {
        [void] ([System.Security.Principal.NTAccount]$CompteService).Translate(
            [System.Security.Principal.SecurityIdentifier])
        $compteExiste = $true
    }
    catch { $compteExiste = $false }

    if (-not $compteExiste) {
        Attention "Compte '$CompteService' INTROUVABLE dans l'annuaire (nom NetBIOS attendu, ex. CIT\\svc-n4sentinel)."
        Attention "Poursuite sous LocalSystem : l'app fonctionnera, mais la supervision WinRM des serveurs N4 sera refusee."
        Attention "Quand le compte existera, accordez-lui l'ecriture puis affectez-le dans services.msc :"
        Info "  icacls `"$DataDir`" /grant `"${CompteService}:(OI)(CI)M`""
    }
    else {
        # (OI)(CI)M = heritage objets + conteneurs, droit Modifier.
        & icacls $DataDir /grant "${CompteService}:(OI)(CI)M" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Attribution des droits a '$CompteService' echouee (icacls code $LASTEXITCODE)."
        }
        Ok "Ecriture accordee a $CompteService sur $DataDir"
    }
}
else {
    Attention "Aucun compte fourni (-CompteService). Le service tournera sous LocalSystem :"
    Attention "l'application fonctionnera, mais la supervision WinRM des serveurs N4 sera refusee."
}

# --- 3. Configuration SQLite (fusion dans appsettings.Production.json) --------
Etape 3 "Configuration SQLite dans appsettings.Production.json"

$cheminBase = Join-Path $DataDir 'n4sentinel.db'
$cheminCles = Join-Path $DataDir 'cles-protection'

$json = Get-Content $config -Raw | ConvertFrom-Json

function Assurer-Objet($parent, $nom) {
    if (-not $parent.PSObject.Properties.Name.Contains($nom) -or $null -eq $parent.$nom) {
        $parent | Add-Member -NotePropertyName $nom -NotePropertyValue ([pscustomobject]@{}) -Force
    }
    return $parent.$nom
}
function Definir($parent, $nom, $valeur) {
    if ($parent.PSObject.Properties.Name.Contains($nom)) { $parent.$nom = $valeur }
    else { $parent | Add-Member -NotePropertyName $nom -NotePropertyValue $valeur -Force }
}

$cs  = Assurer-Objet $json 'ConnectionStrings'
Definir $cs 'N4Sentinel' ("Data Source={0}" -f $cheminBase)

$n4  = Assurer-Objet $json 'N4Sentinel'
$db  = Assurer-Objet $n4 'Database'
Definir $db 'Provider' 'Sqlite'
$dp  = Assurer-Objet $n4 'DataProtection'
Definir $dp 'KeyPath' $cheminCles

Definir $json 'AllowedHosts' $AllowedHosts

$json | ConvertTo-Json -Depth 32 | Set-Content -Path $config -Encoding UTF8
Ok "Provider=Sqlite ; base=$cheminBase"
Ok "Trousseau=$cheminCles ; AllowedHosts=$AllowedHosts"

# --- 4. Installation du service (delegation) ---------------------------------
Etape 4 "Installation du service Windows '$NomService'"
$argsInstall = @{ Source = $Source; Destination = $Destination; Port = $Port; NomService = $NomService }
if ($ForcerRemplacement) { $argsInstall.ForcerRemplacement = $true }
& $installer @argsInstall
if ($LASTEXITCODE -ne 0) { throw "L'installation a echoue (code $LASTEXITCODE)." }

# --- 5. Environnement d'execution du service --------------------------------
Etape 5 "ASPNETCORE_ENVIRONMENT=Production pour le service"
$cleService = "HKLM:\SYSTEM\CurrentControlSet\Services\$NomService"
if (Test-Path $cleService) {
    Set-ItemProperty -Path $cleService -Name Environment `
        -Value @('ASPNETCORE_ENVIRONMENT=Production') -Type MultiString
    Ok "Variable d'environnement du service posee."
}
else {
    Attention "Cle de service introuvable : l'app retombera sur l'environnement Production par defaut."
}

# --- 6. Demarrage + preuve ---------------------------------------------------
if ($PasDeDemarrage) {
    Etape 6 "Demarrage differe (-PasDeDemarrage)"
    Attention "Designez d'abord le compte de service (services.msc), puis :"
    Info "  Start-Service $NomService ; .\Verifier-N4Sentinel.ps1 -Port $Port"
}
else {
    Etape 6 "Demarrage du service et verification /health"
    Start-Service $NomService
    (Get-Service $NomService).WaitForStatus('Running', '00:01:00')
    Ok "Service demarre."
    & $verifier -Port $Port -NomService $NomService
    if ($LASTEXITCODE -ne 0) {
        Attention "La verification a signale un probleme (code $LASTEXITCODE)."
        Attention "Journaux : $Destination\journaux\  puis /health/detail apres connexion."
    }
}

# --- Rappels manuels ---------------------------------------------------------
Write-Host ""
Write-Host "  DEPLOIEMENT TERMINE" -ForegroundColor Green
Write-Host ""
Write-Host "  IL RESTE DEUX GESTES MANUELS :" -ForegroundColor Yellow
Write-Host "   1. Compte de service (services.msc > $NomService > Connexion) :" -ForegroundColor Cyan
Write-Host "      un compte de DOMAINE, pas LocalSystem, pour la supervision WinRM des serveurs N4."
Write-Host "      (Non automatisable : un mot de passe en parametre fuiterait dans les journaux.)"
Write-Host "   2. Premier administrateur : http://$($env:COMPUTERNAME):$Port" -ForegroundColor Cyan
Write-Host "      Le premier ecran cree le compte initial ; aucun compte n'existe avant."
Write-Host ""
Write-Host "  A SAUVEGARDER ENSEMBLE (indissociables) :" -ForegroundColor Yellow
Write-Host "   - $cheminBase"
Write-Host "   - $cheminCles"
Write-Host "  Sans le trousseau, une base restauree ailleurs rend les mots de passe techniques illisibles."
Write-Host ""
