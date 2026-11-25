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
