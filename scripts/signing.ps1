# Outils communs aux scripts de construction : SDK Build Tools (makeappx, signtool) et signature de code FACULTATIVE.
# À charger avec :   . "$PSScriptRoot\signing.ps1"
#
# La signature ne s'active QUE si une configuration est présente (variables d'environnement, voir docs\SIGNATURE.md) :
#   (a) certificat du magasin Windows   SPOKAPILOTE_SIGN_THUMBPRINT      = empreinte SHA-1 du certificat
#   (b) fichier PFX                     SPOKAPILOTE_SIGN_PFX             = chemin du .pfx
#                                       SPOKAPILOTE_SIGN_PFX_PASSWORD    = mot de passe du .pfx
#   (c) Azure Artifact Signing          SPOKAPILOTE_SIGN_AZURE_METADATA  = chemin du metadata.json
#       (ex-« Trusted Signing »)        SPOKAPILOTE_SIGN_AZURE_DLIB      = (facultatif) chemin de Azure.CodeSigning.Dlib.dll
#                                       + connexion Azure : « az login » ou AZURE_TENANT_ID / AZURE_CLIENT_ID / AZURE_CLIENT_SECRET
#   Réglages communs (facultatifs) :    SPOKAPILOTE_SIGN_TIMESTAMP       = serveur d'horodatage RFC 3161
#                                       SPOKAPILOTE_SIGN_REQUIRED=1      = échoue si aucune signature n'est configurée
#                                       SPOKAPILOTE_SIGN_DISABLE=1       = ignore la configuration (construction non signée)
# Sans aucune de ces variables, rien ne change : les fichiers sont construits sans signature.

$script:SpokaRoot = Split-Path -Parent $PSScriptRoot
$script:SpokaSignUrl = "https://github.com/Spokator/SpokaPilote"

# Télécharge (une fois) le paquet NuGet officiel $Id dans packaging\deps\<Folder> et renvoie ce dossier.
function Get-NuGetToolPackage([string]$Id, [string]$Folder) {
    $dest = Join-Path $script:SpokaRoot "packaging\deps\$Folder"
    if ((Test-Path $dest) -and (Get-ChildItem $dest -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1)) { return $dest }
    $lower = $Id.ToLowerInvariant()
    Write-Host "Téléchargement de $Id (nuget.org)…"
    $versions = (Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/$lower/index.json").versions | Where-Object { $_ -notmatch '-' }
    $v = @($versions)[-1]
    New-Item -ItemType Directory -Force $dest | Out-Null
    $nupkg = Join-Path $dest "$lower.$v.nupkg"
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/$lower/$v/$lower.$v.nupkg" -OutFile $nupkg -UseBasicParsing
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($nupkg, $dest)
    Remove-Item $nupkg
    return $dest
}

# makeappx.exe et signtool.exe (x64) du paquet NuGet Microsoft.Windows.SDK.BuildTools : aucun SDK Windows à installer.
function Get-SdkBuildTools {
    $dir = Get-NuGetToolPackage "Microsoft.Windows.SDK.BuildTools" "sdk-buildtools"
    $makeappx = Get-ChildItem $dir -Recurse -Filter makeappx.exe | Where-Object FullName -match '\\x64\\' | Select-Object -First 1
    if (-not $makeappx) { throw "makeappx.exe introuvable dans $dir (supprimez ce dossier pour le retélécharger)." }
    [pscustomobject]@{
        MakeAppx = $makeappx.FullName
        SignTool = Join-Path $makeappx.DirectoryName "signtool.exe"
    }
}

function Get-SpokaEnv([string]$Name) {
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ($value) { return $value.Trim() }
    return $null
}

# Bibliothèque Azure Artifact Signing : variable, outils clients installés (winget), sinon paquet NuGet officiel.
function Find-ArtifactSigningDlib {
    $explicit = Get-SpokaEnv "SPOKAPILOTE_SIGN_AZURE_DLIB"
    if ($explicit) {
        if (-not (Test-Path $explicit)) { throw "SPOKAPILOTE_SIGN_AZURE_DLIB : fichier introuvable ($explicit)." }
        return (Resolve-Path $explicit).Path
    }
    $candidates = @(
        "${env:ProgramFiles(x86)}\Microsoft\ArtifactSigningClientTools",
        "$env:ProgramFiles\Microsoft\ArtifactSigningClientTools",
        "$env:LOCALAPPDATA\Microsoft\ArtifactSigningClientTools",
        "${env:ProgramFiles(x86)}\Microsoft\TrustedSigningClientTools",
        "$env:LOCALAPPDATA\Microsoft\MicrosoftTrustedSigningClientTools"
    ) | Where-Object { $_ -and (Test-Path $_) }
    foreach ($c in $candidates) {
        $dll = Get-ChildItem $c -Recurse -Filter Azure.CodeSigning.Dlib.dll -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($dll) { return $dll.FullName }
    }
    $dir = Get-NuGetToolPackage "Microsoft.ArtifactSigning.Client" "artifact-signing"
    $dll = Get-ChildItem $dir -Recurse -Filter Azure.CodeSigning.Dlib.dll | Where-Object FullName -match '\\x64\\' | Select-Object -First 1
    if (-not $dll) { throw "Azure.CodeSigning.Dlib.dll introuvable dans $dir." }
    return $dll.FullName
}

<#
  Lit la configuration de signature. Renvoie $null si aucune n'est présente (construction non signée).
  Objet renvoyé : Mode, Description, SignTool, Timestamp, SignArgs (arguments de « signtool sign » sans le fichier),
                  Subject (sujet du certificat, pour l'éditeur du MSIX ; inconnu avec Azure), Secret (à masquer).
#>
function Get-SigningConfig {
    if ((Get-SpokaEnv "SPOKAPILOTE_SIGN_DISABLE") -eq "1") {
        Write-Host "Signature désactivée (SPOKAPILOTE_SIGN_DISABLE=1)." -ForegroundColor Yellow
        return $null
    }
    $thumb = Get-SpokaEnv "SPOKAPILOTE_SIGN_THUMBPRINT"
    $pfx = Get-SpokaEnv "SPOKAPILOTE_SIGN_PFX"
    $azure = Get-SpokaEnv "SPOKAPILOTE_SIGN_AZURE_METADATA"
    $configured = @($thumb, $pfx, $azure | Where-Object { $_ })
    if ($configured.Count -eq 0) {
        if ((Get-SpokaEnv "SPOKAPILOTE_SIGN_REQUIRED") -eq "1") {
            throw "SPOKAPILOTE_SIGN_REQUIRED=1 mais aucune signature n'est configurée (voir docs\SIGNATURE.md)."
        }
        return $null
    }
    if ($configured.Count -gt 1) {
        throw "Plusieurs signatures configurées (SPOKAPILOTE_SIGN_THUMBPRINT / _PFX / _AZURE_METADATA) : n'en gardez qu'une."
    }

    $tools = Get-SdkBuildTools
    $secret = $null
    $subject = $null
    if ($thumb) {
        $thumb = ($thumb -replace '[^0-9A-Fa-f]', '').ToUpperInvariant() # copier-coller depuis certmgr : espaces invisibles
        $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object Thumbprint -eq $thumb | Select-Object -First 1
        $modeArgs = @("/sha1", $thumb)
        if (-not $cert) {
            $cert = Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue | Where-Object Thumbprint -eq $thumb | Select-Object -First 1
            if ($cert) { $modeArgs += "/sm" }
        }
        if (-not $cert) { throw "Certificat $thumb introuvable dans le magasin « Personnel » (utilisateur ou ordinateur)." }
        if (-not $cert.HasPrivateKey) { throw "Le certificat $thumb n'a pas de clé privée sur ce poste (clé USB du certificat branchée ?)." }
        $mode = "Certificate"; $description = "certificat du magasin Windows « $($cert.Subject) »"; $subject = $cert.Subject
        $defaultTs = "http://timestamp.digicert.com"
    }
    elseif ($pfx) {
        if (-not (Test-Path $pfx)) { throw "SPOKAPILOTE_SIGN_PFX : fichier introuvable ($pfx)." }
        $pfx = (Resolve-Path $pfx).Path
        $secret = Get-SpokaEnv "SPOKAPILOTE_SIGN_PFX_PASSWORD"
        try {
            $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
            $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfx, $secret, $flags)
        } catch {
            throw "Impossible d'ouvrir $pfx : mot de passe (SPOKAPILOTE_SIGN_PFX_PASSWORD) incorrect ou fichier invalide."
        }
        $modeArgs = @("/f", $pfx)
        if ($secret) { $modeArgs += @("/p", $secret) }
        $mode = "Pfx"; $description = "fichier PFX « $($cert.Subject) »"; $subject = $cert.Subject
        $cert.Dispose()
        $defaultTs = "http://timestamp.digicert.com"
    }
    else {
        if (-not (Test-Path $azure)) { throw "SPOKAPILOTE_SIGN_AZURE_METADATA : fichier introuvable ($azure)." }
        $azure = (Resolve-Path $azure).Path
        try { $meta = Get-Content $azure -Raw -Encoding UTF8 | ConvertFrom-Json } catch { throw "$azure n'est pas un JSON valide." }
        foreach ($field in "Endpoint", "CodeSigningAccountName", "CertificateProfileName") {
            if (-not $meta.$field) { throw "$azure : champ « $field » manquant (voir docs\SIGNATURE.md)." }
        }
        $dlib = Find-ArtifactSigningDlib
        $modeArgs = @("/dlib", $dlib, "/dmdf", $azure)
        $mode = "Azure"; $description = "Azure Artifact Signing (compte $($meta.CodeSigningAccountName), profil $($meta.CertificateProfileName))"
        $defaultTs = "http://timestamp.acs.microsoft.com"
    }
    $ts = Get-SpokaEnv "SPOKAPILOTE_SIGN_TIMESTAMP"
    if (-not $ts) { $ts = $defaultTs }

    [pscustomobject]@{
        Mode        = $mode
        Description = $description
        SignTool    = $tools.SignTool
        Timestamp   = $ts
        SignArgs    = @("/fd", "SHA256", "/tr", $ts, "/td", "SHA256") + $modeArgs
        Subject     = $subject
        Secret      = $secret
    }
}

# Masque le mot de passe éventuel dans un texte affiché.
function Hide-SigningSecret($Config, [string]$Text) {
    if ($Config.Secret) { return $Text.Replace($Config.Secret, "********") }
    return $Text
}

# Signe des fichiers (exe, dll, msix…) : SHA-256, horodatage RFC 3161. Deux tentatives (serveurs d'horodatage capricieux).
function Invoke-CodeSign($Config, [string[]]$Path, [string]$Description = "SpokaPilote") {
    if (-not $Path -or $Path.Count -eq 0) { return }
    $ErrorActionPreference = "Continue" # le stderr de signtool ne doit pas devenir une exception : on lit le code de sortie
    $extra = @()
    $pe = @($Path | Where-Object { $_ -match '\.(exe|dll)$' })
    if ($pe.Count -eq $Path.Count) { $extra = @("/d", $Description, "/du", $script:SpokaSignUrl) }
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        $output = & $Config.SignTool sign @($Config.SignArgs) @extra @Path 2>&1 | ForEach-Object { "$_" }
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Signé ($($Config.Mode)) : $($Path.Count) fichier(s)" -ForegroundColor Green
            return
        }
        Write-Host (Hide-SigningSecret $Config ($output -join "`n")) -ForegroundColor Yellow
        if ($attempt -eq 1) { Write-Host "Nouvelle tentative dans 5 s…" -ForegroundColor Yellow; Start-Sleep -Seconds 5 }
    }
    throw "La signature a échoué ($($Config.Description)) : voir les messages de signtool ci-dessus."
}

<#
  Vérifie qu'un fichier est signé et horodaté. Une signature valide mais émise par un certificat AUTO-SIGNÉ non
  approuvé sur ce poste (certificat de test) est acceptée avec un avertissement ; tout autre problème est une erreur.
#>
function Test-CodeSignature($Config, [string]$Path) {
    $ErrorActionPreference = "Continue"
    $name = Split-Path $Path -Leaf
    if ($Path -match '\.(msix|msixbundle|appx)$') {
        # Get-AuthenticodeSignature ne lit pas les paquets : signtool verify (stratégie Authenticode).
        $output = & $Config.SignTool verify /pa /tw $Path 2>&1 | ForEach-Object { "$_" }
        if ($LASTEXITCODE -eq 0) { Write-Host "Signature vérifiée : $name" -ForegroundColor Green; return }
        $text = $output -join "`n"
        if ($text -match 'not trusted|non approuv|0x800B0109') {
            Write-Host "Signature présente mais certificat non approuvé sur ce poste (normal avec un certificat de test) : $name" -ForegroundColor Yellow
            return
        }
        throw "Signature invalide pour $name :`n$text"
    }
    $sig = Get-AuthenticodeSignature -LiteralPath $Path
    if (-not $sig.SignerCertificate) { throw "$name n'est pas signé." }
    if (-not $sig.TimeStamperCertificate) { throw "$name est signé mais pas horodaté." }
    if ($sig.Status -eq "Valid") { Write-Host "Signature vérifiée : $name ($($sig.SignerCertificate.Subject))" -ForegroundColor Green; return }
    $selfSigned = $sig.SignerCertificate.Subject -eq $sig.SignerCertificate.Issuer
    if ($selfSigned -and $sig.Status -in "UnknownError", "NotTrusted") {
        Write-Host "Signé et horodaté, certificat auto-signé non approuvé sur ce poste (test) : $name" -ForegroundColor Yellow
        return
    }
    throw "Signature de $name : $($sig.Status) — $($sig.StatusMessage)"
}

# Commande de signature pour Inno Setup (ISCC /S<nom>=<commande>) : $q = guillemet, $f = fichier à signer, $$ = « $ ».
function Get-InnoSignCommand($Config) {
    $quote = { param($s) $s = $s.Replace('$', '$$'); if ($s -match '[\s"&|<>^]') { '$q' + $s + '$q' } else { $s } }
    $parts = @((& $quote $Config.SignTool), "sign") + ($Config.SignArgs | ForEach-Object { & $quote $_ }) +
        @("/d", (& $quote "SpokaPilote"), "/du", (& $quote $script:SpokaSignUrl), '$f')
    return ($parts -join " ")
}
