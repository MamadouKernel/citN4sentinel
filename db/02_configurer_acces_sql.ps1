<#
.SYNOPSIS
    Configure en une passe l'acces SQL de N4 Sentinel : mode mixte, login
    applicatif, et mise a jour de la chaine de connexion dans appsettings.json.

.DESCRIPTION
    A EXECUTER UNE SEULE FOIS, EN ADMINISTRATEUR, sur le poste ou le serveur
    portant l'instance SQL Server.

    Le script realise, dans cet ordre :
      1. bascule de l'instance en authentification MIXTE (registre LoginMode) ;
      2. redemarrage du service SQL Server, obligatoire pour prendre effet ;
      3. creation du login [n4sentinel_app] et de son utilisateur de base,
         en moindre privilege - ni sysadmin, ni droits hors de n4sentinel ;
      4. mise a jour de MOT_DE_PASSE_A_DEFINIR dans appsettings.json ;
      5. test de connexion reel avec le compte cree.

    LE MOT DE PASSE N'EST JAMAIS ECRIT SUR DISQUE PAR CE SCRIPT, ni passe en
    argument de ligne de commande (ou il serait visible dans le gestionnaire
    des taches). Il transite par un parametre SQL, et n'atterrit que dans
    appsettings.json - ce qui est le choix retenu pour ce poste.

.PARAMETER ServerInstance
    Instance SQL cible. Defaut : localhost (instance par defaut).

.PARAMETER AppSettingsPath
    Chemin d'appsettings.json a mettre a jour. Defaut : celui du projet Web.

.PARAMETER SkipAuthModeChange
    N'effectue ni le changement de mode ni le redemarrage du service. A
    utiliser si l'instance est deja en mode mixte, ou si ce changement est
    du ressort d'un DBA.

.EXAMPLE
    .\02_configurer_acces_sql.ps1

.EXAMPLE
    .\02_configurer_acces_sql.ps1 -SkipAuthModeChange
        L'instance est deja en mode mixte : cree seulement le login et met
        a jour la configuration.

.NOTES
    Ce script modifie un reglage de securite de SQL Server. Lisez-le avant
    de l'executer.
#>

[CmdletBinding()]
param(
    [string]$ServerInstance = "localhost",
    [string]$AppSettingsPath = (Join-Path $PSScriptRoot "..\src\N4Sentinel.Web\appsettings.json"),
    [switch]$SkipAuthModeChange
)

$ErrorActionPreference = "Stop"

function Write-Etape { param([string]$m) Write-Host "`n=== $m ===" -ForegroundColor Cyan }
function Write-Ok    { param([string]$m) Write-Host "  OK   $m" -ForegroundColor Green }
function Write-Info  { param([string]$m) Write-Host "  ...  $m" -ForegroundColor Gray }
function Write-Warn2 { param([string]$m) Write-Host "  !    $m" -ForegroundColor Yellow }

# ---------------------------------------------------------------------------
# Controles prealables
# ---------------------------------------------------------------------------
Write-Etape "Controles prealables"

$estAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
            ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $SkipAuthModeChange -and -not $estAdmin) {
    Write-Host "Ce script doit etre lance dans une console PowerShell ADMINISTRATEUR" -ForegroundColor Red
    Write-Host "(le changement de mode d'authentification ecrit dans HKLM et redemarre un service)." -ForegroundColor Red
    Write-Host "Si l'instance est deja en mode mixte, relancez avec -SkipAuthModeChange." -ForegroundColor Yellow
    return
}
Write-Ok "Droits suffisants."

if (-not (Test-Path $AppSettingsPath)) {
    Write-Host "appsettings.json introuvable : $AppSettingsPath" -ForegroundColor Red
    return
}
$AppSettingsPath = (Resolve-Path $AppSettingsPath).Path
Write-Ok "Configuration cible : $AppSettingsPath"

# ---------------------------------------------------------------------------
# Saisie du mot de passe
# ---------------------------------------------------------------------------
Write-Etape "Mot de passe du compte applicatif [n4sentinel_app]"
Write-Info "12 caracteres minimum, avec majuscule, minuscule, chiffre et caractere special."
Write-Warn2 "Il sera inscrit en clair dans appsettings.json, fichier suivi par Git."
Write-Warn2 "N'utilisez pas ce mot de passe en UAT ni en Production."

$p1 = Read-Host -Prompt "  Mot de passe" -AsSecureString
$p2 = Read-Host -Prompt "  Confirmation" -AsSecureString

$b1 = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($p1)
$b2 = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($p2)
try {
    $motDePasse  = [Runtime.InteropServices.Marshal]::PtrToStringAuto($b1)
    $confirmation = [Runtime.InteropServices.Marshal]::PtrToStringAuto($b2)
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b1)
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b2)
}

if ($motDePasse -cne $confirmation) {
    Write-Host "Les deux saisies different. Aucune modification effectuee." -ForegroundColor Red
    return
}
if ($motDePasse.Length -lt 12) {
    Write-Host "Mot de passe trop court (12 caracteres minimum). Aucune modification effectuee." -ForegroundColor Red
    return
}
Write-Ok "Mot de passe accepte."

# ---------------------------------------------------------------------------
# Etape 1 - Mode d'authentification mixte
# ---------------------------------------------------------------------------
if (-not $SkipAuthModeChange) {
    Write-Etape "Bascule de l'instance en authentification mixte"

    $racine = "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL"
    $instances = Get-ItemProperty -Path $racine
    $nomInterne = $instances.MSSQLSERVER
    if (-not $nomInterne) {
        Write-Host "Instance par defaut MSSQLSERVER introuvable dans le registre." -ForegroundColor Red
        return
    }
    Write-Info "Instance detectee : $nomInterne"

    $cle = "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\$nomInterne\MSSQLServer"
    $modeActuel = (Get-ItemProperty -Path $cle -Name LoginMode).LoginMode

    if ($modeActuel -eq 2) {
        Write-Ok "Deja en mode mixte - aucun changement, aucun redemarrage."
    } else {
        Set-ItemProperty -Path $cle -Name LoginMode -Value 2
        Write-Ok "LoginMode passe de $modeActuel a 2 (mixte)."

        Write-Info "Redemarrage du service SQL Server (obligatoire pour prise en compte)..."
        Restart-Service -Name MSSQLSERVER -Force
        $svc = Get-Service MSSQLSERVER
        $svc.WaitForStatus('Running', [TimeSpan]::FromSeconds(120))
        Write-Ok "Service redemarre et operationnel."
    }
}

# ---------------------------------------------------------------------------
# Etape 2 - Login, utilisateur et droits
# ---------------------------------------------------------------------------
Write-Etape "Creation du compte applicatif"

$chaineAdmin = "Server=$ServerInstance;Database=master;Integrated Security=True;TrustServerCertificate=True"

# Le mot de passe passe par un PARAMETRE SQL, jamais par concatenation ni par
# la ligne de commande : il n'apparait ni dans un fichier, ni dans la liste
# des processus.
$tsql = @'
SET NOCOUNT ON;

IF SERVERPROPERTY('IsIntegratedSecurityOnly') = 1
BEGIN
    RAISERROR('L''instance est encore en mode Windows uniquement. Relancez sans -SkipAuthModeChange, ou basculez le mode manuellement.', 16, 1);
    RETURN;
END

DECLARE @sql nvarchar(max);

IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'n4sentinel_app')
BEGIN
    SET @sql = N'CREATE LOGIN [n4sentinel_app] WITH PASSWORD = ' + QUOTENAME(@pwd, '''') +
               N', DEFAULT_DATABASE = [n4sentinel], CHECK_POLICY = ON, CHECK_EXPIRATION = OFF;';
    EXEC sp_executesql @sql;
    PRINT 'Login [n4sentinel_app] cree.';
END
ELSE
BEGIN
    SET @sql = N'ALTER LOGIN [n4sentinel_app] WITH PASSWORD = ' + QUOTENAME(@pwd, '''') + N';';
    EXEC sp_executesql @sql;
    PRINT 'Login [n4sentinel_app] deja present - mot de passe mis a jour.';
END

IF DB_ID('n4sentinel') IS NULL
BEGIN
    RAISERROR('La base n4sentinel n''existe pas. Creez-la avant de rejouer ce script.', 16, 1);
    RETURN;
END

SET @sql = N'
USE [n4sentinel];
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N''n4sentinel_app'')
    CREATE USER [n4sentinel_app] FOR LOGIN [n4sentinel_app];
ALTER ROLE [db_datareader] ADD MEMBER [n4sentinel_app];
ALTER ROLE [db_datawriter] ADD MEMBER [n4sentinel_app];
ALTER ROLE [db_ddladmin]   ADD MEMBER [n4sentinel_app];
';
EXEC sp_executesql @sql;
PRINT 'Utilisateur de base et roles appliques (lecture, ecriture, DDL pour les migrations).';
'@

$cn = New-Object System.Data.SqlClient.SqlConnection $chaineAdmin
$cn.add_InfoMessage({ param($s, $e) Write-Info $e.Message })
try {
    $cn.Open()
    $cmd = $cn.CreateCommand()
    $cmd.CommandText = $tsql
    $cmd.CommandTimeout = 60
    $null = $cmd.Parameters.Add("@pwd", [System.Data.SqlDbType]::NVarChar, 128)
    $cmd.Parameters["@pwd"].Value = $motDePasse
    $null = $cmd.ExecuteNonQuery()
    Write-Ok "Compte applicatif configure."
} catch {
    Write-Host "  ECHEC : $($_.Exception.Message)" -ForegroundColor Red
    return
} finally {
    $cn.Close()
}

# ---------------------------------------------------------------------------
# Etape 3 - Test de connexion reel
# ---------------------------------------------------------------------------
Write-Etape "Test de connexion avec le compte applicatif"

$chaineApp = "Server=$ServerInstance;Database=n4sentinel;User Id=n4sentinel_app;Password=$motDePasse;TrustServerCertificate=True;MultipleActiveResultSets=True;Encrypt=True"

$test = New-Object System.Data.SqlClient.SqlConnection $chaineApp
try {
    $test.Open()
    $c = $test.CreateCommand()
    $c.CommandText = "SELECT SUSER_NAME() + ' sur ' + DB_NAME()"
    Write-Ok "Connexion etablie : $($c.ExecuteScalar())"
} catch {
    Write-Host "  ECHEC de connexion : $($_.Exception.Message)" -ForegroundColor Red
    Write-Warn2 "appsettings.json n'a PAS ete modifie."
    return
} finally {
    $test.Close()
}

# ---------------------------------------------------------------------------
# Etape 4 - Mise a jour d'appsettings.json
# ---------------------------------------------------------------------------
Write-Etape "Mise a jour de la configuration"

$contenu = Get-Content -LiteralPath $AppSettingsPath -Raw -Encoding UTF8

if ($contenu -notlike "*MOT_DE_PASSE_A_DEFINIR*") {
    Write-Warn2 "Le gabarit MOT_DE_PASSE_A_DEFINIR n'apparait plus dans appsettings.json."
    Write-Warn2 "Le fichier a deja ete modifie : verifiez-le manuellement, rien n'a ete ecrit."
} else {
    # Sauvegarde horodatee avant ecriture : on ne remplace jamais une
    # configuration sans pouvoir revenir en arriere.
    $sauvegarde = "$AppSettingsPath.$(Get-Date -Format 'yyyyMMdd_HHmmss').bak"
    Copy-Item -LiteralPath $AppSettingsPath -Destination $sauvegarde
    Write-Info "Sauvegarde : $(Split-Path $sauvegarde -Leaf)"

    $contenu = $contenu.Replace("MOT_DE_PASSE_A_DEFINIR", $motDePasse)
    Set-Content -LiteralPath $AppSettingsPath -Value $contenu -Encoding UTF8 -NoNewline
    Write-Ok "Chaine de connexion mise a jour."
}

Write-Etape "Termine"
Write-Host "  Lancez l'application :" -ForegroundColor White
Write-Host "      cd src\N4Sentinel.Web" -ForegroundColor White
Write-Host "      dotnet run" -ForegroundColor White
Write-Host ""
Write-Warn2 "Rappel : appsettings.json contient desormais un mot de passe en clair"
Write-Warn2 "et sera versionne. Prevoyez une valeur differente pour l'UAT et la Production,"
Write-Warn2 "surchargee par variable d'environnement ConnectionStrings__N4Sentinel."
Write-Host ""
