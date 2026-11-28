# Guide de l'interface

L'interface de SpokaPilote tient en six pages, dans la barre de gauche (Ctrl+1 à Ctrl+6). Chaque page dit en une ligne
à quoi elle sert et quoi faire ensuite ; **F1** ouvre la section de ce guide qui décrit la page affichée.

| Page | Pour quoi faire |
|---|---|
| [Accueil](#accueil) | Choisir le type d'analyse, analyser le poste, installer ce qui est proposé |
| [Pilotes](#pilotes) | Voir la liste détaillée, cocher ou décocher, comprendre un pilote bloqué |
| [Périphériques](#périphériques) | Comprendre et réparer un périphérique en erreur |
| [Historique](#historique) | Revoir et annuler une intervention, réinstaller une sauvegarde |
| [Outils](#outils) | Entretenir ce poste ; préparer et suivre un parc (avancé) |
| [Paramètres](#paramètres) | Sources de pilotes, filets de sécurité, console centrale, langue |

Raccourcis : **F5** relance une analyse (du même type que la dernière fois), **Ctrl+1 à Ctrl+6** changent de page,
**F1** ouvre l'aide de la page affichée. Tout se fait aussi au clavier (Tab, Espace, Entrée) et avec un lecteur d'écran.

## Accueil

Le modèle du PC, sa note de santé (A à E) et **une seule action principale** dont le libellé suit l'état :
« Analyser ce poste », puis « Installer N pilotes », puis « Tout est à jour ».

### Analyse rapide ou complète

Le type d'analyse se choisit **avant** de lancer, sur l'Accueil (et dans l'assistant de premier lancement) :

| | Analyse rapide (recommandée) | Analyse complète |
|---|---|---|
| Pilotes manquants (périphériques en erreur) | toutes les sources | toutes les sources |
| Mises à jour proposées par le dépôt, le constructeur du PC (Dell, HP, Lenovo), NVIDIA et Windows Update | oui | oui |
| Pilotes de fabricant **déjà installés** comparés au Microsoft Update Catalog | non | oui |
| Durée | en général moins de 2 minutes | plus long, jusqu'à plusieurs minutes selon le matériel |

L'analyse complète ne propose, pour un composant qui fonctionne, qu'une version **plus récente du même fabricant** ; un
pilote intégré à Windows n'est jamais remplacé. Elle correspond à `spokapilote scan --deep` en ligne de commande. Elle
demande que la source « Microsoft Update Catalog » soit cochée dans les Paramètres (sinon l'Accueil le signale).

Le choix est retenu pour les analyses suivantes (F5, « Analyser à nouveau »). Après chaque analyse, une ligne dit ce
qui a été couvert (et les sources désactivées dans les Paramètres), avec le bouton **Lancer l'analyse complète** après
une analyse rapide, ou **Relancer en analyse rapide** après une analyse complète.

### Après l'analyse

- **Installer N pilotes** installe les pilotes cochés (page Pilotes), après vérification de chacun ; les droits
  administrateur sont demandés si besoin.
- **Voir la liste des pilotes** ouvre la page Pilotes ; **N périphérique(s) en erreur** ouvre la page Périphériques.
- **Points importants** : les constats de la note de santé, l'état du BIOS (jamais mis à jour sans demande explicite).
- Si des pilotes sont bloqués ou retenus par la politique de pilotes, l'Accueil le dit et renvoie à Pilotes › Bloqués.

## Pilotes

La liste de ce que l'analyse propose : cochez ce que vous voulez installer, puis **Installer la sélection**. Les pilotes
optionnels et les BIOS ne sont jamais cochés d'office.

- **Filtres** : Tout, Manquants, Mises à jour, Bloqués. Cliquer sur une ligne affiche son détail (périphériques
  concernés, date, taille, page officielle).
- **Bloqué** : la politique de pilotes de votre organisation, ou la liste de référence des pilotes connus pour
  provoquer des écrans bleus, interdit ce pilote. **Retenu** : la console le diffuse par étapes (anneaux) et le
  proposera à ce poste à la date indiquée. La raison exacte est écrite sous le nom du pilote ; ces pilotes ne peuvent
  pas être cochés. Voir [POLITIQUE.md](POLITIQUE.md).
- **Avant d'installer** : point de restauration et sauvegarde des pilotes actuels (cochés par défaut, réglables dans
  les Paramètres).
- **Vérifier sans installer** télécharge et vérifie les pilotes cochés (empreinte, signature, compatibilité) sans rien
  installer.
- **Exporter le rapport** : rapport HTML ou JSON de ce poste.
- Le volet **Détails techniques** (lecture seule) liste les sources interrogées et la politique appliquée.

## Périphériques

Le matériel détecté et son état. Les périphériques en erreur sont affichés d'abord ; le premier est sélectionné et sa
**cause** s'affiche à droite, avec la **réparation conseillée** (choix de l'action, puis **Réparer ce périphérique**).
La réparation est enregistrée dans l'Historique. Filtre « Sans pilote trouvé » : en erreur, et aucune source officielle
n'a trouvé de pilote. La case « Inclure les périphériques logiciels et virtuels » complète l'inventaire.
Détail des actions : [OUTILS-TECHNICIEN.md, section 2](OUTILS-TECHNICIEN.md#2-réparation-guidée-dun-périphérique).

## Historique

Tout ce que SpokaPilote a modifié sur ce poste : installations, réparations, nettoyages. Chaque ligne dit quoi, quand,
et si l'intervention peut encore être annulée.

- **Annuler cette intervention** retire les pilotes qu'elle a ajoutés : chaque périphérique revient à son pilote
  précédent (droits administrateur).
- **Sauvegardes de pilotes** : les pilotes en place sont sauvegardés avant chaque installation. **Réinstaller cette
  sauvegarde** est le recours si l'annulation ne suffit pas (une confirmation est demandée).

Détail : [OUTILS-TECHNICIEN.md, section 5](OUTILS-TECHNICIEN.md#5-retour-arrière-et-sauvegardes-401).

## Outils

Rien ici n'est nécessaire pour équiper un poste. La page est rangée par usage.

**Pour ce poste**

- **Nettoyage du magasin de pilotes** : « Analyser le magasin », puis « Nettoyer... » (sauvegarde avant suppression,
  jamais un pilote utilisé).
- **Maintenance planifiée** : tâche Windows (compte SYSTEM) qui entretient le poste seule ; quand, quoi, puis
  « Activer ». Voir [IMPRIMANTES-ET-PLANIFICATION.md](IMPRIMANTES-ET-PLANIFICATION.md).
- **Journal technique** : le détail de la session, et le dossier des journaux pour le support.

**Pour le parc (avancé)**

- **Dépôt de pilotes** : dossiers consultés en premier, même hors ligne ; ajouter un dépôt, puis exporter les pilotes
  d'un poste de référence, importer un pilote téléchargé ou télécharger un pack constructeur.
- **Packs constructeur** (Dell, HP, Lenovo, Surface) : rangés dans le dépôt. Voir [PACKS-ET-IMAGES.md](PACKS-ET-IMAGES.md).
- **Console centrale et agent** : état de la connexion (réglée dans les Paramètres) et activation de l'agent, qui
  exécute les actions demandées par la console. Voir [CONSOLE-CENTRALE.md](CONSOLE-CENTRALE.md).
- **Pack hors ligne** et **Rapport de parc (sans console)**.

**Autres outils** : les imprimantes sont désormais gérées par SpokaPrint.

## Paramètres

Un changement s'applique tout de suite ; **Enregistrer** le garde pour les prochains démarrages et les tâches
planifiées (la configuration commune du poste ne s'enregistre qu'en administrateur).

1. **Sources de pilotes** : où chercher (Windows Update, Microsoft Update Catalog — nécessaire pour l'analyse
   complète —, catalogues des constructeurs, pilotes graphiques, BIOS et firmware). Une source décochée est signalée
   avec les résultats de l'analyse.
2. **Avant chaque installation** : point de restauration, sauvegarde des pilotes.
3. **Console centrale (parc)** : adresse, jeton d'enrôlement, « Tester la connexion ». Les **options avancées**
   (source de la politique de pilotes, cache de la console) sont dans un volet replié.
4. **Langue** de l'interface (français, anglais ou langue de Windows).
5. **À propos et mises à jour**.

## Parcours courants

| Je veux… | Comment |
|---|---|
| Équiper un poste | Accueil : Analyse rapide › Analyser ce poste › Installer N pilotes |
| Vérifier aussi les pilotes déjà installés | Accueil : Analyse complète › Analyser ce poste (ou « Lancer l'analyse complète » après une analyse rapide) |
| Savoir pourquoi un pilote n'est pas proposé à l'installation | Pilotes › Bloqués : raison sous le nom du pilote |
| Revenir en arrière | Historique › sélectionner l'intervention › Annuler cette intervention |
| Réparer un périphérique | Accueil › « N périphérique(s) en erreur » › Réparer ce périphérique |
| Ajouter un dépôt de pilotes | Outils › Pour le parc › Dépôt de pilotes › Ajouter un dépôt... |
| Relier le poste à la console | Paramètres › Console centrale : adresse, jeton › Enregistrer › Tester la connexion |
