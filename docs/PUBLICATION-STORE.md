# Publier SpokaPilote sur le Microsoft Store

Le Store signe gratuitement le paquet MSIX : plus d'avertissement SmartScreen, mises à jour automatiques,
installation possible en entreprise via Intune / Microsoft Store pour entreprises / `winget install`.

## 1. Compte développeur

1. Créez un compte sur [Partner Center](https://partner.microsoft.com/dashboard/registration) (compte **individuel**
   gratuit, ou compte **société** si SpokaPilote doit être publié au nom d'une entreprise — vérification d'identité requise).
2. **Applications et jeux → Nouveau produit → Application MSIX ou PWA**, réservez le nom **SpokaPilote**.
3. Dans **Gestion des produits → Identité du produit**, notez :
   - `Package/Identity/Name` (ex. `12345Editeur.SpokaPilote`)
   - `Package/Identity/Publisher` (ex. `CN=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX`)
   - `Package/Properties/PublisherDisplayName`

## 2. Construire le paquet

```powershell
.\packaging\build-msix.ps1 -Version 0.1.0.0 `
    -IdentityName "12345Editeur.SpokaPilote" `
    -Publisher "CN=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX" `
    -PublisherDisplayName "Votre nom"
```

Résultat : `out\SpokaPilote_0.1.0.0_x64.msix` (non signé : c'est voulu, le Store le signe).
Le numéro de version doit augmenter à chaque soumission (le 4ᵉ nombre doit rester 0 pour le Store).

## 3. Capacités restreintes à justifier

Le manifeste déclare deux capacités restreintes. Dans la soumission (**Propriétés → Capacités restreintes**),
fournissez une justification. Texte proposé :

> **runFullTrust** : SpokaPilote est une application de bureau (WPF) de gestion de pilotes. Elle interroge WMI
> pour inventorier le matériel, utilise l'API Windows Update Agent et l'outil Microsoft pnputil pour installer
> des pilotes signés.
>
> **allowElevation** : l'installation de pilotes de périphériques et d'imprimantes est une opération système qui
> exige les droits administrateur. L'application démarre sans élévation (inventaire et recherche) et ne demande
> l'élévation UAC qu'à l'action explicite de l'utilisateur (« Installer »). Seuls des pilotes signés provenant de
> Microsoft (Windows Update / Microsoft Update Catalog) et des constructeurs (Dell, HP, Lenovo) sont installés,
> après vérification de leur signature numérique.

## 4. Fiche Store

- **Catégorie** : Outils de développement / Utilitaires et outils
- **Description courte** : *Pilotes de confiance pour tout le poste et les imprimantes d'entreprise, depuis les sources officielles uniquement.*
- **Captures** : lancez `SpokaPilote.exe --screenshot <dossier> --scan` pour générer une capture de chaque écran.
- **Logo** : `packaging\Assets\StoreListing300.png` (300×300) ; régénérez les visuels avec `tools\make-assets.ps1`.
- **Politique de confidentialité** (URL obligatoire) : SpokaPilote ne collecte aucune donnée personnelle ; il contacte
  uniquement les serveurs de Microsoft et des constructeurs pour télécharger des catalogues et des pilotes.

## 5. Tester le paquet localement avant soumission

```powershell
.\packaging\build-msix.ps1 -SignForTest
```
Puis, dans une console administrateur : importez `out\SpokaPilote-test.cer` dans
`Ordinateur local → Personnes autorisées` (`certlm.msc`), et double-cliquez sur le `.msix`.
La commande `spokapilote` devient disponible dans tous les terminaux (alias d'exécution).

## Version portable (sans Store)

`out\SpokaPilote-<version>-portable-x64.zip` : à décompresser sur une clé USB. Non signée : Windows SmartScreen
peut afficher un avertissement au premier lancement. Pour la distribuer largement hors Store, signez les `.exe`
avec un certificat de signature de code (Azure Trusted Signing est l'option la moins chère).
