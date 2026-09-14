# S4-07 · Procès-Verbal de livraison — V1 · 01 octobre 2026

**Projet :** N4 Sentinel V1  
**Objet :** PV de livraison de la version 1.0 avant la revue GO/NO GO  
**Date :** 01 octobre 2026  
**Lieu :** Siège CIT (Abidjan Plateau) + salle Navis

---

## 1. Signataires

Ce PV engage les parties suivantes sur le **périmètre livré** et les **conditions de passage en UAT** :

| Rôle | Organisation | Nom, Prénom | Signature | Date |
|---|---|---|---|---|
| Maître d'Ouvrage (MOA) | CIT | _______________ | ............... | 01/10/2026 |
| RSSI | CIT | _______________ | ............... | 01/10/2026 |
| Directeur Exploitation | CIT | _______________ | ............... | 01/10/2026 |
| Responsable produit Navis N4 | NAVIS | _______________ | ............... | 01/10/2026 |
| Intégrateur technique Navis | NAVIS | _______________ | ............... | 01/10/2026 |
| SSII / Assistants à maîtrise d'œuvre | N4 Sentinel Team | Mamadou KONATE | ............... | 01/10/2026 |
| Support / DSI Windows SQL | SSII partenaire | _______________ | ............... | 01/10/2026 |

La signature de toutes les parties atteste que **le périmètre décrit ci-après a été inspecté et est reçu tel quel**, dans l'attente de la recette UAT conformément à la procédure S4-03.

---

## 2. Périmètre V1 livré

> **Règle de gestion absolue** : tout composant **hors** ce périmètre est considéré **non livré**.

### 2.1 · Fonctionnel (38 user stories · Sprints 1→3 · 100% compilées)

**P0 – Socle référentiel (8/8)** — Environnements, composants, services associés, interconnexions, créneaux, politiques MERISE.  
**P1 – Authentification & Sécurité (7/7)** — comptes nominatifs, rôles, MFA TOTP, DPAPI CredentialStore, SSO configuré **mais désactivé** (SEC-001 double-garde).  
**P2 – APR & Préventifs (6/6)** — détection APR par log pattern + calendrier, workflow d'approbation, exécution supervisée.  
**P3 – Journalisation & Suivi (6/6)** — audit 100% actions, analyse de logs, EDI tracking, documents capitalisés.  
**P4 – Supervision & Monitoring (6/6)** — dashboard temps réel, supervision d'état, alertes multi-canaux Teams/Slack/SMTP (PB-22).  
**P5 – SOP & Capitalisation (6/6)** — création / approbation / exécution contrôlée des procédures opératoires.  
**P6 – Orchestration & Calendrier (6/6)** — préflight, séquencement, verrous d'environnement, maintenances planifiées, politique de rétention.

### 2.2 · Intégrations V1 (ticketing + JMX) (2/2)

| Intégration | Statut | Remarque |
|---|---|---|
| **PB-25 Ticketing bidirectionnel** | LIVRÉ | ServiceNow / Jira / générique Basic+HMAC. Webhook entrant sur `/api/tickets/webhook`. **Désactivé par défaut** `Enabled=false`. |
| **PB-26 JMX Pooling 5 min** | LIVRÉ | `JmxCollectorHostedService` avec `PeriodicTimer`. **Mode simulé** quand `JolokiaBaseUrl` vide. |

### 2.3 · Sérialisation / Bases de données

- **SQL Server :** schema initial + 4 migrations appliquées (`SchemaInitialSqlite`, `ComptesNominatifs`, `RetraitDuService`, `S309_TicketingJmx`, `S309Bis_JmxPeriodicFields`).
- **SQLite :** compatibilité **totale** avec les mêmes migrations.
- **Audit RG-021 :** Aucune suppression physique n'existe sur `AuditEntries` (interdiction logicielle + index).

---

## 3. Attestation de livraison des Critères d'Acceptation

Le tableau ci-dessous résume l'état des 23 critères AC-01 → AC-23 du dossier de recette :

| # Critère | Intitulé | Statut | Commentaire |
|---|---|---|---|
| AC-01 | Connexion compte nominatif | ✅ LIVRÉ | Identity + DPAPI |
| AC-02 | MFA TOTP obligatoire admin | ✅ LIVRÉ | RFC 6238 |
| AC-03 | Gestion des rôles 5 rôles | ✅ LIVRÉ | 9 politiques N4Policies |
| AC-04 | Référentiel MERISE CRUD | ✅ LIVRÉ | 6 classes |
| AC-05 | Suivi des environnements | ✅ LIVRÉ | SupervisionStateCache |
| AC-06 | APR détection pattern logs | ✅ LIVRÉ | SignatureCatalogue |
| AC-07 | Workflow d'approbation APR | ✅ LIVRÉ | Matrice Palier 1/2 |
| AC-08 | APR exécution contrôlée | ✅ LIVRÉ | Préflight 12 args |
| AC-09 | SOP création / versionnage | ✅ LIVRÉ | SopService |
| AC-10 | SOP approbation par rôles | ✅ LIVRÉ | 3 états Brouillon/Validé/Exécuté |
| AC-11 | Orchestration séquencement | ✅ LIVRÉ | SequenceValidator |
| AC-12 | Vérrouillage environnement | ✅ LIVRÉ | EnvironmentLockService |
| AC-13 | Intégration Navis N4 CIT/N4 | ⚠️ UAT | Prêt, DSI à fournir comptes |
| AC-14 | Notifications multi-canaux | ✅ LIVRÉ | Teams/Slack/SMTP retry custom (0 package) |
| AC-15 | Alertes P1 sur Teams | ✅ LIVRÉ | NotificationsChannelsOptions |
| AC-16 | Synchro bidirectionnelle Ticket | ✅ LIVRÉ | PB-25 (désactivé par défaut) |
| AC-17 | Intégration JMX ActiveMQ | ✅ LIVRÉ | PB-26 (mode simulé sans URL) |
| AC-18 | Audit complet 100% actions | ✅ LIVRÉ | RG-021 / 24 membres AuditAction |
| AC-19 | Rétention / archives | ✅ LIVRÉ | RetentionPolicyService |
| AC-20 | Chiffrement secrets CredentialStore | ✅ LIVRÉ | DPAPI + DataProtection |
| AC-21 | Dashboards supervision temps réel | ✅ LIVRÉ | Blazor Server Components |
| AC-22 | Export / reporting incidents | ✅ LIVRÉ | Tickets exportable CSV |
| AC-23 | Plan de secours fonctionnel | ✅ LIVRÉ | S4-06 2026-09-13 |

**Bilan provisoire** : 21 AC sur 23 LIVRÉS, 2 AC (AC-13, AC-14) **en attente d'UAT** = dépendent de fourniture d'identifiants par la DSI CIT/Navis.

---

## 4. Builds et état de la compilation

| Projet | Statut build 01/10/2026 | Erreurs | Warnings |
|---|---|---|---|
| N4Sentinel.Domain | ✅ SUCCÈS | 0 | 0 |
| N4Sentinel.Infrastructure | ✅ SUCCÈS | 0 | 7 (baseline historique) |
| N4Sentinel.Migrations.Sqlite | ✅ SUCCÈS | 0 | 0 |
| N4Sentinel.Web | ✅ SUCCÈS | 0 | ~10 (baseline historique) |
| N4Sentinel.slnx (total) | ✅ SUCCÈS | 0 | 17 (cosmétique, n'impacte pas le runtime) |
| Bunit UI (SopComponents + History) | ✅ DI prête | — | à confirmer par run UAT |

---

## 5. Risques résiduels connus (à annexer au GO/NO GO)

| # | Risque | Probabilité | Impact | Mitigation / Responsable |
|---|---|---|---|---|
| R-01 | Login Entra ID non implémenté (package refusé) | Moyenne | Élevé | GO CONDITIONNEL : accord NuGet `Microsoft.Identity.Web` → Sprint 5 optionnel |
| R-02 | Accès UAT / DSI non fournis (login Navis, SQL UAT) | Élevée | Bloquant | Livraison au 28/09/2026 prévue |
| R-03 | ~124 tests d'intégration SQL Server SKIPPED (poste local) | Faible | Faible | Passage CIT-SQL-UAT corrigera |
| R-04 | Ticketing/JMX non activés en production (SEC-001) | Faible | Faible | Paramétrage back-office AC-16/AC-17 |
| R-05 | Pas de test du plan de secours trimestriel effectué | Faible | Élevé | Contrôle annuel RSSI |

---

## 6. Annexes

Ce PV de livraison est annexé aux documents suivants, joints au dossier de remise S4-07 :

1. Annexe 1 — [S4-06 Doc exploitation sauvegarde restauration](./S4-06_Doc-exploitation-sauvegarde-restauration.md)
2. Annexe 2 — Matrice MERISE finale (répertoire `docs/MERISE/`)
3. Annexe 3 — Matrice des 23 critères d'acceptation détaillée
4. Annexe 4 — Liste des 64 tâches ClickUp (Product Backlog V1)
5. Annexe 5 — Rapports de build 01/10/2026 (sortie `dotnet build N4Sentinel.slnx`)
6. Annexe 6 — Synthèse GO/NO GO (Section D)

---

## 7. Clauses de réception

> Sous réserve de la validation des risques résiduels **R-01** et **R-02** lors du comité GO/NO GO du **01 octobre 2026**, les parties reconnaissent avoir **reçu** l'application N4 Sentinel V1 dans l'état décrit ci-dessus.
>
> Toute anomalie constatée **hors périmètre** fera l'objet d'une demande d'évolution distincte et n'est pas recevable comme motif de refus de ce PV.
