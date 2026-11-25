using System.Runtime.InteropServices;
using System.Xml.Linq;
using SpokaPilote.Core.Install;

namespace SpokaPilote.Core.Tests;

public class ScheduleTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private const string Exe = @"C:\Program Files\SpokaPilote\spokapilote.exe";
    private static readonly DateTime Today = new(2026, 11, 25, 9, 0, 0);

    private static XDocument Xml(MaintenanceOptions o) => XDocument.Parse(MaintenanceScheduler.BuildTaskXml(o, Exe, Today));

    private static string Value(XDocument d, string element) => d.Descendants(Ns + element).Single().Value;

    [Fact]
    public void Weekly_task_runs_as_system_with_safe_settings()
    {
        var d = Xml(new MaintenanceOptions { Day = DayOfWeek.Monday, Time = new TimeSpan(12, 30, 0) });
        var trigger = d.Descendants(Ns + "CalendarTrigger").Single();
        Assert.Equal("2026-11-25T12:30:00", trigger.Element(Ns + "StartBoundary")!.Value);
        Assert.Equal("PT30M", trigger.Element(Ns + "RandomDelay")!.Value);
        Assert.NotNull(trigger.Element(Ns + "ScheduleByWeek")!.Element(Ns + "DaysOfWeek")!.Element(Ns + "Monday"));
        Assert.Equal("1", trigger.Descendants(Ns + "WeeksInterval").Single().Value);

        Assert.Equal("S-1-5-18", Value(d, "UserId"));
        Assert.Equal("HighestAvailable", Value(d, "RunLevel"));
        Assert.Equal("IgnoreNew", Value(d, "MultipleInstancesPolicy"));
        Assert.Equal("true", Value(d, "StartWhenAvailable"));
        Assert.Equal("true", Value(d, "DisallowStartIfOnBatteries"));
        Assert.Equal("true", Value(d, "StopIfGoingOnBatteries"));
        Assert.Equal("PT2H", Value(d, "ExecutionTimeLimit"));
        Assert.Equal(@"\SpokaPilote\Maintenance", Value(d, "URI"));
        Assert.Equal(Exe, Value(d, "Command"));
        Assert.Equal(@"C:\Program Files\SpokaPilote", Value(d, "WorkingDirectory"));
        Assert.Equal("maintenance --mode missing", Value(d, "Arguments"));
    }

    [Fact]
    public void Xml_declares_utf16_for_schtasks()
        => Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-16\"?>", MaintenanceScheduler.BuildTaskXml(new MaintenanceOptions(), Exe, Today), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Daily_and_startup_triggers()
    {
        var daily = Xml(new MaintenanceOptions { Trigger = MaintenanceTrigger.Daily, Time = new TimeSpan(7, 5, 0), RandomDelay = TimeSpan.Zero });
        Assert.Equal("1", daily.Descendants(Ns + "ScheduleByDay").Single().Element(Ns + "DaysInterval")!.Value);
        Assert.Equal("2026-11-25T07:05:00", Value(daily, "StartBoundary"));
        Assert.Empty(daily.Descendants(Ns + "RandomDelay"));

        var boot = Xml(new MaintenanceOptions { Trigger = MaintenanceTrigger.AtStartup, StartupDelay = TimeSpan.FromMinutes(15) });
        var trigger = boot.Descendants(Ns + "BootTrigger").Single();
        Assert.Equal("PT15M", trigger.Element(Ns + "Delay")!.Value);
        Assert.Empty(boot.Descendants(Ns + "CalendarTrigger"));
    }

    [Fact]
    public void Battery_and_time_limit_options()
    {
        var d = Xml(new MaintenanceOptions { RunOnBattery = true, ExecutionLimit = TimeSpan.FromMinutes(90) });
        Assert.Equal("false", Value(d, "DisallowStartIfOnBatteries"));
        Assert.Equal("false", Value(d, "StopIfGoingOnBatteries"));
        Assert.Equal("PT1H30M", Value(d, "ExecutionTimeLimit"));
    }

    [Fact]
    public void Arguments_include_mode_reports_and_printer_profile()
    {
        var o = new MaintenanceOptions { Mode = MaintenanceMode.Updates, OutputDir = @"\\srv\rapports parc\", PrinterProfile = @"\\srv\profils\imprimantes & co.json" };
        var args = MaintenanceScheduler.BuildArguments(o);
        Assert.Equal(new[] { "maintenance", "--mode", "updates", "--output-dir", @"\\srv\rapports parc\", "--printers", @"\\srv\profils\imprimantes & co.json" }, args);

        var line = Value(Xml(o), "Arguments"); // l'esperluette est échappée en XML puis restituée
        Assert.Equal(@"maintenance --mode updates --output-dir ""\\srv\rapports parc\\"" --printers ""\\srv\profils\imprimantes & co.json""", line);
        Assert.Equal(args, CommandLineToArgs("spokapilote.exe " + line).Skip(1));
    }

    [Theory]
    [InlineData("simple")]
    [InlineData(@"C:\Program Files\x")]
    [InlineData(@"\\srv\rapports\")]
    [InlineData(@"C:\a b\")]
    [InlineData("dit \"bonjour\"")]
    [InlineData(@"a\""b")]
    [InlineData(@"fin\\")]
    [InlineData("")]
    public void Argument_quoting_roundtrips_through_windows_parser(string value)
    {
        var parsed = CommandLineToArgs("spokapilote.exe " + MaintenanceScheduler.QuoteArgument(value) + " suivant");
        Assert.Equal(new[] { "spokapilote.exe", value, "suivant" }, parsed);
    }

    [Theory]
    [InlineData(@"C:\Program Files\SpokaPilote\spokapilote.exe", true)]
    [InlineData(@"C:\Program Files (x86)\SpokaPilote\spokapilote.exe", true)]
    [InlineData(@"C:\Tools\spokapilote.exe", false)]
    [InlineData(@"C:\Program Files\SpokaPilote\autre.exe", false)]
    [InlineData(@"C:\Program Files\Dev\SpokaPilote\src\SpokaPilote.Cli\bin\Release\net10.0\spokapilote.exe", false)]
    [InlineData(@"C:\Users\x\Desktop\spokapilote.exe", false)]
    public void Executable_must_be_installed_in_program_files(string path, bool ok)
        => Assert.Equal(ok, MaintenanceScheduler.ValidateExecutable(path) is null);

    [Fact]
    public void Executable_in_temp_folder_is_refused()
    {
        var error = MaintenanceScheduler.ValidateExecutable(Path.Combine(Path.GetTempPath(), "publish", "spokapilote.exe"));
        Assert.NotNull(error);
        Assert.Contains("temporaire", error);
    }

    [Theory]
    [InlineData("lundi", DayOfWeek.Monday)]
    [InlineData("Mercredi", DayOfWeek.Wednesday)]
    [InlineData("dim", DayOfWeek.Sunday)]
    [InlineData("friday", DayOfWeek.Friday)]
    [InlineData(null, DayOfWeek.Monday)]
    public void Days_are_parsed_in_french_and_english(string? text, DayOfWeek expected)
        => Assert.Equal(expected, MaintenanceScheduler.ParseDay(text));

    [Fact]
    public void Times_and_modes_are_parsed()
    {
        Assert.Equal(new TimeSpan(12, 30, 0), MaintenanceScheduler.ParseTime("12:30"));
        Assert.Equal(new TimeSpan(7, 0, 0), MaintenanceScheduler.ParseTime("7h"));
        Assert.Equal(new TimeSpan(7, 15, 0), MaintenanceScheduler.ParseTime("07h15"));
        Assert.Null(MaintenanceScheduler.ParseTime("25:00"));
        Assert.Null(MaintenanceScheduler.ParseTime("midi"));
        Assert.Null(MaintenanceScheduler.ParseDay("lundimatin"));
        Assert.Equal(MaintenanceMode.Missing, MaintenanceScheduler.ParseMode(null));
        Assert.Equal(MaintenanceMode.Updates, MaintenanceScheduler.ParseMode("updates"));
        Assert.Equal(MaintenanceMode.Scan, MaintenanceScheduler.ParseMode("SCAN"));
        Assert.Null(MaintenanceScheduler.ParseMode("tout"));
    }

    [Fact]
    public void Definition_is_read_back_from_xml()
    {
        var xml = MaintenanceScheduler.BuildTaskXml(new MaintenanceOptions { Day = DayOfWeek.Thursday, Time = new TimeSpan(18, 45, 0), Mode = MaintenanceMode.Scan }, Exe, Today);
        var (command, arguments, trigger) = MaintenanceScheduler.ParseDefinition(xml);
        Assert.Equal(Exe, command);
        Assert.Equal("maintenance --mode scan", arguments);
        Assert.Equal("chaque jeudi à 18:45 (+ jusqu'à 30 min aléatoires)", trigger);

        var boot = MaintenanceScheduler.BuildTaskXml(new MaintenanceOptions { Trigger = MaintenanceTrigger.AtStartup }, Exe, Today);
        Assert.Equal("au démarrage (après 10 min)", MaintenanceScheduler.ParseDefinition(boot).Trigger);
    }

    [Fact]
    public void Result_codes_are_labelled()
    {
        Assert.Equal("succès (0)", MaintenanceScheduler.ResultLabel(0));
        Assert.Equal("échec partiel (2)", MaintenanceScheduler.ResultLabel(2));
        Assert.Equal("jamais exécutée", MaintenanceScheduler.ResultLabel(0x41303));
        Assert.Equal("succès, redémarrage requis (3010)", MaintenanceScheduler.ResultLabel(3010));
        Assert.StartsWith("code 0x", MaintenanceScheduler.ResultLabel(12345));
    }

    // Analyseur de ligne de commande de Windows, celui qu'utilise le Planificateur pour lancer spokapilote.exe.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static string[] CommandLineToArgs(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try
        {
            return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray();
        }
        finally
        {
            LocalFree(argv);
        }
    }
}
