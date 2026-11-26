# Composants tiers

SpokaPilote est distribué sous [licence MIT](LICENSE). Il redistribue ou utilise les composants suivants, chacun sous
sa propre licence.

## Redistribués avec l'application Windows (installeurs et paquet MSIX)

| Composant | Utilisation | Licence |
|---|---|---|
| [.NET](https://github.com/dotnet/runtime) (runtime, bibliothèques de base) | application autonome : le runtime .NET 10 est inclus | MIT — © .NET Foundation et contributeurs |
| [Windows Desktop / WPF](https://github.com/dotnet/wpf) | interface graphique | MIT — © .NET Foundation et contributeurs |
| [System.Management](https://www.nuget.org/packages/System.Management) | inventaire matériel (WMI) | MIT — © .NET Foundation et contributeurs |
| [System.Security.Cryptography.Pkcs](https://www.nuget.org/packages/System.Security.Cryptography.Pkcs) | lecture des catalogues signés | MIT — © .NET Foundation et contributeurs |
| Projection du SDK Windows (`Microsoft.Windows.SDK.NET`, [C#/WinRT](https://github.com/microsoft/CsWinRT)) | API Windows (WinRT) | MIT — © Microsoft Corporation |
| [Inno Setup](https://jrsoftware.org/isinfo.php) (programme d'installation et de désinstallation) | installeurs `SpokaPilote-Setup-*.exe` / `SpokaPilote-Installer-*-arm64.exe` | [licence Inno Setup](https://jrsoftware.org/files/is/license.txt) — © Jordan Russell et Martijn Laan |

## Redistribués avec la console centrale (image Docker)

| Composant | Utilisation | Licence |
|---|---|---|
| [ASP.NET Core](https://github.com/dotnet/aspnetcore) et image `mcr.microsoft.com/dotnet/aspnet` | serveur web | MIT — © .NET Foundation et contributeurs ; paquets du système de base sous leurs licences respectives |
| [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite) | base de données | MIT — © .NET Foundation et contributeurs |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) | accès natif à SQLite | Apache 2.0 — © Eric Sink |
| [SQLite](https://www.sqlite.org/copyright.html) | moteur de base de données | domaine public |

## Outils de construction et de test (non redistribués)

| Composant | Utilisation | Licence |
|---|---|---|
| [Microsoft.Windows.SDK.BuildTools](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools) | `makeappx` (MSIX), `signtool` (signature) | licence logicielle Microsoft |
| [Microsoft.ArtifactSigning.Client](https://www.nuget.org/packages/Microsoft.ArtifactSigning.Client) | signature Azure Artifact Signing (facultative) | licence logicielle Microsoft |
| [xUnit.net](https://xunit.net/) | tests | Apache 2.0 — © .NET Foundation et contributeurs |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest), [Microsoft.AspNetCore.Mvc.Testing](https://www.nuget.org/packages/Microsoft.AspNetCore.Mvc.Testing) | tests | MIT — © Microsoft Corporation / .NET Foundation |
| [coverlet](https://github.com/coverlet-coverage/coverlet) | couverture de tests | MIT — © Toni Solarin-Sodara et contributeurs |

Les pilotes installés par SpokaPilote ne font pas partie de SpokaPilote : ils sont téléchargés depuis leurs éditeurs
(Microsoft, Dell, HP, Lenovo, NVIDIA…) et restent soumis à leurs propres conditions.
