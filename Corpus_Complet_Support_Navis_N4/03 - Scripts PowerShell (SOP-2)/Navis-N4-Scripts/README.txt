SCRIPTS POWERSHELL - EXPLOITATION NAVIS N4 (SOP-2, Niveau 2/3)
(c) KMKernel

===========================================
NOUVEAUTE MAJEURE DE CETTE VERSION
===========================================
LES SCRIPTS N'ATTENDENT PLUS UN STATUT WINDOWS : ILS ATTENDENT UNE PREUVE
DANS LE LOG APPLICATIF.

Le probleme des versions precedentes
------------------------------------
Chaque etape de demarrage attendait au maximum 180 secondes que le service
Windows passe "Running", puis enchainait sur l'etape suivante.
C'etait faux sur le fond ET trop court dans les faits :

  - "Running" veut dire que Windows a lance le processus. Cela ne dit RIEN
    de la JVM N4, qui doit encore charger sa configuration, ouvrir la base,
    rejoindre le cluster Hazelcast et initialiser son tier web. Ces etapes
    prennent couramment plusieurs minutes, parfois plus de 15 sur un
    demarrage a froid.
  - Consequence directe : le script demarrait le noeud Cluster suivant, ou
    lancait XPS, alors que le composant precedent chargeait encore. C'est
    exactement le scenario qui produit des incoherences de cache, une
    desynchronisation N4/XPS, ou un "faux echec" sur un noeud parfaitement
    sain.

Ce qui a change
---------------
Toute attente de demarrage se fait desormais en DEUX temps :

  Phase 1  le service Windows atteint Running          (timeout 5 min)
  Phase 2  le MARQUEUR de fin d'initialisation apparait dans le log
           applicatif du composant                     (timeout 30 min par defaut)

Et le resultat n'est plus un simple vrai/faux, mais un etat a trois valeurs :

  OPERATIONNEL  service Running ET marqueur trouve dans le log
                -> la sequence continue
  ECHEC         service absent / non demarre, ou signature d'echec reperee
                dans le log
                -> la sequence s'arrete, la dependance n'est pas satisfaite
  A CONFIRMER   service Running mais aucune preuve obtenue (marqueur non
                configure, log illisible, ou timeout atteint)
                -> l'etat n'est PAS etabli. L'operateur tranche, ou la
                   sequence s'arrete en mode -Unattended.

Ce troisieme etat est volontaire : les scripts n'affirment pas ce qu'ils
n'ont pas prouve.

Tous les delais et marqueurs se reglent dans Navis-Config.json, section
Readiness. Aucune edition de code n'est necessaire.

===========================================
CONTENU
===========================================
Navis-Config.json              Configuration a editer : serveurs, services, base de
                                 donnees, ET marqueurs/timeouts de demarrage
                                 (section Readiness). AUCUNE edition de code necessaire.
Navis-Config.example.json      Modele de reference (ne pas modifier)

Navis-Integrity.json           Empreintes SHA-256 de reference de chaque script.
                                 NE PAS EDITER MANUELLEMENT (voir protection PIN plus bas).
Navis-Protection.json          Contient le hash du code PIN (jamais le PIN en clair).
Set-N4CopyrightPin.ps1         Change le code PIN (demande l'ancien, puis le nouveau).
New-N4IntegrityManifest.ps1    Regenere Navis-Integrity.json apres une modification
                                 LEGITIME d'un script. Protege par le code PIN.

Navis-Common.psm1              Module partage : configuration, journalisation,
                                 verifications reseau/services/DB, ATTENTE PAR PREUVE
                                 DE LOG, verification d'integrite.

Find-N4ReadinessPattern.ps1    NOUVEAU - Lecture seule. Aide a identifier, dans un log
                                 REEL, le marqueur de fin d'initialisation a declarer
                                 dans Navis-Config.json. A lancer AVANT la premiere
                                 utilisation des sequences (voir plus bas).

Start-N4Sequence.ps1           Demarrage complet dans l'ordre :
                                 Cluster (un par un) -> Center -> [Standby en option]
                                 -> Bridge -> XPS -> ECN4 -> ECN4Web
                                 Chaque etape attend la preuve reelle avant la suivante.
                                 Options : -Unattended, -StartStandby

Stop-N4Sequence.ps1            Arret complet dans l'ordre inverse :
                                 ECN4Web -> ECN4 -> XPS -> Bridge -> Standby ->
                                 Cluster (un par un) -> Center
                                 Delai d'arret configurable par composant, detection
                                 des services bloques en "Stopping", jamais d'arret
                                 force automatique.
                                 Option -InstallUpdates : patching Windows encadre
                                 (DESACTIVE PAR DEFAUT - fenetre de maintenance uniquement).

Fix-N4-AMQCorruption.ps1       Fiche A : corruption dossier amq.
                                 Trois portillons avant toute suppression : services
                                 tous confirmes arretes, fichier non verrouille,
                                 sauvegarde verifiee (taille identique) avant suppression.

Restart-N4-BridgeChain.ps1     Fiche C : desync N4/XPS persistante.
                                 XPS n'est relance qu'apres CONFIRMATION que le Bridge
                                 est connecte (plus de "Start-Sleep 30" forfaitaire).

Restart-N4-CenterOnly.ps1      5.6 : Center Node inactif. Arret confirme puis
                                 redemarrage avec preuve par le log.

Restart-N4-StandbyOnly.ps1     5.7 : Center et Standby actifs simultanement.

RollingRestart-N4-Cluster.ps1  Fiche F / 5.1, 7.8 : noeud lent, incident ILOG.
                                 Un noeud a la fois, chacun confirme operationnel
                                 avant de toucher au suivant. Si un noeud echoue,
                                 la sequence s'arrete au lieu de degrader le cluster.

Test-N4IncidentPreCheck.ps1    A LANCER EN PREMIER des qu'un incident est signale,
                                 AVANT la lecture des logs. Reseau, horloges, disque,
                                 connectivite base. Automatise l'Etape 0 de SOP-0.

Enable-N4FileAuditing.ps1      Active l'audit de securite Windows sur le dossier des
                                 scripts (qui modifie quoi, quand, depuis quelle IP).
                                 A executer UNE FOIS, en Administrateur.

Get-N4FileModificationEvents.ps1  Consulte les evenements de modification enregistres.

===========================================
CONFIGURATION OBLIGATOIRE AVANT PREMIERE UTILISATION
===========================================
Toute la configuration se trouve dans Navis-Config.json, SEPARE du code
PowerShell. Aucune edition de script n'est necessaire, quel que soit le
nombre de serveurs (2, 5, 20...).

ETAPE 1 - Serveurs et services
------------------------------
1. Ouvrir Navis-Config.json
2. Remplacer les valeurs d'exemple (N4CENTER01, N4CLUSTER01...) par les
   VRAIS noms d'hotes de votre installation N4
3. Pour ajouter ou retirer un noeud Cluster : modifier simplement la
   liste "ClusterNodes" (autant d'entrees que necessaire)
4. Verifier les noms EXACTS des services Windows. Sur un serveur N4 :
       Get-Service | Where-Object { $_.DisplayName -like "*Navis*" }
5. Verifier le chemin SharedFolder (dossier UNC contenant amq/conf)
6. Renseigner DatabaseHost / DatabasePort (test de connectivite reseau
   uniquement, aucun identifiant stocke)
7. Adapter LocalLogFolder si besoin (par defaut C:\NavisScripts\Logs)

ETAPE 2 - Marqueurs de demarrage (section Readiness) - INDISPENSABLE
--------------------------------------------------------------------
Sans cette etape, les scripts fonctionnent mais ne peuvent RIEN prouver :
ils retourneront "A CONFIRMER" a chaque etape et vous demanderont de
trancher a chaque fois.

Les valeurs livrees dans la section Readiness sont des CANDIDATS ecrits a
partir de la documentation, PAS des valeurs relevees sur vos serveurs.
Elles varient selon la version de N4, le composant et la configuration de
journalisation. Il faut donc les valider chez vous :

  1. Sur un environnement hors production, redemarrer un composant - ou
     recuperer un log dont vous savez que le demarrage a REUSSI.
  2. Lancer :
         .\Find-N4ReadinessPattern.ps1 -ComponentKey Cluster -ComputerName N4CLUSTER01
     Le script affiche : les motifs configures et s'ils apparaissent, les
     lignes ressemblant a une fin d'initialisation, et les lignes d'erreur.
  3. Choisir le marqueur et le reporter dans Navis-Config.json :
         Readiness > Components > Cluster > ReadyPatterns
  4. Relancer Find-N4ReadinessPattern.ps1 pour confirmer qu'il est reconnu.
  5. Repeter pour chaque composant : Cluster, Center, Standby, Bridge, XPS,
     ECN4, ECN4Web.

CE QUI FAIT UN BON MARQUEUR
  - Stable d'une version a l'autre et d'un demarrage a l'autre.
  - Ecrit UNE SEULE FOIS, a la fin de l'initialisation. Une ligne periodique
    (heartbeat, poll) ne prouve rien : elle apparait aussi sur un composant
    a moitie demarre.
  - Sans element variable. Neutraliser les chiffres avec \d+ :
    "Server startup in \d+ ms" et non "Server startup in 251090 ms".
  - Pour le BRIDGE : preferer la ligne de CONNEXION AU CENTER a la ligne de
    demarrage du daemon. C'est la connexion qui conditionne XPS, pas le
    demarrage du processus.
  - Pour le STANDBY : choisir un marqueur de MODE VEILLE. Un Standby sain
    n'ecrit PAS "Web tier servlet initialized" - c'est normal. S'il l'ecrit
    pendant que le primaire est actif, ce n'est pas un demarrage reussi,
    c'est un conflit de role (incident 5.7).

Chemin du log : LogPath doit etre le chemin LOCAL tel que le SERVEUR le
voit, jamais un chemin UNC vu depuis votre poste. Il peut contenir un
caractere generique (*) - c'est souvent indispensable, voir ci-dessous.

EMPLACEMENTS DES LOGS (documentation editeur, installation standard)
  N4 nodes    C:\ProgramData\Navis\[nom du noeud]\logs\navis-apex.log
  XPS         C:\ProgramData\Navis\xps\log\xps_AAAAMMJJHHMMSS
  Bridge      C:\ProgramData\Navis\bridge\logs\navis-bridged...
  ECN4        C:\ProgramData\Navis\ecn4\logs\navis-ecn4...
  ECN4Web     C:\ProgramData\Navis\ecn4web\logs\navis-ecn4web...
Verifier le nom exact du sous-dossier de chaque noeud sur vos serveurs.

NOMS DE FICHIERS VARIABLES - POURQUOI LE GENERIQUE EST NECESSAIRE
Plusieurs composants n'ecrivent pas dans un fichier au nom fixe :
  - le log XPS est horodate dans son nom ET REPART A ZERO A CHAQUE
    DEMARRAGE : un nouveau fichier est cree a chaque lancement ;
  - les logs Bridge, ECN4 et ECN4Web portent egalement un suffixe de date ;
  - le log apex repart a zero au-dela de 10 Mo (rotation par taille).
Un LogPath contenant * est donc resolu, a chaque interrogation, vers le
fichier le PLUS RECENT qui correspond. Si un nouveau fichier apparait
apres le lancement du service, le script le detecte, le signale, et
l'analyse depuis son debut. La rotation en cours d'attente est geree de
la meme facon.

MARQUEUR CONFIRME PAR LA DOCUMENTATION EDITEUR
Pour les noeuds Cluster et le Center, la documentation Kaleris (N4 IT
Admin 4.x, Day 1: Installation & Startup) demande explicitement de tailler
navis-apex.log et d'attendre la ligne :
    Web tier servlet 'action' initialized...
avant de demarrer le composant suivant. C'est precisement ce que ces
scripts automatisent. Le motif livre est "Web tier servlet .*initialized"
(la partie 'action' est neutralisee car elle peut varier).

Les marqueurs de Bridge, XPS, ECN4 et ECN4Web restent des CANDIDATS : pour
eux, la documentation renvoie au statut ACTIVE dans la vue Cluster
Services de N4, pas a une ligne de log precise. A relever sur vos serveurs.

STATUTS N4 (vue Cluster Services) - CE QUE LES SCRIPTS NE VOIENT PAS
  LOADING       phase de demarrage normale
  INITIALIZING  phase de demarrage normale
  WAITING       attend que le premier serveur N4 charge le cache
  ACTIVE        fonctionnement normal
  RECOVERING    reprise apres une erreur (ex. crash de service)
  SHUTDOWN      service arrete proprement
  INACTIVE      aucun heartbeat depuis 2 minutes
  DISCONNECTED  heartbeat present mais n'atteignant pas le Center
Ces statuts sont applicatifs : ils se lisent dans N4, pas depuis Windows.
Les scripts observent le service Windows et le log ; la confirmation
ACTIVE reste une verification manuelle. Cas normal a connaitre : un
ClusterNode reste DISCONNECTED tant que le Center Node n'est pas demarre.

===========================================
REGLAGE DES DELAIS (SECTION READINESS)
===========================================
Chaque parametre peut etre defini globalement (Readiness > Defaults) puis
surcharge composant par composant (Readiness > Components > <Cle>).

ServiceRunningTimeoutSeconds  Temps laisse au service Windows pour passer
                              Running. Defaut 300 s. Si ce delai est depasse,
                              le probleme est au niveau Windows (compte de
                              service, dependance, executable), pas N4.

LogReadyTimeoutSeconds        Temps laisse a la JVM pour finir son
                              initialisation. Defaut 1800 s (30 min).
                              2400 s pour XPS, dont le chargement initial
                              (plan de parc, equipements) est long.
                              A AUGMENTER si vos serveurs sont plus lents :
                              mieux vaut attendre 40 minutes et savoir, que
                              declarer un echec au bout de 3 minutes et deviner.

StopTimeoutSeconds            Temps laisse a un composant pour s'arreter
                              proprement. Defaut 600 s (10 min). Un composant
                              qui vide ses files ActiveMQ ou flushe KahaDB est
                              occupe, pas bloque.

PollIntervalSeconds           Frequence d'interrogation du serveur (defaut 10 s).
ProgressEverySeconds          Frequence des messages "toujours en attente"
                              (defaut 60 s), avec la derniere ligne de log lue.
PostReadySettleSeconds        Duree d'observation APRES le marqueur, pour
                              detecter une erreur survenant juste apres
                              l'initialisation (0 = desactive).
LogTailBytes                  Volume max de log rapatrie par passe (256 Ko).

ReadyPatterns / ErrorPatterns / IgnorePatterns : expressions regulieres,
insensibles a la casse.
  - ErrorPatterns fait gagner du temps : des qu'une signature d'echec
    apparait, le script arrete d'attendre au lieu de consommer tout le
    timeout pour rien.
  - IgnorePatterns ecarte une ligne AVANT toute evaluation. Utile pour
    neutraliser un ERROR connu et sans consequence au demarrage.

===========================================
COMMENT L'ATTENTE FONCTIONNE (POUR COMPRENDRE LES LOGS)
===========================================
1. AVANT de lancer la commande de demarrage, le script releve la taille
   actuelle du log : c'est le "point de reference".
   Sans lui, le script pourrait relire le marqueur de succes du demarrage
   PRECEDENT, encore present dans le fichier, et conclure a tort que le
   composant est pret. Seules les lignes ecrites APRES ce point comptent.
2. Le script relit ensuite, a chaque passe, uniquement ce qui a ete ecrit
   depuis la passe precedente. Le fichier est ouvert en partage lecture +
   ecriture : indispensable, car la JVM garde son log ouvert.
3. Une rotation de log (fichier redevenu plus court) est detectee et
   l'analyse repart du debut du nouveau fichier.
4. Chaque nouvelle ligne est evaluee dans cet ordre : IgnorePatterns,
   puis ErrorPatterns, puis ReadyPatterns.

CAS PARTICULIER - COMPOSANT DEJA DEMARRE
Si un service tourne DEJA au moment ou le script veut le demarrer, aucune
commande n'est envoyee et l'etat retourne est "A CONFIRMER" : le marqueur
de ce composant a ete ecrit lors de son lancement precedent et ne sera pas
reecrit, la preuve n'est donc pas rejouable. Le script le dit clairement
plutot que de faire semblant d'avoir verifie. Pour obtenir une preuve
fraiche, redemarrer le composant volontairement.

===========================================
MODE SUPERVISE ET MODE NON SURVEILLE
===========================================
Par defaut (mode supervise), un etat "A CONFIRMER" declenche une question
a l'operateur, avec les verifications a mener. Repondre OUI est enregistre
dans le log comme une DEROGATION, avec l'identite de l'utilisateur : c'est
une trace d'audit, a justifier dans le ticket.

Avec -Unattended, aucune question n'est posee et tout etat non prouve
arrete la sequence. A utiliser pour une execution planifiee, jamais pour un
demarrage de crise ou l'operateur est devant l'ecran.

===========================================
PORTILLONS DE VERIFICATION
===========================================
- Start-N4Sequence.ps1
  * Etape 0 : TOUS les serveurs joignables (ping + WinRM) avant de demarrer
    le moindre service. Un seul serveur muet arrete tout.
  * Etape 0 bis : etat initial reel de chaque composant. Un demarrage complet
    part normalement d'un ecosysteme totalement a l'arret ; les composants
    deja actifs sont listes et l'operateur decide.
  * Le Standby n'est PAS demarre sans le parametre explicite -StartStandby.
  * XPS n'est jamais lance tant que le Bridge n'est pas prouve operationnel.

- Stop-N4Sequence.ps1
  * Confirmation explicite avant de commencer (sauf -Unattended).
  * Confirmation supplementaire avant l'arret du Center Node.
  * Un service bloque en "Stopping" est identifie avec le PID du processus
    encore actif et la marche a suivre. AUCUN processus n'est jamais tue
    automatiquement : un arret force pendant une ecriture ActiveMQ/KahaDB
    est une cause connue de corruption.
  * Bilan final listant les arrets non confirmes.
  * (-InstallUpdates) Le patching ne demarre que si 100% des services sont
    confirmes Stopped - pas seulement "commande d'arret envoyee".

- Fix-N4-AMQCorruption.ps1
  * Tous les services confirmes arretes AVANT toute suppression.
  * Verification que db.data n'est plus verrouille par aucun processus.
  * Sauvegarde creee ET verifiee (taille identique) avant que l'original ne
    soit supprime.

- RollingRestart-N4-Cluster.ps1
  * Un noeud a la fois, chacun confirme operationnel avant le suivant.
  * Si un noeud echoue, les noeuds restants ne sont PAS touches.

===========================================
PREREQUIS TECHNIQUES
===========================================
- PowerShell Remoting (WinRM) actif sur tous les serveurs cibles :
    Sur chaque serveur N4 (en Administrateur) : Enable-PSRemoting -Force
    Test de connectivite : Test-WSMan <NomDuServeur>
- Compte d'execution membre du groupe Administrateurs local sur chaque
  serveur cible (ou fournir -Credential (Get-Credential) a chaque script)
- Le compte doit pouvoir LIRE les fichiers de log declares dans LogPath.
  C'est une nouvelle exigence : sans acces au log, pas de preuve possible.
- Tous les scripts doivent rester dans le MEME DOSSIER (ils s'appellent
  entre eux via $PSScriptRoot)
- PowerShell 5.1 ou superieur sur la machine qui orchestre.

Plusieurs environnements (dev/test/prod) : definir la variable
d'environnement NAVIS_N4_CONFIG_PATH avant de lancer un script pour
pointer vers un fichier de config different de celui du dossier courant :
    $env:NAVIS_N4_CONFIG_PATH = "C:\Config\Navis-Config-PROD.json"
    .\Start-N4Sequence.ps1

Cas particulier - Bridge et XPS sur le meme serveur physique : c'est deja
gere nativement. Il suffit de mettre la meme valeur pour BridgeHost et
XPSHost dans Navis-Config.json - les scripts dedupliquent automatiquement
la liste des serveurs.

===========================================
UTILISATION
===========================================
Tester d'abord sur un environnement hors production.

    # 1. Identifier les marqueurs de log (lecture seule, sans risque)
    .\Find-N4ReadinessPattern.ps1 -ComponentKey Bridge
    .\Find-N4ReadinessPattern.ps1 -ComponentKey Cluster -ComputerName N4CLUSTER01

    # 2. Des qu'un incident est signale, AVANT de lire les logs :
    .\Test-N4IncidentPreCheck.ps1 -Credential (Get-Credential)

    # 3. Sequences
    .\Start-N4Sequence.ps1
    .\Start-N4Sequence.ps1 -Credential (Get-Credential)
    .\Start-N4Sequence.ps1 -StartStandby
    .\Start-N4Sequence.ps1 -Unattended          # execution planifiee
    .\Stop-N4Sequence.ps1
    .\RollingRestart-N4-Cluster.ps1 -NodesToRestart "N4CLUSTER02"
    .\Fix-N4-AMQCorruption.ps1

    # 4. Fenetre de maintenance : arret + patching Windows + redemarrage auto
    .\Stop-N4Sequence.ps1 -InstallUpdates -Credential (Get-Credential)
    # Idem sans relancer N4 automatiquement a la fin
    .\Stop-N4Sequence.ps1 -InstallUpdates -SkipAutoStart -Credential (Get-Credential)

Chaque script :
  - Demande confirmation explicite avant toute action destructive
    (taper OUI en majuscules)
  - Affiche des blocs d'instructions encadres : ce qu'il va faire, ce qu'il
    faut verifier avant, et quoi faire en cas de probleme
  - Ecrit un log horodate dans le dossier LocalLogFolder configure
    (nom de fichier : NomDuScript_AAAAMMJJ_HHMMSS.log)
  - Consigne en en-tete de log l'UTILISATEUR (domaine\utilisateur),
    l'ADRESSE IP de la machine d'execution et le fichier de config utilise
  - Consigne "Copyright : (c) KMKernel" en en-tete de chaque session de log
  - Affiche les etapes en couleur (INFO/ACTION/OK/WARN/ERROR)

Chaque fichier .ps1/.psm1 porte la mention Copyright (c) KMKernel dans son
bloc d'aide (.NOTES), visible via Get-Help .\NomDuScript.ps1 -Full

===========================================
PROTECTION PAR CODE PIN (integrite + copyright)
===========================================
CODE PIN PAR DEFAUT : 123456 - A CHANGER IMMEDIATEMENT avec :
    .\Set-N4CopyrightPin.ps1

Fonctionnement :
- Chaque script (.ps1/.psm1) verifie sa propre empreinte SHA-256 au
  demarrage, par rapport a Navis-Integrity.json.
- Si le fichier est identique a sa version de reference : execution
  normale, aucune interruption.
- Si le fichier a ete modifie (y compris un retrait de la mention
  Copyright (c) KMKernel) : le script s'arrete et demande le code PIN.
  Sans le bon code (3 tentatives), il refuse de continuer.
- Toute execution derogatoire (PIN valide sur fichier modifie) est
  consignee dans le log (date, utilisateur, IP) a des fins d'audit.

APRES UNE MISE A JOUR DES SCRIPTS - A FAIRE UNE FOIS
Les scripts ayant ete modifies, leurs empreintes ne correspondent plus au
manifeste : chaque lancement reclamera le code PIN tant que le manifeste
n'aura pas ete regenere. Apres avoir relu et valide les modifications :
    .\New-N4IntegrityManifest.ps1
(le code PIN est demande ; la regeneration est reservee au proprietaire du
code, apres une modification volontaire et verifiee)

Les fichiers .json de configuration ne sont PAS proteges : ils restent
volontairement libres d'edition. Modifier la section Readiness ne declenche
donc aucune alerte d'integrite.

LIMITE HONNETE : un script PowerShell reste un fichier texte en clair.
Cette protection est un GARDE-FOU ET UNE TRACE D'AUDIT contre la
modification accidentelle ou non autorisee - ce n'est PAS une securite
inviolable. Pour une protection plus forte, envisager la signature de code
PowerShell (Set-AuthenticodeSignature) combinee a une politique d'execution
AllSigned, et des permissions NTFS restrictives sur le dossier des scripts.

===========================================
SAVOIR QUI A MODIFIE UN SCRIPT, QUAND, ET DEPUIS QUELLE IP
===========================================
Par defaut (sans configuration supplementaire), une alerte d'integrite
affiche deja deux indices immediats : la date de derniere modification du
fichier et son proprietaire NTFS actuel (pas forcement le dernier editeur).

Pour une tracabilite complete et fiable (qui + quand + IP si acces
reseau), activer l'audit de securite Windows UNE FOIS, en Administrateur :
    .\Enable-N4FileAuditing.ps1

Puis, apres toute alerte d'integrite, consulter qui a fait quoi :
    .\Get-N4FileModificationEvents.ps1
    .\Get-N4FileModificationEvents.ps1 -Days 7

LIMITE HONNETE : l'adresse IP n'est significative que pour un acces via
partage reseau (SMB). Pour un acces LOCAL (console/RDP direct sur la
machine), l'IP remonte comme "locale" (127.0.0.1/::1) - c'est normal, pas
un defaut du script.

===========================================
PROTECTION DES PDF (SOP-0 a SOP-3, Guide Diagnostic)
===========================================
Les PDF livres sont proteges avec le meme code PIN par defaut (123456)
comme mot de passe "proprietaire" :
- Ouverture et lecture : LIBRES, sans mot de passe.
- Modification, extraction de contenu pour edition, remplissage de
  formulaire : necessitent le mot de passe.
- Impression et copie de texte : autorisees.

Pour changer ce mot de passe plus tard, utiliser un outil respectant les
permissions PDF standard (Adobe Acrobat "Proteger un PDF", ou equivalent).

LIMITE HONNETE : la protection PDF par mot de passe proprietaire est un
standard reconnu, mais certains outils ne respectent pas ces permissions.
Ce n'est pas un chiffrement de confidentialite (le PDF s'ouvre librement en
lecture) : c'est une protection contre la modification par les logiciels
qui respectent la norme PDF.

===========================================
PATCHING WINDOWS (Stop-N4Sequence.ps1 -InstallUpdates)
===========================================
- Desactive par defaut : ne se declenche QUE si -InstallUpdates est passe
  explicitement. Un arret d'incident normal ne patche jamais les serveurs.
- Utilise l'API native Windows Update Agent (Microsoft.Update.Session),
  deja presente sur tous les Windows Server - aucun module externe requis.
- Pour chaque serveur touche par la sequence N4 : recherche des mises a
  jour, affichage du nombre et de la liste (KB + titre), telechargement,
  installation.
- Si une mise a jour necessite un redemarrage : le serveur est redemarre
  AUTOMATIQUEMENT et le script attend qu'il soit de nouveau joignable
  (WinRM) avant de continuer.
- Le cycle recherche/installation/redemarrage est repete par serveur
  (jusqu'a -MaxUpdatePasses, defaut 3) car certaines mises a jour ne
  deviennent visibles qu'apres l'installation d'une mise a jour prealable.
- Start-N4Sequence.ps1 n'est appele automatiquement QUE si TOUS les
  serveurs sont confirmes "A JOUR".

===========================================
LIMITES CONNUES / A ADAPTER
===========================================
- CES SCRIPTS N'ONT PAS ETE TESTES SUR UN ENVIRONNEMENT N4 REEL. Ils ont
  ete ecrits a partir de la documentation SOP-0/1/2/3 et du manuel de
  diagnostic. A valider en environnement de test avant tout usage en
  production.
- Le marqueur des noeuds Cluster et du Center est confirme par la
  documentation editeur. Ceux du Bridge, de XPS, d'ECN4 et d'ECN4Web
  restent des CANDIDATS, a relever sur vos serveurs avec
  Find-N4ReadinessPattern.ps1 avant tout usage en production. Tant que ce
  n'est pas fait, ces etapes retourneront "A CONFIRMER" - ce qui est le
  comportement voulu, mais renvoie la decision a l'operateur a chaque fois.
- Le Standby a volontairement une liste ReadyPatterns VIDE. Aucun marqueur
  de mode veille n'est documente, et le marqueur d'un Center actif ne doit
  surtout pas y figurer : un Standby qui l'ecrirait pendant que le primaire
  est actif signale un conflit de role, pas un demarrage reussi. L'etat
  reste donc "A CONFIRMER" et la verification passe par Node Info Desk.
- La preuve par le log ne dit pas QUI DETIENT LE ROLE ACTIF entre Center et
  Standby. Le log prouve qu'un service a fini son initialisation ; seule la
  vue N4 (Cluster Services) dit qui est reellement actif. Cette
  verification reste manuelle.
- De meme, la verification "ACTIVE dans Cluster Services" reste manuelle :
  les scripts observent le service Windows et le log applicatif, ce qui
  n'est pas strictement identique au statut applicatif N4 (ACTIVE/LOADING/
  WAITING). Toujours confirmer visuellement dans N4 avant de considerer un
  incident clos.
- Le Standby Center Node ne s'arrete pas toujours proprement via
  Stop-Service : les scripts detectent ce cas, signalent le PID du
  processus et alertent, sans jamais forcer l'arret automatiquement.
- Start-N4Sequence.ps1 ne demarre PAS le Standby sans -StartStandby
  (choix volontaire de securite).
- Le patching Windows suppose que le service Windows Update est fonctionnel
  sur chaque serveur et qu'une source de mises a jour est configuree
  (Windows Update direct, ou WSUS interne). Cette fonctionnalite n'a pas
  ete testee en conditions reelles.
- Restart-Computer -Wait -For WinRM necessite PowerShell 5.1 ou superieur
  sur la machine qui orchestre le script.
- Le chargement de Navis-Config.json necessite PowerShell 3.0 ou superieur
  (ConvertFrom-Json).
- Les logs sont lus en supposant un encodage UTF-8. Un log dans un autre
  encodage restera lisible pour les caracteres ASCII (ce qui suffit aux
  marqueurs techniques), mais peut afficher des caracteres accentues
  degrades dans les extraits.
