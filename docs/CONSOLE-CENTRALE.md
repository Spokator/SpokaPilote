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
- conserve toutes ses données dans le volume **`/data`** (`spokapilote.db`, `admin-token.txt`, `agent-token.txt`) ;
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
| Mettre à jour SpokaPilote (`self-update`) | Télécharge la dernière version officielle (taille et SHA-256 vérifiés) et l'installe | oui (vérifie seulement) |

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
| Paramètres et alertes | réglages des alertes, alerte de test |
| Politique et profil d'imprimantes | enregistrement, suppression |
| Postes | suppression d'un poste |
| Journal d'audit | export CSV |

Les actions faites avec le jeton administrateur apparaissent sous « Jeton administrateur ». Filtres par catégorie et
recherche libre ; **Exporter (CSV)** produit un fichier UTF-8 séparé par des `;` (ouverture directe dans Excel ; une
valeur commençant par `=`, `+`, `-` ou `@` est neutralisée). Rétention : 5 000 entrées et 180 jours au plus
(`SPOKAPILOTE_AUDIT_MAX_ENTRIES`, `SPOKAPILOTE_AUDIT_DAYS`) ; les plus anciennes sont supprimées automatiquement. Les
mots de passe, jetons et adresses de webhook n'y figurent jamais.

## 8. Sauvegarde et restauration

Toutes les données sont dans le volume `/data`. La base SQLite est en mode WAL : sauvegardez-la **conteneur arrêté**,
ou copiez les trois fichiers `spokapilote.db*` ensemble.

```bash
# Sauvegarde (archive datée dans le dossier courant)
docker compose -f server/docker-compose.yml stop
docker run --rm -v server_spokapilote-data:/data -v "$PWD":/backup alpine \
  tar czf /backup/spokapilote-$(date +%F).tar.gz -C /data .
docker compose -f server/docker-compose.yml start

# Restauration
docker compose -f server/docker-compose.yml stop
docker run --rm -v server_spokapilote-data:/data -v "$PWD":/backup alpine \
  sh -c "rm -rf /data/* && tar xzf /backup/spokapilote-AAAA-MM-JJ.tar.gz -C /data && chown -R 1654:1654 /data"
docker compose -f server/docker-compose.yml start
```

Le nom du volume dépend du nom du projet Compose (`server_spokapilote-data` par défaut ; `docker volume ls` pour le
vérifier). L'archive contient les jetons : conservez-la en lieu sûr.

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

## 9. API

Toutes les routes sont sous `/api` ; réponses JSON (camelCase).

Colonne **Accès** : « admin » = jeton administrateur ou compte Administrateur ; « tech » = Technicien et au-delà ;
« lecture » = tout compte (et le jeton administrateur) ; « poste » = jeton d'enrôlement. Avec une session (cookie), les
requêtes POST, PUT et DELETE exigent l'en-tête `X-SpokaPilote-CSRF` (§ 2.1).

| Méthode et route | Accès | Rôle |
|---|---|---|
| `GET /api/health` | aucun | Sonde de vie (`{"status":"ok","version":"…"}`) |
| `GET /api/whoami` | poste ou lecture | Vérifie un jeton (`{"access":"agent"\|"admin"}` ; `"user"` pour une session) |
| `POST /api/session` | aucun | Connexion d'un compte : `{ "login", "password" }`, en-tête `X-SpokaPilote-CSRF` (valeur libre) ; dépose le cookie et renvoie l'appelant et son jeton anti-CSRF (`csrf`) |
| `GET /api/session` | lecture | Appelant courant (compte ou jeton), rôle, droits (`permissions`), jeton anti-CSRF |
| `DELETE /api/session` | lecture | Déconnexion |
| `PUT /api/session/password` | lecture (compte) | Changer son mot de passe : `{ "currentPassword", "newPassword" }` |
| `POST /api/reports` | poste | Rapport `spokapilote scan --json` (JSON, éventuellement `Content-Encoding: gzip`) |
| `GET /api/fleet?group=` | lecture | Synthèse du parc ou d'un groupe (indicateurs, postes à traiter, matériel sans pilote, modèles, postes non vus) |
| `GET /api/machines?group=` | lecture | Liste des postes (indicateurs du dernier rapport, `groups` : identifiants des groupes, `lastIp`) |
| `GET /api/machines/{nom}` | lecture | Fiche du poste, dernier rapport normalisé, historique, groupes |
| `GET /api/machines/{nom}/reports/{id}` | lecture | Un rapport de l'historique |
| `GET /api/machines/{nom}/raw` | lecture | Dernier rapport complet tel qu'envoyé (pour `spokapilote pack`) |
| `DELETE /api/machines/{nom}` | admin | Retire le poste et son historique |
| `GET /api/enrollment` | admin | Jeton d'enrôlement et réglages de rétention |
| `GET /api/policy` | poste ou lecture | Politique de pilotes (JSON brut ; `204` si aucune) |
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
| `GET /api/groups/{id}` | lecture | Groupe, membres (`manual`, `byRule`), planifications qui l'utilisent |
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
