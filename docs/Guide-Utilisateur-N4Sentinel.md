# Guide Utilisateur — N4 Sentinel

> **Version** : 1.0 · **Date** : 13 septembre 2026 · **GO/NO GO cible** : 01 octobre 2026
>
> **Projet** : Standardisation & sécurisation des opérations Navis N4 — Côte d'Ivoire Terminal (DSI CIT).

---

## 1. Présentation de l'application

N4 Sentinel est la console unique de supervision, pilotage et sécurisation de l'écosystème Navis N4. Elle regroupe :

| Domaine | Ce qu'il permet |
|---|---|
| **Supervision** | Tableaux de bord, vitalité des nœuds, alertes en temps réel, rapports & SLA, AIOps, jumeau numérique |
| **Exploitation** | Opérations, workflows, SOP, diagnostics poussés, planification de maintenance, EDI |
| **Audit & Sécurité** | Historique, journal d'audit, gestion des comptes, ticketing incidents, connecteurs JMX & sauvegarde |
| **Intelligence Suite** | Flight Simulator pour répéter des scénarios sans impact |

---

## 2. Les 8 rôles applicatifs (matrice de responsabilité)

Le principe directeur est le **moindre privilège + séparation des responsabilités** (SEC-002 CdC).
Un Administrateur N4 ne peut pas approuver son propre contournement ; le Validateur n'a aucun droit d'exécution.

| # | Rôle | Court libellé | Ce qu'il FAIT | Capacités portées |
|---|---|---|---|---|
| 1 | `Lecteur-SupportN1` | 👀 Support N1 | Voir, surveiller, alerter | `PeutConsulter` |
| 2 | `Operateur-N4` | ⚙️ Opérateur N4 | Exécuter les scénarios autorisés, lancer des diagnostics, confirmer les étapes | `PeutConsulter` · `PeutDiagnostiquer` · `PeutExecuter` · `PeutExecuterActionUnitaire` |
| 3 | `Administrateur-N4` | 🛠️ Admin N4 | Actions sensibles (contournements, SOP élevés), analyse incidents avancés | `PeutConsulter` · `PeutDiagnostiquer` · `PeutExecuter` · `PeutExecuterActionUnitaire` · `PeutExecuterActionsSensibles` |
| 4 | `Administrateur-Infrastructure` | 🧱 Admin Infra | Connecteurs techniques, JMX, sauvegarde, contrôles réseau | `PeutConsulter` · `PeutDiagnostiquer` · `PeutAdministrerConnecteurs` |
| 5 | `Validateur` | ✅ Validateur | **Approuve ou refuse** les opérations critiques en Production (contrôle à 4 yeux) | `PeutConsulter` · `PeutApprouver` |
| 6 | `Administrateur-Solution` | 👑 Admin Solution | **Détient toutes les capacités** : environnements, workflows, rôles, seuils, SSO, signatures | TOUTES |
| 7 | `Auditeur` | 📋 Auditeur | Lecture seule de l'historique, preuves, journaux d'audit, comptes & habilitations | `PeutConsulter` · `PeutAuditer` |
| 8 | `TOS-Manager` | 📊 Responsable Exploit | Consultation opérationnelle des états & alertes, sans pilotage technique | `PeutConsulter` |

> ⚠️ **Règle d'or** : le code vérifie une **capacité** (`PeutDiagnostiquer`, `PeutExecuter`…), jamais un rôle directement. Ceci permet de faire évoluer la matrice des habilitations sans modifier le code applicatif.

---

## 3. Matrice d'accès aux pages & menus

Légende : **✅ Accès direct** · ⚠️ Accès via sous-menu ou lien conditionnel · ❌ Pas accès

### 3.1. Groupe 1 — Supervision (capacité `PeutConsulter`)

Ce groupe est **visible par TOUS les profils** (8 rôles). La navigation directe par URL est également bloquée sur la même politique.

| Page | Route | 1-SupportN1 | 2-Opérateur | 3-AdminN4 | 4-AdminInfra | 5-Validateur | 6-AdminSol | 7-Auditeur | 8-TOS-Mgr |
|---|---|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| Tableau de bord | `/` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Supervision Temps Réel | `/supervision` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Vitalité des nœuds | `/vitalite` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Centre d'Alertes | `/alertes` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| AIOps — Anomalies | `/aiops` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Jumeau Numérique | `/digital-twin` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Replay d'incident | `/incident-replay` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Rapports & SLA | `/admin/rapports` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Détail composant | `/supervision/composants/{Id}` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

### 3.2. Groupe 2 — Intelligence Suite V2.5

| Page | Route | Capacité | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 |
|---|---|---|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| Flight Simulator N4 | `/flight-simulator` | `PeutConsulter` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

### 3.3. Groupe 3 — Exploitation

La section s'ouvre pour les 8 profils (`PeutConsulter`). Les sous-menus administratifs sont ensuite filtrés.

| Page | Route | Capacité requise | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 |
|---|---|---|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| Environnements (liste) | `/admin/environnements` | `PeutConsulter` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Environnement (détail) | `/admin/environnements/{Id}` | `PeutConsulter` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Mise en service | `/admin/environnements/{Id}/mise-en-service` | `PeutConsulter` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Opérations & Pilotage | `/operations` | `PeutConsulter` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Détail Opération | `/operations/{Id}` | `PeutConsulter` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| **Action Unitaire** | `/operations/ponctuelle` | `PeutExecuterActionUnitaire` | ❌ | ✅ | ✅ | ❌ | ❌ | ✅ | ❌ | ❌ |
| SOP (liste & exécutions) | `/sop` | `PeutConsulter` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Détail Exécution SOP | `/sop/{Id}` | `PeutConsulter` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| EDI & dossiers partagés | `/edi` | `PeutConsulter` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Assistant N4 / Documentation | `/documentation` | `PeutConsulter` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Détail Document | `/documentation/{Id}` | `PeutConsulter` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| **Diagnostics** | `/diagnostics` | `PeutDiagnostiquer` | ❌ | ✅ | ✅ | ✅ | ❌ | ✅ | ❌ | ❌ |
| Détail Diagnostic | `/diagnostics/{Id}` | `PeutDiagnostiquer` | ❌ | ✅ | ✅ | ✅ | ❌ | ✅ | ❌ | ❌ |
| **Planifications** | `/admin/planifications` | `PeutAdministrerReferentiel` | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ |
| Détail Planification | `/admin/planifications/{Id}` | `PeutAdministrerReferentiel` | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ |
| **Calendrier maintenance** | `/admin/maintenance-calendrier` | `PeutAdministrerReferentiel` | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ |
| **Workflows** | `/admin/workflows` | `PeutAdministrerReferentiel` | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ |
| Détail Workflow | `/admin/workflows/{Id}` | `PeutAdministrerReferentiel` | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ |
| **Signatures d'anomalie** | `/admin/signatures` | `PeutAdministrerReferentiel` | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ |
| **Matrice de criticité** | `/admin/matrice-approbation` | `PeutAdministrerReferentiel` | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ |
| **Corbeille** | `/admin/corbeille` | `PeutAdministrerReferentiel` | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ |
| **SOP (administration)** | `/admin/sop` | `PeutAdministrerReferentiel` | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ |
| Détail SOP (admin) | `/admin/sop/{Id}` | `PeutAdministrerReferentiel` | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ |
| **Versement Document** | `/admin/documentation` | `PeutAdministrerReferentiel` | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ |
| **Composant (édition)** | `/admin/composants/{Id}` | `PeutAdministrerReferentiel` | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ |
| **Marqueurs composant** | `/admin/composants/{Id}/marqueurs` | `PeutAdministrerReferentiel` | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ |
| **Serveur (édition)** | `/admin/serveurs/{Id}` | `PeutAdministrerReferentiel` | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ |
| **Comptes techniques** | `/admin/environnements/{Id}/comptes` | `PeutAdministrerReferentiel` | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ |

### 3.4. Groupe 4 — Audit & Sécurité

Ce groupe s'ouvre pour les profils **Auditeur** et **Administrateur Solution** (`PeutAuditer`).
Les sous-menus affinent encore les droits.

| Page | Route | Capacité requise | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 |
|---|---|---|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| Historique & escalade | `/historique` | `PeutConsulter` (dans section) | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ✅ | ❌ |
| Journal d'audit | `/admin/audit` | `PeutAuditer` | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ✅ | ❌ |
| Comptes & droits | `/admin/utilisateurs` | `PeutAuditer` | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ✅ | ❌ |
| **Azure AD / SSO** | `/admin/azure-ad` | `PeutAdministrerReferentiel` | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ |
| **Tickets incidents** | `/admin/tickets` | `PeutExecuterActionsSensibles` | ❌ | ❌ | ✅ | ❌ | ❌ | ✅ | ❌ | ❌ |
| **Connecteurs JMX** | `/admin/jmx` | `PeutAdministrerConnecteurs` | ❌ | ❌ | ❌ | ✅ | ❌ | ✅ | ❌ | ❌ |
| **Sauvegarde** | `/admin/sauvegarde` | `PeutAdministrerConnecteurs` | ❌ | ❌ | ❌ | ✅ | ❌ | ✅ | ❌ | ❌ |

### 3.5. Profil utilisateur (menu du bas)

| Fonction | Capacité | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 |
|---|---|---|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| Mon Profil | Authentifié | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Mon compte d'exploitation | `PeutExecuter` | ❌ | ✅ | ✅ | ❌ | ❌ | ✅ | ❌ | ❌ |
| Se déconnecter | Authentifié | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

---

## 4. Cycles de vie & contrôles à 4 yeux

### 4.1. Contrôle à 4 yeux en Production (RG-0304)

Quand une opération critique est lancée en Production :

1. **Auteur de l'action** : Opérateur N4 ou Admin N4 (détient `PeutExecuter`) → l'opération est créée en statut `En attente d'approbation`.
2. **Approbateur** : Validateur OU Admin Solution (détient `PeutApprouver`) → valide ou refuse.
3. **Règle infranchissable** : le Validateur **ne peut pas être la même personne** que le demandeur (le code vérifie l'identité nominative, pas seulement le rôle).

### 4.2. Palier 2 — Approbation des connecteurs destructifs (RG-039)

- Toute étape `destructive` (arrêt forcé, suppression, purge) qui utilise un connecteur technique en environnement Production impose **deux validations supplémentaires** :
  - ✅ Approbation humaine (Validateur)
  - ✅ Connecteur explicitement autorisé sur cet environnement par l'Admin Solution
- Si l'une des deux est absente → l'étape est **refusée** et une entrée audit `Palier2ConnecteurInterdit` est tracée.

### 4.3. Cycle de vie d'une SOP (FR-088 → FR-090)

`Brouillon → A Valider → Validé → Actif` (révocable à tout moment).
Une SOP `Actif` seule peut être exécutée en Production. Toute exécution SOP déclenche la collecte de feedback qualité & taux de succès.

---

## 5. Premiers pas par rôle

### 5.1. Je suis `Lecteur-SupportN1`

1. Me connecter → arriver sur le Tableau de bord.
2. Consulter les alertes (`/alertes`) et vérifier la vitalité des nœuds (`/vitalite`).
3. En cas d'anomalie → contacter un Opérateur N4 ou faire remonter via la chaîne Support.

### 5.2. Je suis `Operateur-N4`

1. Choisir l'environnement cible (UAT avant PROD systématiquement).
2. Préparer une opération depuis `/operations` ou lancer un diagnostic `/diagnostics`.
3. **Avant toute action en PROD** : demander l'approbation d'un Validateur (colonne « à approuver »).
4. Confirmer chaque étape demandée par le workflow.

### 5.3. Je suis `Validateur`

1. Ouvrir `/operations` → onglet « En attente d'approbation ».
2. Vérifier l'environnement, le motif, et l'identité du demandeur (≠ moi).
3. **Approuver** ou **Refuser** avec motif obligatoire. Le refus est audité.

### 5.4. Je suis `Administrateur-Solution`

1. **Premier démarrage** : `/premier-demarrage` (amorçage des environnements).
2. Créer les environnements et composants dans `/admin/environnements`.
3. Publier les workflows `/admin/workflows` et SOP `/admin/sop`.
4. Gérer les comptes & rôles `/admin/utilisateurs`.
5. Configurer SSO `/admin/azure-ad`, JMX `/admin/jmx`, Sauvegarde `/admin/sauvegarde`.

### 5.5. Je suis `Auditeur`

1. Journal d'audit `/admin/audit` → filtrer par acteur, période, type d'action.
2. Vérifier les habilitations `/admin/utilisateurs`.
3. Revoir l'historique complet `/historique` avec preuves d'exécution.

---

## 6. Sécurité applicative — Rappel aux utilisateurs

| Mesure | Règle |
|---|---|
| **Déconnexion inactivité** | 30 minutes sans interaction → déconnexion forcée |
| **Second facteur** | Exigible sur les capacités d'action (désactivé par défaut, activer via config) |
| **Interdiction d'auto-approbation** | Vérifiée côté code même si l'utilisateur a TOUS les rôles |
| **Production = bannière ROUGE** | Tout changement en PROD est irréversible sans workflow restauratif |
| **Traçabilité totale** | Toute action (y compris les tentatives refusées) est écrite dans le journal d'audit |

---

## 7. Support & contact en cas de blocage

- **Application indisponible** : contacter Administrateur Infrastructure (JMX/connecteurs).
- **Problème de droit / compte verrouillé** : contacter Administrateur Solution.
- **Opération bloquée en attente d'approbation** : contacter Validateur de la semaine (planning astreinte).
- **Incident P1** : utiliser la page `/diagnostics` pour lancer une session diagnostic ; un ticket est automatiquement ouvert dans le système DSI.

---

*Fin du Guide Utilisateur. Pour l'installation et la configuration, voir Guide-Deploiement-N4Sentinel.md.*
