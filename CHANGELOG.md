# Journal des versions

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
