# Procédure de migration EF Core — UAT N4 Sentinel

**Référence** : CIT-CIV-DSI-RFP-0010  
**Version** : 1.0 — 18/08/2026

---

## 1. Vérification avant migration

```powershell
# 1.1 — S'assurer que le build compile sans erreur
cd C:\deploy\N4Sentinel
dotnet build src/N4Sentinel.Web/N4Sentinel.Web.csproj --configuration Release

# 1.2 — Vérifier qu'il n'y a pas de changements de modèle non migrés
dotnet ef migrations has-pending-model-changes `
  --project src/N4Sentinel.Infrastructure `
  --startup-project src/N4Sentinel.Web

# Si le résultat n'est PAS "No changes", ARRÊTER et investiguer
# avant toute migration.

# 1.3 — Lister les migrations en attente d'application
dotnet ef migrations list `
  --project src/N4Sentinel.Infrastructure `
  --startup-project src/N4Sentinel.Web

# 1.4 — Vérifier la connexion à la base cible
# Remplacer par la chaîne de connexion UAT réelle
$env:ConnectionStrings__N4Sentinel = "Server=<SERVEUR_UAT>;Database=n4sentinel_uat;..."
```

---

## 2. Sauvegarde avant migration

> [!CAUTION]
> Ne jamais migrer sans sauvegarde vérifiée. La restauration est décrite en section 5.

```sql
-- Sur le serveur SQL Server UAT, via SSMS ou sqlcmd :

-- 2.1 Sauvegarde complète
BACKUP DATABASE n4sentinel_uat
TO DISK = N'C:\Backups\n4sentinel_uat_pre_migration_YYYYMMDD_HHMM.bak'
WITH INIT, COMPRESSION, CHECKSUM, STATS = 10;

-- 2.2 Vérification de la sauvegarde (obligatoire)
RESTORE VERIFYONLY
FROM DISK = N'C:\Backups\n4sentinel_uat_pre_migration_YYYYMMDD_HHMM.bak'
WITH CHECKSUM;

-- Si RESTORE VERIFYONLY échoue → NE PAS CONTINUER
```

---

## 3. Application des migrations

```powershell
# 3.1 — Méthode recommandée : via l'application (DatabaseSeeder au démarrage)
# La migration est appliquée automatiquement par DatabaseSeeder.SeedAsync()
# à chaque démarrage. Relancer simplement l'application en UAT.

# 3.2 — Méthode alternative : script SQL généré (pour audit du schéma)
dotnet ef migrations script `
  --project src/N4Sentinel.Infrastructure `
  --startup-project src/N4Sentinel.Web `
  --idempotent `
  --output migrations_uat_$(Get-Date -Format 'yyyyMMdd_HHmm').sql

# Relire le script généré AVANT de l'appliquer.
# Le transmettre au DBA si requis par le process interne CIT.
```

---

## 4. Validation après migration

```sql
-- 4.1 — Vérifier que la table __EFMigrationsHistory est à jour
SELECT TOP 10 MigrationId, ProductVersion
FROM __EFMigrationsHistory
ORDER BY MigrationId DESC;

-- Dernière migration attendue : 20260818122109_CloisonnementEtAntivirus
-- (ou ultérieure si des migrations ont été ajoutées depuis)

-- 4.2 — Vérifier que le trigger AuditEntries est présent (FR-092)
SELECT name, type_desc FROM sys.triggers
WHERE parent_id = OBJECT_ID('AuditEntries');

-- Résultat attendu : TR_AuditEntries_NoUpdateDelete

-- 4.3 — Tester le trigger (en base TEST uniquement, jamais en UAT/Production)
-- Ce test est couvert par AuditImmutabiliteTests.cs
```

```powershell
# 4.4 — Relancer les tests d'intégration pour confirmer l'état de la base
$env:N4SENTINEL_TEST_DB = "Server=<SERVEUR_UAT>;Database=n4sentinel_uat_test;..."
dotnet test tests/N4Sentinel.Tests/N4Sentinel.Tests.csproj --filter "Category=Migration"
```

---

## 5. Rollback (si nécessaire)

> [!WARNING]
> Le rollback supprime les données créées après la migration. À n'utiliser qu'en cas d'échec critique et avant tout accès des utilisateurs.

```sql
-- 5.1 — Restauration de la sauvegarde pré-migration
-- Fermer toutes les connexions applicatives d'abord

ALTER DATABASE n4sentinel_uat SET SINGLE_USER WITH ROLLBACK IMMEDIATE;

RESTORE DATABASE n4sentinel_uat
FROM DISK = N'C:\Backups\n4sentinel_uat_pre_migration_YYYYMMDD_HHMM.bak'
WITH REPLACE, CHECKSUM, STATS = 10;

ALTER DATABASE n4sentinel_uat SET MULTI_USER;

-- 5.2 — Redéployer la version précédente de l'application
-- (avant le changement de code qui a introduit les nouvelles migrations)
```

---

## 6. Contacts et escalade

| Rôle | Contact |
|---|---|
| Administrateur SQL UAT | _à renseigner_ |
| Responsable déploiement | _à renseigner_ |
| Référent technique N4 Sentinel | _à renseigner_ |

---

## 7. Checklist rapide (à cocher avant chaque migration)

```
[ ] Build compilé sans erreur
[ ] has-pending-model-changes = FALSE
[ ] Sauvegarde réalisée et vérifiée (RESTORE VERIFYONLY = OK)
[ ] Script SQL relu ou DBA informé
[ ] Fenêtre de maintenance annoncée
[ ] Application redémarrée et migrations appliquées
[ ] Trigger AuditEntries présent
[ ] Tests de smoke passés
```
