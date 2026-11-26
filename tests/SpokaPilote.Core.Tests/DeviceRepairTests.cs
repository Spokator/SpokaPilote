using SpokaPilote.Core.Install;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Repair;

namespace SpokaPilote.Core.Tests;

public class DeviceRepairTests
{
    private static DeviceInfo Device(int error, string cls = "Media", string id = @"USB\VID_1234&PID_0001\5&1&0&1", string[]? hw = null, string name = "Webcam") => new()
    {
        InstanceId = id,
        Name = name,
        ClassName = cls,
        HardwareIds = hw ?? new[] { id[..id.LastIndexOf('\\')] },
        ErrorCode = error,
    };

    private static readonly DeviceInfo UsbDescriptorFailure = Device(43, "USB", @"USB\VID_0000&PID_0002\5&2E786FD2&0&4",
        new[] { @"USB\DEVICE_DESCRIPTOR_FAILURE" }, "Périphérique USB inconnu (échec de demande de descripteur de périphérique)");

    private static RepairActionKind[] Kinds(RepairPlan p) => p.Actions.Select(a => a.Kind).ToArray();

    // ------------------------------------------------------------------ choix des actions par code d'erreur

    [Theory]
    [InlineData(10)]
    [InlineData(43)]
    public void Start_failures_propose_restart_then_reinstall(int code)
    {
        var plan = DeviceRepair.Plan(Device(code));
        Assert.Equal(new[] { RepairActionKind.Restart, RepairActionKind.Reinstall }, Kinds(plan));
        Assert.Equal(RepairActionKind.Restart, plan.Recommended!.Kind);
        Assert.False(string.IsNullOrWhiteSpace(plan.Diagnosis));
    }

    [Fact]
    public void Disabled_device_proposes_enable()
    {
        var plan = DeviceRepair.Plan(Device(22));
        Assert.Equal(new[] { RepairActionKind.Enable }, Kinds(plan));
    }

    [Fact]
    public void Missing_driver_proposes_rescan_and_a_driver_search()
    {
        var plan = DeviceRepair.Plan(Device(28));
        Assert.Equal(RepairActionKind.Rescan, plan.Recommended!.Kind);
        Assert.Contains("install --missing", plan.SuggestedCommand);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(39)]
    [InlineData(52)]
    public void Load_failures_propose_reinstall(int code)
    {
        var plan = DeviceRepair.Plan(Device(code));
        Assert.Equal(RepairActionKind.Reinstall, plan.Recommended!.Kind);
        Assert.All(plan.Actions, a => Assert.False(string.IsNullOrWhiteSpace(a.Explanation)));
    }

    [Fact]
    public void Invalid_signature_advises_a_signed_driver()
    {
        var plan = DeviceRepair.Plan(Device(52));
        Assert.Contains(plan.Advice, a => a.Contains("signé"));
    }

    [Theory]
    [InlineData(12)]
    [InlineData(14)]
    [InlineData(29)]
    [InlineData(45)]
    public void Problems_without_software_fix_only_give_advice(int code)
    {
        var plan = DeviceRepair.Plan(Device(code));
        Assert.Empty(plan.Actions);
        Assert.NotEmpty(plan.Advice);
    }

    [Fact]
    public void Usb_hardware_fault_gives_physical_advice_and_no_software_action()
    {
        var plan = DeviceRepair.Plan(UsbDescriptorFailure);
        Assert.True(plan.IsHardwareFault);
        Assert.Empty(plan.Actions);
        Assert.Null(plan.Recommended);
        Assert.Contains(plan.Advice, a => a.Contains("port USB"));
        Assert.Contains("panne matérielle", plan.Diagnosis);
    }

    [Fact]
    public async Task Usb_hardware_fault_refuses_even_an_explicit_restart()
    {
        var plan = DeviceRepair.Plan(UsbDescriptorFailure);
        var result = await DeviceRepair.ExecuteAsync(plan, RepairActionKind.Restart, new RepairOptions { DryRun = true, RecordHistory = false });
        Assert.Equal(RepairStatus.Refused, result.Status);
        Assert.False(result.Executed);
        Assert.Contains("Panne matérielle", result.Message);
    }

    [Fact]
    public void Software_devices_are_never_removed_for_reinstall()
    {
        var plan = DeviceRepair.Plan(Device(43, "System", @"ROOT\SYSTEM\0003"));
        Assert.DoesNotContain(RepairActionKind.Reinstall, Kinds(plan));
    }

    [Fact]
    public async Task Action_not_offered_for_the_problem_is_refused()
    {
        var plan = DeviceRepair.Plan(Device(22));
        var result = await DeviceRepair.ExecuteAsync(plan, RepairActionKind.Reinstall, new RepairOptions { DryRun = true, RecordHistory = false });
        Assert.Equal(RepairStatus.Refused, result.Status);
        Assert.Contains("enable", result.Message);
    }

    [Fact]
    public async Task Dry_run_lists_the_pnputil_commands_without_running_them()
    {
        var device = Device(43);
        var result = await DeviceRepair.ExecuteAsync(DeviceRepair.Plan(device), RepairActionKind.Reinstall, new RepairOptions { DryRun = true, RecordHistory = false });
        Assert.Equal(RepairStatus.Simulated, result.Status);
        Assert.False(result.Executed);
        Assert.Equal(new[] { $"pnputil /remove-device \"{device.InstanceId}\"", "pnputil /scan-devices" }, result.Commands);
    }

    // ------------------------------------------------------------------ périphériques critiques

    [Theory]
    [InlineData("DiskDrive")]
    [InlineData("HDC")]
    [InlineData("SCSIAdapter")]
    [InlineData("Display")]
    [InlineData("Net")]
    [InlineData("System")]
    public void Critical_classes_are_flagged(string cls)
    {
        var plan = DeviceRepair.Plan(Device(43, cls, @"PCI\VEN_8086&DEV_1234\3&1"));
        Assert.True(plan.IsCritical);
        Assert.Contains(plan.Advice, a => a.Contains("confirmation explicite"));
    }

    [Fact]
    public async Task Critical_device_is_refused_without_explicit_confirmation()
    {
        var gpu = Device(43, "Display", @"PCI\VEN_10DE&DEV_2531\4&1", name: "NVIDIA RTX A2000");
        var plan = DeviceRepair.Plan(gpu);
        // Refus avant toute exécution, même en simulation, tant que la confirmation n'est pas donnée.
        var refused = await DeviceRepair.ExecuteAsync(plan, RepairActionKind.Restart, new RepairOptions { RecordHistory = false });
        Assert.Equal(RepairStatus.Refused, refused.Status);
        Assert.Contains("critique", refused.Message);
        Assert.Empty(refused.Commands);
        var refusedDry = await DeviceRepair.ExecuteAsync(plan, RepairActionKind.Restart, new RepairOptions { DryRun = true, RecordHistory = false });
        Assert.Equal(RepairStatus.Refused, refusedDry.Status);

        var confirmed = await DeviceRepair.ExecuteAsync(plan, RepairActionKind.Restart, new RepairOptions { DryRun = true, ConfirmCritical = true, RecordHistory = false });
        Assert.Equal(RepairStatus.Simulated, confirmed.Status);
    }

    [Fact]
    public void Only_working_keyboard_is_critical_but_not_when_another_one_works()
    {
        var broken = Device(43, "Keyboard", @"HID\VID_046D&PID_C31C&MI_00\7&1", name: "Clavier HID");
        var other = Device(0, "Keyboard", @"HID\VID_413C&PID_2113\7&2", name: "Clavier Dell");
        Assert.NotNull(DeviceRepair.CriticalReason(broken, new[] { broken }));
        Assert.Null(DeviceRepair.CriticalReason(broken, new[] { broken, other }));
    }

    [Fact]
    public void Usb_host_controllers_and_root_hubs_are_critical_but_not_ordinary_usb_devices()
    {
        Assert.NotNull(DeviceRepair.CriticalReason(Device(43, "USB", @"PCI\VEN_8086&DEV_43ED\3&1")));
        Assert.NotNull(DeviceRepair.CriticalReason(Device(43, "USB", @"USB\ROOT_HUB30\4&1", new[] { @"USB\ROOT_HUB30" })));
        Assert.Null(DeviceRepair.CriticalReason(Device(43, "USB", @"USB\VID_1234&PID_0001\5&1")));
    }

    [Theory]
    [InlineData(@"USB\VID_0000&PID_0002\5&2E786FD2&0&4", true)]
    [InlineData(@"PCI\VEN_8086&DEV_43ED&SUBSYS_A1941458&REV_11\3&11583659&0&A0", true)]
    [InlineData("USB\\VID\" /force", false)]
    [InlineData(@"USB\*", false)]
    [InlineData("/scan-devices", false)]
    [InlineData("", false)]
    public void Instance_ids_passed_to_pnputil_are_validated(string id, bool valid) => Assert.Equal(valid, PnpUtil.IsValidInstanceId(id));
}
