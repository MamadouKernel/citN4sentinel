# Playbook Support & Intégration Navis N4
*Guide de référence — à consulter à chaque incident ou projet d'intégration*

---

## PARTIE 1 — Diagnostiquer n'importe quel incident Navis

### Étape 0 — Réflexe immédiat (à faire AVANT toute investigation ciblée)

Avant de chercher la cause spécifique, élimine les causes transverses qui expliquent 70% des incidents "bizarres" :

| Vérifier en premier | Pourquoi |
|---|---|
| Une sauvegarde de base est-elle en cours ? | Explique une lenteur généralisée ponctuelle |
| Une mise à jour manuelle en masse (bulk UI) ou une recherche trop large est-elle en cours ? | Cause n°1 de lenteur signalée |
| Les horloges de tous les serveurs sont-elles synchronisées (< 1 sec d'écart) ? | Cause de statuts DISCONNECTED/INACTIVE trompeurs, et top cause de P1 |
| Un antivirus scanne-t-il les dossiers Navis (amq, conf, Program Data\Navis) ? | Cause n°1 de corruption amq et de double Center/Standby actifs |
| Y a-t-il eu un snapshot VMware récent sur un hébergement NFS ? | Peut geler le système >30s et provoquer une panne |
| Les index sont-ils à jour / la purge est-elle à jour ? | Dégrade toutes les performances progressivement |

**Si aucune de ces causes ne colle → passe à l'étape 1.**

---

### Étape 1 — Classifier le symptôme (arbre de décision)

```
Quel est le symptôme principal ?
│
├─ Un SERVICE ne démarre pas / plante au démarrage
│   → Va à [A] Services & Cluster
│
├─ Tout est LENT (généralisé, à une heure précise, un seul user, ou une action précise)
│   → Va à [B] Performance
│
├─ N4 et XPS affichent des données DIFFÉRENTES (désynchronisation)
│   → Va à [C] Synchronisation N4/XPS/Bridge
│
├─ Un message EDI échoue / se bloque / erreur de mapping
│   → Va à [D] EDI
│
├─ Erreur de CONNEXION BASE DE DONNÉES
│   → Va à [E] Base de données
│
├─ Un AGV/ASC/Grue automatisé se comporte mal (décision, timing, blocage)
│   → Va à [F] Automatisation (ECI)
│
├─ ECN4 / ECN4Web / poste de dispatch a un problème
│   → Va à [G] ECN4
│
└─ Facturation (N4 Billing) : montant à 0, export échoué
    → Va à [H] Billing
```

---

### Étape 2 — Où regarder selon la catégorie

| Catégorie | Premiers réflexes | Logs / vues à consulter | Réf. manuel |
|---|---|---|---|
| **[A] Services & Cluster** | Espace disque, dossier `amq` corrompu ?, antivirus, fuseau horaire du nœud | Cluster Services (statut), Node Info Desk, `navis-apex.log` | §4.4, 4.5, 5.5-5.10 |
| **[B] Performance** | Quel profil : général / horaire / 1 user / 1 action ? | Top SQL, vue *Users* (Horizon, Fetch Row Limit), auto-diagnostic N4 | §4.1, 4.10, 4.11 |
| **[C] Sync N4/XPS/Bridge** | JMX sur nœud Center : QueueSize, DequeueCount, InFlightCount, ConsumerCount (voir tableau ci-dessous) | Files `bridge.*`, `n4-to-bridge-service` | §4.2, 5.1-5.9, 9.1-9.10 |
| **[D] EDI** | Filtre From/To incomplet ? Planification ? Taille fichier < 1 Mo ? | Table des erreurs courantes §3 | §3 (tableau complet des erreurs) |
| **[E] Base de données** | `apex.xml` (identifiants, host, port), pool de connexions saturé ? | Database Sessions/Processes, UCP/Tomcat Data Sources | §4.12, §6 |
| **[F] Automatisation (ECI)** | Connexion base ECI, deadlock, cache drive-time | `navis-apex.log` (chercher "DEADLOCK") | §7 |
| **[G] ECN4** | Threads de tâches en parallèle, "Purple System Bypass" | Logs ECN4/ECN4Web, délais de messages | §10 |
| **[H] Billing** | Mot de passe `n4api` expiré (cause n°1 quasi systématique) | `ARGOBILLING003`, `CGOWS010`, `tomcat-users.xml` | §4.9, §11 |

**Indicateurs universels à surveiller (JMX, nœud Center) — valables pour presque toutes les catégories :**

| Indicateur | Normal | À risque |
|---|---|---|
| QueueSize | stable, proche de 0 | augmente dans le temps |
| DequeueCount | progresse en continu | reste plat |
| InFlightCount | fluctue autour de 0 | reste > 0 |
| ConsumerCount | > 0 | égal à 0 |

---

### Étape 3 — Avant d'escalader à Navis (checklist ticket)

- [ ] Description précise : étapes de reproduction, heure, fréquence, utilisateurs touchés
- [ ] Résultat de l'auto-diagnostic N4 (*Administration → Debug → Node Info Desk → Self Diagnosis Auto-Check*)
- [ ] Vue *Users* (si perf) ou vue pertinente ci-dessus
- [ ] Logs concernés (apex, bridged, xps, ecn4 selon la catégorie)
- [ ] Notes de version vérifiées (bug déjà connu ?)
- [ ] AWR/ASH/Statspack (Oracle) ou Activity Monitor/Profiler (SQL Server) si pertinent

---

## PARTIE 2 — Choisir la bonne intégration, peu importe la solution

### Étape 1 — Identifier le besoin (arbre de décision)

```
Qu'est-ce que le système externe doit faire avec N4 ?
│
├─ LIRE des données N4 (interroger, extraire, dashboard)
│   → Universal Query API (REST/HTTP, requêtes configurables)
│
├─ ÉCRIRE en masse / mettre à jour des lots de conteneurs
│   ├─ Mise à jour incrémentale conteneurs → ICU API
│   └─ Mise à jour des permissions de blocage (holds) → HPU API
│
├─ Échanger en TEMPS RÉEL avec une logique métier custom
│   ├─ Besoin simple, tu contrôles le endpoint → REST API (via Groovy)
│   └─ Besoin SOAP / legacy / système tiers déjà en SOAP → N4 Web Services (Argo Generic/Basic)
│
├─ SYNCHRONISER un système externe en continu (ex : WMS, YMS tiers)
│   → SNX (Sparcs N4 eXtension) — attention overhead important, bien lire §7 Special Considerations
│
├─ Intégrer un ÉQUIPEMENT automatisé (grue, AGV, portique)
│   ├─ Grues automatisées → Crane Automation API / ASC Automated Equipment DB Interface
│   ├─ AGV / véhicules → Automation Database Interface
│   └─ Yard Crane → Automated Yard Crane Database Interface
│
├─ Intégrer un portail / workflow de PORTAIL CAMION (gate)
│   → Gate API Specification + Gate Workflow Customization
│
├─ Monitoring REEFER (conteneurs frigorifiques)
│   → Reefer Monitoring XML API
│
├─ Intégrer la FACTURATION
│   → Billing REST Endpoints
│
└─ Échange par FICHIERS (batch, ancien système, EDIFACT)
    → EDI (hors périmètre "API", cf. Partie 1 §D pour le dépannage)
```

### Étape 2 — Table de référence rapide (avec pages dans le guide 4.0.24)

| Besoin | API | Section (Public APIs 4.0.24) |
|---|---|---|
| Interroger des données | Universal Query API | §1 |
| Mise à jour holds en masse | HPU API | §2 |
| Mise à jour incrémentale conteneurs | ICU API | §3 |
| Reefer monitoring | Reefer Monitoring XML API | §4 |
| Web services SOAP legacy | N4 Web Services | §5 |
| REST custom (Groovy) | N4 REST API | §6 |
| Position CHE en temps réel | CHE Current Position API | §6.5 |
| Ordres de service (nouveau) | Service Order API | §6.9 |
| Synchronisation continue | SNX | §7 |
| Portail camion | Gate API / Gate Workflow | §8, §9 |
| Import/export plan navire | Ship Bin Model | §10 |
| Grue automatisée | Crane Automation API / ASC DB Interface | §11, §14 |
| Yard crane automatisé | Automated Yard Crane DB Interface | §12 |
| AGV / automatisation générale | Automation Database Interface | §13 |
| Facturation | Billing REST Endpoints | §6.8 |

### Étape 3 — Checklist technique générique (quelle que soit l'API choisie)

- [ ] **Authentification** : Basic Auth + HTTPS obligatoire (jamais en clair)
- [ ] **Version N4 côté client confirmée** — les APIs évoluent entre versions (voir Étape 4)
- [ ] **Environnement de test dédié** avant prod (jamais tester contre la prod Navis)
- [ ] **Gestion d'erreurs** : prévoir les codes d'erreur documentés dans "Error Handling" (§5.4 du guide)
- [ ] **Limiter l'accès à l'URL de requête** si Universal Query (§1.4) — sécurité
- [ ] **Volume de données** : une requête Universal Query trop large peut ralentir tout N4 (cf. Partie 1 §B) — toujours paginer/filtrer
- [ ] **Idempotence** côté ICU/HPU si tu rejoues un batch après échec partiel

### Étape 4 — Attention aux versions (3.6 → 4.0.8 → 4.0.24)

D'après les 3 guides que tu as :
- **CHE Current Position API** et **Service Order API** sont apparus **après la 4.0.8** (présents en 4.0.24, absents en 4.0.8).
- Le SDK 3.6 regroupe tout dans un seul document (structure plus monolithique) ; à partir de 4.0.8 la doc est scindée en guides séparés (Public APIs vs SDK vs Setup/Diagnostics).
- **Avant de coder une intégration** : vérifie la version N4 réellement installée chez le client (CIT ou Sigasécurité) et consulte le guide correspondant — un endpoint documenté en 4.0.24 peut ne pas exister en 4.0.8.

---

## Comment utiliser ce document avec moi

- **Incident réel** → donne-moi le symptôme brut, je te dis directement dans quelle case de l'arbre on est et je vais chercher le détail exact dans le manuel/guide.
- **Nouvelle intégration** → dis-moi ce que le système externe doit faire, je descends l'arbre avec toi et je sors la section précise de l'API à utiliser (avec exemples si besoin).
- **Question de version** → dis-moi quelle version tourne chez le client, je vérifie les écarts avant que tu codes.
