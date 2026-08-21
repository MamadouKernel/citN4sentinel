<#
.SYNOPSIS
    Produit le paquet d'installation de N4 Sentinel.

.DESCRIPTION
    Compile, publie et assemble un dossier prêt à copier sur un serveur cible.

    LE PAQUET NE CONTIENT AUCUN SECRET. Le fichier appsettings.Production.json
    livré est un GABARIT : la chaîne de connexion y porte un mot de passe à
    remplacer, et le script d'installation refuse de démarrer tant qu'il n'a
    pas été changé. Un paquet qui embarquerait un mot de passe finirait par
    circuler par courriel, puis par se retrouver sur un partage.

.PARAMETER Destination
    Dossier où assembler le paquet. Créé s'il n'existe pas.

.PARAMETER Version
    Numéro de version apposé au paquet. Par défaut, la date du jour.

.EXAMPLE
    .\Publier-N4Sentinel.ps1 -Destination D:\Paquets -Version 1.0.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Destination,

    [string]$Version = (Get-Date -Format 'yyyy.MM.dd'),

    # Embarque le runtime .NET dans le paquet : la VM n'a alors rien a
    # installer. A choisir ICI, sur le poste de developpement, et non une fois
    # devant la machine cible.
    [switch]$Autonome,

    [string]$Runtime = 'win-x64',

    # Sert uniquement au message affiche en mode dependant du runtime, pour
    # que l'operateur sache quoi installer sur la VM.
    [string]$VersionRuntimeAttendue = '10.0'
)

$ErrorActionPreference = 'Stop'

$racine = Split-Path -Parent $PSScriptRoot
$projet = Join-Path $racine 'src\N4Sentinel.Web\N4Sentinel.Web.csproj'

if (-not (Test-Path $projet)) {
    throw "Projet introuvable : $projet. Executez ce script depuis le depot."
}

$nomPaquet = "N4Sentinel-$Version"
$dossier = Join-Path $Destination $nomPaquet
$application = Join-Path $dossier 'application'

Write-Host ""
Write-Host "  N4 SENTINEL - CONSTITUTION DU PAQUET" -ForegroundColor Cyan
Write-Host "  Version     : $Version"
Write-Host "  Destination : $dossier"
Write-Host ""

if (Test-Path $dossier) {
    Write-Host "  Le dossier existe deja, il est vide avant reconstruction." -ForegroundColor Yellow
    Remove-Item $dossier -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $application | Out-Null

# --- 1. Feuille de style -----------------------------------------------------
# Compilee AVANT la publication : sans elle, l'interface s'affiche sans aucune
# mise en forme, et le defaut ne se voit qu'a la premiere ouverture sur le site.
Write-Host "  [1/4] Compilation de la feuille de style..." -ForegroundColor Cyan

$web = Join-Path $racine 'src\N4Sentinel.Web'
Push-Location $web
try {
    if (Get-Command npm -ErrorAction SilentlyContinue) {
        # PAS de redirection 2>&1 ici. Sous Windows PowerShell 5.1, rediriger
        # la sortie d'erreur d'un executable natif transforme CHAQUE ligne en
        # erreur terminante, y compris un simple avertissement. npm en produit
        # regulierement — « caniuse-lite is outdated », par exemple — et la
        # fabrication du paquet echouait alors alors que la compilation avait
        # parfaitement abouti.
        #
        # Le code de sortie est le seul verdict fiable.
        $preferenceInitiale = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try   { & npm.cmd run build:css | Out-Null }
        finally { $ErrorActionPreference = $preferenceInitiale }

        if ($LASTEXITCODE -ne 0) { throw "La compilation de la feuille de style a echoue (code $LASTEXITCODE)." }
        Write-Host "        Feuille de style compilee." -ForegroundColor Green
    }
    else {
        $css = Join-Path $web 'wwwroot\css\tailwind.min.css'
        if (-not (Test-Path $css)) {
            throw "npm est introuvable ET aucune feuille compilee n'existe. Installez Node.js, ou recuperez wwwroot\css\tailwind.min.css depuis le depot."
        }
        Write-Host "        npm absent : la feuille deja compilee est reprise telle quelle." -ForegroundColor Yellow
    }
}
finally { Pop-Location }

# --- 2. Publication ----------------------------------------------------------
Write-Host "  [2/4] Publication de l'application..." -ForegroundColor Cyan

$argumentsPublication = @(
    $projet,
    '--configuration', 'Release',
    '--output', $application,
    "-p:Version=$Version",
    '--nologo'
)

if ($Autonome) {
    # Publication AUTONOME : le runtime .NET voyage avec l'application. La VM
    # n'a alors rien a installer du tout.
    #
    # Le paquet passe d'environ 20 Mo a environ 150 Mo. Sur un reseau
    # d'exploitation isole, c'est presque toujours le bon compromis : faire
    # approuver l'installation d'un runtime sur un serveur de production prend
    # plus de temps que de copier 150 Mo.
    #
    # Contrepartie a connaitre : les correctifs de securite du runtime ne
    # viennent plus par Windows Update. Une mise a jour du runtime impose de
    # republier et de redeployer.
    Write-Host "        Mode AUTONOME : le runtime est embarque." -ForegroundColor Yellow
    $argumentsPublication += @('--runtime', $Runtime, '--self-contained', 'true')
}
else {
    Write-Host "        Mode DEPENDANT DU RUNTIME : ASP.NET Core $VersionRuntimeAttendue doit etre installe sur la VM." -ForegroundColor Yellow
}

& dotnet publish @argumentsPublication | Out-Null

if ($LASTEXITCODE -ne 0) { throw "La publication a echoue." }

# Le gabarit de developpement n'a rien a faire sur un serveur : il porte une
# chaine de connexion locale qui masquerait la configuration reelle.
Remove-Item (Join-Path $application 'appsettings.Development.json') -Force -ErrorAction SilentlyContinue

Write-Host "        Application publiee." -ForegroundColor Green

# --- 3. Gabarit de configuration --------------------------------------------
Write-Host "  [3/4] Gabarit de configuration..." -ForegroundColor Cyan

$gabarit = @'
{
  "ConnectionStrings": {
    "//": "Encrypt=True SANS TrustServerCertificate : le certificat du serveur SQL est verifie. Avec TrustServerCertificate=True le trafic serait chiffre mais l'identite du serveur non verifiee, ce qui laisse passer une interception sur le chemin reseau - et ce trafic transporte les secrets chiffres des comptes techniques. Installez un certificat SQL Server emis par votre autorite interne.",
    "N4Sentinel": "Server=NOM_DU_SERVEUR_SQL;Database=n4sentinel;User Id=n4sentinel_app;Password=MOT_DE_PASSE_A_DEFINIR;Encrypt=True;MultipleActiveResultSets=True",

    "//_authentification_windows": "A PREFERER : aucun mot de passe n'a alors a figurer dans ce fichier.",
    "N4Sentinel_AuthWindows": "Server=NOM_DU_SERVEUR_SQL;Database=n4sentinel;Trusted_Connection=True;Encrypt=True;MultipleActiveResultSets=True"
  },

  "N4Sentinel": {
    "DataProtection": {
      "//": "Dossier du trousseau de cles. IL DOIT ETRE SAUVEGARDE avec la base : sans lui, les mots de passe des comptes techniques deviennent illisibles.",
      "KeyPath": "C:\\ProgramData\\N4Sentinel\\cles-protection"
    },

    "FirstAdmin": {
      "//": "Laisser vide. Le premier administrateur se cree depuis l'interface au premier demarrage, ce qui evite d'ecrire un mot de passe dans ce fichier.",
      "Email": "",
      "Password": ""
    }
  },

  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.EntityFrameworkCore.Database.Command": "Warning",
        "Microsoft.AspNetCore": "Warning"
      }
    }
  },

  "AllowedHosts": "*"
}
'@

Set-Content -Path (Join-Path $application 'appsettings.Production.json') -Value $gabarit -Encoding utf8

# --- 4. Outils et documentation ---------------------------------------------
Write-Host "  [4/4] Outils et documentation..." -ForegroundColor Cyan

Copy-Item (Join-Path $PSScriptRoot 'Installer-N4Sentinel.ps1') $dossier -Force

# Le script de verification part avec l'installeur : constater que le service
# rend reellement le service fait partie de la mise en service, pas d'un
# outillage optionnel qu'on garde sur son poste.
Copy-Item (Join-Path $PSScriptRoot 'Verifier-N4Sentinel.ps1') $dossier -Force

$docs = Join-Path $racine 'doc'
$guide = Join-Path $docs 'Guide-deploiement.md'
if (Test-Path $guide) {
    New-Item -ItemType Directory -Force -Path (Join-Path $dossier 'documentation') | Out-Null
    Copy-Item $guide (Join-Path $dossier 'documentation') -Force
}

$sql = Join-Path $racine 'db'
if (Test-Path $sql) {
    Copy-Item $sql (Join-Path $dossier 'base-de-donnees') -Recurse -Force
}

# Empreintes : permettent de verifier apres transfert que rien n'a ete altere.
$empreintes = Join-Path $dossier 'empreintes.txt'
Get-ChildItem $application -Recurse -File |
    Get-FileHash -Algorithm SHA256 |
    ForEach-Object { "{0}  {1}" -f $_.Hash, $_.Path.Substring($application.Length + 1) } |
    Set-Content -Path $empreintes -Encoding utf8

$taille = [math]::Round((Get-ChildItem $dossier -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)

Write-Host ""
Write-Host "  PAQUET CONSTITUE" -ForegroundColor Green
Write-Host "  $dossier"
Write-Host "  $taille Mo"
Write-Host ""
Write-Host "  Il ne contient aucun secret : appsettings.Production.json est un gabarit." -ForegroundColor Yellow
Write-Host "  Etape suivante, sur le serveur cible :" -ForegroundColor Cyan
Write-Host "    .\Installer-N4Sentinel.ps1 -Source .\application -Destination C:\N4Sentinel"
Write-Host ""
