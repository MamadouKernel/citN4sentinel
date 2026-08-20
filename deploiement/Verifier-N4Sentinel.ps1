<#
.SYNOPSIS
    Verifie que N4 Sentinel REND LE SERVICE, et pas seulement qu'il tourne.

.DESCRIPTION
    Le statut 'Running' du gestionnaire de services dit une seule chose : le
    processus a demarre et ne s'est pas arrete. Il ne dit pas que Kestrel
    ecoute, que la base est joignable, ni que les migrations ont abouti.

    C'est exactement le raisonnement que N4 Sentinel applique aux composants
    Navis : un service Windows en cours d'execution ne prouve pas qu'un
    composant est operationnel, seul un marqueur dans le journal le prouve.
    Ce script applique la meme exigence au produit lui-meme.

    Trois constats, du plus faible au plus fort :
      1. le service est declare Running       (ne prouve rien a lui seul)
      2. le port repond                        (Kestrel ecoute)
      3. /health repond Healthy                (la base est joignable)

.PARAMETER Port
    Port d'ecoute declare a l'installation.

.PARAMETER NomService
    Nom du service Windows.

.PARAMETER DelaiSecondes
    Duree maximale d'attente. L'amorcage applique les migrations en attente :
    sur une base neuve, il faut laisser le temps.
#>
[CmdletBinding()]
param(
    # Meme defaut que Installer-N4Sentinel.ps1 : deux valeurs differentes
    # feraient conclure a tort que le port ne repond pas.
    [int]$Port = 8443,
    [string]$NomService = 'N4Sentinel',
    [int]$DelaiSecondes = 120
)

$ErrorActionPreference = 'Stop'

function Ecrire([string]$texte, [string]$couleur = 'Gray') {
    Write-Host "  $texte" -ForegroundColor $couleur
}

Write-Host ""
Write-Host "  VERIFICATION DE MISE EN SERVICE" -ForegroundColor Cyan
Write-Host ""

# --- 1. Le service est-il declare demarre ? ----------------------------------
$service = Get-Service -Name $NomService -ErrorAction SilentlyContinue

if (-not $service) {
    Ecrire "Service '$NomService' introuvable. L'installation n'a pas ete faite sur cette machine." Red
    exit 2
}

Ecrire "1. Statut du service : $($service.Status)" $(if ($service.Status -eq 'Running') { 'Green' } else { 'Red' })

if ($service.Status -ne 'Running') {
    Ecrire "   Demarrez-le : Start-Service $NomService" Yellow
    Ecrire "   Si le demarrage echoue sur l'erreur 1053, l'executable publie ne" Yellow
    Ecrire "   se declare pas au gestionnaire de services." Yellow
    exit 2
}

Ecrire "   Ce statut ne prouve rien a lui seul : il dit que le processus tourne." Yellow

# --- 2. Le port repond-il ? ---------------------------------------------------
$limite = (Get-Date).AddSeconds($DelaiSecondes)
$portOuvert = $false

while ((Get-Date) -lt $limite) {
    $test = Test-NetConnection -ComputerName 'localhost' -Port $Port -InformationLevel Quiet -WarningAction SilentlyContinue
    if ($test) { $portOuvert = $true; break }
    Start-Sleep -Seconds 3
}

if (-not $portOuvert) {
    Ecrire "2. Port $Port : AUCUNE REPONSE apres $DelaiSecondes s." Red
    Ecrire "   Le processus tourne mais n'ecoute pas. Regardez les journaux :" Yellow
    Ecrire "   logs\n4sentinel-*.log dans le dossier d'installation." Yellow
    exit 3
}

Ecrire "2. Port $Port : ouvert." Green

# --- 3. /health repond-il Healthy ? ------------------------------------------
# C'est le seul des trois constats qui engage la base de donnees.
$sante = $null
$limite = (Get-Date).AddSeconds($DelaiSecondes)

while ((Get-Date) -lt $limite) {
    try {
        $reponse = Invoke-WebRequest -Uri "http://localhost:$Port/health" -UseBasicParsing -TimeoutSec 10
        $sante = $reponse.Content.Trim()
        break
    }
    catch {
        Start-Sleep -Seconds 3
    }
}

if ($null -eq $sante) {
    Ecrire "3. /health : injoignable apres $DelaiSecondes s." Red
    Ecrire "   Le port repond mais la sonde ne rend pas la main : l'amorcage" Yellow
    Ecrire "   est probablement bloque sur les migrations." Yellow
    exit 4
}

if ($sante -ne 'Healthy') {
    Ecrire "3. /health : $sante" Red
    Ecrire "   L'application tourne mais se declare en mauvaise sante." Yellow
    Ecrire "   Connectez-vous et consultez /health/detail pour le motif." Yellow
    exit 5
}

Ecrire "3. /health : Healthy." Green

Write-Host ""
Write-Host "  N4 SENTINEL REND LE SERVICE." -ForegroundColor Green
Write-Host ""
Write-Host "  Ce qui est prouve : le processus tourne, Kestrel ecoute, la base" -ForegroundColor Gray
Write-Host "  est joignable et les migrations ont abouti." -ForegroundColor Gray
Write-Host ""
Write-Host "  Ce qui ne l'est PAS : l'etat de l'ecosysteme Navis N4 supervise." -ForegroundColor Yellow
Write-Host "  N4 Sentinel peut etre en parfaite sante pendant que la production" -ForegroundColor Yellow
Write-Host "  est a l'arret. Ces deux questions sont distinctes." -ForegroundColor Yellow
Write-Host ""
Write-Host "  Suite : http://$env:COMPUTERNAME`:$Port" -ForegroundColor Cyan
Write-Host ""

exit 0
