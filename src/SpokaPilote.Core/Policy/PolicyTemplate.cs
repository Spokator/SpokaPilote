namespace SpokaPilote.Core.Policy;

/// <summary>Modèle commenté de driver-policy.json (« spokapilote policy init »). Les règles d'exemple ne visent aucun matériel réel.</summary>
public static class PolicyTemplate
{
    public static string Create(DateTime today) => Template.Replace("{{DATE}}", today.ToString("yyyy-MM-dd"));

    private const string Template = """
        // Politique de pilotes SpokaPilote — documentation complète : docs/POLITIQUE.md
        // Les commentaires (// ou /* */) sont autorisés. Toute propriété inconnue rend le fichier invalide :
        // vérifiez-le avec « spokapilote policy check <fichier> » avant de le déployer.
        {
          "schema": 1,                          // version du FORMAT (ne pas modifier)
          "name": "Parc de mon organisation",   // affiché dans les rapports
          "version": "1",                       // VOTRE révision : incrémentez-la à chaque modification
          "updatedAt": "{{DATE}}",
          "description": "Pilotes approuvés par le service informatique.",

          "settings": {
            // Sources autorisées (supprimez la clé pour toutes les autoriser). Une source absente n'est pas interrogée.
            // Valeurs : WindowsUpdate, MicrosoftCatalog, Dell, HP, Lenovo, LocalRepository, Nvidia, Intel, Amd
            "allowedSources": [ "LocalRepository", "Dell", "HP", "Lenovo", "WindowsUpdate", "MicrosoftCatalog" ],
            "allowFirmware": false,             // BIOS / firmware : à déployer par un autre canal
            "allowOptional": true,              // pilotes « optionnels » de Windows Update
            "onlyMissingDrivers": false,        // true = ne traiter que les périphériques sans pilote
            "minimumDriverAgeDays": 14,         // recul avant d'installer une NOUVELLE version (pas les pilotes manquants)
            "useReferenceBlocklist": true,      // ajoute la liste noire de référence SpokaPilote après vos règles
            "ignoreReferenceRules": []          // identifiants de règles de la liste de référence à écarter
          },

          // Les règles sont évaluées DANS L'ORDRE : la première règle « block » ou « allow » qui correspond décide.
          // Placez donc les exceptions (« allow ») AVANT les blocages plus généraux.
          // Critères (combinés par ET ; une liste = l'une des valeurs) : hardwareId, title, manufacturer, category,
          // deviceName, source, kind, versionMin, versionMax, versions. Jokers : * (n'importe quoi) et ? (un caractère).
          "rules": [
            {
              "id": "exemple-exception-validee",
              "action": "allow",
              "hardwareId": "PCI\\VEN_XXXX&DEV_XXXX*",
              "versions": "1.2.3.4",
              "reason": "Version validée par l'IT malgré le blocage général ci-dessous (ticket #0000)."
            },
            {
              "id": "exemple-version-ecrans-bleus",
              "action": "block",
              "hardwareId": [ "PCI\\VEN_XXXX&DEV_XXXX*", "PCI\\VEN_XXXX&DEV_YYYY*" ],
              "versionMin": "1.0",
              "versionMax": "1.9.9999",
              "reason": "Écrans bleus constatés avec les versions 1.x (ticket #0000).",
              "expires": "2099-12-31"           // facultatif : la règle est ignorée après cette date
            },
            {
              "id": "exemple-figer-version",
              "action": "pin",
              "title": "*Exemple XXXX Graphics*",
              "pinVersion": "2.0",
              "reason": "Version certifiée pour le logiciel métier : ne pas dépasser la 2.0."
            },
            {
              "id": "exemple-materiel-gere-a-part",
              "action": "block",
              "deviceName": "*Exemple lecteur XXXX*",
              "hide": true,                     // retire le pilote de la liste au lieu de l'afficher comme bloqué
              "reason": "Matériel géré par le fournisseur du logiciel métier."
            }
          ]
        }
        """;
}
