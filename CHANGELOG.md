# Journal des versions

## 4.0.1 — 28/11/2026

Version de sécurité et de robustesse, issue d'une revue indépendante avant déploiement à grande échelle. **Mise à jour
recommandée pour tous les postes et la console.**

**Sécurité**
- **Cache de la console : transport seulement, sans exception.** Les métadonnées non signées (catalogues XML Lenovo,
  réponses NVIDIA) et la boîte de téléchargement du Microsoft Update Catalog ne passent plus jamais par la console :
  une console compromise ne peut plus choisir ce qu'un poste exécute. Seuls les catalogues `.cab` signés (Dell, HP)
  et les fichiers dont le poste connaît l'empreinte officielle restent relayés. La console 4.0.1 refuse aussi ces
  relais aux postes 4.0.0, qui passent alors directement par la source officielle.
- **Lignes de commande des constructeurs vérifiées** (Lenovo, HP) et lancées sans interpréteur de commandes : seul le
  paquet téléchargé et vérifié peut être exécuté, aucun enchaînement de commandes n'est accepté.
- **Éditeurs approuvés : correspondance exacte**, forme juridique comprise (plus de « Canon » qui accepterait
  « Canonical », ni « NVIDIA GmbH » pour « NVIDIA Corporation ») ; contrôle de révocation. **À vérifier** : une entrée
  de `ExtraTrustedPublishers` doit maintenant reprendre le nom exact du certificat (« Fabrikam, Inc. »).
- **Aucune copie de la console sans empreinte indépendante** : un fichier dont la source ne publie pas d'empreinte est
  toujours téléchargé directement ; les catalogues `.cab` relayés plus anciens que le dernier accepté sont refusés ;
  une entrée du Microsoft Update Catalog est confirmée directement chez Microsoft et la version du pilote extrait doit
  correspondre à celle annoncée.
- Paquets constructeurs dont l'installeur n'est pas un `.exe` / `.cmd` / `.bat` écartés dès l'analyse (au lieu
  d'échouer à chaque maintenance) ; arguments limités aux caractères ASCII.
- **Dossier de données protégé** : `%ProgramData%\SpokaPilote` n'est modifiable que par les administrateurs ;
  téléchargements et extractions dans un dossier de travail privé, recréé à chaque exécution ; fichiers déposés par un
  autre compte refusés ou mis en quarantaine. Le **jeton d'enrôlement** est sorti de `settings.json` et chiffré
  (illisible par un utilisateur standard), masqué partout ; `spokapilote config set ServerToken -` le lit sur l'entrée
  standard (jamais en ligne de commande), et la désinstallation l'efface.
- **Mise à jour automatique** : empreinte publiée obligatoire, adresse GitHub exacte, fichier revérifié juste avant
  son lancement.

**Robustesse sur de grands parcs**
- **Sauvegardes de pilotes limitées** (3 par défaut, 10 Go au plus) et réutilisées quand rien n'a changé ; dossiers de
  téléchargement nettoyés : plus de disque qui se remplit au fil des maintenances.
- **Annulation sûre** : seuls les paquets réellement ajoutés par SpokaPilote et inchangés depuis sont retirés, sans
  forcer ; « annuler la dernière intervention » refusée au-delà de 30 jours.
- **Anneaux sans angle mort** : les pilotes sans numéro de version sont suivis et suspendables ; une suspension
  s'applique au même pilote venu d'une autre source ; la liste de référence téléchargée ne peut que bloquer ; politique
  et décisions revérifiées juste avant l'installation.
- Mode portable : caches de politique séparés par poste.
- Réglages `settings.json` lus sans tenir compte de la casse des noms.
- Désinstallation : les tâches planifiées SpokaPilote sont retirées.

**Déploiement en masse**
- Kit `deploy/` (installation vérifiée par SHA-256, détection, désinstallation, configuration commune) et guide
  [docs/DEPLOIEMENT-ENTREPRISE.md](docs/DEPLOIEMENT-ENTREPRISE.md) : Intune, Configuration Manager, GPO, RMM.

## 4.0.0 — 27/11/2026

SpokaPilote se recentre : un logiciel clair pour les pilotes, une console taillée pour les très grands parcs, et un
nouveau produit, **SpokaPrint**, pour les imprimantes.

**Interface simplifiée**
- Nouvel **Accueil** : modèle du PC, note de santé, état du BIOS et un seul bouton qui s'adapte (Analyser ce poste →
  Installer N pilotes → Tout est à jour), progression et résultats en clair, redémarrage proposé quand il le faut.
- Navigation réduite à six pages dans une barre latérale façon Windows 11 : Accueil, Pilotes (filtres manquants / mises
  à jour / bloqués), Périphériques (réparation guidée visible tout de suite), Historique, Outils (dépôt, packs
  constructeur, pack hors ligne, rapport de parc, nettoyage du magasin, maintenance et agent, console, journal
  technique), Paramètres. Raccourcis Ctrl+1 à Ctrl+6, F5, F1 ; thème sombre corrigé.
- **Imprimantes → SpokaPrint** : la page Imprimantes quitte SpokaPilote. Les commandes `spokapilote printers …` restent
  disponibles pour les scripts existants.

**SpokaPrint 1.0** (nouveau produit, dépôt [Spokator/SpokaPrint](https://github.com/Spokator/SpokaPrint))
- Découverte réseau (modèle exact, n° de série, compteurs, consommables), installation avec pilote officiel vérifié,
  profils de déploiement par site, sous-réseau, nom de poste, **site Active Directory** ou **unité d'organisation**,
  inventaire et nettoyage, ligne de commande `spokaprint` et tâche planifiée. Même moteur de confiance que SpokaPilote.
- Profils d'imprimantes : conditions `adSites` / `organizationalUnits` (un profil qui les utilise porte
  `"version": 2` ; les versions plus anciennes le refusent au lieu de l'appliquer partout).

**Console centrale : très grands parcs**
- **Mesurée sur 20 000 postes** (essai à 100 000) : indicateurs précalculés à l'arrivée de chaque rapport, listes
  paginées, filtrées et triées par le serveur, rapports stockés compressés, écritures sérialisées, index. Interrogation
  d'un agent 1 150 ms → 3 ms (p95), liste des postes 2,6 s → 7 ms, 81 → 660 rapports/s, mémoire divisée par deux,
  base deux fois plus petite. Délestage propre (`503` + `Retry-After` étalé) quand des milliers de postes arrivent en
  même temps.
- **Anneaux de déploiement** : Pilote, Précoce, Général (modifiables, liés aux groupes) ; une nouvelle version de
  pilote n'atteint chaque anneau qu'après son délai, et éventuellement après validation dans l'anneau précédent.
  **Disjoncteur** : trop d'échecs d'installation (ou de périphériques en erreur après installation) et la version est
  suspendue sur tout le parc, avec alerte et journal d'audit ; reprise ou maintien du blocage en un clic. Page
  « Déploiement » dans la console ; les pilotes retenus apparaissent sur le poste avec la date à laquelle ils seront
  autorisés. Conçu pour l'échelle : un poste 4.0 demande une décision pour ses seuls pilotes (quelques Ko, 1 à 10 ms,
  quelle que soit la taille du parc, et décisions gardées en cache s'il perd la console) ; les postes 3.x reçoivent une
  politique bornée qui ne leur laisse jamais installer une version pas encore autorisée.
- **Cache de pilotes et de catalogues sur le réseau local** : la console télécharge chaque fichier officiel et chaque
  catalogue (Microsoft Update Catalog, Dell, HP, Lenovo, NVIDIA) une seule fois et le sert aux postes. Elle n'est
  qu'un relais : chaque poste revérifie empreinte, signature et matériel, et télécharge directement si la console ne
  répond pas. Fini le blocage du catalogue Microsoft quand des milliers de PC sortent par la même adresse.
  Préchargement pour les sites isolés.
- Supervision (`/api/metrics`, format Prometheus), sauvegarde à chaud planifiable, compactage, exports CSV en continu.
- Migration automatique depuis la 3.x ; les postes 3.x continuent de fonctionner (politique prudente pour eux).

**Poste**
- Paramètre `UseConsoleCache` (auto / on / off), visible dans les Paramètres et `spokapilote server status`.
- Le poste renvoie son rapport sans compression si un intermédiaire la refuse, et patiente quand la console est
  surchargée.

## 3.1.0 — 26/11/2026

**Console centrale : travail en équipe**
- **Comptes nominatifs** avec trois rôles : Administrateur (tout, y compris comptes, alertes, politique), Technicien
  (consulter, lancer des actions et des planifications, gérer les groupes), Lecture seule. Mots de passe protégés
  (PBKDF2-SHA256, 310 000 itérations), session sécurisée (cookie HttpOnly / SameSite=Strict, anti-CSRF, expiration
  après 8 h d'inactivité), tentatives limitées. Le jeton administrateur reste valable pour les scripts et sert à créer
  le premier compte.
- **Groupes de postes** : ajout manuel ou règles automatiques (nom, constructeur, modèle, Windows, sous-réseau), avec
  aperçu des postes concernés ; vue d'ensemble et liste filtrables par groupe ; action à distance sur tout un groupe.
- **Actions planifiées** : une action de la liste blanche, chaque jour ou certains jours à heure fixe, pour un groupe
  ou tout le parc, avec fenêtre de maintenance facultative ; pas de doublon si une action identique est déjà en attente ;
  changements d'heure gérés.
- **Journal d'audit** : qui a fait quoi et quand (connexions, actions, planifications, groupes, comptes, réglages,
  politique), filtrable et exportable en CSV ; conservation réglable.
- Migration automatique de la base 3.0 ; les postes en 3.0 continuent de fonctionner sans changement.

**Poste**
- **Mode portable (clé USB du technicien)** : archives `SpokaPilote-Portable-<version>-<arch>.zip` à extraire sur une
  clé ; rien n'est écrit sur le PC hors installation de pilotes ; un dossier par poste sur la clé (journaux,
  historique, sauvegardes) ; dépôt `<clé>\SpokaPilote\Depot` utilisé automatiquement ; mise à jour par archive vérifiée.
  Une intervention ne peut être annulée que sur le poste où elle a été faite.
- **État du BIOS** (Dell, HP, Lenovo) : version installée comparée au catalogue officiel du modèle, importance donnée
  par le constructeur (critique, recommandée…), lien officiel ; dans la note de santé, l'interface, le rapport HTML et
  `spokapilote health`. Rien n'est jamais flashé sans `--include-firmware`, et les précautions (secteur, BitLocker)
  sont rappelées. Firmwares de stations d'accueil / Thunderbolt signalés à titre d'information.
- CLI : aide par commande (`spokapilote scan --help`, `spokapilote help printers`), `spokapilote --version` ; code 64
  pour une commande inconnue.

## 3.0.0 — 26/11/2026

SpokaPilote devient la boîte à outils complète du technicien, et la console pilote le parc à distance.

**Console centrale**
- **Actions à distance** depuis la fiche d'un poste, une sélection ou les postes à traiter : analyser, installer les
  pilotes manquants ou les mises à jour, déployer les imprimantes, annuler la dernière intervention, mettre à jour
  SpokaPilote. Liste blanche d'actions, jamais de commande arbitraire. Vue « Tâches » avec l'état et le résultat.
- **Agent du poste** : tâche planifiée `\SpokaPilote\Agent` (compte SYSTEM) qui récupère et exécute les actions ;
  `spokapilote agent enable|status|run|run-now|disable`, et dans l'interface (Paramètres).
- **Alertes** par e-mail (SMTP), Microsoft Teams, Discord, Slack ou webhook : poste critique, pilote dangereux installé,
  échec d'installation, poste silencieux ; une seule alerte par sujet et par 24 heures ; bouton de test.
- **Note de santé** de chaque poste dans la liste du parc et sur sa fiche.
- Postes identifiés par leur identifiant machine Windows (un poste renommé ou réinstallé n'est plus dupliqué) ;
  migration automatique de la base 2.0.
- Blocage temporaire après des essais de jetons répétés ; profil **HTTPS automatique** (Caddy, Let's Encrypt) dans
  `docker-compose.yml`.

**Poste**
- **Santé des pilotes** : note de A à E (pilotes manquants, en erreur, non signés, très anciens, pilotes dangereux déjà
  installés), dans l'interface, le rapport HTML et `spokapilote health`.
- **Réparation guidée** des périphériques en erreur : redémarrer, désactiver / réactiver, réinstaller le pilote,
  rechercher un pilote ; diagnostic des pannes matérielles ; `spokapilote device list|repair`.
- **Nettoyage du magasin de pilotes** : anciennes versions inutilisées, sauvegardées avant suppression, simulation
  par défaut ; `spokapilote driverstore list|clean`.
- **Packs Microsoft Surface** (Centre de téléchargement Microsoft), à côté de Dell, HP et Lenovo ; firmwares UEFI
  distingués et installés uniquement sur demande (`--include-firmware`).
- **Injection dans une image Windows** validée sur l'image officielle Windows 11 Entreprise (WIM et Windows hors
  ligne) : la mention expérimentale est retirée ; option `--no-firmware`, diagnostic clair de l'erreur DISM 87.
- **Interface en anglais** (Paramètres ou `--lang en`), **assistant de premier lancement**, raccourcis clavier (F5,
  F1, Ctrl+1 à 8), noms accessibles pour les lecteurs d'écran, dernier onglet mémorisé.

**Distribution**
- Signature de code facultative (exe, dll, installeurs) dès qu'un certificat est fourni ; MSIX x64, ARM64 et bundle ;
  manifestes winget générés à chaque version ; site web https://spokator.github.io/SpokaPilote/.

## 2.0.1 — 26/11/2026

- Mise à jour automatique : l'installeur ARM64 s'appelle désormais `SpokaPilote-Installer-<version>-arm64.exe`, pour
  que les postes x64 encore en 1.0.x récupèrent bien l'installeur x64 (`SpokaPilote-Setup-<version>.exe`).

## 2.0.0 — 26/11/2026

SpokaPilote passe du poste au parc.

**Parc**
- **Console centrale « SpokaPilote Server »** (Docker, image `ghcr.io/spokator/spokapilote-server`) : les postes y
  envoient leurs rapports après chaque analyse ou installation ; vue d'ensemble, postes à traiter, matériel sans pilote
  sur tout le parc (export CSV), fiche détaillée et historique de chaque poste, postes non vus depuis 7 jours,
  politique et profil d'imprimantes éditables en ligne. Jetons d'administration et d'enrôlement, conteneur non root
  en lecture seule. `spokapilote server status|upload`.
- **Politique de pilotes** (`driver-policy.json`, depuis la console, un partage ou une URL) : bloquer un pilote, une
  version ou un matériel, figer une version, limiter les sources, délai de recul avant une nouvelle version. Un pilote
  bloqué reste visible avec son motif et n'est jamais installé. Liste de référence intégrée (avis Microsoft sur des
  pilotes Intel Smart Sound provoquant des écrans bleus), rafraîchie depuis GitHub. `spokapilote policy show|check|test|init`.
- **Packs officiels des constructeurs** Dell, HP et Lenovo : le pack de déploiement d'un modèle, catalogues signés
  vérifiés, téléchargé, extrait et rangé dans le dépôt (`spokapilote driverpack list|download`, et dans l'interface).
- **Profils d'imprimantes** : exporter les imprimantes réseau d'un poste, les déployer sur les autres avec conditions
  par sous-réseau ou nom de poste ; idempotent (créée / à jour / mise à jour) ; `printers export-profile|deploy|remove`.
- **Maintenance planifiée** : tâche Windows en compte SYSTEM (hebdomadaire, quotidienne ou au démarrage, délai
  aléatoire pour étaler le parc) : pilotes manquants, mises à jour ou analyse seule, dépôt des rapports, profil
  d'imprimantes. `spokapilote schedule enable|status|run-now|disable`.
- **Injection de pilotes dans une image Windows** (expérimental) : `install.wim` ou Windows hors ligne, avec contrôle
  de signature et d'architecture de chaque pilote. `spokapilote image info|inject|cleanup`.

**Poste**
- **Pilotes graphiques** : NVIDIA entièrement automatique (service officiel, branche Studio par défaut ou Game Ready,
  pilote graphique seul sans NVIDIA App, signature NVIDIA vérifiée) ; Intel et AMD : lien vers la page officielle.
- **ARM64** : installeur dédié pour les PC Snapdragon et Surface ; la mise à jour automatique choisit le bon installeur.
- Correction : un pilote dont le catalogue de signature n'est déclaré que pour son architecture (ex. MediaTek) n'est
  plus refusé à tort.
- Interface : pilotes bloqués et liens officiels non sélectionnables, résumé de la politique, packs constructeur,
  profils d'imprimantes, maintenance planifiée, console centrale (test de connexion, envoi automatique des rapports).

## 1.0.1 — 25/11/2026

- Interface : corrige une erreur de la barre de progression qui pouvait afficher « Erreur inattendue ».
- Contrôle automatique de l'interface avant chaque publication (tous les onglets s'ouvrent sans erreur).

## 1.0.0 — 25/11/2026

Première version complète de SpokaPilote.

**Pilotes du poste**
- Inventaire complet du matériel (identifiants matériels, pilote installé, état, diagnostic des pannes matérielles).
- Sources officielles uniquement, interrogées en parallèle : dépôt interne, catalogues Dell / HP / Lenovo,
  Windows Update, Microsoft Update Catalog.
- Contrôles avant installation : domaines officiels en HTTPS, empreinte, signature Authenticode d'un éditeur
  approuvé, l'INF doit déclarer l'identifiant exact du matériel pour l'architecture et la version de Windows.
- Point de restauration et sauvegarde des pilotes avant chaque intervention.

**Nouveautés de la 1.0**
- **Historique et annulation** : chaque intervention est enregistrée ; « Annuler » retire les paquets ajoutés et
  les périphériques reviennent à leur pilote précédent (`spokapilote history`, `spokapilote rollback last`).
  Réinstallation d'une sauvegarde complète (`spokapilote restore`).
- **Pack hors ligne** pour les postes sans réseau (PC neuf sans pilote de carte réseau) : rapport JSON du poste →
  pack préparé et vérifié sur un poste connecté → installation hors ligne (`spokapilote pack`).
- **Rapport de parc** : consolidation des rapports de tous les postes déposés sur un partage
  (`--output-dir \\srv\rapports`, puis `spokapilote fleet-report \\srv\rapports`) — postes à traiter, matériel
  sans pilote sur tout le parc, modèles. Les pannes matérielles sont distinguées des pilotes manquants.
- **Mise à jour automatique** : SpokaPilote signale une nouvelle version et l'installe en un clic, après
  vérification de la taille et du SHA-256 publiés sur GitHub (`spokapilote self-update` pour le parc).
- `--only` / `--exclude` pour choisir précisément les pilotes à installer.
- Le catalogue Microsoft est interrogé avec parcimonie (pas de recherche pour les pilotes Windows intégrés) ;
  en ligne de commande, SpokaPilote attend la fin d'une limitation de débit au lieu de rendre un résultat partiel.

**Imprimantes**
- Découverte réseau (SNMP, IPP, page web), identification du modèle exact, choix du pilote (déjà présent, dépôt
  interne, Microsoft WHQL exact puis universel, pilote IPP Windows), création du port et de la file d'impression.

**Distribution**
- Installeur Windows `SpokaPilote-Setup-1.0.0.exe` (Program Files, menu Démarrer, commande `spokapilote` dans le
  PATH, installation silencieuse `/VERYSILENT`), paquet Microsoft Store en option.

## 0.1.0 — 25/11/2026

Prototype interne.
