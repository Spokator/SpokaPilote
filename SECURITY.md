# Politique de sécurité

SpokaPilote installe des pilotes, c'est-à-dire du code qui s'exécute au cœur de Windows avec les droits les plus
élevés. Nous prenons tout signalement de sécurité très au sérieux.

## Versions prises en charge

| Version | Correctifs de sécurité |
|---|---|
| 2.0.x (dernière version) | oui |
| 1.0.x | non : mettez à jour (la mise à jour automatique propose la dernière version) |

La console centrale (`ghcr.io/spokator/spokapilote-server`) suit la même règle : seule l'image `latest` / la dernière
version reçoit des correctifs.

## Signaler une vulnérabilité

**N'ouvrez pas de ticket public.** Utilisez le signalement privé de GitHub :
<https://github.com/Spokator/SpokaPilote/security/advisories/new> (onglet **Security → Report a vulnerability**).

Indiquez si possible :

- la version de SpokaPilote (`spokapilote version`) ou de la console (`GET /api/health`) ;
- le composant concerné (application, ligne de commande, installeur, console centrale) ;
- les étapes pour reproduire, et l'impact constaté ou supposé ;
- toute preuve de concept utile (sans données personnelles réelles).

## Ce qui nous intéresse en priorité

- Contournement de la chaîne de contrôle : installation d'un pilote venant d'une source non officielle, non signé,
  signé par un éditeur non approuvé, à l'empreinte incorrecte ou ne correspondant pas au matériel.
- Contournement de la politique de pilotes (règle de blocage ignorée).
- Élévation de privilèges via l'application, la tâche planifiée (compte SYSTEM) ou l'installeur.
- Mise à jour automatique : installation d'un fichier autre que l'installeur officiel publié sur GitHub.
- Console centrale : accès sans jeton, accès administrateur avec le jeton d'enrôlement, injection, fuite de données
  entre postes.

## Notre engagement

- Accusé de réception sous **5 jours ouvrés**.
- Première évaluation (gravité, versions touchées) sous **15 jours**.
- Correctif publié dès que possible, avec un avis de sécurité GitHub qui vous crédite (si vous le souhaitez).

Merci de nous laisser le temps de publier un correctif avant toute divulgation publique.
