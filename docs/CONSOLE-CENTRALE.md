# Console centrale SpokaPilote Server

SpokaPilote Server est une console web auto-hébergée : chaque poste enrôlé y envoie le rapport de ses analyses et
installations, et l'équipe IT suit le parc en temps réel depuis un navigateur.

- **Vue d'ensemble** : postes sains / à traiter, postes non vus depuis 7 jours, matériel sans pilote, pannes
  matérielles, pilotes bloqués par la politique, échecs d'installation.
- **Postes** : liste filtrable et triable ; fiche détaillée (système, périphériques en erreur, pilotes proposés ou
  bloqués, résultats d'installation, historique des derniers rapports, téléchargement du rapport complet pour
  `spokapilote pack`).
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
| `SPOKAPILOTE_DATA_DIR` | `/data` | Dossier des données (à ne changer qu'hors conteneur) |

Avec Compose, placez-les dans un fichier `server/.env` (non versionné), par exemple :

```dotenv
SPOKAPILOTE_ADMIN_TOKEN=...
SPOKAPILOTE_AGENT_TOKEN=...
SPOKAPILOTE_STALE_DAYS=14
```

## 2. Jetons

La console utilise deux jetons, transmis dans l'en-tête `Authorization: Bearer <jeton>` et comparés en temps constant :

| Jeton | Qui l'utilise | Ce qu'il permet |
|---|---|---|
| **Administrateur** | l'équipe IT (console web, scripts d'administration) | tout : postes, synthèse, suppression, politique, profil d'imprimantes |
| **Enrôlement** | chaque poste (`ServerToken`) | envoyer des rapports, lire la politique et le profil d'imprimantes |

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
schtasks /Create /F /TN "SpokaPilote\Analyse" /SC WEEKLY /D MON /ST 09:00 /RU SYSTEM /TR "\"$exe\" scan --quiet"
```

**Reprise de l'existant** : les rapports déjà déposés sur un partage par `--output-dir` (ancienne méthode
`fleet-report`) s'importent depuis n'importe quel poste enrôlé :

```powershell
spokapilote server upload \\srv\rapports
```

Un rapport plus ancien que celui déjà connu pour un poste rejoint son historique sans remplacer son état actuel.

## 4. HTTPS (obligatoire en production)

Les postes **refusent** d'envoyer leurs rapports et leur jeton en HTTP, sauf vers `localhost` / `127.0.0.1` (essais).
Ils refusent aussi les redirections : `ServerUrl` doit être l'adresse HTTPS finale. Le conteneur ne gère pas lui-même
de certificat : publiez-le derrière un reverse proxy HTTPS, et n'exposez le port 8080 qu'à ce proxy
(`"127.0.0.1:8080:8080"` dans `docker-compose.yml` si le proxy tourne sur le même hôte).

Exemple **Caddy** (certificat Let's Encrypt automatique, ou `tls internal` / certificat de l'AD CS en interne) :

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

Pour que les journaux affichent l'adresse réelle des postes plutôt que celle du proxy, ajoutez la variable
`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (n'activez-la que si le conteneur n'est joignable que par le proxy).

Si le certificat est émis par une autorité interne, elle doit être approuvée par les postes (magasin
« Autorités de certification racines de confiance », généralement déjà déployé par GPO) ; sinon
`spokapilote server status` affiche « certificat HTTPS refusé ».

## 5. Sauvegarde et restauration

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

**Mise à jour** du serveur : `git pull` puis `docker compose -f server/docker-compose.yml up -d --build` ; le schéma
de la base est migré automatiquement au démarrage.

## 6. API

Toutes les routes sont sous `/api` ; réponses JSON (camelCase).

| Méthode et route | Jeton | Rôle |
|---|---|---|
| `GET /api/health` | aucun | Sonde de vie (`{"status":"ok","version":"…"}`) |
| `GET /api/whoami` | poste ou admin | Vérifie un jeton (`{"access":"agent"\|"admin"}`) |
| `POST /api/reports` | poste | Rapport `spokapilote scan --json` (JSON, éventuellement `Content-Encoding: gzip`) |
| `GET /api/fleet` | admin | Synthèse du parc (indicateurs, postes à traiter, matériel sans pilote, modèles, postes non vus) |
| `GET /api/machines` | admin | Liste des postes (indicateurs du dernier rapport) |
| `GET /api/machines/{nom}` | admin | Fiche du poste, dernier rapport normalisé, historique |
| `GET /api/machines/{nom}/reports/{id}` | admin | Un rapport de l'historique |
| `GET /api/machines/{nom}/raw` | admin | Dernier rapport complet tel qu'envoyé (pour `spokapilote pack`) |
| `DELETE /api/machines/{nom}` | admin | Retire le poste et son historique |
| `GET /api/enrollment` | admin | Jeton d'enrôlement et réglages de rétention |
| `GET /api/policy` | poste ou admin | Politique de pilotes (JSON brut ; `204` si aucune) |
| `PUT /api/policy` / `DELETE /api/policy` | admin | Enregistre (JSON valide exigé, 1 Mo max) / supprime la politique |
| `GET`, `PUT`, `DELETE /api/printer-profile` | idem | Profil d'imprimantes (même fonctionnement) |

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

## 7. Développement

```powershell
dotnet run --project server\SpokaPilote.Server --urls http://localhost:8080   # données dans server\SpokaPilote.Server\data
dotnet test tests\SpokaPilote.Server.Tests
```

La console web (`server/SpokaPilote.Server/wwwroot`) est en HTML/CSS/JavaScript sans framework ni CDN, avec une
politique de sécurité du contenu stricte (aucun script ni style en ligne). Pour l'automatisation ou les captures, le
lien `http://localhost:8080/#jeton=<jeton-admin>&vue=/postes` ouvre directement une session (le jeton est retiré de
l'adresse aussitôt ; le fragment `#` n'est jamais envoyé au serveur).
