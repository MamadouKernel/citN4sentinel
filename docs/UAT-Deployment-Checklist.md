# Checklist de déploiement UAT — N4 Sentinel

**Référence** : CIT-CIV-DSI-RFP-0010  
**Version** : 1.0 — 18/08/2026

---

## A. Prérequis infrastructure

```
[ ] Windows Server 2019 ou supérieur
[ ] .NET 10 Runtime installé (vérifier : dotnet --version)
[ ] IIS installé avec module ASP.NET Core Hosting Bundle
    OU Kestrel derrière reverse proxy (IIS / nginx)
[ ] SQL Server 2019+ accessible depuis le serveur applicatif
[ ] Accès WinRM (ports 5985/5986) vers les serveurs N4 supervisés
[ ] PowerShell 7+ disponible sur le serveur applicatif
```

---

## B. Configuration de la base de données

```
[ ] Base n4sentinel_uat créée sur l'instance SQL Server UAT
[ ] Compte SQL dédié UAT créé (distinct de Production)
      db/01_activer_connexion_sql.sql exécuté
[ ] Authentification : Windows (recommandé) ou SQL (voir appsettings.UAT.json)
[ ] Chiffrement du canal : Encrypt=True dans la chaîne de connexion
[ ] TrustServerCertificate=False si un certificat CA est installé
[ ] Connexion testée manuellement depuis le serveur applicatif
      Test : sqlcmd -S <SERVEUR> -d n4sentinel_uat -E -Q "SELECT 1"
```

---

## C. Configuration de l'application

> [!IMPORTANT]
> Ne jamais committer de secrets réels dans Git.
> Utiliser des variables d'environnement ou User Secrets pour surcharger.

```
[ ] appsettings.UAT.json déployé (gabarit sans secret)
[ ] Chaîne de connexion surchargée via variable d'environnement :
      IIS : Application Pool → Environment Variables
      Nom : ConnectionStrings__N4Sentinel
      Valeur : Server=<SERVEUR_UAT>;Database=n4sentinel_uat;...
[ ] Compte d'amorçage premier démarrage configuré :
      N4Sentinel:FirstAdmin:Email = <email-admin>
      N4Sentinel:FirstAdmin:Password = <mot-de-passe-12-car-min>
      (À vider après le premier démarrage)
[ ] Dossier de clés DPAPI configuré et sauvegardé :
      N4Sentinel:DataProtection:KeyPath = C:\ProgramData\N4Sentinel\keys
      CE DOSSIER DOIT ÊTRE SAUVEGARDÉ avec la base SQL
```

---

## D. Authentification multifacteur (MFA)

> [!WARNING]
> **Décision DSI requise (SEC-001)** : le cahier des charges demande un MFA par e-mail.
> L'implémentation actuelle fournit TOTP (application d'authentification).
> Voir `docs/ADR/ADR-MFA-001.md` pour le contexte et les options.

```
[ ] Décision formelle DSI sur SEC-001 (TOTP vs e-mail) : ___ TOTP / ___ e-mail
[ ] Si TOTP retenu : s'assurer que tous les comptes habilités à l'action
      ont enrôlé leur authentificateur AVANT d'activer :
      N4Sentinel:Securite:SecondFacteurExigePourAction = true
[ ] Si e-mail retenu : configurer SMTP (N4Sentinel:Smtp:*)
[ ] Second facteur activé ou désactivé délibérément (pas par oubli) :
      SecondFacteurExigePourAction = true  ← recommandé Production
      SecondFacteurExigePourAction = false ← acceptable UAT si comptes pas encore enrôlés
```

---

## E. DPAPI et protection des données

```
[ ] Dossier de clés DPAPI créé et protégé à l'échelle machine :
      C:\ProgramData\N4Sentinel\keys (ou chemin configuré)
[ ] Droits : compte de service IIS en lecture/écriture UNIQUEMENT
[ ] Procédure de sauvegarde du dossier de clés documentée :
      Sauvegarde quotidienne avec la base SQL
      Restaurer les clés ET la base ensemble, jamais séparément
[ ] Test : application démarrée, secrets techniques lisibles après redémarrage
```

---

## F. Journalisation

```
[ ] Dossier de logs créé et accessible en écriture par le compte de service :
      C:\ProgramData\N4Sentinel\logs  (ou sous AppContext.BaseDirectory)
[ ] Rotation configurée : journalier, 30 jours (déjà dans Program.cs)
[ ] Niveau de log configuré dans appsettings.UAT.json :
      Logging:LogLevel:Default = Information (recommandé)
      Logging:LogLevel:Microsoft.AspNetCore = Warning
[ ] Accès aux logs depuis un outil de monitoring UAT si disponible
      (Seq optionnel — voir docs/Observabilite-Serilog.md)
```

---

## G. Migrations EF Core

```
[ ] Procédure docs/UAT-Migration-Procedure.md relue et signée
[ ] Sauvegarde de la base réalisée avant premier démarrage
[ ] Premier démarrage de l'application :
      DatabaseSeeder applique automatiquement les migrations
      Vérifier les logs au démarrage : "Migrations appliquées"
[ ] Trigger AuditEntries présent (FR-092) :
      SELECT name FROM sys.triggers WHERE parent_id = OBJECT_ID('AuditEntries')
      Résultat attendu : TR_AuditEntries_NoUpdateDelete
[ ] Vérifier __EFMigrationsHistory : dernière migration = 20260818122109_CloisonnementEtAntivirus
```

---

## H. Tests de smoke post-déploiement

```
[ ] L'application démarre sans erreur dans les logs
[ ] Page de connexion accessible : https://<SERVEUR_UAT>/
[ ] Parcours premier démarrage : https://<SERVEUR_UAT>/premier-demarrage
      (si c'est le premier démarrage)
[ ] Connexion avec le compte administrateur créé
[ ] Tableau de bord principal affiché
[ ] Menu Administration → Environnements accessible
[ ] Menu Administration → Utilisateurs accessible
[ ] Endpoint /health répond 200 Healthy (si configuré — Phase IX)
[ ] Endpoint /metrics répond 200 avec données Prometheus (authentifié)
[ ] Pas d'erreurs 500 dans les logs après navigation de base

Écrans à valider manuellement (non testés automatiquement) :
[ ] SOP : /admin/sop et /admin/sop/<id>
[ ] Exécutions SOP : /sop-executions
[ ] Opération avec exécution parallèle (case « Parallélisable » dans un workflow)
[ ] Supervision → panneau Dossiers partagés
[ ] Admin → Composant → section Dossier partagé
[ ] EDI : /edi
```

---

## I. Déploiement

```
[ ] Publier avec le script fourni :
      powershell -File deploiement/Publier-N4Sentinel.ps1
[ ] Arrêter le site IIS avant remplacement des fichiers
[ ] Copier les fichiers publiés vers C:\inetpub\N4Sentinel (ou chemin cible)
[ ] NE PAS écraser appsettings.json ni appsettings.UAT.json s'ils contiennent
      des surcharges locales non versionnées
[ ] NE PAS écraser le dossier cles-protection
[ ] Démarrer le site IIS
[ ] Vérifier les logs au démarrage
```

---

## J. Rollback applicatif

```
[ ] Conserver la version précédente dans C:\deploy\N4Sentinel_backup_YYYYMMDD
[ ] Si régression critique :
      1. Arrêter le site IIS
      2. Restaurer les fichiers de la version précédente
      3. Si la nouvelle version avait des migrations : restaurer la BDD
         (voir docs/UAT-Migration-Procedure.md section 5)
      4. Redémarrer le site IIS
```

---

## K. Verdict final

| Section | Statut | Commentaire |
|---|---|---|
| A — Infrastructure | | |
| B — Base de données | | |
| C — Configuration | | |
| D — MFA (décision DSI) | ⛔ En attente | ADR-MFA-001.md |
| E — DPAPI | | |
| F — Journalisation | | |
| G — Migrations | | |
| H — Tests de smoke | | |
| I — Déploiement | | |

**Verdict UAT** : ☐ READY &emsp; ☐ READY WITH RESERVATIONS &emsp; ☐ NOT READY

**Signataires** :

| Rôle | Nom | Date |
|---|---|---|
| Responsable technique | | |
| Représentant DSI CIT | | |
| Administrateur N4 référent | | |
