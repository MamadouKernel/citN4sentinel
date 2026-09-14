# Analyse MERISE — N4 Sentinel
## 06. Modèle Organisationnel des Traitements (MOT)

**But** : Modéliser qui fait quoi — correspondance entre acteurs / profils d'habilitation et processus/traitements.
**Source vérité** : CdC §2.5 Profils d'accès + RG-0801 Séparation des devoirs (qui approbateur ≠ qui exécute ≠ qui audite).

---

## Niveau 0 — Acteurs de l'organisation (8 profils + 4 externes)

### 8 Profils RBAC applicatifs N4 Sentinel (CdC §2.5 + [N4Roles.cs](file:///C:/Users/KERNELMK/Documents/cit/navis/N4sentinel/src/N4Sentinel.Domain/N4Roles.cs))

| ID Profil | Code Profil | Nom du Profil | Type d'Utilisateur Typique | Positionnement dans org. DSI CIV |
|---|---|---|---|---|
| P1 | `Lecteur` | **Lecteur / Support N1** | Équipe Helpdesk N1, TOS Manager lecture | Niveau 1, tout d'abord saisi par cet acteur |
| P2 | `OperateurN4` | **Opérateur N4 / Support N2** | Opérateur équipe Solutions IT | Niveau 2, exécute opérations autorisées, diagnostics confirmés |
| P3 | `AdminN4` | **Administrateur N4** | Responsable N4, Expert N3 N4 | Niveau 3, expert N4, opérations sensibles, arrêt/start complet Prod |
| P4 | `AdminInfra` | **Administrateur Infrastructure** | Équipe Infrastructure (serveurs, AD, réseau, stockage) | Hors périmètre applicatif, réceptionne escalades Infra |
| P5 | `Validateur` | **Validateur / Responsable Habilité** | Chef de Projet, Responsable DSI adjoint, DSI | Approbation double sur opérations critiques Production |
| P6 | `AdminSolution` | **Administrateur Solution (N4 Sentinel lui-même)** | Architecte / DSI / Propriétaire produit N4 Sentinel | Gère référentiel, workflows, signatures, rôles, seuils |
| P7 | `Auditeur` | **Auditeur** | Contrôle interne, conformité, sécurité, commissaire aux comptes | Lecture SEULE de l'audit, historiques, preuves, habilitations |
| P8 | `TOSManager` | **TOS Manager (Terminal Ops Manager)** | Responsable exploitation terminale | Consultation SEULE — aucune action technique |

### 4 Acteurs Externes

| ID | Acteur Externe | Interactions |
|---|---|---|
| E1 | **Support Navis / Kaleris** | Réception de packages d'escalade (Export logs + stats + hypothèses) ; envoi procédures officielles (alimentent Docs + SOP) |
| E2 | **Équipe Infrastructure CIV (hors AdminInfra Sentinel)** | Via formulaire : tickets incidents infra, configuration NTP/VLAN/firewall, backup, VMWare |
| E3 | **DSI / Direction** | Rapports MTTR mensuels, métriques, tableaux de bord, décisions changements majeurs |
| E4 | **Équipe Audit / Sécurité interne** | Consultation journal d'audit (append-only), rapports conformité, habilitations (revue trimestrielle) |

---

## Niveau 1 — Organigramme fonctionnel & séparation des devoirs

```
                   ┌────────────────────────────────────────────────┐
                   │           DSI CIV - Direction                  │
                   │  (Validation stratégique, MTTR, #P1/mois)       │
                   └─────────────────┬──────────────────────────────┘
                                     │ Approbations critiques
                    ┌────────────────▼──────────────────┐
                    │   VALIDATEUR (P5) - Habilité Prod │◄──┐ (double approb)
                    │   - Double approb critique       │   │
                    │   - Contournement étape bloque   │   │
                    └──────────────────┬────────────────┘   │
                                       │                    │
          ┌────────────────────────────┼──────────────────┐ │
          ▼                            ▼                  ▼ │
  ┌──────────────────┐      ┌──────────────────────────┐  │ │
  │  AdminSolution   │      │     AdminN4 (P3) -       │  │ │
  │   P6 - (N4 SENT) │      │     Expert Niveau 3      │  │ │
  │                  │      │  - Opérations sensibles  │  │ │
  │ - Référentiels   │      │  - Complète/Partielle    │  │ │
  │ - Workflows      │      │  - Diagnostics avancés   │  │ │
  │ - Signatures/Règles│    │  - Split-brain + AMQ     │  │ │
  │ - Rôles/Matrices│      │  - Reconstitution Dossier│  │ │
  │ - Seuils APR     │      └──────────┬───────────────┘  │ │
  │ - SOP validation │                 │ Opérations       │ │
  │ - Audit (lect)   │      ┌──────────▼───────────────┐  │ │
  └────────┬─────────┘      │  OpérateurN4 - P2 (N2)  │  │ │
           │                │  - Diagnostic standard   │  │ │
           │                │  - Exécution WF/SOP      │  │ │
           │                │  - Saisie preuve opér.   │  │ │
           │                │  - Niveau 2 résolution   │──┘ │
           │                └──────────┬───────────────┘    │
           │                           │ Diagnostic         │
           │                ┌──────────▼───────────────┐    │
           │                │  Lecteur/Support N1 - P1 │    │
           │                │  - Consultation tableau  │    │
           │                │  - Alerte, signaler P1   │    │
           │                │  - 1ère vérification Top10│    │
           │                └──────────┬───────────────┘    │
           │                           │                    │
           │                ┌──────────▼───────────────┐    │
           │                │  TOS Manager - P8 (Lect.)│    │
           │                │  - Consultation (seul)   │    │
           │                └──────────────────────────┘    │
           │                                                 │
  ┌────────┴──────────────────────────────┐              ┌───▼──────────┐
  │  AUDITEUR - P7 (Conformité)           │              │ AdminInfra P4│
  │  - Journal audit (append-only)        │              │ - Escalade   │
  │  - Historique habilitations          │              │   infra      │
  │  - Conformité SEC-001..SEC-010       │              │ - Horloges   │
  └───────────────────────────────────────┘              └──────────────┘

           ╔═══════════════════════════════════════════════════════════╗
           ║  RÈGLE SÉPARATION DES DEVOIRS (RG-0801)                 ║
           ║  Opérateur (P2/P3) ≠ Approbateur (P5) ≠ Auditeur (P7)   ║
           ║  Même utilisateur NE PEUT PAS cumuler ces rôles pour la  ║
           ║  MÊME opération critique PRODUCTION.                    ║
           ╚═══════════════════════════════════════════════════════════╝
```

---

## Niveau 2 — Matrice Acteurs × Processus

**Échelle des droits** :
- 🔒 **Aucun accès** (droit refusé)
- 👁️ **Consultation R** (Read seul : tableau de bord, alertes, historique)
- ✍️ **Contribution C** (Création/Réponse, pas d'action mutative)
- ⚙️ **Exécution X** (Exécution d'opérations / actions mutatives autorisées)
- ✅ **Validation V** (Approbation simple ou double)
- 🛡️ **Administration A** (Paramétrage, référentiels, habilitations)
- 📜 **Audit-only** (Lecture de logs d'audit + conformité)

| Processus Métier | P1 Lecteur N1 | P2 Opérateur N2 | P3 Admin N4 | P4 Admin Infra | P5 Validateur | P6 Admin Solution | P7 Auditeur | P8 TOS Manager |
|---|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| **P1 Référentiel & Paramétrages** | 🔒 | 🔒 | 👁️ | 👁️ | 🔒 | 🛡️A | 👁️ | 🔒 |
| &nbsp;&nbsp;P1.1 Gérer Environnements | 🔒 | 🔒 | 👁️ | 👁️ | 🔒 | 🛡️ | 👁️ | 🔒 |
| &nbsp;&nbsp;P1.2 Inventaire SRV/CMP | 🔒 | 👁️ | ✍️C | ✍️C | 🔒 | 🛡️ | 👁️ | 🔒 |
| &nbsp;&nbsp;P1.3 Construire Workflows | 🔒 | 👁️ | ✍️C | 🔒 | 👁️ | 🛡️ | 👁️ | 🔒 |
| &nbsp;&nbsp;P1.4 Signatures / Corrélations | 🔒 | 👁️ | ✍️C | 👁️ | 🔒 | 🛡️ | 👁️ | 🔒 |
| &nbsp;&nbsp;P1.5 Matrices approbation / Rétentions | 🔒 | 🔒 | 🔒 | 🔒 | ✅V | 🛡️ | 📜Audit | 🔒 |
| &nbsp;&nbsp;P1.6 Credentials (coffre) | 🔒 | 🔒 | 🔒 | ✍️C | 🔒 | 🛡️ | 🔒 | 🔒 |
| **P2 Supervision / Alertes / APR** | 👁️ | 👁️/✍️ | 👁️/✍️ | 👁️ | 👁️ | 👁️ | 👁️ | 👁️ |
| &nbsp;&nbsp;P2.1 Collecte signaux (auto) | 🔒 | 🔒 | 🔒 | 🔒 | 🔒 | 🛡️ | 🔒 | 🔒 |
| &nbsp;&nbsp;P2.2 État réel Consolidé | 👁️ | 👁️ | 👁️ | 👁️ | 👁️ | 👁️ | 👁️ | 👁️ |
| &nbsp;&nbsp;P2.3 Détection anomalies / Alertes | 👁️ | ✍️C (acquit) | ✍️C (acquit) | ✍️C | 👁️ | 👁️ | 👁️ | 👁️ |
| &nbsp;&nbsp;P2.4 APR (Actions Préventives) | 👁️ | ✍️C (programmer) | ✍️C (programmer) | ✍️C | ✅V (si Prod) | 🛡️ (création) | 👁️ | 🔒 |
| &nbsp;&nbsp;P2.5 Dashboard UI | 👁️ | 👁️ | 👁️ | 👁️ | 👁️ | 👁️ | 👁️ | 👁️ |
| **P3 Orchestration Workflows** | 🔒 | ⚙️X (limité) | ⚙️X (total) | 🔒 | ✅V (Prod critique) | 👁️ | 👁️ | 🔒 |
| &nbsp;&nbsp;P3.1 Préparation opération | 👁️ | ✍️C | ✍️C | 🔒 | 👁️ | 👁️ | 👁️ | 🔒 |
| &nbsp;&nbsp;P3.2 Pré-checks automatiques | 🔒 | 👁️ | 👁️ | 👁️ | 👁️ | 👁️ | 👁️ | 🔒 |
| &nbsp;&nbsp;P3.3 Approbations | 🔒 | 🔒 (opérateur ≠ approb) | 🔒 | ✅V | ✅V | 🔒 | 📜Audit | 🔒 |
| &nbsp;&nbsp;P3.4 Exécution séquencée | 🔒 | ⚙️X (confirmations) | ⚙️X (total) | 🔒 | 🔒 | 🔒 | 👁️ | 🔒 |
| &nbsp;&nbsp;P3.5 Pause/Reprise/Contourn./Annulation | 🔒 | ⚙️X Pause | ⚙️X + Contournement | 🔒 | ✅V contourn. | 🔒 | 👁️ | 🔒 |
| &nbsp;&nbsp;P3.6 Clôture + Rapport auto | 👁️ | ✍️C (note finale) | ✍️C (note finale) | 🔒 | 👁️ | 👁️ | 👁️ | 🔒 |
| **P4 Diagnostic Incident / Logs** | ✍️C (déclaration) | ⚙️X (standard) | ⚙️X (expert) | ✍️C (réponses) | 👁️ | 👁️ | 👁️ | 🔒 |
| &nbsp;&nbsp;P4.1 Ouverture Session Diagnostic | ✍️C | ✍️C | ✍️C | ✍️C | 🔒 | 🔒 | 👁️ | 🔒 |
| &nbsp;&nbsp;P4.2 Collecte Journaux | 🔒 | ⚙️X | ⚙️X | 🔒 | 🔒 | 🔒 | 🔒 | 🔒 |
| &nbsp;&nbsp;P4.3 Parsing + Anomalies (auto) | 👁️ | 👁️ | 👁️ | 👁️ | 👁️ | 👁️ | 👁️ | 🔒 |
| &nbsp;&nbsp;P4.4 Corrélation / Hypothèses (auto) | 👁️ | 👁️ | 👁️ + valid. | 👁️ | 👁️ | 👁️ | 👁️ | 🔒 |
| &nbsp;&nbsp;P4.5 Verdict Final | 🔒 | ✍️C (propos.) | ✍️C (propos.) | ✍️C (avis infra) | ✅V (clos) | 🔒 | 📜Audit | 🔒 |
| &nbsp;&nbsp;P4.6 Clôture + Capitalisation | 🔒 | ✍️C | ✍️C | 🔒 | ✅V (Prod) | 🛡️ | 👁️ | 🔒 |
| **P5 Documentaire / Assistant N4** | 👁️ + Q&R | 👁️ + Q&R | 👁️ + Q&R | 👁️ + Q&R | 👁️ + Q&R | 🛡️ (indexation) | 👁️ | 👁️ + Q&R |
| **P6 SOP (Capitalisation & Exécution)** | 👁️ | ⚙️X (exécution) | ⚙️X + création | 🔒 | ✅V (validation SOP) | 🛡️ (cycle vie) | 👁️ | 🔒 |
| &nbsp;&nbsp;P6.1 Génération SOP auto (Brouillon) | 🔒 | 👁️ | ✍️C | 🔒 | 🔒 | 🛡️ | 👁️ | 🔒 |
| &nbsp;&nbsp;P6.2 Validation SOP | 🔒 | 🔒 | ✍️C (avis) | 🔒 | ✅V | 🛡️ | 📜Audit | 🔒 |
| &nbsp;&nbsp;P6.3 Exécution SOP humaine | 🔒 | ⚙️X | ⚙️X | 🔒 | 🔒 | 🔒 | 👁️ | 🔒 |
| &nbsp;&nbsp;P6.4 Réutilisation SOP suggérée | 👁️ | ⚙️X (applic.) | ⚙️X (applic.) | 🔒 | 🔒 | 🔒 | 👁️ | 🔒 |
| **P7 Historique / Rapports / Métriques** | 👁️ | 👁️ | 👁️ | 👁️ | 👁️ | 👁️ | 📜Audit | 👁️ (résumé) |
| &nbsp;&nbsp;P7.1 Historique Opérations | 👁️ | 👁️ | 👁️ | 👁️ | 👁️ | 👁️ | 📜Audit | 👁️ |
| &nbsp;&nbsp;P7.2 Rapports Incidents / Post-Mortem | 🔒 | ✍️C | ✍️C | 🔒 | ✅V | 👁️ | 📜Audit | 🔒 |
| &nbsp;&nbsp;P7.3 Dashboard Métriques Business (MTTR/#P1/%N2) | 👁️ (résumé) | 👁️ | 👁️ | 👁️ | 👁️ | 👁️ | 📜Audit | 👁️ (résumé) |
| &nbsp;&nbsp;P7.4 Journal d'Audit | 🔒 | 🔒 | 🔒 | 🔒 | 🔒 | 🔒 | 📜Audit seul | 🔒 |
| **P8 Sécurité (RBAC, MFA, Secrets)** | 🔒 | 🔒 | 🔒 | 🔒 | ✅V (revue tr.) | 🛡️ | 📜Audit | 🔒 |

---

## Niveau 3 — Workflow opérationnel typique : Résolution P1 avec séparation des rôles

### Scénario : Incident P1 Bridge queue haute + XPS non synchro

```
 T= 00:00   ┌──────────────┐
           │ LECTEUR (P1)  │  Surveillance Dashboard → Alerte bridge.* QueueSize = 42k +
           │ Support N1   │  Cluster Node 1 affiche DISCONNECTED
           └───────┬──────┘  → Coche obligatoirement RG-0307 TOP 10 causes P1
                   │         1. Horloges ?   → Écart 2.3s ⚠️
                   │         2. Réseau ?     → Timeouts ⚠️
                   │         ... (suite checks)
                   ▼  Déclare incident P1 + Suggère ouverture SESSION DIAG
 T= 00:05   ┌─────────────────────┐
           │ OPÉRATEUR N4 (P2)   │  Ouvre Session Diagnostic P4
           │ Niveau 2            │  Collecte logs Bridge + Cluster 1
           │                     │  + Suggère application SOP-N4-QUEUE-03
           └──────────┬──────────┘  (72% réussite historique)
                      │
                      ▼  Exécute SOP étape 1-2-3
 T= 00:15   ┌─────────────────────┐
           │ PROBLÈME persistant │  Bridge toujours 38k messages →
           │ Split-brain suspect │  Signature SIG-SPLIT-BRAIN détectée
           └──────────┬──────────┘  Règle COR-SPLIT-BRAIN-001 = 92% confiance
                      │
                      ▼  ⛔ Bouton Restart Bridge = GRISÉ (RG-0506)
 T= 00:20   ┌─────────────────────┐
           │  ADMIN N4 (P3)      │  Prend la main (Niveau 3 Expert)
           │  Expert             │  Décide : Redémarrer StandbyCenter d'abord
           │  (ne peut approver) │  → Prépare Workflow Reconstitution
           └──────────┬──────────┘  → Bouton "Soumettre pour approbation"
                      │
                      ▼  Attend les 2 approbations (RG-0304 Prod critique)
 T= 00:22   ┌─────────────────────┐     ┌──────────────────────────┐
           │ VALIDATEUR 1 (P5)   │     │ VALIDATEUR 2 - (DSI) P5  │
           │ Responsable Habilité│────►│ DISTINCT du Validateur 1 │
           │ 1ère approbation ✅ │     │ 2ème approbation ✅ (Prod) │
           └─────────────────────┘     └──────────────────────────┘
                                 ┌─────────────────┐
                                 │ MUTEX GLOBAL    │
                                 │ VERROU acquis   │
                                 └────────┬────────┘
                                          │
 T= 00:25                       ▼
                      ┌───────────────────────────┐
                      │  MOTEUR WORKFLOW + AdminN4│  Étapes semi-auto :
                      │  ✅ Exéc. séquencée P3.4  │  1. Switch Mode Maintenance Standby
                      │  ✅ Proof tech chaque étape│  2. Redémarrage Standby → Preuve pas ACTIVE
                      │  ✅ Contournements loggés │  3. Redémarrer Cluster Node 1
                      └────────────┬──────────────┘  4. Vérif queue baisse progressive
                                   │
                                   ▼
 T= 00:45        Synchro rétablie, bridge queue <100.
                 Résultat : TerminéSuccès
                                 │
 T= 00:50                       ▼
                      ┌──────────────────────────┐
                      │ AdminN4 (P3)             │  Clôture Session Diagnostic
                      │ Verdict : CauseConfirmée │  Cause : Horloges + Split Brain débutant
                      │ Puis :                    │
                      │ Génère FICHE_INCIDENT    │  MTTR = 50 min (baseline 47 min)
                      │ Génère SOP (BROUILLON)   │  Niveau compétence résolution = N3
                      └────────────┬─────────────┘  Lessons : Activer Supervision NTP (APR)
                                   │
 T= 01:10                       ▼
                      ┌──────────────────────────┐
                      │ AdminSolution (P6)       │  + VALIDATEUR (P5) :
                      │ + Validateur revue       │  - Valide SOP-2026-NTP-01
                      │ Valide SOP + Param APR   │  - Programme 2 APR (Supervision NTP +
                      │                          │  Exclusions antivirus Standby vérifiées)
                      └──────────────────────────┘
 T= 01:30                       ▼
                      ┌──────────────────────────┐
                      │ AUDITEUR (P7)            │  Consulte :
                      │  (aucune action)         │   • entrée audit WF
                      │ Lecture seule            │   • entrée audit double approb
                      │                          │   • SOP validé
                      └──────────────────────────┘   • conformité RG-0801 OK
```

---

## Niveau 4 — Matrice Acteurs × Règles de Gestion critiques

| Règle de Gestion | Acteur déclencheur | Acteur contrôle | Acteur approuve (Prod critique) | Acteur audite |
|---|---|---|---|---|
| **RG-0105** 1 seul Center/Standby par Env | AdminSolution | AdminInfra | — | Auditeur |
| **RG-0204** Horloges <1s avant opération | Moteur Sentinel (auto) | OpérateurN2 | — | AdminInfra |
| **RG-0303** Motif/Incident obligatoire Prod | OpérateurN2 | AdminN4 | ValidateurP5 | Auditeur |
| **RG-0304 Double approb Prod critique** | Opérateur/Demandeur | AdminN4 **NI (pas lui)** | **2 Validateurs DISTINCTS** P5 | Auditeur (RG-0801) |
| **RG-0305 Mutex global Environnement** | Moteur Sentinel (auto) | AdminSolution | — | Auditeur |
| **RG-0407 StopForcé Jamais Auto** | AdminN4 **PAS Opérateur N2** | AdminN4 (confirm) | ValidateurP5 si Prod | Auditeur |
| **RG-0408 Retry Auto interdit Critique** | AdminSolution — désactive config | Opérateur (manuel retry) | ValidateurP5 | Auditeur |
| **RG-0506 Queue haute → Restart Bridge interdit** | Moteur Sentinel (blocage UI) | AdminN4 — préfère restart Clusters | — | Auditeur |
| **RG-SPLIT-BRAIN Redémarrer STANDBY 1er** | AdminN4 N3 (workflow imposé) | AdminN4 | ValidateurP5 | Auditeur |
| **RG-0601 Masquage secrets logs** | Moteur Sentinel (auto) | AdminSolution config | — | Auditeur |
| **RG-0704 ElevatedRole SOP sensible** | OpérateurN2 / AdminN4 | — (vérif RBAC auto) | ValidateurP5 validation SOP | Auditeur |
| **RG-0801 Séparation devoirs (exéc≠approb)** | AdminSolution (matrice) | AdminN4 | ValidateurP5 | Auditeur (vérif trimestriel) |
| **SEC-003 Coffre jamais clair** | AdminSolution | AdminInfra | ValidateurP5 | Auditeur sécurité |

---

## Niveau 5 — Notifications et canaux de communication

| Événement | P1 Lecteur N1 | P2 Opérateur N2 | P3 Admin N4 | P4 Admin Infra | P5 Validateur | P6 Admin Solution | P7 Auditeur | P8 TOS Manager |
|---|---|---|---|---|---|---|---|---|
| Alerte Critique env. PROD | 🔔 Email | 🔔 Email + Teams | 🔔 Email + Teams + SMS | 🔔 Email si infra | — | 📋 Dashboard | — | 📋 Dashboard résumé |
| Alerte Majeure | 📋 Dashboard | 🔔 Email + Teams | 🔔 Email + Teams | — | — | 📋 Dashboard | — | — |
| Opération Prod = soumise pour approb | — | — | — | — | 🔔 Email + Teams | 📋 Dashboard | — | — |
| Approbations complètes → démarrage WF | — | 🔔 Email + Teams | 🔔 Email + Teams | — | 📋 Dashboard | 📋 Dashboard | — | — |
| Échec étape critique WF | — | 🔔 Email + Teams | 🔔 Email + Teams + SMS | 🔔 Email si infra | — | 📋 Dashboard | — | 📋 Dashboard (niveau impact) |
| Clôture incident P1 | 📋 Dashboard | 🔔 Email | 🔔 Email | 🔔 Email (si infra) | 🔔 Email Rapport | 📋 Dashboard | 📋 Audit | 📋 Dashboard résumé |
| Revue trimestrielle habilitations | — | — | — | — | 🔔 Email + Rapport | 🔔 Email | 📜 Audit complet + rapport | — |
| APR créée (nouvelle préventive) | — | 📋 Dashboard | 📋 Dashboard | 📋 Dashboard (si infra) | 📋 Dashboard | 🔔 Email | — | — |
| Nouveau SOP à valider | — | — | 📋 Dashboard (avis) | — | 🔔 Email + Dashboard | 🔔 Email + Dashboard | 📜 Audit | — |

---

## Synthèse Matrice des Responsabilités RACI — Opérations sensibles Production

**Légende** : **R** = Responsable (exécute), **A** = Approuve (Accountable), **C** = Consulté, **I** = Informé

| Tâche sensible Production | P1 | P2 Opérateur N2 | P3 Admin N4 | P4 Admin Infra | P5 Validateur | P6 Admin Solution | P7 Auditeur |
|---|---|---|---|---|---|---|---|
| Arrêt complet environnement | I | R (étapes d'exécution confirm) | **R (orchestre, responsable)** | C | **A (double approb)** | I | I (audit) |
| Reconstitution AMQ KahaDB Shared Folder | I | I | R | C (support NFS) | **A (double)** | I | I |
| Résolution Split-brain Center/Standby | I | I | R (redém Standby en 1er) | C (anti-virus, snapshots) | **A** | I | I |
| Génération nouveau workflow modèle | I | C (suggestion) | C (avis expert) | C | A (valide séquence) | **R (construit)** | I |
| Nouveau credential coffre pour prod | I | I | I | R (crée compte) | A | R (stocke sécurisé) | I (nom + date) |
| Contournement d'une étape bloquante en Prod | I | I (peut demander) | R (exécute après approb) | C si infra | **A (double)** | I | I |
| Revue habilitation trimestrielle (conformité) | I | I | I | C | A (applique décisions) | R (prépare rapport) | **R (auditeur)** |
| Modification seuil APR (prévention) | I | I | C | C si infra | A (si Prod) | R | I |
| Clôture incident P1 + capitalisation | I | C (notes) | R (rédige) | C si infra | A | I | I (audit) |

---

## Fin du Modèle Organisationnel des Traitements (MOT)
