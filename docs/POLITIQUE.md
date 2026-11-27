# Politique de pilotes (approbation / liste noire)

La politique de pilotes permet au service informatique de décider, pour tout le parc, **ce que SpokaPilote a le
droit d'installer** : « cette version de ce pilote provoque des écrans bleus, on ne l'installe pas », « ce matériel
est géré par le fournisseur du logiciel métier », « on ne dépasse pas la version certifiée », « uniquement depuis
notre dépôt et le constructeur », « pas de BIOS », « 15 jours de recul avant toute nouvelle version »…

Elle s'écrit dans un fichier `driver-policy.json` lisible par un humain, se vérifie avec `spokapilote policy check`,
s'essaie sur un poste avec `spokapilote policy test`, puis se déploie par un partage réseau, une URL HTTPS ou la
console centrale.

- Un pilote **bloqué** reste visible dans l'analyse avec son motif (« ⛔ bloqué par la politique : … ») et n'est
  **jamais** installé, ni par la ligne de commande, ni par l'interface, ni par un appelant qui l'aurait sélectionné
  (le moteur d'installation le refuse avec le statut `Blocked`).
- Une politique **invalide ou injoignable ne fait jamais échouer l'analyse** : le poste garde la dernière politique
  valide reçue (cache local) et l'indique clairement.
- La politique s'ajoute à la chaîne de confiance de SpokaPilote (sources officielles, empreinte, signature, ID
  matériel) ; elle ne peut pas l'affaiblir : impossible d'autoriser une source non officielle par ce biais.

## Démarrage en 4 commandes

```powershell
spokapilote policy init  \\srv\deploiement\driver-policy.json          # 1. modèle commenté
spokapilote policy check \\srv\deploiement\driver-policy.json          # 2. validation détaillée
spokapilote policy test  --policy \\srv\deploiement\driver-policy.json # 3. essai sur un poste (rien n'est installé)
spokapilote config set PolicySource \\srv\deploiement\driver-policy.json   # 4. activation (ou par GPO/Intune)
```

`spokapilote policy show` affiche ensuite la politique en vigueur, son origine (fichier, URL, console ou cache)
et toutes les règles, y compris celles de la liste de référence SpokaPilote.

## Format du fichier

Le fichier est du JSON ; les **commentaires** (`//` et `/* */`) et les virgules finales sont acceptés.
**Toute propriété inconnue rend le fichier invalide** : une faute de frappe dans un critère (`hardwareIds`,
`versionmaximum`…) élargirait sinon silencieusement la règle. Les noms de propriétés ne tiennent pas compte de la casse.

```jsonc
{
  "schema": 1,                         // version du FORMAT (gérée par SpokaPilote)
  "name": "Parc Siège",                // nom affiché dans les rapports
  "version": "2026.11-3",              // VOTRE révision (texte ou nombre), à incrémenter à chaque modification
  "updatedAt": "2026-11-25",           // date de modification (ISO 8601)
  "description": "Politique validée par le service informatique.",
  "settings": { ... },                 // réglages généraux (facultatif)
  "rules": [ ... ]                     // règles (facultatif)
}
```

| Propriété | Type | Rôle |
|---|---|---|
| `schema` | nombre | Version du format. Actuellement `1`. Un fichier d'un format plus récent que celui que connaît le poste est refusé (mettez SpokaPilote à jour). |
| `name` | texte | Nom de la politique (rapports, `policy show`). |
| `version` | texte ou nombre | Révision choisie par l'IT ; visible dans chaque rapport de poste (`policy.version`) pour vérifier que le parc a bien reçu la dernière. |
| `updatedAt` | date | Date de dernière modification. |
| `description`, `comment` | texte | Notes libres. |

### Réglages généraux (`settings`)

| Réglage | Défaut | Effet |
|---|---|---|
| `allowedSources` | toutes | Liste des sources autorisées : `WindowsUpdate`, `MicrosoftCatalog`, `Dell`, `HP`, `Lenovo`, `LocalRepository`, `Nvidia`, `Intel`, `Amd`. Une source absente **n'est même pas interrogée** (gain de temps) et aucune règle ne peut la réautoriser. Une liste vide est refusée. |
| `allowFirmware` | `true` | `false` : toute mise à jour BIOS / firmware est bloquée. |
| `allowOptional` | `true` | `false` : les pilotes « optionnels » de Windows Update sont bloqués. |
| `onlyMissingDrivers` | `false` | `true` : seuls les pilotes **manquants** (périphérique sans pilote) sont autorisés ; aucune mise à jour. |
| `minimumDriverAgeDays` | `0` | Recul minimal, en jours, avant d'installer une **nouvelle version** (0 à 3650). Ne s'applique pas aux pilotes manquants. L'âge est calculé à partir de la date du pilote fournie par la source (date `DriverVer` de l'INF pour Windows Update et le catalogue, date de publication pour Dell/HP/Lenovo) ; un pilote sans date n'est pas bloqué par ce réglage. |
| `useReferenceBlocklist` | `true` | Ajoute la [liste de référence SpokaPilote](#liste-de-référence-spokapilote) après vos règles. |
| `ignoreReferenceRules` | `[]` | Identifiants de règles de la liste de référence à ne pas appliquer. |

### Règles (`rules`)

Chaque règle combine une **action**, des **critères** (tous doivent être vrais : ET) et un **motif**.

| Propriété | Rôle |
|---|---|
| `id` | Identifiant court (recommandé) : il apparaît dans les motifs affichés et les rapports. |
| `action` | `block` (bloquer), `allow` (autoriser explicitement), `pin` (ne pas dépasser `pinVersion`). |
| `reason` | **Motif affiché aux techniciens** (ex. « Écrans bleus sur Latitude 5420, ticket #4512 »). |
| `pinVersion` | Pour `pin` : version maximale autorisée (incluse). |
| `expires` | Facultatif : date après laquelle la règle est ignorée. Une date sans heure (`2026-12-31`) couvre toute la journée. |
| `hide` | Pour `block` / `pin` : `true` retire le pilote de la liste au lieu de l'afficher comme bloqué (il reste tracé dans le rapport JSON, `policy.hiddenCandidates`, et dans `policy test`). |
| `comment`, `sourceUrl` | Notes libres ; lien vers l'avis officiel. |

**Critères** — une valeur ou une liste de valeurs (la liste est satisfaite si **l'une** des valeurs correspond) :

| Critère | Comparé à | Exemple |
|---|---|---|
| `hardwareId` | ID matériel de la correspondance **et** tous les ID matériels et compatibles des périphériques visés | `"PCI\\VEN_10DE*"` |
| `title` | Titre du pilote proposé | `"*GeForce*"` |
| `manufacturer` | Fabricant / fournisseur du pilote, ou fabricant du périphérique visé | `"Intel*"` |
| `category` | Classe du pilote ou du périphérique (`Display`, `Net`, `MEDIA`, `System`…) | `"Display"` |
| `deviceName` | Nom du périphérique visé (Gestionnaire de périphériques) | `"*Management Engine*"` |
| `source` | Source : `WindowsUpdate`, `MicrosoftCatalog`, `Dell`, `HP`, `Lenovo`, `LocalRepository`, `Nvidia`, `Intel`, `Amd` | `["WindowsUpdate", "MicrosoftCatalog"]` |
| `kind` | Type : `MissingDriver`, `Update`, `Optional`, `Firmware` | `"Firmware"` |
| `versionMin` / `versionMax` | Version proposée, bornes **incluses** | `"560.70"` / `"560.94"` |
| `versions` | Versions exactes | `["10.29.0.5152", "10.30.0.5152"]` |

- **Jokers** : `*` = n'importe quelle suite de caractères, `?` = un caractère. Insensible à la casse.
  Le motif doit couvrir **toute** la valeur : `"NVIDIA"` ne correspond pas à « NVIDIA GeForce… », écrivez `"NVIDIA*"`.
- En JSON, la barre oblique inverse se double : `PCI\VEN_10DE` s'écrit `"PCI\\VEN_10DE"`.
- **Versions** : comparées numériquement (`10.30.00.5714` = `10.30.0.5714`, `552.44` < `560.70`), sur la version
  telle que la source la publie (Windows Update et le catalogue donnent la version de l'INF, par ex. `31.0.15.5244`
  pour un pilote NVIDIA « 552.44 » : vérifiez la version affichée par `spokapilote scan`).
  Si la version proposée est illisible, les critères de version ne correspondent pas ; une règle `pin`, elle, bloque
  (impossible de garantir le plafond).
- Une règle **sans aucun critère est refusée** (elle viserait tous les pilotes). Pour tout viser volontairement,
  écrivez `"title": "*"`.

### Ordre d'évaluation et priorité

Pour chaque pilote proposé, SpokaPilote évalue dans cet ordre ; **le premier verdict rendu l'emporte** :

1. **Sources autorisées** (`allowedSources`) : une source non autorisée n'est pas interrogée. Non contournable.
2. **Règles, dans l'ordre du fichier** : d'abord celles de l'organisation, puis celles de la liste de référence.
   Les règles expirées sont ignorées.
   - `block` qui correspond → **bloqué**, fin de l'évaluation ;
   - `allow` qui correspond → **autorisé**, fin de l'évaluation (les réglages généraux de l'étape 3 sont levés) ;
   - `pin` qui correspond → **bloqué** si la version dépasse `pinVersion`, sinon l'évaluation **continue**
     (une règle suivante peut encore bloquer une version précise sous le plafond).
3. **Réglages généraux** : `allowFirmware`, `allowOptional`, `onlyMissingDrivers`, `minimumDriverAgeDays`.
4. Sinon : **autorisé**.

Conséquences pratiques :

- **Une exception se place AVANT la règle générale** qu'elle doit lever (comme une liste de contrôle d'accès de
  pare-feu). `policy check` signale une règle placée après une règle au moins aussi générale qui décide toujours
  avant elle (« elle ne s'appliquera jamais »).
- Les règles de l'organisation passent avant la liste de référence : un `allow` de l'organisation lève un blocage de
  la liste de référence (pour écarter une règle de référence précise, préférez `ignoreReferenceRules`).
- Un `allow` explicite lève `minimumDriverAgeDays`, `onlyMissingDrivers`, `allowFirmware` et `allowOptional`
  (ex. correctif de sécurité urgent), mais jamais `allowedSources`.
- Si un pilote bloqué et un pilote autorisé d'une autre source visent le même périphérique, le pilote **autorisé**
  est proposé (même s'il vient d'une source moins prioritaire) ; le pilote bloqué n'est affiché que si rien d'autre
  ne couvre le périphérique.

### Validation

`spokapilote policy check <fichier|url>` liste les erreurs (fichier refusé) et les avertissements avec le
**numéro de la règle** (1 = première du fichier) et son identifiant :

```
  ✗ Règle n°2 : Propriété inconnue « hardwareIds » (ligne 6, colonne 42). Vérifiez l'orthographe (voir « spokapilote policy init »).
  ✗ Règle n°3 (« c ») : Une règle « pin » doit indiquer « pinVersion » (version maximale autorisée).
  ✗ Règle n°4 (« d ») : « versionMin » (3) est supérieure à « versionMax » (2).
  ⚠ Règle n°5 (« ok-552 ») : elle ne s'appliquera jamais : la règle n°1 (block), au moins aussi générale et placée avant, décide toujours en premier.
✗ Politique INVALIDE : 3 erreur(s) (règle(s) n°2, n°3, n°4).
```

Code de sortie : `0` si valide, `1` sinon (utilisable dans une chaîne de validation avant publication).
Erreurs : JSON illisible, propriété inconnue, action inconnue, règle sans critère, valeur vide, source ou type
inconnu, version illisible, `versionMin` > `versionMax`, `pin` sans `pinVersion`, date illisible, format trop récent,
`allowedSources` vide, `minimumDriverAgeDays` hors limites. Avertissements : nom absent, motif absent, identifiant en
double, règle expirée, règle masquée par une règle précédente, propriété sans effet.

**Une politique invalide n'est jamais appliquée partiellement** : le poste la refuse entièrement et garde la
précédente (supprimer silencieusement une règle de blocage fautive serait plus dangereux).

## Exemples concrets

### Bloquer une version de pilote graphique qui provoque des écrans bleus

```json
{
  "id": "nvidia-560-bsod",
  "action": "block",
  "hardwareId": "PCI\\VEN_10DE*",
  "versionMin": "32.0.15.6070",
  "versionMax": "32.0.15.6094",
  "reason": "Écrans bleus constatés sur les postes CAO (ticket #4512) — en attente du correctif NVIDIA",
  "expires": "2027-03-31"
}
```

### Une exception validée avant un blocage général

```json
"rules": [
  { "id": "realtek-valide", "action": "allow", "hardwareId": "HDAUDIO\\FUNC_01&VEN_10EC&DEV_0897*", "versions": "6.0.9600.1",
    "reason": "Version testée sur le modèle Z590 (ticket #3980)" },
  { "id": "realtek-gel", "action": "block", "hardwareId": "HDAUDIO\\FUNC_01&VEN_10EC*",
    "reason": "Pilotes audio Realtek gelés : seule la version validée est autorisée" }
]
```

### Figer une version certifiée pour un logiciel métier

```json
{ "id": "gpu-certifie", "action": "pin", "deviceName": "*Quadro*", "pinVersion": "31.0.15.3742",
  "reason": "Version certifiée par l'éditeur du logiciel de CAO : ne pas dépasser" }
```

### Matériel géré à part (masqué)

```json
{ "id": "lecteur-badges", "action": "block", "hardwareId": "USB\\VID_072F&PID_*", "hide": true,
  "reason": "Lecteurs de badges : pilote déployé par le prestataire du contrôle d'accès" }
```

### Politique « prudente » complète

```json
{
  "schema": 1,
  "name": "Parc administratif",
  "version": 4,
  "updatedAt": "2026-11-25",
  "settings": {
    "allowedSources": [ "LocalRepository", "Dell", "HP", "Lenovo", "WindowsUpdate" ],
    "allowFirmware": false,
    "allowOptional": false,
    "minimumDriverAgeDays": 21
  },
  "rules": [
    { "id": "securite-urgente", "action": "allow", "hardwareId": "PCI\\VEN_8086&DEV_15F3*", "versions": "1.1.4.43",
      "reason": "Correctif de sécurité Intel Ethernet : déploiement immédiat" }
  ]
}
```

## Essayer avant de déployer

```powershell
spokapilote policy test --policy .\driver-policy.json          # verdict pour chaque pilote proposé sur ce poste
spokapilote policy test --policy .\driver-policy.json --deep   # en interrogeant aussi le catalogue Microsoft
spokapilote install --dry-run --policy .\driver-policy.json    # simulation complète : les pilotes bloqués ne sont pas sélectionnés
```

`policy test` classe les pilotes en **bloqués** (avec la règle et le motif), **autorisés explicitement** et
**autorisés sans règle**. L'option `--policy <fichier|url>` (aussi acceptée par `scan`, `install` et `policy show`)
remplace la source configurée pour cette exécution seulement ; elle n'est pas mise en cache.

## Déployer sur le parc

Le poste cherche sa politique dans cet ordre (réglages de `%ProgramData%\SpokaPilote\settings.json`, déployable par
GPO / Intune / script) :

1. `PolicySource` : chemin local, partage UNC ou URL HTTPS d'un fichier `driver-policy.json` ;
2. sinon, si `ServerUrl` est renseigné : la console centrale, `{ServerUrl}/api/policy` ;
3. sinon : aucune politique d'organisation (seule la liste de référence s'applique).

La politique est relue **à chaque analyse** (interface, `scan`, `install`), en parallèle de l'inventaire.

### 1. Fichier sur un partage réseau (le plus simple)

```powershell
spokapilote config set PolicySource \\srv\deploiement$\SpokaPilote\driver-policy.json
```

ou dans `settings.json` : `"policySource": "\\\\srv\\deploiement$\\SpokaPilote\\driver-policy.json"`.
Donnez aux postes (comptes ordinateur, `Ordinateurs du domaine`) un accès **en lecture seule** au partage ; seul
l'IT peut le modifier. Les variables d'environnement sont développées (`%LOGONSERVER%\netlogon\driver-policy.json`).

### 2. URL HTTPS (intranet, stockage web, dépôt Git interne)

```powershell
spokapilote config set PolicySource https://intranet.exemple.fr/spokapilote/driver-policy.json
```

HTTPS est obligatoire (HTTP n'est accepté que pour `localhost` / `127.0.0.1`, pour les essais). Le proxy système est
utilisé. Taille maximale : 2 Mo.

### 3. Console centrale SpokaPilote Server

```json
{ "serverUrl": "https://spokapilote.exemple.fr", "serverToken": "<jeton d'enrôlement>" }
```

Le poste appelle `GET {ServerUrl}/api/policy` avec l'en-tête `Authorization: Bearer {ServerToken}` et
`Accept: application/json`. Contrat attendu de la console :

| Réponse | Signification pour le poste |
|---|---|
| `200` + JSON | Politique au format ci-dessus |
| `204 No Content` | Aucune politique pour ce poste : l'éventuelle politique en cache est **oubliée** |
| `401` / `403` | Jeton refusé : politique en cache conservée, avertissement |
| autre / injoignable | Politique en cache conservée, avertissement |

Le jeton n'est envoyé qu'à l'hôte de `ServerUrl` (jamais à une autre URL indiquée dans `PolicySource`).

Console 4.0 : la politique servie peut contenir en tête les retenues de la **diffusion par anneaux** (voir
[Anneaux de déploiement et suspension automatique](#anneaux-de-déploiement-et-suspension-automatique)) ; le poste
envoie alors aussi son identifiant (`X-SpokaPilote-Machine-Id`, `X-SpokaPilote-Machine`) à la console, et à elle seule.

### Hors ligne et robustesse

- Chaque politique valide reçue est copiée dans `%ProgramData%\SpokaPilote\policy-cache.json` (contenu brut, origine
  et date). Partage injoignable, console en panne, portable hors du réseau ou fichier invalide : le poste applique
  la **dernière politique valide** et l'affiche (« Dernière politique valide conservée : « Parc Siège » v3 (cache du
  25/11/2026 08:12) »).
- Sans cache : seule la liste de référence s'applique, avec un avertissement. L'analyse n'échoue jamais à cause de
  la politique.
- Si aucune source n'est plus configurée, le cache est ignoré.
- Délai maximal de lecture : 15 s.

### Suivre l'application sur le parc

Chaque rapport de poste (`--output-dir \\srv\rapports`, rapport JSON) contient :

```json
"policy": {
  "name": "Parc Siège", "version": "3", "updatedAt": "2026-11-25",
  "origin": "\\\\srv\\deploiement$\\SpokaPilote\\driver-policy.json", "fromCache": false,
  "organizationRules": 6, "referenceVersion": "2026.11.25", "referenceRules": 2,
  "blockedCandidates": 1, "hiddenCandidates": [], "messages": []
}
```

et, pour chaque pilote, `blockedReason` s'il est bloqué. Le rapport HTML affiche la politique appliquée et le motif
de chaque blocage. `fromCache: true` ou des `messages` signalent un poste qui n'a pas pu lire la politique à jour.

## Anneaux de déploiement et suspension automatique

Avec la console centrale (4.0), une nouvelle version de pilote n'arrive plus sur tout le parc le jour où Windows Update
ou le constructeur la publie : elle est **diffusée par anneaux**, comme les mises à jour de Windows Update for Business
ou d'Intune. Les postes pilotes la reçoivent d'abord ; les autres attendent quelques jours, et éventuellement qu'elle ait
fait ses preuves dans l'anneau précédent. Si elle échoue trop, le **disjoncteur** la suspend sur tout le parc.

Tout se règle dans la console, page **Déploiement** (voir [CONSOLE-CENTRALE.md](CONSOLE-CENTRALE.md), § 5) ; il n'y a
rien à écrire dans `driver-policy.json`. Les postes 4.0 demandent à la console une décision pour chacune de leurs
propositions (`{ServerUrl}/api/rollout/decide`) ; les postes 3.x reçoivent les retenues avec leur politique, par
`{ServerUrl}/api/policy` (voir « Ce que reçoit le poste »).

### Anneaux

Un anneau a un **nom**, un **délai** (jours), des **groupes de postes** et, facultativement, une **validation** exigée
dans l'anneau précédent. Suggestion de départ (modifiable) :

| Anneau | Délai | Groupes | Validation |
|---|---|---|---|
| Pilote | 0 jour | « Informatique », volontaires | — |
| Précoce | 3 jours | un échantillon de chaque modèle | (facultatif) 3 installations réussies dans « Pilote », aucun échec |
| Général | 10 jours | le reste (anneau par défaut) | (facultatif) 5 installations réussies dans « Précoce », aucun échec |

- **Première détection** d'une version : date de **réception par la console** du premier rapport (de n'importe quel poste)
  qui la **propose** — même si elle y est bloquée ou retenue. Une version est identifiée par sa source, son titre et son
  numéro de version (« Windows Update · Intel - Display - 31.0.101.5590 · 31.0.101.5590 »). La date est enregistrée à
  la réception du rapport (aucun recalcul sur l'historique) et ne change plus ensuite ; l'horloge du poste n'est pas
  utilisée (un ancien rapport renvoyé plus tard ne peut pas l'avancer). Les propositions sans numéro de version lisible
  ne sont pas soumises aux anneaux.
- Une version est **disponible pour un anneau** à l'instant *première détection + délai* (délai × 24 h, calculé en temps
  universel : ni le fuseau de la console, ni celui du poste, ni le changement d'heure ne le décalent ; les dates sont
  affichées à l'heure locale). Si l'anneau exige une **validation**, il faut en plus, dans l'anneau précédent, au moins
  *N* postes où la version est installée avec succès et au plus *M* postes en échec (*N* et *M* réglés sur l'anneau qui
  l'exige ; un poste compte une fois, avec son dernier résultat). La validation est réévaluée en permanence : un échec qui
  apparaît dans l'anneau précédent retire l'autorisation aux anneaux suivants (les postes déjà installés ne sont pas touchés).
- **Anneau d'un poste** : celui de ses groupes (ajouts à la main et règles automatiques). Un poste membre de groupes de
  **plusieurs anneaux reçoit le plus tardif** — règle prudente : un poste ajouté par erreur au groupe pilote ne reçoit rien
  plus tôt s'il appartient aussi à un groupe d'un anneau plus tardif (pour qu'un poste soit pilote, il ne doit être dans
  aucun groupe d'un anneau suivant). Un poste **sans groupe d'anneau** reçoit l'**anneau par défaut** (le dernier, sauf
  réglage contraire) ; un poste encore inconnu de la console aussi.
- Les **pilotes manquants** (périphérique sans pilote) ne sont pas soumis aux délais par défaut (réglage « Soumettre aussi
  les pilotes manquants »), comme pour `minimumDriverAgeDays`.
- **Diffuser partout maintenant** (Administrateur) autorise une version dans tous les anneaux sans attendre (correctif
  urgent) ; une suspension reste prioritaire. Une règle `allow` de la politique de l'organisation **ne lève pas** une
  retenue d'anneau : les règles de diffusion sont évaluées avant celles de l'organisation.

### Ce que reçoit le poste

Le coût des anneaux pour un poste est proportionnel à **ses** propositions, pas au catalogue de pilotes du parc (un parc
hétérogène de dizaines de milliers de postes voit des milliers de familles et des dizaines de milliers de versions en
cours de diffusion) :

- un **poste 4.0** demande à la console une **décision pour chacune des propositions** que ses sources ont trouvées
  (quelques dizaines) ; sa politique ne contient que les règles de l'organisation et les suspensions ;
- un **poste 3.x** reçoit des règles ordinaires du format 1, dans un document **borné** (2 000 règles et 1 Mo par
  défaut), plus prudent au besoin, mais jamais plus permissif.

#### Postes 4.0 : décisions par proposition

En demandant sa politique, le poste s'identifie et l'annonce (`X-SpokaPilote-Rollout-Decisions: 1`) : la console lui
sert la politique de l'organisation, les suspensions (règles `anneau-suspendu-N`) et la section `rollout` avec
`"decisions": true` — quelques Ko quel que soit le parc. Une fois les pilotes trouvés, **avant** d'appliquer la politique,
le poste envoie la liste de ses propositions (source, titre, version, type ; compressée) à `POST /api/rollout/decide` et
reçoit, dans le même ordre, une décision par proposition :

| Décision | Quand | Sur le poste |
|---|---|---|
| `suspended` | version suspendue sur tout le parc (même source, même titre sans tenir compte de la casse, même version numérique) — tous anneaux, tous types, pilotes manquants compris | bloquée (⏳ retenue), motif de la suspension |
| `held` | version **connue** de la console, pas encore autorisée pour l'anneau du poste | retenue, avec la date de disponibilité (heure locale) si elle ne dépend que du délai |
| `unknown` | version **jamais vue** par la console, si l'anneau retient les nouveautés (délai ou validation) : famille inconnue, version plus récente que la dernière autorisée de sa famille (même source, même plage de version majeure), ou famille sans version autorisée | retenue jusqu'à ce qu'un rapport la signale |
| `allowed` | tout le reste : version autorisée pour l'anneau, version inconnue inférieure à la dernière autorisée de sa famille, anneau sans délai (pilote), pilote manquant (sauf réglage), version sans numéro lisible, anneaux désactivés | les règles de l'organisation s'appliquent ensuite normalement |

Les décisions sont évaluées **avant** les règles de l'organisation (une règle `allow` ne lève pas une retenue ; un blocage
de l'organisation d'une version `unknown` reste affiché comme tel). Elles suivent exactement les règles de la politique
complète (prouvé par un test qui compare, sur des milliers de propositions, les décisions à la politique lue par
l'analyseur exact de la 3.1), avec une différence volontaire : une **famille jamais vue** est retenue (`unknown`) pour un
poste 4.0. **Demander une décision n'enregistre rien** : la première détection reste la réception du premier *rapport* qui
propose la version (un poste pilote, en général), après quoi elle est `held` avec sa date pour les anneaux suivants.

Taille et temps mesurés (§ « Mesures » ci-dessous) : 20 à 30 propositions = 2 à 3,5 Ko envoyés (0,6 à 0,9 Ko compressés),
4 à 6,5 Ko reçus (1 à 1,4 Ko compressés), quelques millisecondes côté console, quelques millisecondes côté poste.

#### Postes 3.x (et postes 4.0 qui ne demandent pas de décisions)

La console ajoute **en tête** des règles de la politique de l'organisation des règles ordinaires du format 1 (identifiants
`anneau-…`), recalculées pour l'anneau du poste :

1. `anneau-suspendu-N` : `block` d'une version suspendue (tous les anneaux, tous les types de proposition) ;
2. `anneau-retenue-N` : `block` d'une version **en cours de diffusion** pas encore autorisée pour l'anneau, avec un motif
   lisible : « Pilote Intel - Display - 31.0.101.5600 : disponible pour votre anneau « Général » le 08/12/2026 à 09:00 » ;
3. `anneau-famille-N` : une règle par **famille** de pilotes (titre dont les numéros de version sont remplacés par `*`,
   par ex. « Intel - Display - * », par plage de version majeure) : `pin` à la version la plus récente autorisée pour
   l'anneau, ou `block` si aucune ne l'est encore. Une version **plus récente que toutes celles que la console connaît**
   est donc retenue elle aussi : c'est ce qui protège le parc quand des milliers de postes analysent à la même heure le
   jour d'une publication, avant que le premier rapport ne l'ait signalée à la console.

Seules les versions en cours de diffusion sont listées (une version autorisée partout disparaît des règles ; une famille
est une règle par plage de version majeure quel que soit le nombre de ses versions ; une famille plus vue depuis un an
n'est plus listée).

**Document borné.** Les postes refusent une politique de plus de 2 Mo (ils garderaient alors leur politique précédente,
sans aucune retenue récente) et la lecture d'une politique de *n* règles coûte de l'ordre de *n²* comparaisons sur un
poste 3.x. La console ne dépasse donc jamais `SPOKAPILOTE_ROLLOUT_MAX_RULES` règles de diffusion (2 000) ni
`SPOKAPILOTE_ROLLOUT_MAX_KB` (1 Mo), et remplit ce budget par ordre de priorité :

1. les **suspensions** (au-delà d'un quart du budget, une règle par source qui bloque ces numéros de version quel que soit
   le titre) ;
2. les règles **indispensables** : règles de famille des familles en cours de diffusion (`pin` à la dernière version
   autorisée, ou `block` si aucune ne l'est), et règle datée des seules versions retenues **inférieures** au plafond de leur
   famille (que le `pin` laisserait passer). Ensemble, elles bloquent **toute** version connue pas encore autorisée ;
3. si ces règles ne tiennent pas, les sources les plus chargées sont **retenues en bloc** (`anneau-source-WindowsUpdate` :
   `block`, `source`, `title: "*"`, types de mise à jour) — toutes leurs mises à jour attendent, avec un motif clair, jusqu'à
   ce que leurs règles tiennent à nouveau (une source non reconnue ne peut être visée que par une retenue de toutes les
   sources, choisie en dernier) ;
4. les règles de famille des **diffusions terminées** (les plus récemment vues d'abord) : protection contre une version
   plus récente jamais vue ;
5. les règles datées des autres versions retenues (motif avec la date de disponibilité).

Forme obtenue (`legacy.form` dans l'aperçu, `rollout.form` pour un poste 4.0 sans décisions) : **complète** (petit parc :
le document est le même qu'avant, une règle datée par version retenue), **compacte** (versions retenues couvertes par les
règles de famille, motif sans date ; familles terminées en partie omises) ou **par source** (en plus, des sources retenues
en bloc). Compromis assumé : un poste 3.x d'un très grand parc peut attendre plus longtemps que nécessaire une mise à jour
déjà autorisée (forme « par source »), ou ne plus retenir une version jamais vue d'une famille terminée omise (comme une
famille inconnue) — mais il **n'installe jamais une version connue de la console qui n'est pas autorisée pour l'anneau le
plus prudent**, ni une version suspendue (prouvé par des tests qui lisent la politique servie avec l'analyseur exact de la
3.1, jusqu'à 10 000 versions et des plafonds de 300 règles / 200 Ko). Mettez les postes en 4.0 : ils ne dépendent plus de
ce plafond.

**Section `rollout` (postes 4.0)** : en demandant leur politique, ils s'identifient (en-têtes `X-SpokaPilote-Machine-Id`,
identifiant Windows, et `X-SpokaPilote-Machine`, nom) et demandent la section `rollout` (`X-SpokaPilote-Rollout: 1`) :
anneau, délai, validation exigée, dates de disponibilité (affichées à l'heure locale du poste), consigne de retenir aussi
une version d'une **famille jamais vue** par la console (nouveau matériel), tant qu'un rapport ne l'a pas signalée, et
`decisions: true` si la console décide proposition par proposition. La section est lue avec tolérance (une console plus
récente peut y ajouter des informations). Une console 4.0 antérieure ignore l'en-tête `X-SpokaPilote-Rollout-Decisions` :
le poste applique alors les retenues de sa politique, comme avant.

**Postes 3.x (compatibilité)** : ils refusent toute propriété inconnue d'une politique — c'est pourquoi la section
`rollout` ne leur est **jamais** envoyée (elle ne l'est que sur demande) : ils reçoivent uniquement des règles `block` /
`pin` qu'ils comprennent déjà, et les appliquent à l'identique (prouvé par un test qui lit la politique servie avec
l'analyseur exact de la 3.1.0). Ils ne s'identifient pas en lisant leur politique : la console leur sert celle de
l'**anneau le plus prudent** (une version ne leur est autorisée que lorsqu'elle l'est pour tous les anneaux). Mettez les
postes pilotes à jour en 4.0 pour qu'ils reçoivent leur propre anneau. Limite : un poste 3.x ne retient pas une version
d'une famille jamais vue par la console (nouveau matériel) avant le premier rapport qui la signale.

**Hors ligne** : un poste qui ne joint pas la console applique la dernière politique reçue (cache), retenues comprises ;
les retenues datées n'expirent pas d'elles-mêmes (la console peut avoir suspendu la version entre-temps) : elles sont
levées au retour de la console. Un poste 4.0 garde aussi ses **dernières décisions** (`rollout-decisions.json`, à côté de
`policy-cache.json` : 5 000 propositions au plus, 180 jours, décisions d'une autre console ignorées). Console injoignable
(au chargement de la politique ou à la demande de décisions), réponse invalide ou statut inconnu : chaque proposition
reçoit sa dernière décision connue ; une proposition **sans décision connue est retenue** si l'anneau retient les
nouveautés (« décision de diffusion indisponible… jusqu'au retour de la console ») — jamais « tout autoriser » faute de
réponse. Les suspensions, présentes dans la politique en cache, s'appliquent toujours.

#### Mesures

Console de test (Docker, poste de développement) alimentée par `tools/LoadTest` (`seed --history 3`, `groups`, puis
`rollout --setup` : anneaux suggérés, groupes rattachés, diffusion activée), deux parcs : **réaliste** (`--fleet realistic` :
50 modèles, 1 500 familles de pilotes partagées par les modèles, une centaine de nouvelles versions par semaine, ~20
propositions par poste) et **extrême** (propositions synthétiques toutes différentes). Avant = console 4.0 précédente
(liste des retenues de tout le parc dans chaque politique), après = cette version.

| Parc | Versions en diffusion | Politique 3.x avant → après | Politique 4.0 avant → après | Poste 4.0 : lecture + application avant → après |
|---|---|---|---|---|
| réaliste, 3 000 postes | 2 673 | 1,7 Mo, 4 264 règles → 711 Ko, 2 000 règles (compacte) | 1,6 Mo → 1,8 Ko + décisions | 4,1 s → 1 ms (+ demande de décisions 2 à 13 ms) |
| réaliste, 20 000 postes | 3 117 | 1,9 Mo, 4 718 règles (au bord du refus à 2 Mo) → 711 Ko, 2 000 règles | 1,7 Mo → 5 Ko + décisions | 3,5 s → 1 ms (+ décisions p50 1,9 ms) |
| extrême, 3 000 postes | 24 086 | 14,6 Mo, 37 928 règles (**refusée** par les postes : > 2 Mo) → 645 o (toutes sources retenues) | 13,7 Mo (refusée ; lecture : 310 s) → 0,5 Ko + décisions | 310 s → 10 à 30 ms de bout en bout (politique + décisions + application, 30 propositions) |
| extrême, 20 000 postes | 160 022 | (non mesuré avant : ~100 Mo) → 645 o | → 0,5 Ko + décisions | — |

Demande de décisions (`POST /api/rollout/decide`, 200 postes) : parc réaliste, 20 000 postes, 20 propositions : p50
1,9 ms, p95 8,1 ms, 0,6 Ko envoyés / 1,2 Ko reçus (compressés) ; parc extrême, 30 propositions : p50 1,1 ms, p95 1,8 ms ;
160 000 versions : p50 1,4 ms, p95 5,6 ms (index des décisions construit en 0,5 s au premier appel après chaque recalcul de
l'état de la diffusion). Avant, chaque poste du parc extrême téléchargeait 0,7 Mo compressés (14 Mo décompressés) qu'il
refusait, et gardait sa politique précédente — sans aucune retenue récente. Mémoire de la console (parc extrême, 3 000
postes) : 2,9 Go → 0,7 Go. Page Déploiement (160 000 versions) : liste paginée p50 1,6 ms, recherche p50 26 ms. Un poste sans console (fichier, partage, URL) ne connaît pas les anneaux : rien ne change
pour lui.

`spokapilote policy show` affiche l'anneau du poste, son délai, la validation exigée, les versions suspendues et les
versions retenues avec leur date (« Pilote X 31.0.101.5590 : disponible pour votre anneau « Général » le 08/12/2026 à
09:00 »). `policy test`, `scan` et l'interface séparent les pilotes **retenus** (⏳, non sélectionnables, jamais installés)
des pilotes bloqués par la politique ; le rapport JSON indique `policy.rolloutRing` et `policy.rolloutHeldCandidates`.

### Suspension automatique (disjoncteur)

À chaque rapport qui signale un échec d'installation d'une version, la console compte, **anneau par anneau**, sur une
fenêtre glissante, les postes qui ont tenté de l'installer et ceux en échec. Si un anneau atteint **les deux seuils** —
par défaut **au moins 3 postes en échec et au moins 20 % des tentatives en 48 heures** (réglables) — la version est
**suspendue sur tout le parc** : règle `block` servie à tous les postes (motif « suspendu automatiquement : 3 échecs sur
7 installations dans l'anneau « Pilote » en 48 h »), alerte « Pilote suspendu automatiquement » sur les canaux configurés
(une par version et par 24 h), entrée du journal d'audit (auteur « Disjoncteur automatique »), pastille rouge dans le menu
de la console et encart sur la vue d'ensemble.

- **Reprendre** (Technicien) : la version redevient installable selon les anneaux ; seuls les échecs **postérieurs** à
  la reprise comptent ensuite pour le disjoncteur.
- **Maintenir le blocage** (Technicien) : la suspension reste, elle n'est plus signalée comme à examiner.
- **Suspendre ce pilote** (Technicien) : suspension manuelle d'une version, avec un motif.
- Chaque poste compte une fois par version et par anneau (son dernier résultat) : un poste qui échoue à chaque passage ne
  déclenche pas seul le disjoncteur ; le même rapport reçu deux fois n'est compté qu'une fois.

**Régressions** (réglage « compter comme un échec ») : un périphérique en erreur dans le rapport qui suit une installation
réussie (dans les 7 jours), avec **comme pilote installé la version installée**, compte comme un échec de cette version
(« Régression : « NVIDIA RTX A2000 » en erreur après l'installation (code 43) »), une fois par installation. Ce que les
rapports ne permettent pas de voir : écrans bleus et redémarrages en boucle (le poste n'envoie plus de rapport), lenteurs,
périphérique qui fonctionne mal sans être en erreur, périphérique en erreur avec un autre pilote que celui installé.

### Limites

- Le regroupement en **familles** repose sur le titre publié par la source : deux pilotes différents au titre identique
  (hors numéros) sont dans la même famille, plafonnée à la plus récente version autorisée — une nouvelle version d'un
  pilote **inférieure** au plafond de sa famille n'est alors pas retenue avant le premier rapport qui la signale.
- Une version encore jamais proposée à un poste de l'anneau pilote n'y est jamais validée : si l'anneau suivant exige une
  validation, elle y reste retenue (« en attente de validation ») jusqu'à une décision (« Diffuser partout »).
- L'identité envoyée par un poste n'est pas prouvée (le jeton d'enrôlement est commun) : un poste peut se déclarer membre
  de l'anneau pilote. Les anneaux sont une protection contre les accidents, pas contre un poste malveillant.
- Les installations comptent dans l'anneau du poste **au moment du rapport** ; un poste qui change d'anneau garde ses
  résultats dans l'ancien.
- Décisions (postes 4.0) : la famille d'une version inconnue est celle de **son** titre (numéros remplacés par « * »),
  pas toute famille dont le motif lui correspondrait ; une version inconnue inférieure au plafond de sa famille est
  autorisée (comme avec la règle `pin`). Une analyse qui trouve plus de 1 000 propositions retient les suivantes.
- Postes 3.x d'un très grand parc : forme compacte ou par source (voir « Document borné ») ; aucun plafond pour les postes
  4.0 en mode décisions.

## Liste de référence SpokaPilote

SpokaPilote intègre une liste noire de **versions de pilotes signalées publiquement** par Microsoft ou par les
constructeurs (`src/SpokaPilote.Core/Policy/known-bad-drivers.json`, même format). Elle est ajoutée **après** les
règles de l'organisation, qui restent prioritaires (`useReferenceBlocklist`, `ignoreReferenceRules`).

Elle est rafraîchie au plus une fois par jour depuis
`https://raw.githubusercontent.com/Spokator/SpokaPilote/main/src/SpokaPilote.Core/Policy/known-bad-drivers.json`
(copie dans `%ProgramData%\SpokaPilote\Cache`) ; la copie téléchargée n'est retenue que si elle est valide et au
moins aussi récente (`updatedAt`) que la liste intégrée. Ce domaine ne sert qu'à ce fichier de métadonnées : il ne
fait pas partie des domaines autorisés pour télécharger des pilotes. Sans accès à Internet, la liste intégrée
s'applique.

Contenu actuel (vérifié sur les avis officiels) :

| Règle | Versions bloquées | Source |
|---|---|---|
| `ref-intel-sst-intcaudiobus-5152` | Intel Smart Sound Technology (IntcAudioBus.sys) 10.29.0.5152 et 10.30.0.5152 — écrans bleus sous Windows 11 (Intel Core 11e génération) | [Microsoft, Windows release health](https://learn.microsoft.com/en-us/windows/release-health/known-issues/known-issue-2746) |
| `ref-intel-sst-intcoed-10.24` | Intel SST OED (IntcOED.sys) 10.24.0.2863, .3316, .3488, .3633, .3694, .3851, .3950 | [Microsoft, KB5078895](https://support.microsoft.com/en-us/topic/kb5078895-compatibility-issues-with-intel-smart-sound-technology-drivers-on-windows-11-f2da4985-f4b0-47b0-b907-85356d8eda1a) |

Contribuer : n'ajouter que des versions documentées par un avis public officiel (lien dans `sourceUrl`), avec un
ciblage précis (versions exactes ou plage + fabricant / ID matériel), un `reason` factuel en français, et mettre à
jour `updatedAt` ; `spokapilote policy check` doit rester sans erreur (un test automatique le vérifie aussi).

## Limites connues

- La politique s'applique aux pilotes proposés par l'analyse (interface, `scan`, `install`). La préparation d'un pack
  hors ligne (`spokapilote pack`) ne la consulte pas, mais les pilotes du pack sont de nouveau évalués lors de
  l'installation sur le poste cible (`install --repo`).
- Les critères portent sur les informations publiées par la source (titre, version, date, fabricant) et sur
  l'inventaire du poste ; un constructeur qui republie la même version sous un autre numéro n'est pas reconnu.
- `minimumDriverAgeDays` utilise la date du pilote (souvent antérieure à sa date de mise en ligne) : le recul réel
  peut donc être plus court que prévu pour Windows Update et le catalogue Microsoft.
