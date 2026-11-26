# Signature de code des installeurs

Aujourd'hui, les installeurs de SpokaPilote ne sont pas signés : au premier lancement, Windows SmartScreen peut afficher
« Windows a protégé votre ordinateur » et la fenêtre d'administration (UAC) indique « Éditeur : inconnu ».

Tout est prêt pour signer **automatiquement** dès que vous disposez d'un certificat : il suffit de définir **une seule
variable d'environnement** sur le PC qui construit les versions. Sans cette variable, rien ne change.

## Ce qui est signé

| Fichier | Quand |
|---|---|
| `SpokaPilote.App.exe`, `spokapilote.exe` et leurs DLL (`SpokaPilote.*.dll`, `spokapilote.dll`) | à chaque publication (`scripts\publish.ps1`) |
| Installeurs `SpokaPilote-Setup-<version>.exe` et `SpokaPilote-Installer-<version>-arm64.exe` | `scripts\build-installer.ps1` (Inno Setup) |
| Désinstalleur (`unins000.exe` installé sur les postes) | idem : Inno Setup le signe aussi |
| Paquets MSIX hors Store | `scripts\build-store.ps1 -Sign` |

Les fichiers du runtime .NET et des bibliothèques Microsoft ne sont pas re-signés : ils portent déjà la signature de
Microsoft. Chaque signature utilise **SHA-256** et un **horodatage RFC 3161** : elle reste valable après l'expiration du
certificat. Après signature, chaque fichier est vérifié ; la construction s'arrête au moindre problème.

`signtool.exe` est téléchargé automatiquement (paquet NuGet officiel `Microsoft.Windows.SDK.BuildTools`) : aucun SDK
Windows à installer. Les noms des installeurs ne changent pas : la mise à jour automatique des postes continue de
fonctionner (elle vérifie le SHA-256 publié par GitHub, calculé sur le fichier signé).

## Quelle option choisir ?

| Option | Pour qui | Coût indicatif (fin 2026, à vérifier) | Effet sur SmartScreen |
|---|---|---|---|
| **Microsoft Store** ([PUBLICATION-STORE.md](PUBLICATION-STORE.md)) | tout le monde | compte développeur gratuit (particulier) | aucun avertissement : le Store signe le paquet |
| **Azure Artifact Signing** (ex-« Trusted Signing ») | entreprise de l'UE, du Royaume-Uni, des États-Unis ou du Canada (les particuliers : États-Unis et Canada seulement) | ≈ 10 $ / mois (offre Basic) | nom de l'éditeur vérifié ; la réputation SmartScreen se construit avec les téléchargements |
| **Certificat OV** (Sectigo, DigiCert, SSL.com, Certum…) | particulier ou entreprise | ≈ 200 à 500 € / an ; Certum propose une offre « Open Source » à quelques dizaines d'euros pour les développeurs de logiciels libres | idem : la réputation se construit avec le temps |
| **Certificat EV** | entreprise | ≈ 300 à 700 € / an | depuis 2024, plus de réputation immédiate : même effet qu'un OV |
| **PKI interne** (Active Directory Certificate Services) | parc d'une seule entreprise | gratuit | aucun effet hors de l'entreprise ; sur le parc, éditeur approuvé si le certificat est déployé par GPO |

Ce qu'il faut savoir sur SmartScreen :

- **Une signature ne supprime pas tout de suite l'avertissement.** Elle prouve qui a publié le fichier et qu'il n'a pas
  été modifié ; SmartScreen accorde ensuite sa confiance au fil des installations sans incident (quelques semaines,
  quelques centaines de téléchargements). La réputation est attachée au certificat (OV / EV) ou à l'identité vérifiée
  (Artifact Signing).
- **Le Microsoft Store** est le seul moyen d'éviter totalement l'avertissement, et il est gratuit.
- Depuis 2023, les autorités de certification ne livrent plus de fichier `.pfx` pour un certificat public : la clé
  privée est sur une **clé USB sécurisée** (ou un service cloud). C'est l'option (a) ci-dessous.

## Les variables à définir (une seule option à la fois)

| Option | Variable | Valeur |
|---|---|---|
| (a) certificat du magasin Windows (clé USB, carte à puce, certificat importé) | `SPOKAPILOTE_SIGN_THUMBPRINT` | empreinte (SHA-1) du certificat |
| (b) fichier PFX | `SPOKAPILOTE_SIGN_PFX` | chemin du `.pfx` |
| | `SPOKAPILOTE_SIGN_PFX_PASSWORD` | mot de passe du `.pfx` |
| (c) Azure Artifact Signing | `SPOKAPILOTE_SIGN_AZURE_METADATA` | chemin du `metadata.json` |
| | `SPOKAPILOTE_SIGN_AZURE_DLIB` | *(facultatif)* chemin de `Azure.CodeSigning.Dlib.dll` |
| Réglages communs *(facultatifs)* | `SPOKAPILOTE_SIGN_TIMESTAMP` | serveur d'horodatage (défaut : DigiCert ; Microsoft pour Azure) |
| | `SPOKAPILOTE_SIGN_REQUIRED` | `1` : la construction échoue si aucune signature n'est configurée |
| | `SPOKAPILOTE_SIGN_DISABLE` | `1` : construction non signée malgré la configuration |

Pour définir une variable **une fois pour toutes** (PowerShell, puis fermez et rouvrez le terminal) :

```powershell
[Environment]::SetEnvironmentVariable("SPOKAPILOTE_SIGN_THUMBPRINT", "0123456789ABCDEF0123456789ABCDEF01234567", "User")
```

Pour la retirer : même commande avec `$null` à la place de la valeur.

---

## « J'ai acheté un certificat » (clé USB) : étape par étape

1. **Installez le logiciel de la clé** fourni par l'autorité de certification (souvent *SafeNet Authentication
   Client*), branchez la clé. Le certificat apparaît dans `certmgr.msc` → **Personnel → Certificats**.
2. **Relevez l'empreinte** du certificat :
   ```powershell
   Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Format-List Subject, Thumbprint, NotAfter
   ```
3. **Enregistrez-la** (remplacez par votre empreinte), puis fermez et rouvrez le terminal :
   ```powershell
   [Environment]::SetEnvironmentVariable("SPOKAPILOTE_SIGN_THUMBPRINT", "VOTRE_EMPREINTE", "User")
   ```
4. **Construisez** : `.\scripts\build-installer.ps1`. Le script affiche
   `Signature : certificat du magasin Windows « CN=… »`, puis `Signature vérifiée` pour chaque fichier.
   La clé USB peut demander son code PIN (dans SafeNet, l'option *Single Logon* évite de le saisir pour chaque fichier).
5. **Vérifiez** : clic droit sur `dist\SpokaPilote-Setup-<version>.exe` → **Propriétés → Signatures numériques**.
6. **Publiez** comme d'habitude : `.\scripts\release.ps1`. Conseil : définissez aussi `SPOKAPILOTE_SIGN_REQUIRED=1`
   pour ne jamais publier une version non signée par oubli (clé débranchée…).

Si vous avez reçu un fichier `.pfx` (ancien certificat, PKI interne) : option (b), en définissant
`SPOKAPILOTE_SIGN_PFX` et `SPOKAPILOTE_SIGN_PFX_PASSWORD`. Gardez le `.pfx` **hors du dépôt Git**.

## « J'ai un compte Azure » : Azure Artifact Signing, étape par étape

1. Dans le [portail Azure](https://portal.azure.com), créez une ressource **Artifact Signing account** (région
   *West Europe* par exemple, offre *Basic*).
2. Dans ce compte : **Identity validation → New identity → Public**, au nom de votre entreprise. Attendez la validation
   (quelques jours ; des justificatifs peuvent être demandés).
3. **Certificate profiles → Create → Public Trust**, en choisissant l'identité validée. Notez le **nom du compte**,
   le **nom du profil** et l'**URI du compte** (ex. `https://weu.codesigning.azure.net`).
4. **Access control (IAM)** du compte : donnez-vous le rôle **Artifact Signing Certificate Profile Signer**.
5. Sur le PC de construction, installez les outils clients et Azure CLI, puis connectez-vous :
   ```powershell
   winget install -e --id Microsoft.Azure.ArtifactSigningClientTools
   winget install -e --id Microsoft.AzureCLI
   az login
   ```
6. Créez un fichier `metadata.json` **hors du dépôt** (ex. `%USERPROFILE%\SpokaPilote-signature\metadata.json`) :
   ```json
   {
     "Endpoint": "https://weu.codesigning.azure.net",
     "CodeSigningAccountName": "nom-du-compte",
     "CertificateProfileName": "nom-du-profil",
     "ExcludeCredentials": [
       "ManagedIdentityCredential", "WorkloadIdentityCredential", "SharedTokenCacheCredential",
       "VisualStudioCredential", "VisualStudioCodeCredential", "AzurePowerShellCredential",
       "AzureDeveloperCliCredential", "InteractiveBrowserCredential"
     ]
   }
   ```
   (L'URI doit correspondre à la région du compte, sinon : erreur 403.)
7. Enregistrez son chemin, puis fermez et rouvrez le terminal :
   ```powershell
   [Environment]::SetEnvironmentVariable("SPOKAPILOTE_SIGN_AZURE_METADATA", "$env:USERPROFILE\SpokaPilote-signature\metadata.json", "User")
   ```
8. Construisez : `.\scripts\build-installer.ps1` (le script affiche `Signature : Azure Artifact Signing (compte …)`).

Pour un serveur de construction sans `az login` : créez une *App Registration*, donnez-lui le même rôle, et définissez
`AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET` (retirez alors `EnvironmentCredential` de la liste
d'exclusion — elle n'y est pas dans l'exemple). Si les outils clients ne sont pas installés, le script télécharge la
bibliothèque officielle (paquet NuGet `Microsoft.ArtifactSigning.Client`) ; elle exige alors le runtime .NET 8 et
Visual C++ Redistributable.

Les certificats Azure ne sont valables que 3 jours : l'horodatage (automatique) garde les signatures valides ensuite.

## Paquet MSIX hors Store

```powershell
.\scripts\build-store.ps1 -Arch all -Sign
```

L'éditeur déclaré dans le paquet doit être **exactement** le sujet du certificat : avec les options (a) et (b), le
script le reprend automatiquement ; avec Azure, indiquez-le (`-Publisher "CN=…, O=…, L=…, C=FR"`, visible dans le
profil de certificat). Pour le Store, ne signez pas : `.\scripts\build-store.ps1 -Arch all` (le Store signe lui-même).

## Essayer sans acheter de certificat

Un certificat auto-signé permet de vérifier que la chaîne fonctionne (il ne sert à rien pour SmartScreen) :

```powershell
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=SpokaPilote Test" -CertStoreLocation Cert:\CurrentUser\My
$env:SPOKAPILOTE_SIGN_THUMBPRINT = $cert.Thumbprint     # pour ce terminal seulement
.\scripts\build-installer.ps1 -Arch x64 -SkipTests      # « certificat auto-signé non approuvé sur ce poste (test) » : normal
Remove-Item Env:\SPOKAPILOTE_SIGN_THUMBPRINT
# suppression du certificat de test (Windows en place aussi une copie dans « Autorités intermédiaires »)
Get-ChildItem Cert:\CurrentUser -Recurse | Where-Object Thumbprint -eq $cert.Thumbprint | Remove-Item
```

## En cas de problème

| Message | Cause probable |
|---|---|
| `Certificat … introuvable dans le magasin « Personnel »` | clé USB débranchée, ou empreinte mal recopiée |
| `n'a pas de clé privée sur ce poste` | certificat importé sans sa clé : réimportez le `.pfx`, ou branchez la clé USB |
| `Impossible d'ouvrir … .pfx` | mot de passe incorrect |
| `La signature a échoué` après deux tentatives | serveur d'horodatage indisponible : réessayez, ou changez `SPOKAPILOTE_SIGN_TIMESTAMP` (ex. `http://timestamp.sectigo.com`) |
| Azure : `403` | URI de région, nom du compte / du profil, ou rôle *Certificate Profile Signer* manquant |
| Azure : signtool échoue sans message | runtime .NET 8 absent : installez les outils clients (étape 5) |
