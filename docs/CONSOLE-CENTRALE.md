# Console centrale SpokaPilote Server

SpokaPilote Server est une console web auto-hébergée : chaque poste enrôlé y envoie le rapport de ses analyses et
installations, et l'équipe IT suit le parc en temps réel depuis un navigateur.

- **Vue d'ensemble** : postes sains / à traiter, postes non vus depuis 7 jours, matériel sans pilote, pannes
  matérielles, pilotes bloqués par la politique, échecs d'installation, tâches à distance, santé moyenne du parc.
- **Postes** : liste filtrable et triable ; fiche détaillée (système, santé, périphériques en erreur, pilotes proposés
  ou bloqués, résultats d'installation, historique des derniers rapports, téléchargement du rapport complet pour
  `spokapilote pack`).
- **Actions à distance** (3.0) : lancer une analyse, une installation, le déploiement des imprimantes, une annulation
  ou une mise à jour sur un poste, une sélection de postes ou tous les postes à traiter ; vue **Tâches** (§ 5).
- **Alertes** (3.0) : e-mail et webhooks Microsoft Teams, Discord, Slack ou JSON, page **Paramètres** (§ 6).
- **Groupes de postes** (3.1) : par site, service ou matériel, à la main et/ou par règles automatiques ; vue d'ensemble
  et liste des postes filtrées par groupe ; action sur tout un groupe (§ 5).
- **Actions planifiées** (3.1) : une action de la liste fermée, chaque jour ou certains jours, sur un groupe ou tout
  le parc, avec fenêtre de maintenance facultative (§ 5).
- **Comptes nominatifs et rôles** (3.1) : Administrateur, Technicien, Lecture seule ; connexion par identifiant et mot
  de passe (§ 2).
- **Journal d'audit** (3.1) : qui a fait quoi et quand, exportable en CSV (§ 7).
- **Matériel sans pilote** : agrégé par ID matériel sur tout le parc, exportable en CSV.
- **Politique de pilotes** et **profil d'imprimantes** : éditeurs JSON (validation, enregistrement) ; les postes les
  récupèrent avec leur jeton.
- **Enrôlement** : commandes et script prêts à copier pour connecter les postes.
- **Déploiement** (4.0) : diffusion des nouvelles versions de pilotes par **anneaux** (Pilote, Précoce, Général…) avec
  délai et validation, et **suspension automatique** sur tout le parc d'une version qui échoue trop (§ 5).
- **Grands parcs** (4.0) : listes paginées et filtrées par le serveur, indicateurs précalculés, rapports compressés,
  afflux de postes maîtrisés (`503` + `Retry-After`), supervision (`/api/metrics`, Prometheus), sauvegarde à chaud ;
  mesuré sur 20 000 postes × 20 rapports (§ 11).
- **Cache de pilotes et de catalogues** (4.0) : les postes demandent à la console les fichiers officiels (pilote NVIDIA
  de 1 Go, paquets Dell, HP, Lenovo, Microsoft Update Catalog) et les catalogues ; un seul téléchargement Internet pour
  tout le parc, plus de limitation du Microsoft Update Catalog pour des milliers de postes derrière une même adresse
  IP. La console n'est qu'un transport : chaque poste revérifie tout comme pour un téléchargement direct (§ 12).

Le serveur est un unique conteneur Linux (ASP.NET Core, .NET 10) avec une base SQLite dans un volume. Aucune base de
données externe, aucun service tiers.

## 1. Installation avec Docker

Prérequis : Docker (ou Docker Desktop) sur un serveur Linux ou Windows, et — pour la production — un nom DNS et un
certificat HTTPS (voir § 4).

Depuis la racine du dépôt :

```bash
docker compose -f server/docker-compose.yml up -d --build
docker compose -f server/docker-compose.yml logs spokapilote     # jetons générés au premier démarrage
```

Ou sans Compose :

```bash
docker build -f server/Dockerfile -t spokapilote-server .
docker run -d --name spokapilote-server --restart unless-stopped \
  -p 8080:8080 -v spokapilote-data:/data spokapilote-server
```

La console répond sur `http://<serveur>:8080`. L'image :

- est construite en plusieurs étapes (`sdk:10.0` → `aspnet:10.0`) et s'exécute sous l'utilisateur non root `app`
  (UID 1654) ;
- écoute sur le port **8080** ;
- conserve toutes ses données dans le volume **`/data`** (`spokapilote.db`, `admin-token.txt`, `agent-token.txt`,
  `backups/`, et `cache/` pour le cache de pilotes, § 12) ;
- déclare un `HEALTHCHECK` sur `GET /api/health` (état visible dans `docker ps`) ;
- fonctionne avec un système de fichiers racine en lecture seule (`read_only: true` dans le fichier Compose).

> Montage d'un dossier de l'hôte au lieu d'un volume nommé (ex. `-v /srv/spokapilote:/data`) : le dossier doit
> appartenir à l'UID 1654 (`sudo chown -R 1654:1654 /srv/spokapilote`).

### Variables d'environnement

| Variable | Défaut | Rôle |
|---|---|---|
| `SPOKAPILOTE_ADMIN_TOKEN` | généré | Jeton administrateur (console web et API d'administration) |
| `SPOKAPILOTE_AGENT_TOKEN` | généré | Jeton d'enrôlement des postes (envoi des rapports, lecture de la politique) |
| `SPOKAPILOTE_HISTORY` | `20` | Nombre de rapports conservés par poste |
| `SPOKAPILOTE_STALE_DAYS` | `7` | Un poste sans rapport depuis ce délai est signalé « non vu » (`0` = jamais) |
| `SPOKAPILOTE_MAX_REPORT_MB` | `5` | Taille maximale d'un rapport, après décompression |
| `SPOKAPILOTE_JOB_EXPIRY_DAYS` | `7` | Une tâche à distance jamais récupérée par son poste expire après ce délai |
| `SPOKAPILOTE_JOB_TIMEOUT_HOURS` | `6` | Une tâche « en cours » sans résultat après ce délai est déclarée expirée |
| `SPOKAPILOTE_AUTH_FAILURES_PER_MINUTE` | `10` | Jetons ou connexions refusés tolérés par adresse IP et par minute, ensuite réponse 429 (`0` = pas de limite) |
| `SPOKAPILOTE_SESSION_HOURS` | `8` | (3.1) Session de la console fermée après ce nombre d'heures d'inactivité (1 à 168) ; 7 jours au plus dans tous les cas |
| `SPOKAPILOTE_AUDIT_MAX_ENTRIES` | `5000` | (3.1) Journal d'audit : nombre maximal d'entrées conservées (100 à 1 000 000) |
| `SPOKAPILOTE_AUDIT_DAYS` | `180` | (3.1) Journal d'audit : ancienneté maximale des entrées, en jours |
| `SPOKAPILOTE_HISTORY_DAYS` | `0` | (4.0) Historique : rapports de plus de N jours supprimés (le dernier de chaque poste est gardé) ; `0` = pas de limite d'âge (seulement `SPOKAPILOTE_HISTORY`) |
| `SPOKAPILOTE_MAX_CONCURRENT_UPLOADS` | nb de processeurs (2 à 16) | (4.0) Rapports traités en même temps ; les suivants attendent leur tour (§ 11) |
| `SPOKAPILOTE_UPLOAD_QUEUE` | `200` | (4.0) Rapports en attente au plus ; au-delà, ou après 20 s d'attente, réponse `503` + `Retry-After` |
| `SPOKAPILOTE_MAX_CONCURRENT_AGENTS` / `SPOKAPILOTE_AGENT_QUEUE` | `32` / `1000` | (4.0) Idem pour les interrogations des agents et les résultats des tâches |
| `SPOKAPILOTE_RETRY_AFTER_SECONDS` | `60` | (4.0) Délai conseillé aux postes refusés, étalé au hasard entre la moitié et une fois et demie |
| `SPOKAPILOTE_BACKUP_TIME` | — | (4.0) Heure de la sauvegarde automatique quotidienne (`03:00`, fuseau `TZ`) ; vide = pas de sauvegarde automatique |
| `SPOKAPILOTE_BACKUP_KEEP` | `7` | (4.0) Sauvegardes conservées dans `/data/backups` |
| `SPOKAPILOTE_CACHE_ENABLED` | `true` | (4.0) Cache de pilotes et de catalogues (§ 12) ; une variable `SPOKAPILOTE_CACHE_*` définie verrouille le réglage dans la console |
| `SPOKAPILOTE_CACHE_MAX_GB` | `20` | (4.0) Taille maximale du cache ; au-delà, les fichiers les moins récemment demandés sont supprimés |
| `SPOKAPILOTE_CACHE_MIN_FREE_GB` | `10` | (4.0) Espace toujours laissé libre sur le volume (et au moins 5 % du volume) ; en dessous, plus rien n'est mis en cache |
| `SPOKAPILOTE_CACHE_CATALOGS` | `true` | (4.0) Relais des catalogues (Microsoft Update Catalog, Dell, HP, Lenovo, NVIDIA) |
| `SPOKAPILOTE_CACHE_CATALOG_HOURS` | `12` | (4.0) Conservation d'un catalogue avant de le redemander à la source (1 à 168) |
| `SPOKAPILOTE_CACHE_FILE_DAYS` | `30` | (4.0) Un fichier plus ancien est retéléchargé à la demande suivante (même contenu : aucun doublon) |
| `SPOKAPILOTE_CACHE_MAX_DOWNLOADS` | `8` | (4.0) Téléchargements simultanés au plus vers les sources ; au-delà, les postes téléchargent directement |
| `SPOKAPILOTE_CACHE_DIR` | `/data/cache` | (4.0) Dossier du cache (par ex. un second volume) |
| `SPOKAPILOTE_ROLLOUT_MAX_RULES` | `2000` | (4.0) Règles de diffusion par anneaux au plus dans la politique d'un poste 3.x (ou 4.0 sans décisions) ; au-delà, forme compacte (voir [POLITIQUE.md](POLITIQUE.md#ce-que-reçoit-le-poste)) ; 50 à 20 000 |
| `SPOKAPILOTE_ROLLOUT_MAX_KB` | `1024` | (4.0) Taille maximale de cette politique, en Ko (64 à 1 900 : les postes refusent une politique de plus de 2 Mo) |
| `HTTPS_PROXY`, `NO_PROXY` | — | (4.0) Proxy sortant de la console vers les sources officielles (Compose : `SPOKAPILOTE_HTTPS_PROXY`, `SPOKAPILOTE_NO_PROXY`) |
| `SPOKAPILOTE_PORT` | `8080` | (Compose) port publié sur l'hôte ; `127.0.0.1:8080` derrière un reverse proxy |
| `SPOKAPILOTE_BEHIND_PROXY` | `false` | (Compose) `true` derrière un reverse proxy : adresse réelle des postes (§ 4) |
| `SPOKAPILOTE_DOMAIN` | — | (Compose, profil `https`) nom DNS public de la console pour le certificat automatique (§ 4) |
| `SPOKAPILOTE_SMTP_*`, `SPOKAPILOTE_WEBHOOK_*`... | — | Alertes : voir § 6 |
| `SPOKAPILOTE_DATA_DIR` | `/data` | Dossier des données (à ne changer qu'hors conteneur) |

Avec Compose, placez-les dans un fichier `server/.env` (non versionné), par exemple :

```dotenv
SPOKAPILOTE_ADMIN_TOKEN=...
SPOKAPILOTE_AGENT_TOKEN=...
SPOKAPILOTE_STALE_DAYS=14
```

## 2. Jetons, comptes et rôles

La console utilise deux jetons, transmis dans l'en-tête `Authorization: Bearer <jeton>` et comparés en temps constant,
et — depuis la 3.1 — des **comptes nominatifs** pour l'équipe (§ 2.1) :

| Jeton | Qui l'utilise | Ce qu'il permet |
|---|---|---|
| **Administrateur** | l'équipe IT (console web, scripts d'administration) | tout : postes, synthèse, suppression, politique, profil d'imprimantes, actions à distance, alertes |
| **Enrôlement** | chaque poste (`ServerToken`) | envoyer des rapports, lire la politique et le profil d'imprimantes, récupérer **ses** tâches à distance et en renvoyer le résultat |

Le jeton d'enrôlement est copié sur tous les postes : il ne donne **aucun** accès à la console. Le jeton
administrateur ne doit jamais être déployé sur les postes (`spokapilote server status` le signale).

**Sans variable d'environnement**, chaque jeton est généré au premier démarrage (256 bits aléatoires), écrit **une
seule fois** dans les journaux du conteneur et conservé dans le volume :

```bash
docker compose -f server/docker-compose.yml logs spokapilote | grep "Jeton"
docker exec spokapilote-server cat /data/admin-token.txt
docker exec spokapilote-server cat /data/agent-token.txt
```

Les deux jetons doivent être différents (le serveur refuse de démarrer sinon). Une valeur fournie par variable
d'environnement est prioritaire sur le fichier ; 24 caractères au moins sont recommandés
(ex. `openssl rand -base64 32`).

**Changer un jeton** : définissez la nouvelle valeur dans la variable correspondante (ou supprimez le fichier
`/data/*-token.txt` pour en générer un nouveau), puis redémarrez le conteneur. Après un changement du jeton
d'enrôlement, mettez à jour `ServerToken` sur les postes.

Dans la console web, le jeton administrateur n'est conservé que dans l'onglet du navigateur (`sessionStorage`) : il
disparaît à sa fermeture.

### 2.1 Comptes nominatifs et rôles (3.1)

Chaque membre de l'équipe se connecte avec **son** compte (identifiant et mot de passe) : l'historique des tâches et le
journal d'audit indiquent alors qui a fait quoi. Trois rôles :

| Rôle | Peut |
|---|---|
| **Administrateur** | Tout : comptes, paramètres et alertes, jeton d'enrôlement (page Enrôlement), politique de pilotes et profil d'imprimantes, suppression de postes, journal d'audit — plus tout ce que peut un technicien |
| **Technicien** | Consulter le parc ; lancer et annuler des actions à distance ; créer, modifier et supprimer des planifications et des groupes (membres compris) |
| **Lecture seule** | Consulter : vue d'ensemble, postes, groupes, tâches, planifications, matériel sans pilote, politique et profil d'imprimantes (sans les modifier) |

Le **jeton administrateur** garde tous les droits (rôle Administrateur) : scripts et API comme en 3.0, et secours si
plus aucun administrateur ne peut se connecter. Ses actions sont consignées sous le nom « Jeton administrateur ».

**Premier compte** : sur la page de connexion, choisissez **Jeton administrateur**, collez le jeton, puis ouvrez
**Comptes** › **Nouveau compte** et créez votre compte avec le rôle **Administrateur**. Déconnectez-vous, reconnectez-vous
avec ce compte (onglet **Compte**) et créez ceux de l'équipe. Tant qu'aucun compte n'existe, la vue d'ensemble le
rappelle.

- **Identifiant** : 2 à 64 caractères (lettres minuscules, chiffres, `.` `_` `-` `@`), insensible à la casse.
- **Mot de passe** : 10 caractères au moins, sans l'identifiant. Il n'est jamais conservé en clair : empreinte
  **PBKDF2-HMAC-SHA-256**, 310 000 itérations, sel aléatoire propre à chaque compte, comparaison en temps constant.
  Le bouton **Générer** propose un mot de passe aléatoire à transmettre par un canal sûr ; chacun change le sien dans
  **Mon compte** (ses autres sessions sont alors fermées).
- Un administrateur peut **désactiver** un compte, changer son rôle ou **réinitialiser** son mot de passe (les
  sessions ouvertes du compte sont fermées). Au moins un compte Administrateur actif doit rester tant qu'il existe des
  comptes ; personne ne peut supprimer, désactiver ou rétrograder son propre compte.
- **Session** : cookie `SpokaPilote.Session` aléatoire (256 bits) `HttpOnly`, `SameSite=Strict`, `Secure` en HTTPS,
  effacé à la fermeture du navigateur ; seule son empreinte est conservée en base. Fermée après 8 heures sans activité
  (`SPOKAPILOTE_SESSION_HOURS`, expiration glissante) et au plus tard 7 jours après la connexion.
- **Protection CSRF** : en plus de `SameSite=Strict`, toute requête qui modifie quelque chose (POST, PUT, DELETE) avec
  une session doit porter l'en-tête `X-SpokaPilote-CSRF` contenant le jeton anti-CSRF de la session (fourni par
  `POST /api/session` et `GET /api/session`) ; la console le fait d'elle-même. Les appels avec un jeton
  (`Authorization: Bearer`) ne sont pas concernés.
- **Tentatives de connexion** : limitées par adresse IP comme les jetons refusés (§ 7) ; la réponse ne dit jamais si
  le compte existe. Derrière un reverse proxy HTTPS, activez `SPOKAPILOTE_BEHIND_PROXY` (§ 4) : la console voit alors
  la requête en HTTPS (cookie `Secure`) et l'adresse réelle du navigateur.

## 3. Enrôlement des postes

La page **Enrôlement** de la console affiche l'adresse, le jeton d'enrôlement et les commandes prêtes à copier.
Sur un poste où SpokaPilote est installé, dans une console **administrateur** :

```powershell
spokapilote config set ServerUrl https://spokapilote.mon-entreprise.fr
spokapilote config set ServerToken <jeton-d-enrolement>
spokapilote server status        # teste l'adresse, le certificat HTTPS et le jeton
```

La configuration est enregistrée dans `%ProgramData%\SpokaPilote\settings.json`, commune à tout le poste : ce
fichier peut aussi être déployé directement par GPO ou Intune (clés `serverUrl` et `serverToken`).

Ensuite, **chaque `spokapilote scan` ou `spokapilote install` envoie son rapport** à la console (compressé). Un échec
d'envoi n'est jamais bloquant : l'analyse ou l'installation se termine normalement, avec un avertissement, et le
rapport local reste dans `%ProgramData%\SpokaPilote\Reports`. L'option `--no-upload` désactive l'envoi pour une
exécution.

Déploiement sur le parc (Intune « script de plateforme », RMM, GPO de démarrage), avec une analyse hebdomadaire :

```powershell
$exe = "$env:ProgramFiles\SpokaPilote\spokapilote.exe"
& $exe config set ServerUrl "https://spokapilote.mon-entreprise.fr"
& $exe config set ServerToken "<jeton-d-enrolement>"
& $exe server status
& $exe scan --quiet                   # premier rapport : le poste apparaît dans la console
& $exe agent enable --every 15        # actions à distance (§ 5)
schtasks /Create /F /TN "SpokaPilote\Analyse" /SC WEEKLY /D MON /ST 09:00 /RU SYSTEM /TR "\"$exe\" scan --quiet"
```

**Un poste renommé reste le même poste.** Depuis la 3.0, chaque rapport contient l'identifiant Windows du poste
(`MachineGuid`, clé `HKLM\SOFTWARE\Microsoft\Cryptography`). La console s'en sert en priorité : après un changement de
nom, la fiche, l'historique et les tâches suivent le poste sous son nouveau nom. Les postes encore en 2.x (rapports sans
identifiant) restent reconnus par leur nom et reçoivent l'identifiant à leur premier rapport 3.0. Une réinstallation de
Windows change l'identifiant : le poste reste reconnu par son nom.

**Reprise de l'existant** : les rapports déjà déposés sur un partage par `--output-dir` (ancienne méthode
`fleet-report`) s'importent depuis n'importe quel poste enrôlé :

```powershell
spokapilote server upload \\srv\rapports
```

Un rapport plus ancien que celui déjà connu pour un poste rejoint son historique sans remplacer son état actuel.

## 4. HTTPS (obligatoire en production)

Les postes **refusent** d'envoyer leurs rapports et leur jeton en HTTP, sauf vers `localhost` / `127.0.0.1` (essais).
Ils refusent aussi les redirections : `ServerUrl` doit être l'adresse HTTPS finale. Le conteneur ne gère pas lui-même
de certificat : publiez-le derrière un reverse proxy HTTPS, et n'exposez le port 8080 qu'à ce proxy.

### HTTPS automatique avec Caddy (le plus simple)

Le fichier Compose contient un profil facultatif `https` : un conteneur **Caddy** demande, installe et renouvelle seul
un certificat **Let's Encrypt** pour votre domaine, redirige HTTP vers HTTPS et relaie vers la console.

Prérequis : un nom DNS **public** (ex. `spokapilote.mon-entreprise.fr`) qui pointe sur le serveur, et les ports **80 et
443** joignables depuis Internet (Let's Encrypt vérifie le domaine par ces ports).

1. Créez `server/.env` :

   ```dotenv
   SPOKAPILOTE_DOMAIN=spokapilote.mon-entreprise.fr
   SPOKAPILOTE_PORT=127.0.0.1:8080      # la console n'est plus joignable directement, seulement via Caddy
   SPOKAPILOTE_BEHIND_PROXY=true        # adresse réelle des postes dans les journaux et la limitation des tentatives
   SPOKAPILOTE_PUBLIC_URL=https://spokapilote.mon-entreprise.fr   # liens des alertes
   ```

2. Démarrez avec le profil :

   ```bash
   docker compose -f server/docker-compose.yml --profile https up -d
   docker compose -f server/docker-compose.yml logs caddy      # « certificate obtained successfully »
   ```

3. Sur les postes : `spokapilote config set ServerUrl https://spokapilote.mon-entreprise.fr`.

Le certificat est conservé dans le volume `caddy-data` (pensez à le sauvegarder avec celui de la console). Pour une
adresse de contact Let's Encrypt, décommentez le bloc `email` en tête de `server/Caddyfile`.

**Domaine interne** (non joignable depuis Internet) : Let's Encrypt ne peut pas le vérifier. Dans `server/Caddyfile`,
ajoutez `tls internal` dans le bloc du domaine (autorité de Caddy, à déployer sur les postes) ou
`tls /certs/cert.pem /certs/key.pem` avec un certificat de votre autorité interne (montez le dossier `/certs`).

### Autre reverse proxy

Exemple **Caddy** installé sur l'hôte (certificat Let's Encrypt automatique, ou `tls internal` / certificat de l'AD CS en interne) :

```caddyfile
spokapilote.mon-entreprise.fr {
    reverse_proxy 127.0.0.1:8080
}
```

Exemple **nginx** :

```nginx
server {
    listen 443 ssl http2;
    server_name spokapilote.mon-entreprise.fr;
    ssl_certificate     /etc/ssl/spokapilote/fullchain.pem;
    ssl_certificate_key /etc/ssl/spokapilote/privkey.pem;

    client_max_body_size 6m;            # rapports : 5 Mo par défaut côté serveur
    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

La console peut aussi être publiée sous un sous-chemin (`https://intranet/spokapilote/`) : elle n'utilise que des
adresses relatives ; `ServerUrl` prend alors ce sous-chemin.

Pour que les journaux et la limitation des tentatives (§ 7) voient l'adresse réelle des postes plutôt que celle du
proxy, mettez `SPOKAPILOTE_BEHIND_PROXY=true` (variable `ASPNETCORE_FORWARDEDHEADERS_ENABLED` du conteneur). N'activez-la
que si le conteneur n'est joignable **que** par le proxy (`SPOKAPILOTE_PORT=127.0.0.1:8080`) : sinon n'importe qui
pourrait se faire passer pour une autre adresse.

Si le certificat est émis par une autorité interne, elle doit être approuvée par les postes (magasin
« Autorités de certification racines de confiance », généralement déjà déployé par GPO) ; sinon
`spokapilote server status` affiche « certificat HTTPS refusé ».

## 5. Actions à distance

Depuis la console, l'IT peut demander à un poste (ou à plusieurs) d'exécuter une action. Le poste ne reçoit **jamais**
de commande libre ni de script : seules six actions existent, vérifiées par la console **et** par le poste.

| Action (type) | Ce que fait le poste | Simulation |
|---|---|---|
| Analyser le poste (`scan`) | Inventaire et recherche des pilotes ; envoie le rapport | — (ne modifie rien) |
| Installer les pilotes manquants (`install-missing`) | `install --missing` : pilotes des périphériques en erreur, après point de restauration et sauvegarde | oui |
| Installer manquants + mises à jour (`install-updates`) | `install` : manquants et mises à jour (jamais les pilotes bloqués par la politique) | oui |
| Déployer le profil d'imprimantes (`deploy-printers`) | `printers deploy` avec le profil publié sur la console | oui |
| Annuler la dernière intervention (`rollback-last`) | `rollback last`, puis nouvelle analyse envoyée à la console | oui |
| Mettre à jour SpokaPilote (`self-update`) | Télécharge la dernière version officielle (taille et SHA-256 publiés par GitHub vérifiés — sans empreinte publiée, rien n'est lancé ; signature contrôlée si l'installeur est signé) et l'installe | oui (vérifie seulement) |

**Simulation** : la case « Simulation » de la fenêtre de confirmation montre ce qui serait fait, sans rien modifier.
Utilisez-la la première fois sur un poste ou un groupe de postes.

### Activer l'agent sur les postes

Les actions sont exécutées par l'**agent** : une tâche planifiée `\SpokaPilote\Agent` (compte SYSTEM) qui interroge la
console toutes les N minutes. Dans une console **administrateur** (ou par Intune / GPO, avec le script d'enrôlement) :

```powershell
spokapilote agent enable --every 15     # toutes les 15 minutes (5 à 1440)
spokapilote agent status                # tâche, dernière exécution, dernier contact avec la console
spokapilote agent run                   # exécuter tout de suite les tâches en attente (sans attendre la tâche)
spokapilote agent disable               # supprimer la tâche
```

- La tâche est créée seulement si `spokapilote.exe` est la version installée dans Program Files.
- Sans console configurée (`ServerUrl` vide), l'agent ne fait rien.
- Une interrogation sans tâche ne coûte qu'une requête ; un journal `Logs\agent-AAAAMMJJ-HHMMSS.log` n'est écrit que
  lorsqu'une tâche est exécutée (30 conservés).
- La tâche de maintenance (`spokapilote schedule`) est indépendante et continue de fonctionner.

### Lancer une action depuis la console

- **Fiche d'un poste** › section **Actions à distance** : un bouton par action. La fiche indique si l'agent du poste
  est actif (dernier contact) et liste les tâches du poste.
- **Postes** : cochez des postes, choisissez l'action, puis **Lancer sur la sélection**, ou **Lancer sur tous les
  postes à traiter** (tous les postes actuellement « à traiter », cochés ou non).
- Chaque action demande une confirmation qui explique ce qui va se passer. Le champ « Demandée par » (facultatif)
  apparaît dans l'historique.
- Une action identique déjà en attente pour un poste n'est pas dupliquée.
- Avec un compte nominatif, « Demandée par » est l'identité du compte ; le champ libre n'est proposé qu'avec le jeton
  administrateur.
- Rôles : un compte **Lecture seule** voit les tâches mais ne peut ni en lancer ni en annuler.

### Groupes de postes (3.1)

La page **Groupes** range les postes par site, service ou matériel. Un poste fait partie d'un groupe s'il y a été
**ajouté à la main** ou s'il remplit les **règles automatiques** du groupe (ou les deux).

- **À la main** : dans **Postes**, cochez des postes puis **Ajouter au groupe** / **Retirer du groupe** ; ou, dans la
  fiche du groupe, saisissez un nom de poste. Un poste renommé reste dans ses groupes (il est suivi par son identifiant).
- **Règles** : une ou plusieurs conditions, toutes requises ou au moins une. L'aperçu affiche aussitôt les postes
  concernés.

| Champ | Comparé à | Opérateurs |
|---|---|---|
| Nom du poste | nom actuel | commence par, contient, est égal à, correspond au motif (`*` = n'importe quelle suite, `?` = un caractère ; ex. `PC-*-SIEGE`) |
| Constructeur | constructeur déclaré (ex. `Dell Inc.`, `LENOVO`) | idem |
| Modèle | modèle, nom commercial et « Constructeur Modèle » | idem |
| Windows | « Microsoft Windows 11 Professionnel 24H2 » et version (`10.0.26100`) | idem |
| Sous-réseau | adresse IP d'envoi du **dernier rapport**, vue par la console | est dans : `10.1.20.0/24`, une adresse, IPv6 `fd00::/64` ; plusieurs séparés par des virgules |

Les comparaisons ignorent les majuscules. Le sous-réseau n'est pas transmis par le poste : c'est l'adresse vue par la
console. Derrière un reverse proxy, activez `SPOKAPILOTE_BEHIND_PROXY` (§ 4), sinon tous les postes semblent venir du
proxy ; derrière un NAT ou un VPN, c'est l'adresse traduite qui compte. Un poste dont aucun rapport n'a été reçu depuis la
mise à jour en 3.1 n'a pas encore d'adresse connue.

Ensuite :

- **Vue d'ensemble** et **Postes** : sélecteur **Groupe** (les indicateurs, les tâches et les liens suivent le groupe ;
  « Sans groupe » dans la liste des postes) ;
- **Action sur tout le groupe** : depuis la fiche du groupe, ou **Lancer sur le groupe** dans la liste des postes filtrée
  par groupe ; sont visés les membres au moment du lancement ;
- **Planifier…** : planification préremplie pour le groupe (ci-dessous).

Supprimer un groupe ne supprime aucun poste ; un groupe utilisé par une planification ne peut pas être supprimé.

### Actions planifiées (3.1)

La page **Planifications** lance automatiquement une action de la liste fermée (aucun nouveau type de tâche) :

- **Postes visés** : un groupe ou tous les postes ; les membres du groupe sont évalués à chaque exécution.
- **Fréquence** : chaque jour, ou certains jours de la semaine, à une heure donnée — **heure du serveur** (fuseau du
  conteneur, `TZ`, `Europe/Paris` par défaut dans le fichier Compose).
- **Simulation** : comme pour une action lancée à la main.
- **Fenêtre de maintenance** (facultative, 30 minutes à 12 heures) : une tâche que le poste n'a pas récupérée avant la
  fin de la fenêtre **expire** au lieu de s'exécuter plus tard (en pleine journée). Sans fenêtre, elle attend le poste
  jusqu'à 7 jours, comme une tâche manuelle. Choisissez une fenêtre au moins égale à l'intervalle de l'agent
  (`agent enable --every`, 15 minutes par défaut).
- **Pas de doublon** : un poste qui a déjà une tâche **du même type en attente** (planifiée ou manuelle, simulation ou
  non) n'en reçoit pas une seconde.

Le planificateur vérifie les échéances toutes les 30 secondes et crée les tâches dans la file habituelle (vue **Tâches**,
badge « planifiée »). La liste affiche la dernière exécution (nombre de tâches créées, postes ignorés) et la prochaine.
Une planification peut être désactivée sans être supprimée.

- **Changement d'heure** : une heure qui n'existe pas (passage à l'heure d'été, ex. 02:30 le dernier dimanche de mars)
  est reportée à la première minute qui existe (03:00) ; une heure qui existe deux fois (retour à l'heure d'hiver) ne
  déclenche qu'une exécution, à la première occurrence.
- **Console arrêtée** à l'échéance : l'exécution manquée est rattrapée une fois au redémarrage, sauf si sa fenêtre de
  maintenance est déjà terminée (le résultat indique alors « manquée »).

### Suivre les tâches

La vue **Tâches** liste toutes les tâches, filtrables par état : **en attente** (pas encore récupérée), **en cours**
(récupérée par le poste), **réussie**, **échouée**, **expirée**, **annulée**. Le bouton **Détail** affiche le message,
le code de sortie, le **journal d'exécution** du poste et le lien vers le **rapport** produit.

- Une tâche en attente peut être **annulée**. Une tâche en cours ne peut plus l'être (elle s'exécute déjà).
- Une tâche jamais récupérée expire après 7 jours (poste éteint, agent non activé) ; une tâche en cours sans résultat
  après 6 heures expire aussi (poste éteint ou redémarré pendant l'exécution). Les tâches terminées sont conservées
  90 jours.
- Un poste ne voit que ses propres tâches : il s'identifie par son identifiant Windows (`MachineGuid`), et ne peut ni
  lire ni compléter la tâche d'un autre poste.
- Codes de sortie : `0` réussie, `3010` réussie (redémarrage requis), `2` échec partiel, `1` erreur.

### Déploiement : anneaux et suspension automatique (4.0)

La page **Déploiement** diffuse les nouvelles versions de pilotes progressivement, anneau par anneau, et suspend
automatiquement celles qui échouent. Fonctionnement détaillé (première détection, règles servies aux postes, postes 3.x,
disjoncteur, régressions, limites) : [POLITIQUE.md, « Anneaux de déploiement et suspension automatique »](POLITIQUE.md#anneaux-de-déploiement-et-suspension-automatique).

- **Mise en route** (Administrateur) : « Créer les anneaux suggérés » (Pilote 0 jour, Précoce 3 jours, Général 10 jours),
  associez des **groupes** à chaque anneau (« Modifier »), puis « Activer la diffusion par anneaux ». Avant l'activation,
  rien ne change pour les postes, hormis les suspensions (manuelles ou du disjoncteur, toujours appliquées) ; les versions proposées sont déjà suivies depuis la mise à jour de la console, pour que
  les délais soient justes dès l'activation.
- **Anneaux** : ordre de diffusion (flèches), délai, validation exigée dans l'anneau précédent (installations réussies au
  moins, échecs tolérés), groupes, nombre de postes. Un poste dans plusieurs anneaux reçoit le **plus tardif** ; un poste
  sans groupe d'anneau, l'**anneau par défaut** (le dernier). L'indicateur « Dans plusieurs anneaux » compte ces postes.
- **Réglages** : activation, anneau par défaut, pilotes manquants soumis ou non aux délais, disjoncteur (au moins 3 postes
  en échec et 20 % des tentatives en 48 h par défaut, régressions comptées ou non), mémoire des familles de pilotes.
- **Pilotes en cours de diffusion** : tableau paginé et calculé par le serveur (recherche, filtres « en cours et
  suspendus », « suspensions à examiner », « diffusion terminée », « tous les pilotes vus ») : pilote, source, première
  détection, **état par anneau** (« en attente jusqu'au 08/12/2026 à 09:00 », « validation en attente (2/3 réussies) »,
  « autorisé », « validé », « suspendu ») et installations réussies / en échec par anneau. La fiche d'un pilote détaille
  les anneaux, les derniers échecs (poste, message, régression) et l'historique.
- **Actions** : **Suspendre** / **Reprendre** / **Maintenir le blocage** (Technicien et Administrateur), **Diffuser
  partout** et l'annuler (Administrateur). Lecture seule : consultation. Tout est consigné dans le journal d'audit
  (catégorie « Déploiement ») et dans l'**historique** de la page.
- **Suspension automatique** : pastille rouge sur « Déploiement » dans le menu, encart sur la vue d'ensemble et en tête de
  la page, ligne surlignée ; alerte « Pilote suspendu automatiquement » (§ 6).
- **Aperçu** : `GET /api/rollout/preview?machine=NOM` renvoie ce que reçoit un poste : son anneau, la décision de la
  console pour chaque proposition de son dernier rapport (postes 4.0), et la taille et la forme de la politique servie aux
  postes 3.x (complète, compacte ou sources retenues en bloc).

## 6. Alertes

La console prévient l'équipe IT par **e-mail** et/ou **webhook** (Microsoft Teams, Discord, Slack, ou JSON générique pour
un outil de supervision ou d'automatisation). Réglages : page **Paramètres** de la console, ou variables
d'environnement (une variable définie **prime** sur la console : le champ y apparaît verrouillé).

| Déclencheur | Quand |
|---|---|
| Poste à traiter | Un poste sain (ou nouveau) envoie un rapport avec un pilote en erreur ou un échec d'installation |
| Échec d'installation | Un rapport contient au moins un pilote non installé |
| Tâche à distance échouée | Une tâche se termine en échec, ou reste « en cours » sans résultat |
| Poste non vu | Aucun rapport depuis N jours (7 par défaut ; vérification tous les quarts d'heure) — une alerte groupée par période d'absence |
| Pilote dangereux détecté | Le rapport signale un pilote connu pour provoquer des pannes (constat `critical` de code `known-bad-driver`, § 9) |
| Pilote suspendu automatiquement (4.0) | Le disjoncteur de la diffusion vient de suspendre une version sur tout le parc (une alerte par version et par 24 h) |
| Résumé quotidien (facultatif) | Chaque jour à l'heure choisie : postes à traiter, non vus, échecs, tâches, santé moyenne |

**Anti-spam** : une même alerte (même poste, même motif) n'est jamais envoyée deux fois en moins de 24 heures. Aucune
alerte n'est générée tant qu'aucun canal n'est configuré. Le bouton **Envoyer une alerte de test** envoie aussitôt un
message sur chaque canal et affiche le résultat de chacun (erreur SMTP, webhook refusé...). La page liste aussi les
dernières alertes envoyées.

### E-mail (SMTP)

| Variable | Exemple | Rôle |
|---|---|---|
| `SPOKAPILOTE_SMTP_HOST` | `smtp.office365.com` | Serveur SMTP |
| `SPOKAPILOTE_SMTP_PORT` | `587` | Port |
| `SPOKAPILOTE_SMTP_SECURITY` | `starttls` | `starttls` (587, recommandé), `ssl` (TLS implicite, 465) ou `none` (relais interne, 25) |
| `SPOKAPILOTE_SMTP_USERNAME` / `_PASSWORD` | `alertes@...` | Identifiants (facultatifs pour un relais interne) |
| `SPOKAPILOTE_SMTP_FROM` | `spokapilote@mon-entreprise.fr` | Expéditeur |
| `SPOKAPILOTE_SMTP_TO` | `support-it@..., chef@...` | Destinataires, séparés par des virgules |

Le certificat du serveur SMTP est vérifié normalement (STARTTLS et SSL). Office 365 : autorisez « SMTP authentifié »
pour la boîte d'envoi, ou utilisez un connecteur de relais.

### Webhooks

| Variable | Exemple | Rôle |
|---|---|---|
| `SPOKAPILOTE_WEBHOOK_URL` | `https://...` | Adresse du webhook (un seul par variable ; d'autres s'ajoutent dans la console, 10 au plus) |
| `SPOKAPILOTE_WEBHOOK_FORMAT` | `teams` | `teams`, `discord`, `slack` ou `json` |

- **Teams** : dans le canal, application **Workflows** › « Publier dans un canal lorsqu'une requête webhook est reçue »
  (ou ancien « webhook entrant ») ; la console envoie une carte adaptative.
- **Discord** : Paramètres du salon › Intégrations › Webhooks › Nouveau webhook › Copier l'URL.
- **Slack** : application « Incoming Webhooks » › Ajouter au canal › Copier l'URL.
- **JSON générique** : `POST` d'un objet `{ source, version, event, eventLabel, severity, title, message, machine,
  timestamp, url, details: [ { label, value } ] }` (`event` : `to-process`, `install-failed`, `job-failed`, `stale`,
  `dangerous-driver`, `daily-summary`, `test`).

Les adresses de webhook contiennent un secret et le mot de passe SMTP est sensible : ils sont conservés dans la base
(volume `/data`) et **ne sont jamais réaffichés** dans la console (laisser le champ vide conserve la valeur enregistrée).
Les redirections ne sont pas suivies.

### Autres réglages

| Variable | Exemple | Rôle |
|---|---|---|
| `SPOKAPILOTE_ALERT_STALE_DAYS` | `7` | Alerte « poste non vu » après ce délai (`0` = jamais ; défaut : `SPOKAPILOTE_STALE_DAYS`) |
| `SPOKAPILOTE_DAILY_SUMMARY` | `08:00` | Heure du résumé quotidien (fuseau du conteneur, `TZ`) ; vide = pas de résumé |
| `SPOKAPILOTE_PUBLIC_URL` | `https://spokapilote.mon-entreprise.fr` | Adresse de la console pour les liens des alertes |

Les déclencheurs s'activent ou se désactivent un par un dans la page **Paramètres**.

## 7. Sécurité

- **Tentatives refusées** : après 10 jetons ou connexions (identifiant ou mot de passe) refusés en une minute depuis une
  même adresse IP, la console répond `429 Too Many Requests` à cette adresse jusqu'à la fin de la minute, même avec un
  bon jeton ou le bon mot de passe (réglage `SPOKAPILOTE_AUTH_FAILURES_PER_MINUTE`). Une requête sans jeton ne compte
  pas ; la sonde `/api/health` reste libre. Chaque connexion refusée est inscrite au journal d'audit.
  Derrière un reverse proxy, activez `SPOKAPILOTE_BEHIND_PROXY` (§ 4) : sinon toutes les requêtes semblent venir du proxy.
- **Actions à distance** : liste fermée côté console et côté poste ; le jeton d'enrôlement ne permet pas de créer de
  tâche ; un poste ne voit et ne complète que ses tâches.
- Le jeton administrateur ne doit jamais être déployé sur les postes.
- **Comptes et sessions** : voir § 2.1 (empreintes PBKDF2, cookie `HttpOnly` / `SameSite=Strict`, jeton anti-CSRF).

### Journal d'audit (3.1)

La page **Journal d'audit** (rôle Administrateur) consigne qui a fait quoi, quand et depuis quelle adresse :

| Catégorie | Actions consignées |
|---|---|
| Connexions | connexion réussie, connexion refusée (identifiant saisi, motif), déconnexion |
| Comptes | création, modification (rôle, activation, nom), réinitialisation et changement de mot de passe, suppression |
| Actions à distance | action lancée (type, cible, nombre de tâches), tâche annulée |
| Planifications | création, modification, suppression, chaque exécution (acteur « Planificateur ») |
| Groupes | création, modification (nom, règles), membres ajoutés ou retirés, suppression |
| Paramètres et alertes | réglages des alertes, alerte de test, réglages du cache de pilotes (avant → après) |
| Cache de pilotes | vidage (portée, place libérée), préchargement (modèles) |
| Politique et profil d'imprimantes | enregistrement, suppression |
| Postes | suppression d'un poste |
| Journal d'audit | export CSV |

Les actions faites avec le jeton administrateur apparaissent sous « Jeton administrateur ». Filtres par catégorie et
recherche libre ; **Exporter (CSV)** produit un fichier UTF-8 séparé par des `;` (ouverture directe dans Excel ; une
valeur commençant par `=`, `+`, `-` ou `@` est neutralisée). Rétention : 5 000 entrées et 180 jours au plus
(`SPOKAPILOTE_AUDIT_MAX_ENTRIES`, `SPOKAPILOTE_AUDIT_DAYS`) ; les plus anciennes sont supprimées automatiquement. Les
mots de passe, jetons et adresses de webhook n'y figurent jamais.

## 8. Sauvegarde et restauration

Toutes les données sont dans le volume `/data`. La base SQLite est en mode WAL : ne copiez jamais `spokapilote.db`
seul pendant que la console tourne.

### Sauvegarde à chaud (4.0, recommandé)

La console copie elle-même sa base, **sans s'arrêter** (API de sauvegarde en ligne de SQLite : copie cohérente, les
rapports continuent d'arriver pendant la copie), dans `/data/backups/spokapilote-AAAAMMJJ-HHMMSS.db`. La copie est
vérifiée (`PRAGMA quick_check`) puis rendue autonome (un seul fichier, sans journal WAL) ; une copie incomplète n'est
jamais laissée sous ce nom. Les `SPOKAPILOTE_BACKUP_KEEP` plus récentes (7) sont conservées.

- **À la demande** : page **Paramètres** › **Sauvegarder maintenant**, ou `POST /api/admin/backup?wait=true`
  (Administrateur ; consigné au journal d'audit).
- **Chaque jour** : `SPOKAPILOTE_BACKUP_TIME=03:00`.
- **Hors du serveur** : `/data/backups` est dans le volume ; copiez-le ailleurs (sauvegarde de l'hôte, `docker cp`) :

```bash
docker cp spokapilote-server:/data/backups ./spokapilote-backups      # ou le dossier du volume sur l'hôte
```

Durée mesurée : 0,2 s pour 300 postes, 10 s pour une base de 20 000 postes × 20 rapports (874 Mo) ; les envois de rapports
continuent pendant la copie (§ 11).

**Restauration** d'une sauvegarde à chaud (conteneur arrêté) :

```bash
docker compose -f server/docker-compose.yml stop
docker run --rm -v server_spokapilote-data:/data alpine sh -c \
  "cp /data/backups/spokapilote-AAAAMMJJ-HHMMSS.db /data/spokapilote.db && rm -f /data/spokapilote.db-wal /data/spokapilote.db-shm && chown 1654:1654 /data/spokapilote.db"
docker compose -f server/docker-compose.yml start
```

Les jetons (`admin-token.txt`, `agent-token.txt`) ne sont pas dans la base : sauvegardez-les une fois (ou fixez-les par
variable d'environnement). La base contient les empreintes de mots de passe, le mot de passe SMTP et les adresses de
webhooks : conservez les sauvegardes en lieu sûr.

### Sauvegarde complète du volume (conteneur arrêté)

```bash
# Sauvegarde (archive datée dans le dossier courant)
docker compose -f server/docker-compose.yml stop
docker run --rm -v server_spokapilote-data:/data -v "$PWD":/backup alpine \
  tar czf /backup/spokapilote-$(date +%F).tar.gz --exclude=./cache -C /data .
docker compose -f server/docker-compose.yml start

# Restauration
docker compose -f server/docker-compose.yml stop
docker run --rm -v server_spokapilote-data:/data -v "$PWD":/backup alpine \
  sh -c "rm -rf /data/* && tar xzf /backup/spokapilote-AAAA-MM-JJ.tar.gz -C /data && chown -R 1654:1654 /data"
docker compose -f server/docker-compose.yml start
```

Le nom du volume dépend du nom du projet Compose (`server_spokapilote-data` par défaut ; `docker volume ls` pour le
vérifier). L'archive contient les jetons : conservez-la en lieu sûr. Le cache de pilotes (`/data/cache`, § 12) en est
exclu : il se reconstitue tout seul.

Volume perdu sans sauvegarde : les postes réapparaissent à leur prochain rapport, mais la politique, le profil
d'imprimantes et l'historique sont perdus, et de nouveaux jetons sont générés (sauf s'ils sont fixés par variable).

Avec le profil `https`, sauvegardez aussi le volume `server_caddy-data` (certificat) de la même façon.

**Mise à jour** du serveur : `git pull` puis `docker compose -f server/docker-compose.yml up -d --build` ; le schéma
de la base est migré automatiquement au démarrage (passage en 3.0 : postes, historique, politique et profil sont
conservés). Sauvegardez le volume avant une mise à jour majeure.

### Passage de la 3.0 à la 3.1

- **Automatique** : au premier démarrage, la base passe au schéma 3.1 (nouvelles tables : groupes, planifications,
  comptes, sessions, journal d'audit ; adresse du dernier rapport et fenêtre de maintenance des tâches). Rien n'est
  supprimé ni modifié : postes, historique, tâches, politique, profil d'imprimantes, alertes et jetons sont conservés.
  Un serveur 3.0 ne sait pas relire une base 3.1 : pour revenir en arrière, restaurez la sauvegarde.
- **Postes** : rien à faire. Les postes 3.0 (et 2.x) continuent d'envoyer leurs rapports, de lire la politique et le
  profil et d'exécuter les actions à distance avec le même jeton d'enrôlement ; l'API des postes est inchangée.
- **Jeton administrateur** : inchangé, toujours valable pour la console et les scripts.
- **À faire** : connectez-vous avec le jeton, créez votre compte Administrateur puis ceux de l'équipe (§ 2.1). Derrière
  un reverse proxy HTTPS, vérifiez que `SPOKAPILOTE_BEHIND_PROXY=true` (cookie `Secure`, adresses réelles pour le
  journal et les règles de sous-réseau). Fixez `TZ` si les planifications doivent suivre un autre fuseau que
  `Europe/Paris`.
- Les groupes « sous-réseau » ne contiennent un poste qu'après son premier rapport reçu par la 3.1.

### Passage de la 3.1 à la 4.0

Sauvegardez d'abord (§ 8) : un serveur 3.1 ne sait pas relire une base 4.0. Ensuite, tout est automatique, en deux
volets.

**Grands parcs (indicateurs précalculés, stockage compressé)**

- **Automatique**, en deux temps. Au démarrage : nouvelles tables, table des postes reconstruite avec les indicateurs de
  chaque poste calculés depuis son dernier rapport, membres des groupes par règle, index (14 s mesurées pour 20 000 postes
  × 20 rapports). La console est utilisable dès la fin de cette étape. Ensuite, **en arrière-plan** et par petits lots :
  l'historique des rapports est compressé (81 s pour 395 000 rapports, sans interrompre la réception des rapports) ; les
  rapports pas encore convertis restent lisibles. Rien n'est supprimé.
- **Postes** : rien à faire ; l'API des postes est inchangée (les postes 4.0 savent en plus attendre et réessayer quand la
  console est surchargée).
- **Scripts** : les routes et réponses existantes sont inchangées (`GET /api/machines` renvoie toujours le tableau de
  tous les postes) ; la pagination et les filtres passent par de nouveaux paramètres (§ 9).
- **Après la compression** (message « Historique compressé » dans les journaux), lancez une fois un compactage en période
  creuse : `POST /api/admin/vacuum` (17 s et 2,2 Go → 0,9 Go mesurés sur 20 000 postes). Sans lui, SQLite réutilise la
  place libérée pour les nouveaux rapports, mais le fichier ne diminue pas.

**Diffusion par anneaux et cache de pilotes**

- **Automatique et instantané** : la migration ajoute des tables (anneaux, versions vues, installations par anneau,
  historique) sans réécrire aucune donnée existante. La diffusion par anneaux est **désactivée** après la mise à jour :
  les postes reçoivent leur politique exactement comme avant tant qu'aucun anneau n'est activé (le disjoncteur, lui, est
  actif, mais ne compte que les installations rapportées après la mise à jour).
- **Postes 3.x** : rien à faire, ils restent compatibles (règles du format 1 uniquement, anneau le plus prudent) ; mettez
  au moins les postes pilotes en 4.0 pour qu'ils reçoivent leur propre anneau (voir POLITIQUE.md).
- **Scripts** : `GET /api/policy` avec le jeton administrateur ou un compte renvoie toujours le document enregistré ; seul
  le jeton d'enrôlement reçoit la politique effective.

## 9. API

Toutes les routes sont sous `/api` ; réponses JSON (camelCase). `GET /api/metrics` contient aussi (4.0) un objet
`driverCache` (taille, réussites, octets évités et téléchargés, renvois en direct, empreintes refusées, garde d'espace
disque) et, au format Prometheus, les séries `spokapilote_cache_*`.

Colonne **Accès** : « admin » = jeton administrateur ou compte Administrateur ; « tech » = Technicien et au-delà ;
« lecture » = tout compte (et le jeton administrateur) ; « poste » = jeton d'enrôlement. Avec une session (cookie), les
requêtes POST, PUT et DELETE exigent l'en-tête `X-SpokaPilote-CSRF` (§ 2.1).

| Méthode et route | Accès | Rôle |
|---|---|---|
| `GET /api/health` | aucun | Sonde de vie (`{"status":"ok","version":"…","capabilities":[…]}` ; `capabilities` depuis la 4.0) |
| `GET /api/whoami` | poste ou lecture | Vérifie un jeton (`{"access":"agent"\|"admin"}` ; `"user"` pour une session) |
| `POST /api/session` | aucun | Connexion d'un compte : `{ "login", "password" }`, en-tête `X-SpokaPilote-CSRF` (valeur libre) ; dépose le cookie et renvoie l'appelant et son jeton anti-CSRF (`csrf`) |
| `GET /api/session` | lecture | Appelant courant (compte ou jeton), rôle, droits (`permissions`), jeton anti-CSRF |
| `DELETE /api/session` | lecture | Déconnexion |
| `PUT /api/session/password` | lecture (compte) | Changer son mot de passe : `{ "currentPassword", "newPassword" }` |
| `POST /api/reports` | poste | Rapport `spokapilote scan --json` (JSON, éventuellement `Content-Encoding: gzip` ; limite appliquée après décompression) ; `503` + `Retry-After` si la console est surchargée (4.0) |
| `GET /api/fleet?group=&top=` | lecture | Synthèse du parc ou d'un groupe (indicateurs, postes à traiter, matériel sans pilote, modèles, postes non vus) ; `top=N` (4.0) limite les listes aux N premiers, `missingDriverCount` donne le nombre total de types de matériel sans pilote |
| `GET /api/machines?group=` | lecture | Liste des postes (indicateurs du dernier rapport, `groups` : identifiants des groupes, `lastIp`) — tableau de tous les postes, comme en 3.1 |
| `GET /api/machines?page=&pageSize=` | lecture | (4.0) Une page calculée par la base : `{ total, offset, limit, items, machineCount, toProcessCount, hasHealth, group }` ; aussi `offset=&limit=` (1 000 au plus). Filtres (avec ou sans pagination) : `q` (nom, modèle, n° de série, Windows), `status` (`todo`, `healthy`, `stale`, `unresolved`, `hardware`, `failed`, `blocked`, `dangerous`, `jobs`), `group` (identifiant ou `none`), tri `sort` (`status`, `name`, `model`, `os`, `driverProblems`, `unresolved`, `proposed`, `failed`, `lastReport`, `health`) et `dir` (`asc`/`desc`) |
| `GET /api/export/machines?…` | lecture | (4.0) Postes filtrés (mêmes paramètres) en CSV, écrit en continu |
| `GET /api/missing-drivers?q=&offset=&limit=&machines=&group=` | lecture | (4.0) Matériel sans pilote paginé ; `machines` = postes listés par ligne (12 par défaut, `moreMachines` = les autres) |
| `GET /api/export/missing-drivers?q=&group=` | lecture | (4.0) Matériel sans pilote complet en CSV |
| `GET /api/machines/{nom}` | lecture | Fiche du poste, dernier rapport normalisé, historique, groupes |
| `GET /api/machines/{nom}/reports/{id}` | lecture | Un rapport de l'historique |
| `GET /api/machines/{nom}/raw` | lecture | Dernier rapport complet tel qu'envoyé (pour `spokapilote pack`) |
| `DELETE /api/machines/{nom}` | admin | Retire le poste et son historique |
| `GET /api/enrollment` | admin | Jeton d'enrôlement et réglages de rétention |
| `GET /api/policy` | poste ou lecture | Politique de pilotes (JSON brut ; `204` si aucune). (4.0) Avec le jeton d'enrôlement : politique **effective** du poste (retenues et suspensions de la diffusion en tête ; en-têtes facultatifs `X-SpokaPilote-Machine-Id`, `X-SpokaPilote-Machine` ; section `rollout` si `X-SpokaPilote-Rollout: 1` ; avec `X-SpokaPilote-Rollout-Decisions: 1` en plus (postes 4.0), suspensions seules et `rollout.decisions: true` — les retenues sont alors demandées par `POST /api/rollout/decide` ; réponse compressée si `Accept-Encoding: gzip` ; en-tête de réponse `X-SpokaPilote-Ring`) ; sans anneau actif ni suspension, le document tel quel. Document **borné** (`SPOKAPILOTE_ROLLOUT_MAX_RULES`, `SPOKAPILOTE_ROLLOUT_MAX_KB`) quelle que soit la taille du parc |
| `POST /api/rollout/decide` | poste | (4.0) Décisions de diffusion pour les propositions d'un poste : `{ "candidates": [ { "source": "WindowsUpdate", "title", "version", "kind": "Update" } ] }` (1 000 au plus, `Content-Encoding: gzip` accepté) ; en-têtes `X-SpokaPilote-Machine-Id`, `X-SpokaPilote-Machine` (sinon anneau le plus prudent) ; réponse : anneau (`ring`, `delayDays`, `holdUnknown`…) et `decisions` dans l'ordre des propositions, chacune `{ "status": "allowed" \| "held" \| "suspended" \| "unknown", "availableAt", "reason" }`. Lecture seule : rien n'est enregistré |
| `PUT /api/policy` / `DELETE /api/policy` | admin | Enregistre (JSON valide exigé, 1 Mo max) / supprime la politique |
| `GET`, `PUT`, `DELETE /api/printer-profile` | idem | Profil d'imprimantes (même fonctionnement) |
| `GET /api/job-types` | lecture | Actions à distance autorisées (liste fermée) |
| `GET /api/jobs?state=&machine=&limit=` | lecture | Tâches (état : `pending`, `running`, `succeeded`, `failed`, `expired`, `cancelled`, ou `active` / `finished`) |
| `POST /api/jobs` | tech | Crée des tâches : `{ "type": "scan", "machines": ["PC-01"], "dryRun": false, "requestedBy": "..." }` ou `"target": "to-process"` / `"all"`, ou `"target": "group", "group": 3` |
| `GET /api/jobs/{id}` | lecture | Détail d'une tâche, avec le journal d'exécution (`notAfter` : fin de la fenêtre de maintenance, `scheduleId`) |
| `POST /api/jobs/{id}/cancel` | tech | Annule une tâche en attente (`409` si déjà en cours) |
| `GET /api/agent/jobs` | poste | Tâches en attente du poste, passées « en cours » ; en-têtes `X-SpokaPilote-Machine-Id` (MachineGuid, obligatoire) et `X-SpokaPilote-Machine` (nom) |
| `POST /api/agent/jobs/{id}/result` | poste | Résultat : `{ "success", "exitCode", "message", "reportId", "log" }` (mêmes en-têtes) |
| `GET`, `PUT /api/settings/alerts` | admin | Réglages des alertes (secrets jamais renvoyés ; `null` = inchangé, `""` = effacé) |
| `POST /api/alerts/test` | admin | Envoie une alerte de test sur chaque canal et renvoie le résultat de chacun |
| `GET /api/alerts/events` | admin | Dernières alertes envoyées |
| `GET /api/groups` | lecture | Groupes (règles, nombre de membres, membres manuels, planifications) |
| `GET /api/groups/{id}?limit=&offset=` | lecture | Groupe, membres (`manual`, `byRule`), planifications qui l'utilisent ; (4.0) `limit` pour une page de membres, `memberTotal` et `toProcessCount` pour tout le groupe |
| `GET /api/group-fields` | lecture | Champs et opérateurs des règles |
| `POST /api/groups/preview` | lecture | Postes désignés par des règles : `{ "rules": { ... } }` |
| `POST /api/groups` / `PUT /api/groups/{id}` | tech | `{ "name", "description", "rules": { "match": "all"\|"any", "conditions": [ { "field": "name", "operator": "startsWith", "value": "PC-COMPTA" } ] } }` |
| `DELETE /api/groups/{id}` | tech | Supprime le groupe (`409` s'il est utilisé par une planification) |
| `POST /api/groups/{id}/members` | tech | Membres manuels : `{ "add": ["PC-01"], "remove": ["PC-02"] }` |
| `GET /api/schedules` | lecture | Planifications (dernière et prochaine exécution, fuseau du serveur) |
| `POST /api/schedules` / `PUT /api/schedules/{id}` | tech | `{ "name", "type": "scan", "dryRun": false, "groupId": 3, "frequency": "daily"\|"weekly", "days": [1, 4], "time": "22:00", "windowMinutes": 120, "enabled": true }` (jours : 1 = lundi … 7 = dimanche ; `groupId` null = tous les postes) |
| `DELETE /api/schedules/{id}` | tech | Supprime la planification (les tâches déjà créées restent) |
| `GET /api/users` | admin | Comptes (jamais d'empreinte de mot de passe) et rôles |
| `POST /api/users` / `PUT /api/users/{id}` | admin | `{ "login", "displayName", "role": "admin"\|"technician"\|"readonly", "password", "enabled" }` (en modification, `password` vide = inchangé) |
| `DELETE /api/users/{id}` | admin | Supprime un compte |
| `GET /api/audit?category=&q=&limit=` | admin | Journal d'audit (les plus récentes d'abord) |
| `GET /api/audit/export?category=&q=` | admin | Journal d'audit complet en CSV |
| `GET /api/metrics?format=` | admin | (4.0) Supervision : postes, base (taille, pages libres, migration), tâches, files d'attente des postes, cache, mémoire, temps de réponse par route (p50/p95/p99) ; `format=prometheus` pour Prometheus (§ 11) |
| `GET /api/admin/backups` | admin | (4.0) Sauvegardes présentes et résultat de la dernière |
| `POST /api/admin/backup?wait=` | admin | (4.0) Sauvegarde à chaud (`202` en arrière-plan, `wait=true` : attend et renvoie le résultat ; `409` si une sauvegarde est en cours) |
| `POST /api/admin/vacuum` | admin | (4.0) Compactage de la base (bloque les écritures pendant sa durée : période creuse) |
| `GET /api/cache/status` | poste ou lecture | (4.0) État du cache : actif, taille, nombre de fichiers, taux de réussite, octets évités (aucune adresse de fichier) |
| `GET /api/cache/file?url=&sha256=&sha1=&md5=&size=` | poste | (4.0) Fichier officiel par son adresse HTTPS (§ 12) ; `Range` accepté une fois le fichier en cache ; en-tête `X-SpokaPilote-Cache` : `hit`, `miss`, `wait`, `bypass` (`503` : téléchargez directement), `refused` (`403`), `disabled` (`404`) |
| `GET /api/cache/catalog?url=` | poste | (4.0) Catalogue ou métadonnée officielle (Dell, HP, Lenovo, NVIDIA) ; `404` + `X-SpokaPilote-Origin-Status: 404` si absent de la source ; `X-SpokaPilote-Fetched-At` : date d'obtention |
| `GET /api/cache/catalog/mscatalog-search?q=` / `mscatalog-download?updateId=` | poste | (4.0) Recherche et liens de téléchargement du Microsoft Update Catalog ; `429` + `Retry-After` pendant une pause « Too many search requests » |
| `GET`, `PUT /api/settings/cache` | admin | (4.0) Réglages, statistiques, fichiers les plus demandés, dernier préchargement : `{ "enabled", "catalogs", "maxSizeGb", "catalogTtlHours", "fileMaxAgeDays", "minFreeGb" }` (`null` = inchangé) |
| `POST /api/cache/purge?scope=` | admin | (4.0) Vide le cache : `all`, `files` (paquets) ou `catalogs` |
| `POST /api/cache/prefetch` | admin | (4.0) Préchargement en arrière-plan : `{ "models": ["Latitude 7440"] }` ou `{ "all": true }`, `"packages": false` pour les seuls catalogues (`202`) |
| `GET /api/rollout/summary` | lecture | (4.0) `{ enabled, active, rings, inRollout, suspended, attention }` |
| `GET /api/rollout` | lecture | (4.0) Réglages, anneaux (groupes, postes, anneau par défaut), postes dans plusieurs anneaux, compteurs |
| `PUT /api/rollout/settings` | admin | (4.0) `{ "enabled", "defaultRingId", "applyToMissingDrivers", "familyRetentionDays", "breaker": { "enabled", "minFailures", "minFailureRatePercent", "windowHours", "countRegressions" } }` |
| `POST /api/rollout/rings` / `PUT /api/rollout/rings/{id}` | admin | (4.0) `{ "name", "delayDays", "requireValidation", "minSuccesses", "maxFailures", "description", "groups": [id…] }` |
| `DELETE /api/rollout/rings/{id}` | admin | (4.0) Supprime un anneau (ses postes passent dans l'anneau par défaut) |
| `PUT /api/rollout/rings/order` | admin | (4.0) `{ "ids": [premier, …, dernier] }` (tous les anneaux) |
| `POST /api/rollout/rings/suggested` | admin | (4.0) Crée Pilote (0 j), Précoce (3 j), Général (10 j) s'il n'y a encore aucun anneau |
| `GET /api/rollout/versions?status=&q=&offset=&limit=` | lecture | (4.0) Versions vues, avec l'état par anneau ; `status` : `active` (défaut), `rollout`, `suspended`, `attention`, `completed`, `all` ; 500 au plus par page |
| `GET /api/rollout/versions/{id}` | lecture | (4.0) Fiche : état par anneau, derniers échecs, historique |
| `POST /api/rollout/versions/{id}/suspend` · `/resume` · `/acknowledge` | tech | (4.0) Suspendre (`{ "reason" }`), reprendre, maintenir le blocage (`{ "comment" }`) |
| `POST /api/rollout/versions/{id}/approve` · `/unapprove` | admin | (4.0) Diffuser partout maintenant / revenir à la diffusion progressive |
| `GET /api/rollout/history?offset=&limit=` | lecture | (4.0) Historique (suspensions, reprises, diffusions forcées, activation) |
| `GET /api/rollout/preview?machine=` | lecture | (4.0) Ce que reçoit un poste : anneau, politique 4.0 (`bytes`, `policy`), `decisions` pour les propositions de son dernier rapport (`items` : statut, date, motif), et taille / forme des documents sans décisions (`withoutDecisions`) et 3.x (`legacy` : `bytes`, `rules`, `form`, `sourceHolds`, `undatedHolds`, `omittedFamilies`, plafonds) |

`POST /api/reports` renvoie aussi `reportId` (à indiquer dans le résultat d'une tâche) et `renamedFrom` (ancien nom si le
poste, reconnu par son identifiant, a été renommé).

**Santé du poste (contrat facultatif)** : un rapport peut contenir un objet `health` :

```json
"health": {
  "score": 72, "grade": "B",
  "findings": [
    { "severity": "critical", "code": "known-bad-driver", "title": "Pilote audio X 1.2", "detail": "Écrans bleus connus", "deviceId": "HDAUDIO\\..." }
  ]
}
```

`score` de 0 à 100, `grade` de `A` à `E`, `severity` `critical` / `warning` / `info`. S'il est présent, la console
affiche la note dans la liste des postes, la fiche (constats triés par gravité) et la **santé moyenne** du parc ; un
constat `critical` de code `known-bad-driver` déclenche l'alerte « pilote dangereux ». Sans cet objet, rien ne change.

Exemple (PowerShell) :

```powershell
$h = @{ Authorization = "Bearer $env:SPOKAPILOTE_ADMIN_TOKEN" }
Invoke-RestMethod https://spokapilote.mon-entreprise.fr/api/fleet -Headers $h
Invoke-RestMethod -Method Put https://spokapilote.mon-entreprise.fr/api/policy -Headers $h `
    -ContentType "application/json" -InFile .\driver-policy.json
```

Règles de calcul (identiques à `spokapilote fleet-report`) : un poste est **sain** s'il n'a aucun périphérique en
erreur à cause d'un pilote et aucun échec d'installation. Les pannes matérielles USB (ID matériel contenant
`DESCRIPTOR_FAILURE`, `RESET_FAILURE`, `SET_ADDRESS_FAILURE`, `DESCRIPTOR_VALIDATION_FAILURE`) sont comptées à part :
aucun pilote ne peut les corriger. Un périphérique dont seuls des pilotes **bloqués par la politique** sont proposés
reste « sans pilote ».

## 10. Développement

```powershell
dotnet run --project server\SpokaPilote.Server --urls http://localhost:8080   # données dans server\SpokaPilote.Server\data
dotnet test tests\SpokaPilote.Server.Tests
```

La console web (`server/SpokaPilote.Server/wwwroot`) est en HTML/CSS/JavaScript sans framework ni CDN, avec une
politique de sécurité du contenu stricte (aucun script ni style en ligne). Pour l'automatisation ou les captures, le
lien `http://localhost:8080/#jeton=<jeton-admin>&vue=/postes` ouvre directement une session avec le jeton administrateur
(le jeton est retiré de l'adresse aussitôt ; le fragment `#` n'est jamais envoyé au serveur).

**Test de charge** (`tools/LoadTest`, outil de développement, ni dans l'image ni dans l'installateur) : parc fictif
réaliste (rapports de la forme de `spokapilote scan --json`, ~200 périphériques, ~195 Ko, 15 Ko compressés), puis
mesures (§ 11). Exemple sur un conteneur de test :

```powershell
$lt = "dotnet tools\LoadTest\bin\Release\net10.0\SpokaPilote.LoadTest.dll"
& $lt seed   --url http://127.0.0.1:18080 --agent <jeton-enrôlement> --machines 20000 --history 20 --concurrency 64
& $lt groups --url http://127.0.0.1:18080 --admin <jeton-admin> --machines 20000
& $lt bench  --url http://127.0.0.1:18080 --admin <jeton-admin> --agent <jeton-enrôlement> --machines 20000 --api paged --duration 180 --container <conteneur>
& $lt burst  --url http://127.0.0.1:18080 --agent <jeton-enrôlement> --machines 20000 --concurrency 200 --duration 60
```

## 11. Grands parcs

La console 4.0 est conçue pour des parcs de plusieurs dizaines de milliers de postes sur un seul conteneur et une base
SQLite. Ce qui la rend possible :

- **Rien n'est recalculé à partir des rapports** : les indicateurs de chaque poste (état, compteurs, santé, modèle,
  Windows...), son matériel sans pilote et son appartenance aux groupes par règle sont calculés **une fois**, à la
  réception de son rapport. Les listes, filtres, tris, la synthèse et les groupes lisent ces colonnes ; la synthèse du
  parc est gardée en mémoire et ne relit que les postes modifiés depuis le dernier calcul.
- **Pagination côté serveur** : la console ne charge jamais tout le parc (pages de 50 à 500 postes, recherche envoyée
  au serveur quand la saisie s'arrête, exports CSV écrits en continu). Les scripts qui appellent `GET /api/machines`
  sans pagination reçoivent toujours le tableau complet (14 Mo pour 20 000 postes).
- **Stockage compact** : rapports normalisés compressés, rapport brut conservé tel que le poste l'a envoyé (gzip),
  table des postes sans données volumineuses. Environ **46 Ko par poste** avec 20 rapports d'historique.
- **Écritures en file** : une écriture à la fois, sans attente active de SQLite ; une interrogation d'agent ne fait
  qu'une lecture (le « dernier contact » est écrit au plus une fois par minute).
- **Afflux maîtrisés** : au-delà de la capacité de traitement, les rapports attendent leur tour dans une file bornée,
  puis la console répond `503` avec un `Retry-After` **étalé au hasard** (30 à 90 s par défaut) au lieu de s'effondrer.

### Mesures

Banc d'essai : conteneur limité à **4 vCPU et 2 Go de RAM** (Docker Desktop, SSD), parc fictif de **20 000 postes ×
20 rapports** (395 000 rapports, ~195 Ko chacun avant compression), 27 groupes (sites, constructeurs, sous-réseau,
500 membres manuels). Charge mixte pendant 3 minutes : 33 interrogations d'agent par seconde (chaque poste toutes les
10 minutes), 2 rapports par seconde, 5 utilisateurs de la console qui enchaînent les pages. Temps de réponse en ms
(p50 / p95), 3.1 puis 4.0 sur la même base (migrée) :

| Requête | 3.1 | 4.0 |
|---|---:|---:|
| Interrogation de l'agent (`GET /api/agent/jobs`) | 27 / 1 150 | 1,6 / 3,2 |
| Envoi d'un rapport (`POST /api/reports`) | 43 / 1 269 | 3,0 / 9,8 |
| Vue d'ensemble (`/api/fleet?top=12` ; 3.1 : `/api/fleet`) | 1 321 / 2 533 | 42 / 108 |
| Synthèse complète (`/api/fleet`, 5,4 Mo) | 1 321 / 2 533 | 80 / 139 |
| Synthèse d'un groupe | 1 222 / 2 006 | 10 / 48 |
| Liste des postes, une page de 100 (3.1 : liste complète) | 1 605 / 2 635 | 4,5 / 7,4 |
| Recherche dans la liste (page de 100) | (dans le navigateur, après la liste complète) | 22 / 41 |
| Liste complète (`/api/machines`, 14 Mo) | 1 605 / 2 635 | 140 / 270 |
| Fiche d'un poste | 19 / 398 | 2,2 / 3,6 |
| Groupes (`/api/groups`) | 1 505 / 2 129 | 5,6 / 9,2 |
| Fiche d'un groupe | 1 272 / 2 154 | 18 / 64 |
| Matériel sans pilote (page ; 3.1 : `/api/fleet`) | 1 321 / 2 533 | 36 / 86 |
| Export CSV de tout le parc (5,9 Mo) | — | 274 / 421 |
| Mémoire du conteneur (max pendant la charge) | 1 520 Mo | 796 Mo |
| Processeur (moyenne pendant la charge, 4 vCPU = 400 %) | 324 % | 76 % |

Afflux d'envois (tous les postes envoient en même temps, rapports de 15 Ko compressés) :

| Scénario | 3.1 | 4.0 |
|---|---|---|
| 200 envois simultanés en continu (60 s) | 81 rapports/s, p50 2,3 s, p99 5,8 s | **660 rapports/s**, p50 0,28 s, p99 0,49 s |
| 1 000 envois simultanés (60 s) | 78 rapports/s, p50 11,3 s, p99 18,9 s | 597 rapports/s acceptés, p50 0,32 s, p99 0,68 s ; 3 % renvoyés à plus tard (`503` + `Retry-After`) |
| Création du parc (400 000 envois, 64 simultanés) | 92 rapports/s, p99 2,2 s | (non refait : base 3.1 migrée) |

Base et exploitation (20 000 postes × 20 rapports) :

| | 3.1 | 4.0 |
|---|---:|---:|
| Taille de la base | 1,83 Go | 0,92 Go (après compactage) |
| Migration 3.1 → 4.0 au démarrage | — | 14 s |
| Compression de l'historique en arrière-plan | — | 81 s |
| Compactage (`VACUUM`) | — | 17 s |
| Sauvegarde à chaud (`POST /api/admin/backup`) | — | 10 s |

**Essai à 100 000 postes** (même conteneur 4 vCPU / 2 Go, 97 000 postes × 2 rapports, base de 2,1 Go ; 111 interrogations
d'agent/s, 5 rapports/s, 5 utilisateurs, dont les routes complètes historiques) : agents 1,7 / 22 ms, envois
4,8 / 32 ms, page de la liste 10 / 23 ms, recherche 110 / 188 ms, fiche d'un poste 3 / 29 ms, groupes 21 / 57 ms ;
vue d'ensemble 728 / 1 544 ms et matériel sans pilote 794 / 1 727 ms (recalculés à chaque nouveau rapport) ; liste
complète historique (69 Mo) 1,0 / 1,4 s ; export CSV de tout le parc (28 Mo) 1,7 / 2,2 s ; afflux de 200 envois
simultanés : 620 rapports/s, p99 0,68 s ; création d'un groupe par règle : 0,3 s ; mémoire 1,6 Go (dont cache disque).

**Diffusion par anneaux (4.0)**, mesurée par le test `RolloutPerformanceTests` (serveur en mémoire, poste de développement,
20 000 postes, 3 anneaux par groupes, 300 versions de 60 familles en cours de diffusion) : politique effective servie à
2 000 postes pris au hasard **p50 0,33 ms, p95 0,78 ms, p99 1,2 ms** (un document par anneau, en cache ; une requête ne
fait que chercher l'anneau du poste) ; document de l'anneau le plus tardif 146 Ko, **7 Ko compressés** (anneau pilote :
0,5 Ko) ; recalcul complet de l'état de diffusion 2 ms ; enregistrement d'un rapport de 15 propositions et
5 installations p50 1,9 ms, p95 3,5 ms (coût proportionnel au rapport, pas au parc). Sur une console Docker alimentée par
`tools/LoadTest rollout` (20 000 postes, parc réaliste de 1 500 familles : 3 117 versions en cours de diffusion ; parc
extrême : 160 022) : décisions pour les 20 propositions d'un poste 4.0 p50 1,9 ms, p95 8,1 ms, 0,6 Ko envoyés et 1,2 Ko
reçus (compressés) ; politique d'un poste 4.0 5 Ko (suspensions seules), d'un poste 3.x 711 Ko au plus (2 000 règles,
plafond) ; avant, 1,9 Mo pour le parc réaliste et 14,6 Mo dès 3 000 postes pour le parc extrême (refusée par les postes :
plus de 2 Mo). Détail : [POLITIQUE.md, « Mesures »](POLITIQUE.md#mesures).

### Dimensionnement

| Parc | Processeur | Mémoire | Disque (base + 7 sauvegardes) |
|---|---|---|---|
| jusqu'à 5 000 postes | 2 vCPU | 1 Go | 2 Go |
| 5 000 à 20 000 postes | 2 à 4 vCPU | 2 Go | 10 Go (SSD) |
| 20 000 à 50 000 postes | 4 vCPU | 3 Go | 25 Go (SSD) |
| 50 000 à 100 000 postes | 4 à 8 vCPU | 4 Go | 50 Go (SSD) |

- **Disque** : ~46 Ko par poste avec 20 rapports d'historique (`SPOKAPILOTE_HISTORY`), dont ~15 Ko pour le dernier
  rapport complet. Comptez la base, une marge de 50 % pour le compactage et les sauvegardes de `/data/backups`
  (`SPOKAPILOTE_BACKUP_KEEP`). Un SSD est recommandé.
- **Capacité d'envoi** : ~620 à 660 rapports/s sur 4 vCPU (20 000 comme 100 000 postes), soit un parc de 20 000 postes
  qui envoie tout en même temps absorbé en ~30 s. Les analyses planifiées n'ont donc pas besoin d'être étalées pour la
  console ; elles le sont pour le réseau et pour les sources de pilotes.
- **Agents** : une interrogation coûte ~2 ms. 20 000 agents toutes les 15 minutes = 22 requêtes/s, moins de 5 % d'un cœur.
  Les tâches planifiées des postes ajoutent elles-mêmes un délai aléatoire (jusqu'à 1 minute) ; pour un très grand parc,
  préférez `agent enable --every 30`.
- **Validé jusqu'à 20 000 postes × 20 rapports** avec la charge décrite ci-dessus (tous les temps sous 0,3 s au p95
  hors exports complets) ; **essayé à 100 000 postes × 2 rapports** : la console fonctionne (postes, agents et pages
  rapides), mais la vue d'ensemble et le matériel sans pilote, recalculés sur tout le parc après chaque rapport, prennent
  0,7 à 1,5 s, et la liste complète sans pagination (`GET /api/machines` sans paramètre, anciens scripts) et l'export
  de tout le parc 1 à 2 s : utilisez la pagination, les filtres et les groupes. Au-delà de 100 000 postes, rien n'a été
  mesuré. Le facteur limitant de SQLite est l'écriture unique (~650 rapports/s ici) ; si un parc dépasse ce que ces
  chiffres permettent (par ex. plusieurs centaines de milliers de postes, ou des envois soutenus au-delà de 500/s),
  l'étape suivante serait une synthèse tenue à jour de façon incrémentale, puis une base serveur (PostgreSQL) — non
  nécessaires aux tailles mesurées.

### Afflux de postes (coupure de courant, campagne d'analyses)

Quand des milliers de postes démarrent en même temps, leurs envois et interrogations sont admis dans la limite de
`SPOKAPILOTE_MAX_CONCURRENT_UPLOADS` (nombre de processeurs) et `SPOKAPILOTE_MAX_CONCURRENT_AGENTS` (32), les suivants
attendent dans une file (`SPOKAPILOTE_UPLOAD_QUEUE`, `SPOKAPILOTE_AGENT_QUEUE`) au plus 20 s. Au-delà, réponse `503`
avec `Retry-After` entre la moitié et une fois et demie de `SPOKAPILOTE_RETRY_AFTER_SECONDS` (60 s) : les postes 4.0
réessaient au plus deux fois après ce délai (plus un aléa), les postes plus anciens renvoient le rapport au passage
suivant (l'envoi n'est jamais bloquant). La console reste consultable pendant un afflux : seules les requêtes des postes
sont limitées. Derrière un reverse proxy, laissez au proxy un délai d'attente d'au moins 30 s.

### Supervision

`GET /api/metrics` (Administrateur ou jeton administrateur) renvoie : nombre de postes, à traiter, non vus, agents vus
depuis 24 h ; taille de la base, du journal WAL et part de pages libres ; tâches en attente et en cours ; files des
postes (en attente, refusées depuis le démarrage) ; mémoire et processeur ; temps de réponse par route (p50, p95, p99
sur les 2 048 dernières requêtes) ; dernière sauvegarde. Avec `?format=prometheus`, les mêmes valeurs au format texte de
Prometheus, par exemple :

```yaml
scrape_configs:
  - job_name: spokapilote
    metrics_path: /api/metrics
    params: { format: [prometheus] }
    authorization: { credentials: "<jeton-admin>" }
    static_configs: [{ targets: ["spokapilote.mon-entreprise.fr"] }]
    scheme: https
```

À surveiller : `spokapilote_queue_reports_rejected_total` (afflux refusés : augmentez
`SPOKAPILOTE_MAX_CONCURRENT_UPLOADS` si le processeur n'est pas saturé), `spokapilote_database_free_ratio` (au-delà de
30 %, un compactage rendra de la place), `spokapilote_backup_last_success`, `spokapilote_http_latency_ms`. La sonde
publique `GET /api/health` reste la sonde de vie (elle ne révèle rien du parc).

### Entretien de la base

Automatique et par petits lots (jamais de long verrou) : tâches terminées de plus de 90 jours, journal d'audit, sessions
expirées, historique au-delà de `SPOKAPILOTE_HISTORY` rapports par poste (et de `SPOKAPILOTE_HISTORY_DAYS` jours si
défini) ; `PRAGMA optimize` (statistiques de l'optimiseur, `ANALYZE` si nécessaire) toutes les heures. Le **compactage**
(`VACUUM`) n'est jamais automatique : il bloque les écritures pendant sa durée (17 s pour 20 000 postes, les postes
reçoivent alors des `503` et réessaient). Lancez-le en période creuse après une grosse suppression ou après la migration
depuis la 3.1, quand `freeRatio` de `/api/metrics` dépasse 30 % : `POST /api/admin/vacuum`.

## 12. Cache de pilotes

Sans cache, 5 000 postes téléchargent chacun le même pilote NVIDIA de 1 Go, le même `CatalogPC.cab` de Dell, les mêmes
références HP ; et des milliers de postes derrière une même adresse IP publique se font limiter par le Microsoft Update
Catalog (« Too many search requests »). Avec la console 4.0 et des postes 4.0, **la console télécharge une fois, les
postes se servent chez elle** (réseau local), et elle relaie les catalogues avec une durée de conservation.

### Fonctionnement

- **Fichiers** (`GET /api/cache/file`, jeton d'enrôlement) : le poste demande l'adresse officielle du fichier (et
  l'empreinte publiée par la source quand elle existe). La console :
  - n'accepte que les adresses que le poste accepte lui-même : HTTPS, port standard, sans identifiants, vers la **même
    liste de domaines officiels** que le client (fichier source partagé, `OfficialHosts.cs`) ; chaque **redirection** est
    revérifiée, une redirection vers un autre domaine est refusée ;
  - télécharge **une seule fois** par adresse : les demandes simultanées rejoignent le téléchargement en cours et
    reçoivent les octets **au fil de l'eau** (pas d'attente de la fin, pas de mise en mémoire : quelques tampons de 1 Mo) ;
  - vérifie l'**empreinte publiée** (SHA-256, SHA-1 ou MD5) ; les derniers 64 Ko ne partent qu'après la vérification,
    et en cas de discordance la connexion est coupée et rien n'est gardé ;
  - range le fichier **sous son empreinte SHA-256** dans `/data/cache/blobs` : un même fichier atteint par deux adresses
    n'est stocké qu'une fois ; index dans `/data/cache/index.json` (rapproché du disque au démarrage) ;
  - sert ensuite le fichier depuis le disque, avec **`Range`** (reprise d'un transfert interrompu, `ETag` = empreinte) ;
  - supprime les fichiers **les moins récemment demandés** au-delà de `SPOKAPILOTE_CACHE_MAX_GB` ; un fichier plus
    volumineux que 90 % du cache n'est pas gardé (le poste le télécharge directement) ;
  - **ne remplit jamais le volume** : elle laisse toujours `SPOKAPILOTE_CACHE_MIN_FREE_GB` (10 Go) et au moins 5 % du
    volume libres pour la base, ses sauvegardes et son compactage ; en dessous, plus rien n'est mis en cache (la page
    Paramètres l'indique, `diskGuardActive` dans `/api/metrics`) et les postes téléchargent directement ;
  - limite ses téléchargements simultanés vers les sources (`SPOKAPILOTE_CACHE_MAX_DOWNLOADS`, 8) : au-delà, le poste
    télécharge directement.
- **Catalogues** (`/api/cache/catalog*`) : les postes 4.0.1 n'y demandent que ce qu'ils peuvent authentifier seuls —
  les catalogues `.cab` **signés** par leur éditeur : `CatalogPC.cab` et `DriverPackCatalog.cab` (Dell), références et
  catalogue des packs HP — et, à titre d'indication, les pages de **recherche** du Microsoft Update Catalog (clé : la
  recherche normalisée — casse et espaces). Le reste est toujours lu directement à la source (voir le modèle de
  sécurité ci-dessous). Chaque catalogue est gardé `SPOKAPILOTE_CACHE_CATALOG_HOURS` heures (12 par défaut) puis
  redemandé une fois pour tout le parc ; un catalogue absent de la source (`404`) est mémorisé une heure ; si la source
  est injoignable, la dernière copie est servie. Le poste date sa copie locale du moment où la console l'a obtenue :
  l'âge maximal compte depuis la source. (La console sait toujours relayer catalogues Lenovo, réponses NVIDIA et liens
  du Microsoft Update Catalog pour les postes 4.0.0, qui les demandent encore.)
- **Microsoft Update Catalog** : la console fait ses recherches deux à la fois, espacées de 400 ms. Une page
  « Too many search requests » **n'est jamais mise en cache** : la console suspend ses recherches 3 minutes et répond
  `429` + `Retry-After` (ou sert la copie précédente) ; le poste applique alors sa pause habituelle (et, en mode
  automatisé, attend puis reprend, comme pour une limitation directe). Une page inattendue (erreur, maintenance) est
  transmise sans être gardée.

### Modèle de sécurité : la console n'est qu'un transport

Une console compromise ne doit pas pouvoir faire installer à un poste ce qu'il n'aurait pas accepté de la source
officielle. Côté poste (`Online/ConsoleCache`, `Install/Downloader`) :

1. Seules les adresses que le poste juge officielles (`TrustPolicy`) sont demandées à la console ; le jeton
   d'enrôlement ne part que vers `ServerUrl` (pas de redirection suivie, HTTPS obligatoire hors localhost).
2. Un fichier reçu de la console n'est **gardé que si le poste peut le vérifier seul, avant tout usage** : empreinte
   publiée par la source et obtenue **directement** par le poste (catalogue Dell, HP, Lenovo, lien du Microsoft Update
   Catalog, MD5 de NVIDIA), ou à défaut **signature Authenticode valide de l'éditeur attendu pour ce domaine**
   (`.exe`, `.msi`, `.cab`, `.msu`) : un fichier de `us.download.nvidia.com` doit être signé par NVIDIA Corporation, un
   fichier de `downloads.dell.com` par Dell — pas seulement par « un » éditeur approuvé. La révocation du certificat est
   alors contrôlée en ligne : un certificat **révoqué** est refusé ; une révocation **invérifiable** (poste isolé,
   serveur de révocation injoignable) est tolérée et journalisée, pour ne pas bloquer les sites sans accès Internet
   direct. Un fichier sans empreinte et non signable (ex. `.zip`) ne passe jamais par la console.
3. Copie refusée → avertissement dans le journal du poste (« Copie fournie par la console REFUSÉE… ») et
   **téléchargement direct** depuis la source officielle ; les copies refusées par la console elle-même sont comptées
   dans `hashMismatches`.
4. Ensuite, **les contrôles habituels s'appliquent sans changement** : empreinte, signature Authenticode et éditeur
   approuvé de l'installeur, signature du catalogue `.cat` et ID matériel déclaré par l'INF.
5. **Catalogues et métadonnées** : elles désignent les installeurs à lancer, leurs empreintes et leurs **lignes de
   commande** (exécutées en SYSTEM) ; le poste n'en accepte de la console que ce qu'il peut authentifier seul.
   - Un `.cab` (Dell, HP) reçu de la console doit porter une signature Authenticode valide **de l'éditeur attendu**
     (Dell pour `downloads.dell.com`, HP pour `hpia.hpcloud.hp.com`), sinon il est refusé et téléchargé directement.
   - **Jamais par la console** (lus directement à la source, en HTTPS) : le catalogue Lenovo `{MTM}_Win11.xml`, les
     descripteurs de paquets Lenovo, le catalogue des packs Lenovo `catalogv2.xml` (XML sans signature vérifiable :
     le catalogue n'est pas signé et porte les empreintes des descripteurs, qui portent les lignes de commande), les
     réponses du service NVIDIA et les **liens de téléchargement du Microsoft Update Catalog** (dont le nom porte le
     SHA-1 du paquet). Ces requêtes sont légères (quelques Ko par paquet) et ne subissent pas la limitation des
     recherches. Choix retenu plutôt que la vérification de la signature XML des descripteurs Lenovo : elle ne suffirait
     pas (le catalogue qui les désigne n'est pas signé) et ajouterait une dépendance pour un gain marginal.
   - La **recherche** du Microsoft Update Catalog peut passer par la console (c'est elle que Microsoft limite par
     adresse IP) mais n'est qu'une **indication** : l'entrée retenue est résolue directement chez Microsoft (liens et
     SHA-1), et le paquet, d'où qu'il vienne, doit avoir ce SHA-1, être signé WHQL et déclarer l'ID matériel.
   - Les lignes de commande des catalogues (Dell, HP, Lenovo) ne passent plus par `cmd /c` : le programme doit être
     l'installeur téléchargé et vérifié ou un fichier extrait du paquet (`.exe`, ou script `.cmd`/`.bat` du paquet),
     sans enchaînement, redirection ni variable (`&`, `|`, `<`, `>`, `^`, `!`, `%` hors `%FILE%`/`%PACKAGEPATH%`/
     `%LOGPATH%`, retour à la ligne) ; il est lancé directement. Un paquet à la commande inattendue est écarté dès
     l'analyse et refusé à l'installation.

   Limite restante : la page de recherche relayée n'est pas authentifiée ; une console compromise pourrait donc
   **orienter le choix** vers une autre entrée officielle du Microsoft Update Catalog, signée WHQL et déclarant le
   matériel (par exemple une version plus ancienne, que Windows n'installe pas s'il juge le pilote actuel meilleur), ou
   servir un ancien catalogue `.cab` encore validement signé — jamais un fichier non officiel, non signé, destiné à un
   autre matériel ou une commande arbitraire. C'est moins que ce qu'une console compromise peut déjà faire (publier une
   politique de pilotes, lancer des actions à distance) ; pour l'exclure, `ConsoleCacheCatalogs = false` sur les postes
   (ou `SPOKAPILOTE_CACHE_CATALOGS=false`) garde les fichiers en cache et interroge les catalogues directement.
6. **Jamais d'échec à cause du cache** : console injoignable, trop lente (60 s sans en-têtes ou sans octet), en erreur,
   refusant le fichier (`403`, `502`, `503`), cache désactivé ou console 3.x → téléchargement direct (journalisé). Une
   console injoignable ou figée n'est plus sollicitée pendant 5 minutes ; une console 3.x pendant une heure. Une
   erreur **du poste** (fichier temporaire verrouillé, accès refusé, disque plein) donne aussi un téléchargement direct,
   sans mettre la console à l'écart. Un transfert interrompu d'un fichier en cache reprend une fois là où il s'était
   arrêté (`Range` + `If-Range`).

Les tests le prouvent : copie altérée (même taille, empreinte différente ; fichier non signé sans empreinte ; fichier
signé par un autre éditeur que celui de la source) refusée et remplacée par le fichier officiel, catalogue `.cab` non
signé ou signé par un autre éditeur refusé, XML Lenovo, réponses NVIDIA et liens du Microsoft Update Catalog jamais
demandés à la console, paquet du Microsoft Update Catalog servi par la console vérifié par le SHA-1 obtenu chez
Microsoft, descripteur Lenovo ou SoftPaq HP à la commande injectée écarté, redirection vers un domaine non officiel
refusée par la console, empreinte publiée différente → rien en cache.

### Réglages

**Console** (variables d'environnement, ou page **Paramètres** → « Cache de pilotes et de catalogues » ; une variable
définie y est verrouillée) : `SPOKAPILOTE_CACHE_ENABLED`, `SPOKAPILOTE_CACHE_MAX_GB`, `SPOKAPILOTE_CACHE_MIN_FREE_GB`,
`SPOKAPILOTE_CACHE_CATALOGS`, `SPOKAPILOTE_CACHE_CATALOG_HOURS`, `SPOKAPILOTE_CACHE_FILE_DAYS`,
`SPOKAPILOTE_CACHE_MAX_DOWNLOADS`, `SPOKAPILOTE_CACHE_DIR` (§ 1). La console doit pouvoir joindre les sources
officielles en HTTPS (directement ou par `HTTPS_PROXY`). La page affiche la taille, le taux de réussite, les octets
évités, l'espace libre, les fichiers les plus demandés ; **Vider le cache** (tout, paquets ou catalogues) et chaque
modification des réglages sont inscrits au journal d'audit.

**Cache actif par défaut**, parce qu'il est sûr par construction (transport seulement, vérification indépendante sur
chaque poste, repli automatique) et que son bénéfice est maximal sans rien configurer. Il ne consomme rien tant
qu'aucun poste 4.0 ne s'en sert, et il ne prend jamais la place réservée à la base. Désactivez-le
(`SPOKAPILOTE_CACHE_ENABLED=false`) si la console est hébergée hors du réseau des postes (cloud, autre site) : les
postes iraient chercher les gros fichiers chez elle plutôt que sur les CDN des constructeurs.

**Postes** (`settings.json`, déployable par GPO / Intune, ou page Paramètres de l'application) :

| Réglage | Défaut | Rôle |
|---|---|---|
| `UseConsoleCache` | `auto` | `auto` : utilisé quand `ServerUrl` et `ServerToken` sont configurés et que le cache de la console est actif ; `on` : idem, avec un avertissement dans le journal à chaque repli sur le téléchargement direct ; `off` : jamais |
| `ConsoleCacheCatalogs` | `true` | Demander aussi à la console les catalogues `.cab` signés et les recherches du Microsoft Update Catalog (voir la limite du point 5 ci-dessus) |

```powershell
spokapilote config set UseConsoleCache auto
spokapilote server status
#   ✓ Cache de pilotes : actif, 12,4 Go sur 20, 93 % de réussite, 380,2 Go évités ; catalogues relayés
```

**Préchargement** (site isolé, réseau à préserver aux heures de bureau) : page Paramètres, ou
`POST /api/cache/prefetch` avec `{ "models": ["Latitude 7440"] }` ou `{ "all": true }`. La console lit les derniers
rapports des postes de ces modèles (25 par modèle au plus), télécharge leurs catalogues constructeur (Dell, référence HP
de la plateforme et de la version de Windows, catalogue Lenovo du type de machine) puis les paquets qui leur sont
proposés (dans la limite de 80 % du cache). Les candidats du Microsoft Update Catalog, résolus à l'installation, n'y
figurent pas.

### Compatibilité

- **Postes 3.x avec une console 4.0** : inchangés (ils ignorent le cache et téléchargent directement).
- **Postes 4.0 avec une console 3.x** : `GET /api/cache/status` répond `404` ; le poste le note pour une heure et
  télécharge directement, sans message d'erreur.
- **Mode portable** : facultatif, voir [MODE-PORTABLE.md](MODE-PORTABLE.md) § 7.
- Volume : rien à faire ; le dossier `/data/cache` est créé au démarrage. Il n'a pas besoin d'être sauvegardé (il se
  reconstitue), et la sauvegarde à chaud (`/data/backups`) ne le contient pas.

### Mesures

Banc d'essai (`DriverCacheBenchmark`, lancé à la demande avec `SPOKAPILOTE_CACHE_BENCH=1 dotnet test
tests\SpokaPilote.Server.Tests --filter DriverCacheBenchmark`) : vraie console (Kestrel) sur la boucle locale, source
« officielle » simulée par un serveur local que seul le test substitue à `downloads.dell.com`, **200 postes demandent en
même temps le même fichier de 500 Mo** ; console, postes et source dans le même processus, sur un poste de travail
(Core i7-11700K, 16 fils, 32 Go, SSD NVMe).

| Scénario | Durée totale | Premier octet (p50 / p95) | Fin par poste (p50 / p95) | Requêtes vers la source | Mémoire (pic, tout compris) |
|---|---:|---:|---:|---:|---:|
| Cache vide, source illimitée | 46,2 s | 0,56 / 0,59 s | 44,9 / 46,1 s | **1** (500 Mo) | 571 Mo |
| Cache vide, source à 800 Mbit/s | 47,9 s | 1,19 / 1,24 s | 46,7 / 47,7 s | **1** (500 Mo) | 569 Mo |
| Fichier en cache | 52,0 s | 0,06 / 0,16 s | 51,4 / 51,9 s | 0 | 595 Mo |

- 97,7 Go servis par passage (~2 Go/s agrégés, limite de la boucle locale et du processeur partagé par les 200 postes
  simulés) ; **une seule requête vers la source** pour les 200 postes ; empreinte SHA-256 vérifiée côté poste.
- Les postes reçoivent les octets pendant que la console télécharge : avec une source à 800 Mbit/s (5 s pour 500 Mo),
  la durée est celle de la distribution sur le réseau local, pas celle de la source.
- La mémoire ne dépend pas de la taille du fichier (tampons de 1 Mo par poste, aucun fichier en mémoire).
- Sur un vrai réseau, le débit est celui du lien de la console : un lien de 10 Gbit/s sert ces 200 postes en ~80 s,
  au lieu de 97,7 Go téléchargés depuis Internet.
