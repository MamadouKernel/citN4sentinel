/* =====================================================================
   N4 Sentinel - Connexion SQL applicative
   ---------------------------------------------------------------------
   A EXECUTER PAR UN ADMINISTRATEUR, une seule fois, apres avoir bascule
   l'instance en mode d'authentification MIXTE.

   CONTEXTE
   L'instance est actuellement en mode "Windows uniquement" : aucune
   connexion SQL ne fonctionnera tant que ce mode n'aura pas ete change.
   Ce changement est un reglage de securite du serveur : il doit etre
   fait sciemment, par vous, et documente.

   ETAPE 1 - Basculer l'instance en mode mixte
     SQL Server Management Studio :
       clic droit sur l'instance > Properties > Security
       > Server authentication : "SQL Server and Windows Authentication mode"
       > OK, puis REDEMARRER le service SQL Server (obligatoire).

     Equivalent en ligne de commande (PowerShell administrateur) :
       Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\MSSQL17.MSSQLSERVER\MSSQLServer' `
                        -Name LoginMode -Value 2
       Restart-Service MSSQLSERVER -Force

     Verification apres redemarrage :
       SELECT SERVERPROPERTY('IsIntegratedSecurityOnly');   -- doit renvoyer 0

   ETAPE 2 - Executer ce script
     sqlcmd -S localhost -E -i 01_activer_connexion_sql.sql

   ETAPE 3 - Renseigner le mot de passe dans la configuration
     Ouvrir src/N4Sentinel.Web/appsettings.json et remplacer
     MOT_DE_PASSE_A_DEFINIR par le mot de passe defini ci-dessous.

     ATTENTION : appsettings.json est suivi par Git. Le mot de passe entrera
     dans l'historique du depot et un retrait ulterieur ne l'en effacera pas.
     Choix assume pour le poste de developpement.

     Pour l'UAT et la Production, ne pas reprendre ce mot de passe et
     surcharger la valeur sans toucher au fichier, par variable
     d'environnement sur le serveur :
       ConnectionStrings__N4Sentinel=Server=...;User Id=...;Password=...
     ou, mieux, faire tourner l'application sous un compte de service Windows
     avec Trusted_Connection=True : il n'y a alors plus aucun mot de passe a
     proteger (SEC-003).

   ---------------------------------------------------------------------
   REMPLACEZ le mot de passe ci-dessous avant execution.
   Ne conservez pas ce fichier avec un mot de passe reel a l'interieur.
   ===================================================================== */

USE [master];
GO

DECLARE @motDePasse sysname = N'CHANGEZ_MOI_Avant_Execution!2026';

IF SERVERPROPERTY('IsIntegratedSecurityOnly') = 1
BEGIN
    RAISERROR('ARRET : l''instance est encore en mode "Windows uniquement". Realisez l''etape 1 (mode mixte + redemarrage du service) avant de rejouer ce script.', 16, 1);
    RETURN;
END

/* --- Login applicatif ------------------------------------------------
   Compte de service dedie a N4 Sentinel. Il n'est PAS sysadmin :
   moindre privilege (SEC-002). Il ne peut agir que sur sa propre base. */
IF NOT EXISTS (SELECT 1 FROM sys.sql_logins WHERE name = N'n4sentinel_app')
BEGIN
    DECLARE @sqlLogin nvarchar(max) = N'
        CREATE LOGIN [n4sentinel_app]
            WITH PASSWORD = ' + QUOTENAME(@motDePasse, '''') + N',
                 DEFAULT_DATABASE = [n4sentinel],
                 CHECK_POLICY = ON,
                 CHECK_EXPIRATION = OFF;';
    EXEC sp_executesql @sqlLogin;
    PRINT 'Login [n4sentinel_app] cree.';
END
ELSE
    PRINT 'Login [n4sentinel_app] deja present - mot de passe inchange.';
GO

USE [n4sentinel];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'n4sentinel_app')
BEGIN
    CREATE USER [n4sentinel_app] FOR LOGIN [n4sentinel_app];
    PRINT 'Utilisateur de base [n4sentinel_app] cree.';
END
GO

/* --- Droits ----------------------------------------------------------
   db_datareader + db_datawriter + ddl_admin.
   ddl_admin est necessaire aux migrations EF Core (creation et
   modification des tables). Si vous appliquez les migrations avec un
   compte distinct - ce qui est preferable en Production - retirez
   db_ddladmin de ce compte applicatif. */
ALTER ROLE [db_datareader] ADD MEMBER [n4sentinel_app];
ALTER ROLE [db_datawriter] ADD MEMBER [n4sentinel_app];
ALTER ROLE [db_ddladmin]   ADD MEMBER [n4sentinel_app];
GO

/* --- Controle final -------------------------------------------------- */
SELECT
    'login'      = SUSER_SNAME(SUSER_SID(N'n4sentinel_app')),
    'utilisateur'= USER_NAME(USER_ID(N'n4sentinel_app')),
    'roles'      = STUFF((
                     SELECT ', ' + r.name
                     FROM sys.database_role_members m
                     JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
                     JOIN sys.database_principals u ON u.principal_id = m.member_principal_id
                     WHERE u.name = N'n4sentinel_app'
                     FOR XML PATH('')), 1, 2, '');
GO
