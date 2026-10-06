using System.Text.Json;
using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

/// <summary>All system writes here go to a fake backend; all files go to a unique temporary directory.</summary>
public sealed class HardwareDisplayEditorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HardwareDisplayEditorTests", Guid.NewGuid().ToString("N"));
    private readonly FakeBackend _backend = new();
    private string ProfilePath => Path.Combine(_directory, "hardware-display-profile.json");
    private string BackupPath => Path.Combine(_directory, "hardware-display-system-backup.json");
    private HardwareDisplayEditorService Create(IHardwareDisplayFileStore? files = null) => new(_directory, _backend, files);

    [Fact]
    public void AllSixCategories_SaveAndReloadLocalProfileWithoutAdminOrSystemWrites()
    {
        _backend.IsAdmin = false;
        var service = Create();
        var items = service.LoadSnapshot().Devices.Select(d => d with
        { CurrentName = $"自定义 {d.Category}", CurrentManufacturer = "本地厂商" }).ToArray();

        Assert.Equal(6, items.Length);
        Assert.All(service.SaveProfile(items), result => Assert.True(result.Success, result.Message));
        Assert.Empty(_backend.Writes);
        Assert.False(service.HasSystemBackup);
        Assert.All(Create().LoadSnapshot().Devices, device =>
        {
            Assert.Equal($"自定义 {device.Category}", device.CurrentName);
            Assert.Equal("本地厂商", device.CurrentManufacturer);
            Assert.Equal("未检测到设备", device.OriginalName);
            Assert.False(device.CanApplySystem);
        });
    }

    [Fact]
    public void SelectedGpuOnly_ChangesFriendlyNameAndLeavesOtherGpuAndManufacturerAlone()
    {
        var first = _backend.AddPnp(HardwareModelCategory.Gpu, "GPU1", "First GPU");
        var second = _backend.AddPnp(HardwareModelCategory.Gpu, "GPU2", "Second GPU");
        var service = Create();
        var selected = first with { CurrentName = "Target GPU", CurrentManufacturer = "Local vendor" };

        Assert.All(service.SaveProfile([selected]), r => Assert.True(r.Success));
        Assert.All(service.ApplySystemChanges([selected]), r => Assert.True(r.Success, r.Message));

        Assert.Single(_backend.Writes);
        Assert.Equal("GPU1", _backend.Writes[0].Property.Path);
        Assert.Equal("FriendlyName", _backend.Writes[0].Property.ValueName);
        Assert.Equal("Second GPU", _backend.Current(second.Id));
        Assert.Equal("Detected vendor", _backend.Devices.Single(d => d.Device.Id == first.Id).Device.OriginalManufacturer);
        Assert.Equal("Local vendor", Create().LoadSnapshot().Devices.Single(d => d.Id == first.Id).CurrentManufacturer);
    }

    [Fact]
    public void Cpu_AppliesNameToAllLogicalProcessorsWithoutEditingVendorOrFrequency()
    {
        var cpu = _backend.AddCpu(4);
        var results = Create().ApplySystemChanges([cpu with { CurrentName = "New CPU", CurrentManufacturer = "New vendor" }]);

        Assert.All(results, r => Assert.True(r.Success, r.Message));
        Assert.Equal(4, _backend.Writes.Count);
        Assert.All(_backend.Writes, write => Assert.Equal("ProcessorNameString", write.Property.ValueName));
        Assert.All(_backend.Values.Values, value => Assert.Equal("New CPU", value.AsString()));
    }

    [Fact]
    public void RepeatedApplyAndRestart_PreserveFirstOriginalForRestore()
    {
        var device = _backend.AddPnp(HardwareModelCategory.Disk, "Disk1", "Original disk");
        var service = Create();
        Assert.All(service.ApplySystemChanges([device with { CurrentName = "First edit" }]), r => Assert.True(r.Success));
        Assert.All(Create().ApplySystemChanges([device with { CurrentName = "Second edit" }]), r => Assert.True(r.Success));
        Assert.Equal("Original disk", Create().LoadSnapshot().Devices.Single(d => d.Id == device.Id).OriginalName);

        Assert.All(Create().RestoreSystem(), r => Assert.True(r.Success, r.Message));

        Assert.Equal("Original disk", _backend.Current(device.Id));
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void MissingFriendlyName_RestoreDeletesPropertyInsteadOfCreatingFallbackValue()
    {
        var device = _backend.AddPnp(HardwareModelCategory.Monitor, "Monitor1", "EDID display", friendlyExists: false);
        var property = _backend.Devices.Single().Properties.Single();
        var service = Create();
        Assert.All(service.ApplySystemChanges([device with { CurrentName = "Display alias" }]), r => Assert.True(r.Success));
        Assert.Equal("EDID display", service.LoadSnapshot().Devices.Single(d => d.Id == device.Id).OriginalName);

        Assert.All(service.RestoreSystem(), r => Assert.True(r.Success, r.Message));

        Assert.False(_backend.Values[property].Exists);
        Assert.False(_backend.Writes.Last().Value.Exists);
        Assert.Equal("EDID display", _backend.Current(device.Id));
    }

    [Fact]
    public void SystemAlreadyRestoredMissingFriendlyName_ClearsBackupWithoutWritingAgain()
    {
        var device = _backend.AddPnp(HardwareModelCategory.Monitor, "Monitor1", "EDID display", friendlyExists: false);
        var property = _backend.Devices.Single().Properties.Single();
        var service = Create();
        Assert.All(service.ApplySystemChanges([device with { CurrentName = "Display alias" }]), r => Assert.True(r.Success));
        _backend.Values[property] = HardwareDisplayValue.Missing;
        _backend.FailWrites.Add(property);
        var writeCount = _backend.Writes.Count;

        Assert.All(service.RestoreSystem(), r => Assert.True(r.Success, r.Message));

        Assert.Equal(writeCount, _backend.Writes.Count);
        Assert.False(service.HasSystemBackup);
    }

    [Fact]
    public void UnchangedDeviceWithoutFriendlyName_DoesNotCreatePropertyOrSystemBackup()
    {
        var device = _backend.AddPnp(HardwareModelCategory.Monitor, "Monitor1", "EDID display", friendlyExists: false);

        Assert.True(Assert.Single(Create().ApplySystemChanges([device])).Success);

        Assert.Empty(_backend.Writes);
        Assert.False(File.Exists(BackupPath));
        Assert.False(_backend.Values.Single().Value.Exists);
    }

    [Fact]
    public void RegistryOriginalKindAndRawBytes_AreRestoredExactly()
    {
        var board = _backend.AddBoard();
        var property = _backend.Devices.Single().Properties.First();
        // REG_EXPAND_SZ is deliberately different from the newly written REG_SZ.
        var original = new HardwareDisplayValue(true, 2, System.Text.Encoding.Unicode.GetBytes("%BOARD_NAME%\0\0"));
        _backend.Values[property] = original;
        var service = Create();
        Assert.All(service.ApplySystemChanges([board with { CurrentName = "Board alias" }]), r => Assert.True(r.Success));

        Assert.All(service.RestoreSystem(), r => Assert.True(r.Success, r.Message));

        Assert.True(_backend.Values[property].EqualsValue(original));
    }

    [Fact]
    public void BackupSaveFailure_PreventsAnySystemWrite()
    {
        var device = _backend.AddPnp(HardwareModelCategory.Gpu, "GPU1", "Original");
        var service = Create(new FaultFiles { FailBackupWrites = true });

        var result = Assert.Single(service.ApplySystemChanges([device with { CurrentName = "Target" }]));

        Assert.False(result.Success);
        Assert.Empty(_backend.Writes);
        Assert.False(service.HasSystemBackup);
        Assert.Equal("Original", _backend.Current(device.Id));
    }

    [Fact]
    public void ExistingMalformedBackup_FailsClosedAndRetainsFile()
    {
        var device = _backend.AddPnp(HardwareModelCategory.Gpu, "GPU1", "Original");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(BackupPath, "{broken}");

        var result = Assert.Single(Create().ApplySystemChanges([device with { CurrentName = "Target" }]));

        Assert.False(result.Success);
        Assert.Empty(_backend.Writes);
        Assert.Equal("{broken}", File.ReadAllText(BackupPath));
    }

    [Fact]
    public void PartialRestore_RemovesSuccessfulEntriesAndRetainsFailedOriginalForRetry()
    {
        var first = _backend.AddPnp(HardwareModelCategory.Gpu, "GPU1", "Original GPU");
        var second = _backend.AddPnp(HardwareModelCategory.Disk, "Disk1", "Original disk");
        var service = Create();
        Assert.All(service.ApplySystemChanges([first with { CurrentName = "GPU alias" }, second with { CurrentName = "Disk alias" }]),
            r => Assert.True(r.Success, r.Message));
        var failing = _backend.Devices.Single(d => d.Device.Id == second.Id).Properties.Single();
        _backend.FailWrites.Add(failing);

        var results = service.RestoreSystem();

        Assert.Contains(results, r => r.Success && r.DeviceId == first.Id);
        Assert.Contains(results, r => !r.Success && r.DeviceId == second.Id);
        var remaining = JsonSerializer.Deserialize<List<HardwareDisplayBackup>>(File.ReadAllText(BackupPath))!;
        Assert.Equal(second.Id, Assert.Single(remaining).DeviceId);
        Assert.Equal("Original GPU", _backend.Current(first.Id));
        Assert.Equal("Disk alias", _backend.Current(second.Id));
        _backend.FailWrites.Clear();
        Assert.All(Create().RestoreSystem(), r => Assert.True(r.Success, r.Message));
        Assert.Equal("Original disk", _backend.Current(second.Id));
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void BackendSaysWriteSucceededButReadbackUnchanged_DoesNotReportSuccess()
    {
        var device = _backend.AddPnp(HardwareModelCategory.Gpu, "GPU1", "Original");
        _backend.IgnoreWrites = true;

        var result = Assert.Single(Create().ApplySystemChanges([device with { CurrentName = "Target" }]));

        Assert.False(result.Success);
        Assert.Contains("读取校验", result.Message);
        Assert.True(File.Exists(BackupPath));
    }

    [Fact]
    public void OriginalReadFailure_PreventsWriteAndBackupCreation()
    {
        var device = _backend.AddPnp(HardwareModelCategory.Gpu, "GPU1", "Original");
        _backend.FailReads = true;

        var result = Assert.Single(Create().ApplySystemChanges([device with { CurrentName = "Target" }]));

        Assert.False(result.Success);
        Assert.Empty(_backend.Writes);
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public void LiveValueMatchingTarget_UsesNoWriteEvenWhenOriginalNameDiffers()
    {
        var device = _backend.AddPnp(HardwareModelCategory.Gpu, "GPU1", "Original");
        var service = Create();
        var target = device with { CurrentName = "Target" };
        Assert.True(Assert.Single(service.ApplySystemChanges([target])).Success);
        _backend.Writes.Clear();

        Assert.True(Assert.Single(service.ApplySystemChanges([target])).Success);
        Assert.Empty(_backend.Writes);
    }

    [Fact]
    public void RemovedDevice_DoesNotReportSystemApplySuccess()
    {
        var device = _backend.AddPnp(HardwareModelCategory.Gpu, "GPU1", "Original");
        _backend.Devices.Clear();

        Assert.False(Assert.Single(Create().ApplySystemChanges([device with { CurrentName = "Target" }])).Success);
        Assert.Empty(_backend.Writes);
    }

    [Fact]
    public void MemoryProfile_DoesNotCreateSystemBackupOrWriteReadOnlySmbios()
    {
        var device = _backend.AddMemory();
        var edited = device with { CurrentName = "Memory alias", CurrentManufacturer = "Local vendor" };
        var service = Create();

        Assert.True(Assert.Single(service.SaveProfile([edited])).Success);
        Assert.True(Assert.Single(service.ApplySystemChanges([edited])).Success);
        Assert.Empty(_backend.Writes);
        Assert.False(service.HasSystemBackup);
        Assert.Equal("Real PartNumber", Create().LoadSnapshot().Devices.Single(d => d.Category == HardwareModelCategory.Memory).OriginalName);
    }

    [Fact]
    public void NonAdmin_SystemApplyFailsButSavedLocalProfileRemainsUsable()
    {
        var device = _backend.AddPnp(HardwareModelCategory.Disk, "Disk1", "Original");
        _backend.IsAdmin = false;
        var service = Create();
        var edited = device with { CurrentName = "Profile disk" };

        Assert.True(Assert.Single(service.SaveProfile([edited])).Success);
        Assert.False(Assert.Single(service.ApplySystemChanges([edited])).Success);
        Assert.Empty(_backend.Writes);
        Assert.False(service.HasSystemBackup);
        Assert.Equal("Profile disk", Create().LoadSnapshot().Devices.Single(d => d.Id == device.Id).CurrentName);
    }

    [Fact]
    public void RestoreUntrustedBackupPath_RejectsWriteAndPreservesBackup()
    {
        var device = _backend.AddBoard();
        Directory.CreateDirectory(_directory);
        var entry = new HardwareDisplayBackup(HardwareModelCategory.Motherboard, device.Id,
            new(HardwareDisplayPropertyKind.Registry, @"SYSTEM\Forbidden", "SerialNumber"), HardwareDisplayValue.String("original"));
        File.WriteAllText(BackupPath, JsonSerializer.Serialize(new[] { entry }));

        Assert.False(Assert.Single(Create().RestoreSystem()).Success);
        Assert.Empty(_backend.Writes);
        Assert.True(File.Exists(BackupPath));
    }

    [Fact]
    public void DiscoveryFailure_ReturnsWarningsAndSixLocalEntries()
    {
        _backend.FailDiscovery = true;

        var snapshot = Create().LoadSnapshot();

        Assert.Equal(6, snapshot.Devices.Count);
        Assert.NotEmpty(snapshot.ReadWarnings);
        Assert.All(snapshot.Devices, d => Assert.False(d.CanApplySystem));
    }

    [Fact]
    public void NullProfileRecord_DoesNotBreakSnapshotAndReportsReadWarning()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ProfilePath, "[null]");

        var snapshot = Create().LoadSnapshot();

        Assert.Equal(6, snapshot.Devices.Count);
        Assert.Contains(snapshot.ReadWarnings, warning => warning.Contains("配置读取失败"));
    }

    [Fact]
    public void NullOriginalBackupRecord_FailsClosedBeforeSystemWrite()
    {
        var device = _backend.AddPnp(HardwareModelCategory.Gpu, "GPU1", "Original");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(BackupPath, "[null]");

        Assert.False(Assert.Single(Create().ApplySystemChanges([device with { CurrentName = "Target" }])).Success);
        Assert.Empty(_backend.Writes);
        Assert.Equal("[null]", File.ReadAllText(BackupPath));
    }

    [Fact]
    public void MemoryProfileIdentity_IsIndependentOfEnumerationOrderAndChangesWhenModuleModelChanges()
    {
        var first = WindowsHardwareDisplayBackend.MemoryProfileId("Physical Memory 0", "DIMM_A1", "BANK 0", "Part A", "Vendor");
        var same = WindowsHardwareDisplayBackend.MemoryProfileId("Physical Memory 0", "DIMM_A1", "BANK 0", "Part A", "Vendor");
        var replaced = WindowsHardwareDisplayBackend.MemoryProfileId("Physical Memory 0", "DIMM_A1", "BANK 0", "Part B", "Vendor");
        var otherSlot = WindowsHardwareDisplayBackend.MemoryProfileId("Physical Memory 1", "DIMM_B1", "BANK 1", "Part A", "Vendor");

        Assert.Equal(first, same);
        Assert.NotEqual(first, replaced);
        Assert.NotEqual(first, otherSlot);
    }

    [Fact]
    public void ClearProfile_DoesNotDeleteNewSystemBackupOrLegacyBackup()
    {
        var device = _backend.AddPnp(HardwareModelCategory.Gpu, "GPU1", "Original");
        var edited = device with { CurrentName = "Target" };
        var service = Create();
        service.SaveProfile([edited]);
        service.ApplySystemChanges([edited]);
        var legacy = Path.Combine(_directory, "hardware_spoofer_backup.json");
        File.WriteAllText(legacy, "legacy-original");

        service.ClearProfile();

        Assert.False(File.Exists(ProfilePath));
        Assert.True(File.Exists(BackupPath));
        Assert.Equal("legacy-original", File.ReadAllText(legacy));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeBackend : IHardwareDisplayBackend
    {
        public bool IsAdmin { get; set; } = true;
        public bool IgnoreWrites { get; set; }
        public bool FailReads { get; set; }
        public bool FailDiscovery { get; set; }
        public List<HardwareDisplayDetectedDevice> Devices { get; } = [];
        public Dictionary<HardwareDisplayProperty, HardwareDisplayValue> Values { get; } = [];
        public List<(HardwareDisplayProperty Property, HardwareDisplayValue Value)> Writes { get; } = [];
        public HashSet<HardwareDisplayProperty> FailWrites { get; } = [];

        public EditorDevice AddPnp(HardwareModelCategory category, string instance, string name, bool friendlyExists = true) =>
            Add(category, "pnp/" + instance, name, [new(HardwareDisplayPropertyKind.PnpFriendlyName, instance, "FriendlyName")], friendlyExists);
        public EditorDevice AddCpu(int cores) => Add(HardwareModelCategory.Cpu, "registry/cpu", "Original CPU",
            Enumerable.Range(0, cores).Select(i => new HardwareDisplayProperty(HardwareDisplayPropertyKind.Registry,
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\" + i, "ProcessorNameString")).ToArray());
        public EditorDevice AddBoard() => Add(HardwareModelCategory.Motherboard, "registry/baseboard", "Original board",
            [new(HardwareDisplayPropertyKind.Registry, @"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardProduct"),
             new(HardwareDisplayPropertyKind.Registry, @"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardManufacturer", true)]);
        public EditorDevice AddMemory() => Add(HardwareModelCategory.Memory, "memory/slot1", "Real PartNumber", []);

        private EditorDevice Add(HardwareModelCategory category, string id, string name,
            HardwareDisplayProperty[] properties, bool exists = true)
        {
            var device = new EditorDevice(category, id, name, "Detected vendor", name, "Detected vendor", "Test scope", properties.Length > 0);
            Devices.Add(new(device, properties, name));
            foreach (var property in properties)
                Values[property] = exists ? HardwareDisplayValue.String(property.IsManufacturer ? "Detected vendor" : name) : HardwareDisplayValue.Missing;
            return device;
        }

        public string Current(string id)
        {
            var device = Devices.Single(d => d.Device.Id == id);
            var property = device.Properties.FirstOrDefault(p => !p.IsManufacturer);
            return property is not null && Values[property].Exists ? Values[property].AsString() : device.NativeName;
        }

        public HardwareDisplayDiscovery Discover()
        {
            if (FailDiscovery) throw new IOException("Synthetic discovery error");
            return new(Devices.Select(d => d with { Device = d.Device with
            { OriginalName = Current(d.Device.Id), CurrentName = Current(d.Device.Id) } }).ToArray(), []);
        }

        public HardwareDisplayValue Read(HardwareDisplayProperty property)
        {
            if (FailReads) throw new IOException("Synthetic original read failure");
            return Values[property];
        }

        public void Write(HardwareDisplayProperty property, HardwareDisplayValue value)
        {
            if (FailWrites.Contains(property)) throw new IOException("Synthetic write failure");
            Writes.Add((property, value));
            if (!IgnoreWrites) Values[property] = value;
        }
    }

    private sealed class FaultFiles : IHardwareDisplayFileStore
    {
        private readonly HardwareDisplayFileStore _inner = new();
        public bool FailBackupWrites { get; init; }
        public bool Exists(string path) => _inner.Exists(path);
        public string ReadAllText(string path) => _inner.ReadAllText(path);
        public void Delete(string path) => _inner.Delete(path);
        public void WriteAtomically(string path, string contents)
        {
            if (FailBackupWrites && Path.GetFileName(path) == "hardware-display-system-backup.json")
                throw new IOException("Synthetic atomic backup save failure");
            _inner.WriteAtomically(path, contents);
        }
    }
}
