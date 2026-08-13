# Note d'arbitrage — périmètre de la V1 de N4 Sentinel

**Référence** CIT-CIV-DSI-RFP-0010
**Objet** Décision sur le périmètre livré au 1<sup>er</sup> octobre 2026
**Rédigée le** 13/08/2026
**À valider par** Direction des Systèmes d'Information

---

## 1. Pourquoi cette note

Le cahier des charges décrit quatre lots et environ cent exigences. La
réalisation dispose de **35 jours ouvrés et d'un développeur** jusqu'au
1<sup>er</sup> octobre 2026.

Ce volume ne rentre pas. La question n'est donc pas *si* le périmètre sera
réduit, mais *qui* décide de la réduction et *quand*.

Sans décision prise maintenant, l'arbitrage se fera de fait en fin de parcours,
sous contrainte de calendrier, par le développeur. Cette note existe pour que
ce choix revienne à la DSI, et qu'il soit fait à froid.

Une seconde raison, plus concrète : les scénarios de recette **AC-18 à AC-23**
du cahier des charges portent sur des fonctions qu'il est proposé de reporter.
Si leur report n'est pas acté, la recette finale échouera mécaniquement sur des
fonctions dont l'absence aura pourtant été décidée.

---

## 2. Ce qui est proposé au 1<sup>er</sup> octobre

### 2.1 Livré et recetté

| Domaine | Contenu |
|---|---|
| Référentiel | Environnements, serveurs, composants, dépendances, cycle de validation |
| Mise en service | Parcours guidé, import d'inventaire, comptes techniques chiffrés, test de configuration |
| Supervision | État consolidé à huit valeurs, tableau de bord, cartographie, alertes |
| Orchestration | Workflows versionnés, moteur persistant, vue pas-à-pas, reprise, verrouillage |
| Scénarios N4 | Démarrage et arrêt complets, unitaires, groupes, rolling restart, simulation, pré-check |
| Journaux | Import et collecte ciblée, analyse, signatures, verdict nuancé, masquage des secrets |
| Sécurité | Authentification, huit rôles, second facteur TOTP, journal d'audit inaltérable |

### 2.2 Livré en version réduite

| Fonction | Ce qui est livré | Ce qui ne l'est pas |
|---|---|---|
| Moteur de diagnostic | Hypothèses classées par domaine, preuves, niveau de confiance, vérifications recommandées | Corrélation multi-serveurs, comparaison à une période saine de référence |
| Assistant documentaire | Recherche sur le corpus indexé, réponse citant document, section et page | Réponse générative en langage naturel |
| Approbations | Validation simple | Double validation par deux personnes distinctes |

### 2.3 Reporté après le 1<sup>er</sup> octobre

- Création, exécution guidée, versionnement et réutilisation des SOP
- Supervision avancée des dossiers partagés et reconstitution ActiveMQ / KahaDB
- Suivi des intégrations EDI
- Indicateurs, notifications et rapports d'incident automatiques
- Automatisation de bout en bout sans confirmation humaine (palier 2)
- Application mobile, intégration Azure AD / SSO, supervision JMX temps réel

---

## 3. Conséquences à accepter explicitement

**Sur la recette.** Les scénarios AC-18 à AC-23 sortent du périmètre de recette
du 1<sup>er</sup> octobre. Ils seront rejoués lorsque les fonctions
correspondantes seront livrées.

**Sur la mise en production.** Le cahier des charges impose la réussite des
scénarios de recette en UAT avant tout passage en Production. Le
1<sup>er</sup> octobre livre au mieux un **procès-verbal de recette et un
paquet installable** ; la bascule en Production reste soumise à une recette
sur l'environnement réel du site, conduite après cette date.

**Sur la double validation.** Tant qu'elle n'est pas déployée, aucune opération
ne peut exiger deux approbateurs distincts. Les opérations critiques en
Production restent soumises à confirmation explicite et à traçabilité, mais
sous la responsabilité d'un seul valideur.

**Sur les marqueurs de démarrage.** Les valeurs livrées pour le Bridge, XPS,
ECN4 et ECN4Web sont des candidats issus de la documentation éditeur, non
relevés sur un environnement réel. Ils devront être confirmés site par site,
au moment de la mise en service, avec l'assistant prévu à cet effet. Tant
qu'ils ne le sont pas, l'application déclarera l'état de ces composants
« à confirmer » — comportement voulu, mais qui limite l'automatisation.

---

## 4. Le risque si aucune décision n'est prise

Arriver le 1<sup>er</sup> octobre avec environ 80 % de chaque fonction, donc
aucune fonction recettable, et rien de déployable. À l'inverse, 100 % d'un
périmètre réduit se met en service.

---

## 5. Décision

- [ ] Le périmètre décrit en section 2 est **validé en l'état**
- [ ] Le périmètre est validé **avec les modifications suivantes** :

  _____________________________________________________________________

  _____________________________________________________________________

  À volume constant, toute fonction réintégrée implique le report d'une
  fonction de charge équivalente.

- [ ] La date du 1<sup>er</sup> octobre est **une livraison recettée**, la mise
      en Production intervenant après recette sur site
- [ ] La date du 1<sup>er</sup> octobre est **une mise en Production** —
      auquel cas le périmètre doit être réduit davantage

**Renfort éventuel.** Une seconde personne, même à mi-temps sur les sprints S6
et S7, permettrait de récupérer le diagnostic complet et l'assistant génératif.

- [ ] Aucun renfort — le périmètre réduit s'applique
- [ ] Renfort envisagé : _______________________________

---

| | Nom | Date | Signature |
|---|---|---|---|
| Rédigé par | | | |
| Validé par | | | |
| Approuvé par (DSI) | | | |
