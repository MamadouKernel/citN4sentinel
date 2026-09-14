# Guide de Déploiement — N4 Sentinel

> **Version** : 1.0 · **Date** : 13 septembre 2026 · **Statut** : GO CONDITIONNEL
>
> **Jalon GO/NO GO** : 01 octobre 2026 · **Attendus** : UAT SQL Server (DSI CIT), validation MOA (23 scénarios AC), décision package SSO.

---

## 1. Vue d'ensemble du déploiement

N4 Sentinel est une application **Blazor Server .NET 10**, livrée sous deux modes :

| Mode | Usage | Taille | Runtime requis sur cible |
|---|---|---|---|
| **Dépendant du Runtime** | Préféré si ASP.NET Core 10 est déjà installé sur la VM | ~20 Mo | ✅ ASP.NET Core Hosting Bundle 10 |
| **Autonome** (Recommandé pour PROD isolée) | Embarque le runtime, aucune dépendance | ~150 Mo | ❌ Rien (Windows Server 2019+) |

L'application s'installe :
- En **service Windows** (recommandé, sans IIS) → `sc.exe create ... binPath=<exe> --urls http://+:PORT`
- Sous **SQL Server** (Production / UAT) ou **SQLite** (Test, Développement, POC)

---

## 2. Prérequis

### 2.1. Matériel minimum (Production)

| Ressource | Minimum | Recommandé |
|---|---|---|
| Processeur | 4 vCPU | 8 vCPU |
| Mémoire vive | 8 Go | 16 Go |
| Disque | 40 Go SSD (journaux + trousseau + sauvegardes) | 100 Go SSD |
| Système d'exploitation | Windows Server 2019 (build 17763) | Windows Server 2022 |

### 2.2. Logiciels sur le poste de build (pour publier)

- [ ] [.NET SDK 10](https://dotnet.microsoft.com/download) (`dotnet --version` = 10.x)
- [ ] [Node.js LTS](https://nodejs.org/) (seulement pour recompiler Tailwind CSS, sinon la CSS précompilée suffit)
- [ ] PowerShell 5.1 ou PowerShell 7+ (inclus dans Windows 11 / Server 2022)

### 2.3. Prérequis sur la VM cible

| Contrôle | Valeur | Vérification PowerShell |
|---|---|---|
| Compte de service (domaine AD) | ex : `CIT\svc-n4sentinel` | `Get-ADUser svc-n4sentinel` |
| Droit du compte de service | `Logon as a Service`, accès lecture aux serveurs N4 | GPO Local Security Policy |
| Permissions SQL Server | rôle `db_owner` sur la base `n4sentinel` (ou `db_datareader + db_datawriter + DDL admin`) | Voir script `db/01_activer_connexion_sql.sql` |
| Port HTTP réservé | par défaut **TCP 8443** | `netstat -ano \| findstr :8443` |
| Pare-feu Windows | Règle entrante TCP 8443 (Domaine) | Script d'installation la crée |
| Serveur NTP interne | Le même que pour les serveurs N4 Navis | `w32tm /query /status` |

> ⚠️ **Horloges — RG-0204** : l'écart entre Sentinel et un nœud N4 doit rester < 2 s.
> Au-delà, les workflows critiques sont refusés par `MeriseBusinessPolicyService`.

---

## 3. Procédure de publication (poste de build → paquet)

> Les scripts sont dans le dossier `deploiement/` du dépôt.

### 3.1. Générer le paquet autonome (RECOMMANDÉ)

```powershell
cd deploiement
.\Publier-N4Sentinel.ps1 -Destination D:\Paquets -Version 1.0.0 -Autonome
```

**Sortie** : dossier `D:\Paquets\N4Sentinel-1.0.0\` avec :

```
N4Sentinel-1.0.0/
├── application/              # Binaires + appsettings.Production.json (GABARIT)
├── documentation/            # Guides utilisateur & déploiement (intégrés au paquet)
├── base-de-donnees/          # Scripts SQL d'activation
├── Installer-N4Sentinel.ps1
├── Verifier-N4Sentinel.ps1
└── empreintes.txt            # SHA256 de chaque fichier (anti-altération)
```

Vérification :
```powershell
Get-Content .\empreintes.txt | Select-Object -First 5
```

### 3.2. Générer le paquet dépendant du runtime

```powershell
# Sur la VM cible, ASP.NET Core Hosting Bundle 10 doit être installé au préalable
.\Publier-N4Sentinel.ps1 -Destination D:\Paquets -Version 1.0.0
```

---

## 4. Installation sur le serveur cible

### 4.1. Transférer le paquet

Copier `N4Sentinel-1.0.0/` sur la VM cible, **dans un dossier sans espace** (ex : `C:\Support-Deploiement\`).

### 4.2. Renseigner la configuration avant installation

> **Règle d'or** : `Installer-N4Sentinel.ps1` REFUSE de démarrer si les mots de passe gabarit sont encore présents.

Éditer `application\appsettings.Production.json` :

```json
{
  "ConnectionStrings": {
    "N4Sentinel": "Server=SQLCIT01;Database=n4sentinel;User Id=n4sentinel_app;Password=MotDePasseFort123!;Encrypt=True;MultipleActiveResultSets=True"
  },
  "N4Sentinel": {
    "DataProtection": {
      "KeyPath": "C:\\ProgramData\\N4Sentinel\\cles-protection"
    },
    "FirstAdmin": { "Email": "", "Password": "" }
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
```

### 4.3. Préparer la base SQL Server (si applicable)

```sql
-- Sur SQLCIT01, depuis db/01_activer_connexion_sql.sql
CREATE DATABASE n4sentinel;
CREATE LOGIN n4sentinel_app WITH PASSWORD = 'MotDePasseFort123!';
USE n4sentinel;
CREATE USER n4sentinel_app FOR LOGIN n4sentinel_app;
ALTER ROLE db_owner ADD MEMBER n4sentinel_app;
```

> ⚠️ **Première connexion** : `Encrypt=True` impose de vérifier le certificat SQL. Si votre SQL utilise un certificat auto-signé (hors Production), ajoutez `TrustServerCertificate=True` temporairement — **jamais en Production**.

### 4.4. Lancer l'installation (PowerShell ÉLEVÉ)

```powershell
cd N4Sentinel-1.0.0
.\Installer-N4Sentinel.ps1 -Source .\application -Destination C:\N4Sentinel -Port 8443
```

Ce script fait, dans l'ordre :
1. ✅ Vérifie qu'il est lancé en administrateur
2. ✅ Vérifie l'absence de mots de passe gabarit
3. ⏹️ Stoppe et supprime tout service existant
4. 📋 Copie l'application (**préserve** le trousseau de clés si existant)
5. 🔐 Crée `C:\ProgramData\N4Sentinel\cles-protection`
6. 📝 Enregistre le service Windows (redémarrage auto sur crash)
7. 🔥 Ajoute la règle pare-feu (profil Domaine, TCP 8443)

### 4.5. Configurer le compte de service (POST-INSTALL, NON AUTOMATISABLE)

```
services.msc → N4 Sentinel → Propriétés → Connexion
→ Ce compte  : CIT\svc-n4sentinel
→ Mot de passe : ********** (le vrai)
→ Appliquer → OK → Redémarrer le service
```

### 4.6. Création du 1er administrateur

1. Ouvrir `http://SERVEUR:8443`
2. Aucun compte n'existe encore → formulaire de premier démarrage apparaît
3. Saisir :
   - **Email** : prenom.nom@cit-ci.ci (doit être joignable)
   - **Mot de passe** : ≥ 12 caractères, chiffres, majuscules, symboles
   - **Nom affiché** : Administrateur Solution
4. Valider → le compte est créé avec **TOUS les rôles** (seed Administrateur-Solution)

> ⚠️ Aucun compte « par défaut » n'est livré : une tentative de connexion avant la création échoue (sécurité).

### 4.7. Vérification post-installation

```powershell
.\Verifier-N4Sentinel.ps1 -Port 8443
```

Contrôles effectués par le script :
| Check | Explication |
|---|---|
| Statut service = Running | Simple vérification du SCM |
| HTTP /health = 200 OK | Amorçage EF Core + migrations + base joignable |
| JSON santé | DB check, supervsion check, JMX check, SLA check |

---

## 5. Configuration applicative détaillée

### 5.1. Clés de configuration importantes

| Chemin JSON | Valeur par défaut | Usage |
|---|---|---|
| `ConnectionStrings.N4Sentinel` | gabarit | Chaîne SQL Server / SQLite |
| `N4Sentinel:DataProtection:KeyPath` | `C:\ProgramData\N4Sentinel\cles-protection` | Trousseau DPAPI → SAUVEGARDER avec la BDD |
| `N4Sentinel:Securite:SecondFacteurExigePourAction` | **false** | Activer `true` après enrollement 2FA de TOUS les comptes |
| `N4Sentinel:Jolokia:Enabled` | **false** (SEC-001) | Connecteur JMX ActiveMQ (désactivé par défaut) |
| `N4Sentinel:Ticketing:Enabled` | **false** (SEC-001) | Intégration CIT (ServiceNow/GLPI) — désactivé par défaut |
| `N4Sentinel:Notifications:Enabled` | **false** (SEC-001) | Notifications Push/Email |
| `AzureAd:IsEnabled` | **false** (SEC-001) | SSO Entra ID — toute la config existe mais reste OFF |
| `N4Sentinel:Apr:Enabled` | **true** | Moteur APR Préventif (15 min glissant) |

### 5.2. Activation du SSO Entra ID (optionnel)

L'intégration est **entièrement codée mais désactivée par défaut** (SEC-001).
Pour l'activer :

```json
// appsettings.Production.json
{
  "AzureAd": {
    "IsEnabled": true,
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "<GUID-TENANT-AAD>",
    "ClientId": "<GUID-CLIENT-APPREGISTRATION>",
    "ClientSecret": "<SECRET-DPAPI-PROTECTED>",
    "CallbackPath": "/signin-oidc"
  }
}
```

⚠️ Ensuite, depuis `/admin/azure-ad` → associer les rôles applicatifs aux groupes Entra ID (mapping 1:1 ou n:1).

### 5.3. Activation du connecteur JMX Jolokia

```json
{
  "N4Sentinel": {
    "Jolokia": {
      "Enabled": true,
      "BaseUrl": "https://amq-cit.cit-ci.ci:8161/jolokia",
      "UserName": "jolokia_reader",
      "Password": "<à stocker via DPAPI>"
    }
  }
}
```

Puis aller dans `/admin/jmx` pour déclarer les endpoints `ActiveMQBroker`, `N4Center`, `N4Web`, etc.

### 5.4. Activation du Ticketing CIT

```json
{
  "N4Sentinel": {
    "Ticketing": {
      "Enabled": true,
      "Provider": "ServiceNow",
      "InstanceUrl": "https://cit.service-now.com",
      "ApiUser": "api-n4sentinel",
      "ApiKey": "<SECRET-DPAPI>",
      "WebhookSecret": "<GENERATE-HMAC-KEY-32-BYTES>",
      "DefaultAssignmentGroup": "N4-Support"
    }
  }
}
```

> **Sécurité Webhook — fail-closed** : toute requête sans signature `X-HMAC-SHA256`, ou signature invalide, reçoivent **HTTP 401** immédiatement (validation via `CryptographicOperations.FixedTimeEquals`). Voir la correction du pentest du 13/09/2026.

---

## 6. Sécurité & durcissement Production

### 6.1. Trousseau Data Protection (DPAPI)

📍 **Emplacement** : `C:\ProgramData\N4Sentinel\cles-protection\`

Ce dossier contient les clés qui déchiffrent :
- Les mots de passe des comptes techniques N4
- Les secrets JMX / Ticketing / SSO stockés en base

**Procédure de sauvegarde (HEBDOMADAIRE, en même temps que la BDD)** :
```powershell
Compress-Archive -Path "C:\ProgramData\N4Sentinel\cles-protection" `
    -DestinationPath "\\SAUVEGARDE\Heure\N4Sentinel-cles-DATE.zip" `
    -CompressionLevel Optimal
```

⚠️ **Perte du trousseau = perte de TOUS les secrets.** Il faudra réencoder manuellement chaque compte dans l'interface.

### 6.2. Sauvegarde & restauration

La page `/admin/sauvegarde` propose :
- ✅ Sauvegarde chaude SQLite / SQL Server via `BACKUP DATABASE` ou `VACUUM INTO`
- ✅ Whitelist stricte des extensions : `.db`, `.bak`, `.sqlite`
- ✅ Normalisation systématique via `Path.GetFullPath` — élimination de tout Path Traversal
- ✅ Historique des 30 dernières sauvegardes (rotation auto)

Procédure de restauration :
```powershell
# 1. Arrêt du service
Stop-Service N4Sentinel

# 2. Copie de la sauvegarde
Copy-Item \\SAUVEGARDE\n4sentinel-20260913.bak C:\N4Sentinel\Data\n4sentinel.db

# 3. Restaurer SQL Server (si applicable)
# RESTORE DATABASE n4sentinel FROM DISK = '...\n4sentinel-20260913.bak' WITH REPLACE;

# 4. Redémarrer
Start-Service N4Sentinel
.\Verifier-N4Sentinel.ps1 -Port 8443
```

### 6.3. Durcissement IIS / Reverse Proxy (si applicable)

Si on expose Sentinel derrière un reverse proxy :
```
RequestHeaders → X-Forwarded-For, X-Forwarded-Proto
CORS : limiter aux origines connues
WAF : règles OWASP Top 10 (SQLi, XSS, CSRF gérés nativement par Blazor/Antiforgery)
TLS 1.2 minimum, cipher suites ECDHE uniquement
```

---

## 7. Gestion des comptes et rôles

Aller dans `/admin/utilisateurs` (réservé à Auditeur + Admin Solution).

### 7.1. Création d'un nouveau compte

1. Bouton « Nouvel utilisateur » → saisir email, nom, prénom
2. Générer un mot de passe initial (12 caractères min)
3. **Attribuer SEULEMENT les rôles nécessaires** (principe du moindre privilège)
4. Envoyer le mot de passe initial par **canal séparé** de l'email d'invitation

### 7.2. Mapping recommandé — rôles → personnes

| Rôle | Profils CIT typiques | Nombre conseillé |
|---|---|---|
| Administrateur-Solution | DSI — Chef de projet / Architecte | 2 (astréinte + remplaçant) |
| Validateur | DSI — Responsable exploitation | 2–3 |
| Administrateur-N4 | Équipe Navis N4 (pilotes autorisés) | 2–4 |
| Administrateur-Infrastructure | DSI — Infrastructure / Systèmes | 2 |
| Opérateur-N4 | Support N2 N4 / Équipe de nuit | 4–8 |
| Lecteur-SupportN1 | Support N1 / Helpdesk | 4+ |
| Auditeur | Contrôle interne / RSSI | 1–2 |
| TOS-Manager | Responsable exploitation terminal | 2–3 |

### 7.3. Activation du 2e facteur (recommandée pour TOUS)

1. Aller sur **Mon Profil** → **Double Authentification**
2. Scanner le QR code avec Authenticator / Microsoft Authenticator
3. Saisir le code OTP 6 chiffres
4. **Télécharger les codes de récupération** (10 codes) → les conserver en coffre-fort

Puis, QUAND TOUS LES COMPTES sont enrôlés :

```json
{
  "N4Sentinel": { "Securite": { "SecondFacteurExigePourAction": true } }
}
```

→ Toute capacité d'exécution/validation nécessite alors un 2FA validé dans les 15 dernières minutes.

---

## 8. Planification de maintenance (automatique via P6)

Aller dans `/admin/planifications` (réservé Administrateur Solution).

Les 3 workflows standards pré-configurés :
| Planification | Récurrence | Horaire | Ce qu'elle fait |
|---|---|---|---|
| **Redémarrage mensuel complet** | Mensuel (1er samedi) | 02:00 UTC | BDD → ActiveMQ → Standby Center → Primary Center → XPS → ECN4 → Web |
| **Contrôle hebdomadaire santé** | Hebdomadaire (dimanche) | 03:00 UTC | Santé BDD (index fragmentation, tailles fichiers), espace disque |
| **Maintenance mensuelle snapshots** | Mensuel (avant patch Tuesday) | 01:30 UTC | Snapshots VM + purge KahaDB AMQ + sauvegarde |

Aller dans `/admin/maintenance-calendrier` pour :
- Déclarer les **jours fériés CIT** (actions décalées automatiquement)
- Déclarer les **pics d'activité** (Black Friday, fêtes de fin d'année, réceptions géantes) — Sentinel refusera alors toute maintenance planifiée.

---

## 9. Dépannage & logs

### 9.1. Localisation des journaux

| Source | Chemin |
|---|---|
| Application Serilog | `C:\N4Sentinel\journaux\` → fichiers `n4sentinel-YYYYMMDD.log` |
| Windows Event Log | `Applications et services\ → N4Sentinel` (si activé) |
| Journal d'audit applicatif | Interrogeable via `/admin/audit` |
| Santé temps réel | `http://SERVEUR:8443/health` (JSON détaillé) |

### 9.2. Problèmes fréquents

| Symptôme | Cause probable | Résolution |
|---|---|---|
| Service Running mais HTTP 500 | Base SQL injoignable ou migration en échec | Voir `journaux\n4sentinel-*.log` → `DatabaseMigration` |
| Erreur « connexion n'est pas approuvée » SQL | Certificat SQL non vérifiable par le client | Installer le certificat AC interne dans le magasin Machine → Trusted Root |
| Erreur `Palier2ConnecteurInterdit` | Étape destructive sur connecteur non whitelisté en PROD | Admin Solution → Environnement → autoriser le connecteur |
| Erreur HMAC 401 sur webhook Ticketing | Signature HMAC absente ou invalide | Régénérer `WebhookSecret` 32 bytes hex ; synchroniser des deux côtés |
| Échec auto-approbation | Même utilisateur demandeur ET approbateur | Choisir un Validateur DIFFÉRENT (le code vérifie les noms, pas seulement les rôles) |
| Perte trousseau DPAPI | Restauration depuis sauvegarde ZIP du trousseau | Décompresser dans `cles-protection\` puis redémarrer le service |
| Mots de passe comptes techniques « illisibles » | Clé DPAPI absente → message générique | Réencoder les secrets depuis `/admin/environnements/{Id}/comptes` |

---

## 10. Checklist de Mise en Production (à signer)

### 10.1. Pré-GO (24h avant)

- [ ] **Tests UAT** : les 23 scénarios AC sont 100% OK et signés MOA
- [ ] **Infrastructure** : VM avec ressources dispo, snapshot pris avant déploiement
- [ ] **Base** : sauvegarde pré-GO + restauration testée sur instance BAC
- [ ] **Trousseau DPAPI** : sauvegarde ZIP testée (dézip OK + clés lisibles)
- [ ] **Compte de service** : test de connexion SQL réalisé manuellement
- [ ] **Horloge NTP** : écart Sentinel ↔ tous nœuds N4 < 1 seconde (via `/vitalite`)
- [ ] **Pare-feu** : règle TCP 8443 Domaine activée, testée de poste distant

### 10.2. GO — 01 octobre 2026

- [ ] Installation scriptée réalisée sans erreur
- [ ] Service `N4Sentinel` = Running
- [ ] `/health` = 200 OK et tous les checks internes = Healthy
- [ ] Premier admin créé
- [ ] 2 Validateurs et 2 Administrateurs N4 créés, rôles attribués
- [ ] Test d'approbation à 4 yeux : Opérateur demande → Validateur → Opération OK
- [ ] Environnement Production créé dans `/admin/environnements` + composants déclarés
- [ ] Planifications maintenance activées
- [ ] Bannière ROUGE « PROD » bien visible sur tous les écrans
- [ ] Support N1 / N2 formés et avertis de la mise en ligne

### 10.3. Post-GO (J+1, J+3, J+7)

- [ ] J+1 : Revue des alertes 24h, journal d'audit, pas de tentative intrus
- [ ] J+3 : Vérification espace disque, sauvegarde automatique réalisée
- [ ] J+7 : 1er rapport SLA `admin/rapports` → MTTR, taux succès, APR évitées

---

## 11. Annexes & références

| Document | Emplacement |
|---|---|
| Procédure sauvegarde/restauration détaillée | `docs/S4-06_Doc-exploitation-sauvegarde-restauration.md` |
| PV de livraison Sprint 4 (GO CONDITIONNEL) | `docs/S4-07_PV-Livraison-01-octobre-2026.md` |
| Sprint Review Finale & couverture fonctionnelle | `docs/S4-08_Sprint-Review-Finale.md` |
| Modèles de données MERISE (MCD, MLD, MPD) | `docs/MERISE/` |
| Règles de gestion MERISE | `docs/MERISE/01_Regles_de_Gestion.md` |
| Procédures SOP N4-001 et N4-002 | `docs/SOP/` |
| Scripts installation SQL | `db/01_activer_connexion_sql.sql`, `db/02_configurer_acces_sql.ps1` |

*Fin du Guide de Déploiement. Pour l'usage au quotidien voir Guide-Utilisateur-N4Sentinel.md.*
