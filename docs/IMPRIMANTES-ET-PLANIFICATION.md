# Profils d'imprimantes et maintenance planifiée

Deux fonctions pour les parcs : **configurer les imprimantes une fois et les déployer partout**, et **laisser le
parc s'entretenir tout seul** grâce à une tâche planifiée Windows.

> **Interface graphique** : la recherche et le déploiement des imprimantes ne sont plus dans l'interface de
> SpokaPilote ; ils passent à **SpokaPrint**, un outil dédié (Outils › Autres outils › « Imprimantes → SpokaPrint »). Les
> commandes `spokapilote printers ...` décrites ici restent disponibles et inchangées pour vos scripts. La maintenance
> planifiée se règle dans **Outils › Pour ce poste › Maintenance planifiée** (l'agent, dans **Outils › Pour le parc ›
> Console centrale et agent**).

---

## 1. Profils d'imprimantes

Un profil est un fichier JSON qui décrit les imprimantes à installer, éventuellement selon le site (sous-réseau) ou
le nom du poste. Le même profil peut être appliqué à tout le parc : chaque poste n'installe que ce qui le concerne.

### Commandes

```powershell
spokapilote printers export-profile \\srv\deploiement\imprimantes.json   # depuis un poste déjà configuré
spokapilote printers deploy \\srv\deploiement\imprimantes.json --dry-run # simulation (sans droits admin)
spokapilote printers deploy \\srv\deploiement\imprimantes.json --yes     # application (console admin)
spokapilote printers deploy https://intranet.corp.fr/imprimantes.json    # profil publié en HTTPS
spokapilote printers deploy                                              # profil de la console centrale
spokapilote printers remove "Copieur RDC" --yes                          # retire la file + le port s'il ne sert plus
```

Le déploiement est **idempotent** : on peut le relancer à chaque démarrage ou chaque semaine.

| Résultat | Signification |
|---|---|
| `créée` | l'imprimante n'existait pas : port TCP/IP et file d'impression créés |
| `à jour` | une imprimante du même nom pointe déjà vers la même adresse (et le bon pilote s'il est imposé) : rien n'est touché |
| `mise à jour` | l'adresse, le pilote, l'emplacement, le commentaire ou le partage diffèrent : corrigés ; l'ancien port inutilisé est retiré |
| `retirée` | imprimante réseau non listée, retirée parce que `removeUnlisted` est activé |
| `ignorée` | non applicable dans ce contexte (ex. connexion partagée exécutée en SYSTEM) |
| `échec` | avec le motif (pilote introuvable, refus du spouleur...) |

En simulation (`--dry-run`), les mêmes lignes s'affichent en « à créer », « à mettre à jour », « à retirer ».
Codes de sortie : `0` tout est conforme, `2` au moins un échec, `1` tout a échoué ou profil invalide.

### Choix du pilote

1. `driverName` du profil s'il est **déjà présent** sur le poste ;
2. sinon, s'il s'agit d'une mise à jour d'adresse sans `driverName`, le **pilote actuel est conservé** ;
3. sinon l'imprimante est **identifiée sur le réseau** (SNMP, IPP, page web) et la logique habituelle de SpokaPilote
   s'applique : pilote présent → dépôt interne → Microsoft Update Catalog (modèle exact puis universel du constructeur)
   → pilote IPP de Windows. Si l'imprimante ne répond pas, le champ `model` du profil sert à la recherche ;
4. un `driverName` absent du poste et introuvable ailleurs est cherché dans le **magasin de pilotes de Windows**
   (pilotes intégrés) ; à défaut, le choix automatique prend le relais et le rapport le signale.

Une imprimante déjà conforme ne déclenche **aucune** recherche réseau : un déploiement hebdomadaire sur 100 postes ne
sollicite ni les imprimantes ni le catalogue Microsoft.

### Format

```jsonc
{
  "version": 1,
  "name": "Imprimantes du groupe",
  "removeUnlisted": false,
  "printers": [ /* imprimantes de tous les postes */ ],
  "groups": [
    { "name": "Site de Lyon", "when": { "subnets": [ "10.2.0.0/16" ] }, "printers": [ /* ... */ ] }
  ]
}
```

Champs d'une imprimante :

| Champ | Rôle |
|---|---|
| `name` | nom de la file d'impression Windows (obligatoire, ni `\` ni `,`) |
| `address` | adresse IP ou **nom DNS** (recommandé : survit aux changements d'IP) |
| `connection` | à la place de `address` : file partagée d'un serveur d'impression `\\serveur\file` |
| `model` | modèle exact (ex. `KYOCERA TASKalfa 3253ci`) : aide au choix du pilote |
| `driverName` | pilote préféré, nom Windows exact (voir `Get-PrinterDriver`) |
| `location`, `comment` | emplacement et commentaire affichés dans Windows |
| `setDefault` | imprimante par défaut de l'utilisateur qui exécute le déploiement |
| `shared` | `true` partage la file, `false` arrête le partage, absent = laissé tel quel |
| `ippOnly` | utilise uniquement le pilote IPP de Windows (aucun téléchargement) |
| `ippUrl` | URL IPP explicite (défaut : `http://adresse:631/ipp/print`) |
| `protocol` | `raw` (défaut, port 9100), `lpr` ou `ipp` ; absent = automatique |
| `portNumber`, `lprQueue` | port RAW (défaut 9100), file LPR (défaut `lp`) |
| `when` | condition propre à cette imprimante |

Conditions (`when`, sur une imprimante ou un groupe ; celles du groupe et de l'imprimante s'additionnent) :

| Critère | Rempli si |
|---|---|
| `subnets` | une adresse du poste appartient à l'un des sous-réseaux CIDR (`10.1.0.0/16`, `192.168.30.0/24`) |
| `computerNames` | le nom du poste correspond à l'un des motifs (`COMPTA-*`, `PC-??`, sans tenir compte de la casse) |
| `excludeComputerNames` | exclusions prioritaires (`*-TEST*`) |
| `adSites` | le site Active Directory du poste correspond à l'un des motifs (`PARIS`, `LYON-*`) — profil `"version": 2` |
| `organizationalUnits` | le compte ordinateur est dans l'une des unités d'organisation (`OU=Compta,OU=Paris,DC=corp,DC=local`, ou simplement `OU=Paris`) — profil `"version": 2` |

Tous les critères renseignés doivent être remplis ; dans un critère, une seule valeur suffit.

Les critères Active Directory sont lus dans le registre du poste, sans requête réseau (site mémorisé par Netlogon,
unité d'organisation mémorisée par le dernier traitement des stratégies de groupe). Sur un poste hors domaine, ou si
l'information n'est pas encore connue, le critère n'est **pas** rempli : l'imprimante n'est pas installée. Un profil
qui les utilise doit porter `"version": 2` ; les versions antérieures (SpokaPilote 3.1 et avant) refusent alors le
profil au lieu d'ignorer le critère et d'installer l'imprimante partout.

`removeUnlisted: true` retire les imprimantes **réseau** (port TCP/IP ou IPP) qui ne figurent pas parmi les imprimantes
applicables au poste. Les imprimantes locales ou virtuelles (PDF, XPS, OneNote, Fax, USB) et les connexions partagées
ne sont jamais retirées. Désactivé par défaut : testez d'abord avec `--dry-run`.

Le profil est vérifié avant tout changement (adresses, CIDR, motifs, noms en double sans condition...) : un profil
invalide n'est jamais appliqué partiellement.

### Export depuis un poste de référence

`export-profile` reprend les imprimantes réseau installées (adresse et protocole du port, pilote, emplacement,
commentaire, partage, imprimante par défaut) et les connexions partagées `\\serveur\file`. Sont ignorés, avec le
motif affiché : imprimantes virtuelles et locales, et ports **WSD** (adresse non exportable : recréez ces imprimantes
en TCP/IP). Il reste à ajouter les conditions `when` ou à répartir les imprimantes en groupes.

### Profil fourni par la console centrale

Si `ServerUrl` est configuré (`spokapilote config set ServerUrl https://spokapilote.corp.fr`) et qu'aucun fichier
n'est indiqué, SpokaPilote interroge :

```
GET {ServerUrl}/api/printer-profile
Authorization: Bearer {ServerToken}
→ 200 + profil JSON (même format)   → 204 : aucun profil pour ce poste (rien à faire)
```

HTTPS obligatoire (sauf `localhost` pour les essais), redirections vers HTTP refusées, 2 Mo maximum. Le jeton n'est
envoyé qu'à la console elle-même, jamais à une autre URL. Les conditions sont évaluées sur le poste.

### Scénario : 3 sites, imprimantes différentes par site

Paris (10.1.0.0/16), Lyon (10.2.0.0/16) et Nantes (10.3.0.0/16). La comptabilité de Paris a son propre copieur ;
la direction a une imprimante sur tous les sites.

1. Sur un poste de chaque site déjà configuré : `spokapilote printers export-profile C:\temp\paris.json` (etc.).
2. Assemblez un seul profil `\\srv\deploiement\imprimantes.json` :

```jsonc
{
  "name": "Imprimantes Groupe",
  "printers": [
    { "name": "Direction", "address": "imp-direction.corp.local", "driverName": "HP Universal Printing PCL 6",
      "when": { "computerNames": [ "DIR-*" ] } }
  ],
  "groups": [
    { "name": "Paris", "when": { "subnets": [ "10.1.0.0/16" ] },
      "printers": [
        { "name": "Paris - Accueil", "address": "10.1.0.25", "model": "KYOCERA TASKalfa 3253ci",
          "location": "Paris, RDC", "setDefault": true },
        { "name": "Paris - Compta", "address": "10.1.0.26", "when": { "computerNames": [ "COMPTA-*" ] } }
      ] },
    { "name": "Lyon", "when": { "subnets": [ "10.2.0.0/16" ] },
      "printers": [ { "name": "Lyon - Accueil", "address": "imp-lyon.corp.local", "model": "Xerox VersaLink C405" } ] },
    { "name": "Nantes", "when": { "subnets": [ "10.3.0.0/16" ] },
      "printers": [ { "name": "Nantes - Atelier", "address": "10.3.0.40", "ippOnly": true } ] }
  ]
}
```

3. Vérifiez sur un poste de chaque site : `spokapilote printers deploy \\srv\deploiement\imprimantes.json --dry-run`.
4. Déployez : script de démarrage, RMM, ou la tâche planifiée ci-dessous (`--printers`). Un portable qui passe de
   Paris à Lyon reçoit l'imprimante de Lyon au déploiement suivant (activez `removeUnlisted` pour qu'il perde
   celles de Paris).

---

## 2. Maintenance planifiée

Une tâche planifiée Windows **`\SpokaPilote\Maintenance`** exécute SpokaPilote en compte **SYSTEM** (privilèges
maximum), sans session ouverte.

```powershell
spokapilote schedule enable --weekly lundi --time 12:00 --mode missing --output-dir \\srv\rapports
spokapilote schedule status      # déclencheur, prochaine exécution, dernière exécution et son résultat
spokapilote schedule run-now     # lance la tâche immédiatement
spokapilote schedule disable     # supprime la tâche (et le dossier \SpokaPilote s'il est vide)
```

| Option | Défaut | Rôle |
|---|---|---|
| `--weekly <jour>` · `--daily` · `--at-startup` | `--weekly lundi` | déclencheur (jours en français ou en anglais) |
| `--time 12:30` | 12:00 | heure (hebdomadaire et quotidien) |
| `--mode missing` · `updates` · `scan` | `missing` | pilotes manquants seulement · manquants + mises à jour · analyse seule |
| `--output-dir \\srv\rapports` | — | dépose `<POSTE>.json` / `.html` pour `spokapilote fleet-report` |
| `--printers <profil>` | — | déploie aussi un profil d'imprimantes : fichier, URL HTTPS ou `serveur` (console centrale) |
| `--random-delay 30` | 30 min | délai aléatoire : étale la charge (réseau, catalogue Microsoft) sur le parc |
| `--startup-delay 10` | 10 min | délai après le démarrage (`--at-startup`) |
| `--max-duration 120` | 120 min | durée maximale d'une exécution |
| `--on-battery` | non | autorise l'exécution sur batterie (sinon : jamais sur batterie) |

Autres réglages de la tâche : rattrapage si le poste était éteint à l'heure prévue (`StartWhenAvailable`), une seule
instance à la fois, définition XML conservée dans `%ProgramData%\SpokaPilote\maintenance-task.xml`.

La tâche lance `spokapilote maintenance --mode ...` (commande utilisable aussi à la main). Chaque exécution écrit un
journal complet `%ProgramData%\SpokaPilote\Logs\maintenance-AAAAMMJJ-HHMMSS.log` (les 30 derniers sont conservés).
Dernier résultat affiché par `schedule status` : `0` succès, `2` échec partiel, `1` erreur, `3010` redémarrage
requis (SpokaPilote ne redémarre jamais le poste de lui-même).

**Sécurité** : la tâche tourne en SYSTEM, elle doit donc lancer la version **installée dans Program Files** (non
modifiable par un utilisateur standard). `schedule enable` refuse un exécutable situé dans un dossier temporaire, un
dossier de compilation ou ailleurs (bureau, clé USB...).

### Scénario : entretien automatique chaque lundi midi

Objectif : chaque lundi vers midi, chaque poste installe les pilotes manquants, applique le profil d'imprimantes et
dépose son rapport pour le suivi du parc.

1. Installez SpokaPilote sur le parc (installation silencieuse), puis, sur chaque poste (script de démarrage, RMM,
   Intune), en administrateur :

   ```powershell
   spokapilote schedule enable --weekly lundi --time 12:00 --random-delay 45 --mode missing `
       --output-dir \\srv\rapports --printers \\srv\deploiement\imprimantes.json
   ```

   Les postes démarrent entre 12:00 et 12:45, ce qui évite 100 téléchargements simultanés.
2. Donnez au groupe **Ordinateurs du domaine** le droit d'écrire dans `\\srv\rapports` et de lire
   `\\srv\deploiement` : en SYSTEM, le poste accède au réseau avec son compte ordinateur.
3. Le lundi après-midi : `spokapilote fleet-report \\srv\rapports` produit `parc.html` (postes à traiter).
4. Sur un poste précis : `spokapilote schedule status`, puis le dernier journal dans `%ProgramData%\SpokaPilote\Logs`.

Pour appliquer aussi les mises à jour de pilotes : `--mode updates`. Pour un audit sans aucun changement : `--mode scan`.

---

## Limites connues

- **Imprimante par défaut et connexions partagées** (`\\serveur\file`) sont propres à chaque utilisateur. En tâche
  SYSTEM, `setDefault` est ignoré et les connexions sont signalées « ignorées » : pour celles-ci, utilisez une GPO
  ou exécutez `printers deploy` dans la session de l'utilisateur. De plus, si Windows gère lui-même l'imprimante par
  défaut (réglage « Laisser Windows gérer mon imprimante par défaut »), il peut la changer à tout moment.
- Les imprimantes **WSD** ne sont pas exportables (pas d'adresse fixe) : recréez-les en TCP/IP.
- Passer une file existante en IPP (`ippOnly`) la **recrée** : les préférences d'impression de cette file sont perdues.
- Un pilote n'est jamais désinstallé par `printers remove` ni par `removeUnlisted` : seuls la file et son port le sont.
- Le spouleur garde parfois un port quelques secondes après la suppression de la file : SpokaPilote réessaie, et le
  signale si le port reste retenu (il disparaît alors au redémarrage du spouleur).
- Validé en réel sur Windows 11 25H2 : déploiement (création, idempotence, changement d'adresse puis de pilote),
  export, retrait vers 127.0.0.1 ; tâche planifiée en SYSTEM (création, état, exécution en mode `scan`, journal,
  suppression) ; contrat de la console sur `localhost`. Pas encore testé sur de vraies imprimantes ni avec une
  vraie connexion `\\serveur\file`.
