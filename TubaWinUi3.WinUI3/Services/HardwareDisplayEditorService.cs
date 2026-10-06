using System.ComponentModel;
using System.Collections.Concurrent;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace TubaWinUi3.Services;

/// <summary>Names in this editor are display preferences, never hardware capabilities or identity.</summary>
public sealed record EditorDevice(HardwareModelCategory Category, string Id, string OriginalName,
    string OriginalManufacturer, string CurrentName, string CurrentManufacturer, string Scope, bool CanApplySystem)
{
    /// <summary>
    /// Effective Windows display name observed during discovery, before applying the saved local profile.
    /// A PnP device without FriendlyName uses its device description; local-only devices have no value.
    /// </summary>
    public string? CurrentWindowsName { get; init; }
}

public sealed record HardwareEditorSnapshot(IReadOnlyList<EditorDevice> Devices, IReadOnlyList<string> ReadWarnings);
public sealed record HardwareEditorResult(HardwareModelCategory Category, string DeviceId, bool Success, string Message);

/// <summary>
/// Stores local names for all six categories. Optional system writes are limited to CPU/baseboard display
/// strings and one present PnP device's FriendlyName. The hardware-information service remains independent.
/// </summary>
public sealed class HardwareDisplayEditorService
{
    private static readonly ConcurrentDictionary<string, object> DirectoryGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate;
    private readonly string _profilePath;
    private readonly string _backupPath;
    private readonly IHardwareDisplayBackend _backend;
    private readonly IHardwareDisplayFileStore _files;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public HardwareDisplayEditorService() : this(ConfigManager.GetDataDir(), new WindowsHardwareDisplayBackend()) { }

    internal HardwareDisplayEditorService(string dataDirectory, IHardwareDisplayBackend backend,
        IHardwareDisplayFileStore? files = null)
    {
        _gate = DirectoryGates.GetOrAdd(Path.GetFullPath(dataDirectory), _ => new object());
        _profilePath = Path.Combine(dataDirectory, "hardware-display-profile.json");
        _backupPath = Path.Combine(dataDirectory, "hardware-display-system-backup.json");
        _backend = backend;
        _files = files ?? new HardwareDisplayFileStore();
    }

    public bool IsAdmin => _backend.IsAdmin;
    public bool HasSystemBackup => _files.Exists(_backupPath);

    /// <summary>Call from a worker task. Memory WMI discovery has a bounded wait.</summary>
    public HardwareEditorSnapshot LoadSnapshot()
    {
        lock (_gate)
        {
            var warnings = new List<string>();
            var detected = Discover(warnings);
            var profiles = new List<HardwareDisplayProfile>();
            var backups = new List<HardwareDisplayBackup>();
            try { profiles = ReadDocument<HardwareDisplayProfile>(_profilePath); }
            catch (Exception ex) { warnings.Add($"本地展示配置读取失败：{ex.Message}"); }
            try { backups = ReadDocument<HardwareDisplayBackup>(_backupPath); }
            catch (Exception ex) { warnings.Add($"系统名称备份读取失败，写入前需要修复备份：{ex.Message}"); }

            var devices = new List<EditorDevice>();
            foreach (var entry in detected)
            {
                var device = entry.Device;
                var nameBackup = backups.FirstOrDefault(b => b.DeviceId == device.Id &&
                    entry.Properties.Any(p => p == b.Property && !p.IsManufacturer));
                var manufacturerBackup = backups.FirstOrDefault(b => b.DeviceId == device.Id &&
                    entry.Properties.Any(p => p == b.Property && p.IsManufacturer));
                var originalName = nameBackup is null ? device.OriginalName :
                    nameBackup.Original.Exists ? nameBackup.Original.AsString() : entry.NativeName;
                var originalManufacturer = manufacturerBackup is null ? device.OriginalManufacturer :
                    manufacturerBackup.Original.Exists ? manufacturerBackup.Original.AsString() : "";
                var profile = profiles.LastOrDefault(p => p.Category == device.Category && p.DeviceId == device.Id);
                devices.Add(device with
                {
                    OriginalName = originalName,
                    OriginalManufacturer = originalManufacturer,
                    CurrentWindowsName = device.CanApplySystem ? device.CurrentName : null,
                    CurrentName = profile?.Name ?? device.CurrentName,
                    CurrentManufacturer = profile?.Manufacturer ?? device.CurrentManufacturer
                });
            }

            foreach (var category in Enum.GetValues<HardwareModelCategory>())
            {
                if (devices.Any(d => d.Category == category)) continue;
                var id = $"local/{category}";
                var profile = profiles.LastOrDefault(p => p.Category == category && p.DeviceId == id);
                devices.Add(new(category, id, "未检测到设备", "", profile?.Name ?? "", profile?.Manufacturer ?? "",
                    "仅本工具展示配置（未检测到设备）", false));
            }
            return new(devices, warnings);
        }
    }

    public IReadOnlyList<HardwareEditorResult> SaveProfile(IEnumerable<EditorDevice> items)
    {
        lock (_gate)
        {
            var selected = items.ToArray();
            var results = new List<HardwareEditorResult>();
            try
            {
                var profiles = ReadDocument<HardwareDisplayProfile>(_profilePath);
                foreach (var item in selected)
                {
                    ValidateName(item);
                    profiles.RemoveAll(p => p.Category == item.Category && p.DeviceId == item.Id);
                    profiles.Add(new(item.Category, item.Id, item.CurrentName.Trim(), item.CurrentManufacturer.Trim()));
                }
                _files.WriteAtomically(_profilePath, JsonSerializer.Serialize(profiles, JsonOptions));
                results.AddRange(selected.Select(i => Result(i, true, "本地展示配置已保存")));
            }
            catch (Exception ex)
            {
                results.AddRange(selected.Select(i => Result(i, false, $"本地展示配置未保存：{ex.Message}")));
            }
            return results;
        }
    }

    public void ClearProfile()
    {
        lock (_gate) { if (_files.Exists(_profilePath)) _files.Delete(_profilePath); }
    }

    public IReadOnlyList<HardwareEditorResult> ApplySystemChanges(IEnumerable<EditorDevice> items)
    {
        lock (_gate)
        {
            var selected = items.ToArray();
            var results = new List<HardwareEditorResult>();
            List<HardwareDisplayBackup> backup;
            try { backup = ReadDocument<HardwareDisplayBackup>(_backupPath); }
            catch (Exception ex)
            {
                return selected.Select(i => Result(i, false, $"未写入系统：原始备份无法读取（{ex.Message}）")).ToArray();
            }
            var warnings = new List<string>();
            var devices = Discover(warnings);
            foreach (var item in selected)
            {
                try
                {
                    ValidateName(item);
                    var actual = devices.FirstOrDefault(d => d.Device.Id == item.Id && d.Device.Category == item.Category);
                    if (actual is null && item.CanApplySystem)
                    {
                        results.Add(Result(item, false, "原设备当前未检测到，未写入系统；请刷新设备后重试"));
                        continue;
                    }
                    if (actual is null || !actual.Device.CanApplySystem || actual.Properties.Count == 0)
                    {
                        results.Add(Result(item, true, "此设备仅支持本地展示配置，未写入系统"));
                        continue;
                    }
                    var changes = new List<(HardwareDisplayProperty Property, HardwareDisplayValue Value)>();
                    foreach (var property in actual.Properties)
                    {
                        var name = property.IsManufacturer ? item.CurrentManufacturer.Trim() : item.CurrentName.Trim();
                        // An empty optional manufacturer leaves the real board manufacturer alone.
                        if (property.IsManufacturer && name.Length == 0) continue;
                        var original = _backend.Read(property);
                        if (original.Exists && original.Kind == 1 && original.AsString() == name) continue;
                        if (!original.Exists && property.Kind == HardwareDisplayPropertyKind.PnpFriendlyName &&
                            actual.Device.CurrentName == name) continue;
                        if (original.Exists && property.Kind == HardwareDisplayPropertyKind.PnpFriendlyName && original.Kind != 1)
                            throw new IOException("原 FriendlyName 不是 REG_SZ，未修改，以便保留原始类型");
                        changes.Add((property, HardwareDisplayValue.String(name)));
                        if (!backup.Any(b => b.Property == property))
                            backup.Add(new(item.Category, item.Id, property, original));
                    }
                    if (changes.Count == 0)
                    {
                        results.Add(Result(item, true, "系统显示名称已是目标名称，无需写入"));
                        continue;
                    }
                    if (!IsAdmin)
                    {
                        results.Add(Result(item, false, "同步系统显示名称需要管理员权限；本地展示配置不需要管理员权限"));
                        // Do not retain unsaved new originals after an unsupported attempt.
                        backup = ReadDocument<HardwareDisplayBackup>(_backupPath);
                        continue;
                    }
                    // Failure to durably persist the exact first original MUST stop all writes for this device.
                    _files.WriteAtomically(_backupPath, JsonSerializer.Serialize(backup, JsonOptions));
                    var errors = new List<string>();
                    var applied = 0;
                    foreach (var change in changes)
                    {
                        try
                        {
                            _backend.Write(change.Property, change.Value);
                            if (!_backend.Read(change.Property).EqualsValue(change.Value))
                                throw new IOException("写入后读取校验不一致");
                            applied++;
                        }
                        catch (Exception ex) { errors.Add($"{change.Property.ValueName}：{ex.Message}"); }
                    }
                    results.Add(Result(item, errors.Count == 0, errors.Count == 0
                        ? $"已同步 {applied} 项系统显示名称（仅显示字符串）"
                        : $"系统同步 {applied}/{changes.Count} 项成功；{string.Join("；", errors)}。原始备份已保留"));
                }
                catch (Exception ex)
                {
                    results.Add(Result(item, false, $"未完成系统名称同步：{ex.Message}"));
                    // Reload durable state: a failed read/save must not become a later item's original backup.
                    try { backup = ReadDocument<HardwareDisplayBackup>(_backupPath); }
                    catch { break; }
                }
            }
            return results;
        }
    }

    public IReadOnlyList<HardwareEditorResult> RestoreSystem()
    {
        lock (_gate)
        {
            var results = new List<HardwareEditorResult>();
            List<HardwareDisplayBackup> backup;
            try { backup = ReadDocument<HardwareDisplayBackup>(_backupPath); }
            catch (Exception ex) { return [new(HardwareModelCategory.Cpu, "backup", false, $"备份读取失败：{ex.Message}")]; }
            if (backup.Count == 0) return results;
            var warnings = new List<string>();
            var actual = Discover(warnings);
            var remaining = new List<HardwareDisplayBackup>();
            foreach (var entry in backup)
            {
                try
                {
                    if (!IsAdmin) throw new UnauthorizedAccessException("恢复系统显示名称需要管理员权限");
                    // Resolve against live allowlisted properties; never trust a path supplied by a JSON file.
                    var device = actual.FirstOrDefault(d => d.Device.Id == entry.DeviceId && d.Device.Category == entry.Category);
                    if (device is null || !device.Properties.Contains(entry.Property))
                        throw new InvalidOperationException("原设备当前不存在或备份字段不属于允许恢复的显示名称");
                    // Reboot or driver reinstall may already have restored the original,
                    // including deletion of a FriendlyName that did not exist before editing.
                    if (!_backend.Read(entry.Property).EqualsValue(entry.Original))
                        _backend.Write(entry.Property, entry.Original);
                    if (!_backend.Read(entry.Property).EqualsValue(entry.Original))
                        throw new IOException("恢复后读取校验不一致");
                    results.Add(new(entry.Category, entry.DeviceId, true, $"已恢复 {entry.Property.ValueName}"));
                }
                catch (Exception ex)
                {
                    remaining.Add(entry);
                    results.Add(new(entry.Category, entry.DeviceId, false, $"{entry.Property.ValueName} 恢复失败：{ex.Message}；备份保留以便重试"));
                }
            }
            try
            {
                if (remaining.Count == 0) _files.Delete(_backupPath);
                else _files.WriteAtomically(_backupPath, JsonSerializer.Serialize(remaining, JsonOptions));
            }
            catch (Exception ex)
            {
                results.Add(new(HardwareModelCategory.Cpu, "backup", false,
                    $"恢复结果的备份记录更新失败：{ex.Message}；原备份保留，可再次恢复"));
            }
            return results;
        }
    }

    private List<HardwareDisplayDetectedDevice> Discover(List<string> warnings)
    {
        try
        {
            var discovery = _backend.Discover();
            warnings.AddRange(discovery.Warnings);
            return discovery.Devices.ToList();
        }
        catch (Exception ex) { warnings.Add($"硬件读取失败：{ex.Message}"); return []; }
    }

    private List<T> ReadDocument<T>(string path)
    {
        if (!_files.Exists(path)) return [];
        var records = JsonSerializer.Deserialize<List<T>>(_files.ReadAllText(path))
            ?? throw new IOException("文件内容为空或格式无效");
        foreach (var record in records)
        {
            if (record is null) throw new IOException("配置记录不能为 null");
            if (record is HardwareDisplayProfile profile && (!Enum.IsDefined(profile.Category) ||
                string.IsNullOrWhiteSpace(profile.DeviceId) || string.IsNullOrWhiteSpace(profile.Name) || profile.Manufacturer is null))
                throw new IOException("本地展示配置记录不完整");
            if (record is HardwareDisplayBackup backup && (!Enum.IsDefined(backup.Category) ||
                string.IsNullOrWhiteSpace(backup.DeviceId) || backup.Property is null || backup.Original is null ||
                backup.Original.Data is null || string.IsNullOrWhiteSpace(backup.Property.Path) || string.IsNullOrWhiteSpace(backup.Property.ValueName)))
                throw new IOException("原始系统备份记录不完整");
        }
        return records;
    }

    private static HardwareEditorResult Result(EditorDevice item, bool success, string message) =>
        new(item.Category, item.Id, success, message);

    private static void ValidateName(EditorDevice item)
    {
        if (!Enum.IsDefined(item.Category) || string.IsNullOrWhiteSpace(item.Id))
            throw new ArgumentException("设备类别或设备标识无效");
        if (string.IsNullOrWhiteSpace(item.CurrentName) || item.CurrentName.Length > 256 || item.CurrentName.Any(char.IsControl))
            throw new ArgumentException("型号名称需为 1–256 个字符，且不能包含控制字符");
        if (item.CurrentManufacturer is null || item.CurrentManufacturer.Length > 256 || item.CurrentManufacturer.Any(char.IsControl))
            throw new ArgumentException("厂商名称不能超过 256 个字符或包含控制字符");
    }
}

internal sealed record HardwareDisplayProfile(HardwareModelCategory Category, string DeviceId, string Name, string Manufacturer);
internal sealed record HardwareDisplayBackup(HardwareModelCategory Category, string DeviceId,
    HardwareDisplayProperty Property, HardwareDisplayValue Original);
internal enum HardwareDisplayPropertyKind { Registry, PnpFriendlyName }
internal sealed record HardwareDisplayProperty(HardwareDisplayPropertyKind Kind, string Path, string ValueName, bool IsManufacturer = false);
internal sealed record HardwareDisplayDetectedDevice(EditorDevice Device, IReadOnlyList<HardwareDisplayProperty> Properties, string NativeName);
internal sealed record HardwareDisplayDiscovery(IReadOnlyList<HardwareDisplayDetectedDevice> Devices, IReadOnlyList<string> Warnings);
internal sealed record HardwareDisplayValue(bool Exists, int Kind, byte[] Data)
{
    public static HardwareDisplayValue Missing => new(false, 0, []);
    public static HardwareDisplayValue String(string value) => new(true, 1, Encoding.Unicode.GetBytes(value + '\0'));
    public string AsString() => Exists && (Kind == 1 || Kind == 2) ? Encoding.Unicode.GetString(Data).TrimEnd('\0') : "";
    public bool EqualsValue(HardwareDisplayValue other) => Exists == other.Exists &&
        (!Exists || (Kind == other.Kind && Data.AsSpan().SequenceEqual(other.Data)));
}

internal interface IHardwareDisplayBackend
{
    bool IsAdmin { get; }
    HardwareDisplayDiscovery Discover();
    HardwareDisplayValue Read(HardwareDisplayProperty property);
    void Write(HardwareDisplayProperty property, HardwareDisplayValue value);
}

internal interface IHardwareDisplayFileStore
{
    bool Exists(string path);
    string ReadAllText(string path);
    void WriteAtomically(string path, string contents);
    void Delete(string path);
}

internal sealed class HardwareDisplayFileStore : IHardwareDisplayFileStore
{
    public bool Exists(string path) => File.Exists(path);
    public string ReadAllText(string path) => File.ReadAllText(path);
    public void Delete(string path) => File.Delete(path);
    public void WriteAtomically(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                var bytes = Encoding.UTF8.GetBytes(contents);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed class WindowsHardwareDisplayBackend : IHardwareDisplayBackend
{
    private Task<List<HardwareDisplayDetectedDevice>>? _memoryTask;
    private const string ProcessorRoot = @"HARDWARE\DESCRIPTION\System\CentralProcessor";
    private const string BoardPath = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const uint FriendlyName = 0xC, DeviceDescription = 0, Manufacturer = 0xB;
    private const int InsufficientBuffer = 122, InvalidData = 13, NoMoreItems = 259, FileNotFound = 2;
    private static readonly (HardwareModelCategory Category, Guid Class)[] DeviceClasses =
    [
        (HardwareModelCategory.Gpu, new("4d36e968-e325-11ce-bfc1-08002be10318")),
        (HardwareModelCategory.Monitor, new("4d36e96e-e325-11ce-bfc1-08002be10318")),
        (HardwareModelCategory.Disk, new("4d36e967-e325-11ce-bfc1-08002be10318"))
    ];

    public bool IsAdmin
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    public HardwareDisplayDiscovery Discover()
    {
        var devices = new List<HardwareDisplayDetectedDevice>();
        var warnings = new List<string>();
        // WMI providers can ignore their own timeout. The outer bounded worker wait protects the editor.
        var memoryTask = _memoryTask ??= Task.Run(ReadMemory);
        try { ReadProcessors(devices); }
        catch (Exception ex) { warnings.Add($"CPU 读取失败：{ex.Message}"); }
        try { ReadBoard(devices); }
        catch (Exception ex) { warnings.Add($"主板读取失败：{ex.Message}"); }
        foreach (var (category, deviceClass) in DeviceClasses)
        {
            try { ReadPnpDevices(category, deviceClass, devices, warnings); }
            catch (Exception ex) { warnings.Add($"{category} 设备读取失败：{ex.Message}"); }
        }
        try
        {
            if (memoryTask.Wait(TimeSpan.FromSeconds(3)))
            {
                devices.AddRange(memoryTask.GetAwaiter().GetResult());
                _memoryTask = null;
            }
            else
            {
                warnings.Add("内存读取超时；可先保存本地展示配置");
                _ = memoryTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        catch (Exception ex)
        {
            _memoryTask = null;
            warnings.Add($"内存读取失败：{ex.GetBaseException().Message}");
        }
        return new(devices, warnings);
    }

    private void ReadProcessors(List<HardwareDisplayDetectedDevice> devices)
    {
        using var root = Registry.LocalMachine.OpenSubKey(ProcessorRoot);
        if (root is null) return;
        var properties = root.GetSubKeyNames().Where(n => int.TryParse(n, out _)).OrderBy(n => int.Parse(n))
            .Select(n => new HardwareDisplayProperty(HardwareDisplayPropertyKind.Registry,
                ProcessorRoot + "\\" + n, "ProcessorNameString")).ToArray();
        if (properties.Length == 0) return;
        var name = Read(properties[0]).AsString().Trim();
        using var first = Registry.LocalMachine.OpenSubKey(properties[0].Path);
        var manufacturer = first?.GetValue("VendorIdentifier") as string ?? "";
        var packageCount = ReadProcessorPackageCount();
        var canApply = packageCount == 1;
        var scope = canApply ? "Windows CPU 显示名称（单处理器所有逻辑核心；厂商仅本地保存）"
            : "仅本工具展示配置（多处理器或处理器拓扑未知，无法可靠关联各核心注册表键）";
        devices.Add(new(new(HardwareModelCategory.Cpu, "registry/cpu", name, manufacturer, name, manufacturer,
            scope, canApply), canApply ? properties : [], name));
    }

    private static int ReadProcessorPackageCount()
    {
        uint length = 0;
        if (!GetLogicalProcessorInformationEx(3 /* RelationProcessorPackage */, null, ref length) &&
            Marshal.GetLastWin32Error() != InsufficientBuffer) return 0;
        if (length < 8 || length > 1024 * 1024) return 0;
        var bytes = new byte[length];
        if (!GetLogicalProcessorInformationEx(3, bytes, ref length)) return 0;
        var count = 0;
        for (var offset = 0; offset + 8 <= length;)
        {
            var relation = BitConverter.ToUInt32(bytes, offset);
            var size = BitConverter.ToUInt32(bytes, offset + 4);
            if (size < 8 || offset + size > length) return 0;
            if (relation == 3) count++;
            offset += (int)size;
        }
        return count;
    }

    private void ReadBoard(List<HardwareDisplayDetectedDevice> devices)
    {
        using var board = Registry.LocalMachine.OpenSubKey(BoardPath);
        if (board is null) return;
        HardwareDisplayProperty[] properties =
        [new(HardwareDisplayPropertyKind.Registry, BoardPath, "BaseBoardProduct"),
         new(HardwareDisplayPropertyKind.Registry, BoardPath, "BaseBoardManufacturer", true)];
        var name = Read(properties[0]).AsString();
        var manufacturer = Read(properties[1]).AsString();
        devices.Add(new(new(HardwareModelCategory.Motherboard, "registry/baseboard", name, manufacturer, name,
            manufacturer, "Windows 主板名称和厂商显示字符串", true), properties, name));
    }

    private static List<HardwareDisplayDetectedDevice> ReadMemory()
    {
        var devices = new List<HardwareDisplayDetectedDevice>();
        using var searcher = new ManagementObjectSearcher(new ManagementScope("root\\CIMV2"),
            new ObjectQuery("SELECT Tag, DeviceLocator, BankLabel, PartNumber, Manufacturer FROM Win32_PhysicalMemory"),
            new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(3), ReturnImmediately = false });
        using var collection = searcher.Get();
        var modules = new List<(string Tag, string Locator, string Bank, string PartNumber, string Manufacturer)>();
        foreach (ManagementObject memory in collection)
        {
            using (memory)
            {
                modules.Add((memory["Tag"]?.ToString()?.Trim() ?? "", memory["DeviceLocator"]?.ToString()?.Trim() ?? "",
                    memory["BankLabel"]?.ToString()?.Trim() ?? "", memory["PartNumber"]?.ToString()?.Trim() ?? "",
                    memory["Manufacturer"]?.ToString()?.Trim() ?? ""));
            }
        }
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var module in modules.OrderBy(m => m.Locator, StringComparer.Ordinal).ThenBy(m => m.Bank, StringComparer.Ordinal)
                     .ThenBy(m => m.Tag, StringComparer.Ordinal).ThenBy(m => m.PartNumber, StringComparer.Ordinal)
                     .ThenBy(m => m.Manufacturer, StringComparer.Ordinal))
        {
            var id = MemoryProfileId(module.Tag, module.Locator, module.Bank, module.PartNumber, module.Manufacturer);
            ids.TryGetValue(id, out var duplicate);
            ids[id] = duplicate + 1;
            if (duplicate > 0) id += "/" + duplicate;
            var name = module.PartNumber.Length == 0 ? "内存模块" : module.PartNumber;
            devices.Add(new(new(HardwareModelCategory.Memory, id, name, module.Manufacturer, name, module.Manufacturer,
                $"仅本工具展示配置（{(module.Locator.Length == 0 ? "内存插槽" : module.Locator)}；SMBIOS PartNumber 为只读）", false), [], name));
        }
        return devices;
    }

    internal static string MemoryProfileId(string tag, string locator, string bank, string partNumber, string manufacturer)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(partNumber + "\0" + manufacturer)))[..16];
        return $"memory/{Uri.EscapeDataString(locator)}/{Uri.EscapeDataString(bank)}/{Uri.EscapeDataString(tag)}/{fingerprint}";
    }

    private static void ReadPnpDevices(HardwareModelCategory category, Guid deviceClass,
        List<HardwareDisplayDetectedDevice> devices, List<string> warnings)
    {
        var set = SetupDiGetClassDevs(ref deviceClass, null, IntPtr.Zero, 2 /* DIGCF_PRESENT */);
        if (set == new IntPtr(-1)) throw Error();
        try
        {
            for (uint index = 0; ; index++)
            {
                var device = DeviceInfo.Create();
                if (!SetupDiEnumDeviceInfo(set, index, ref device))
                {
                    if (Marshal.GetLastWin32Error() == NoMoreItems) break;
                    throw Error();
                }
                try
                {
                    var idBuffer = new StringBuilder(4096);
                    if (!SetupDiGetDeviceInstanceId(set, ref device, idBuffer, idBuffer.Capacity, out _)) throw Error();
                    var instanceId = idBuffer.ToString();
                    var fallback = ReadPnpProperty(set, ref device, DeviceDescription).AsString();
                    var friendly = ReadPnpProperty(set, ref device, FriendlyName);
                    var name = friendly.Exists ? friendly.AsString() : fallback;
                    var manufacturer = ReadPnpProperty(set, ref device, Manufacturer).AsString();
                    var property = new HardwareDisplayProperty(HardwareDisplayPropertyKind.PnpFriendlyName, instanceId, "FriendlyName");
                    devices.Add(new(new(category, "pnp/" + instanceId, name, manufacturer, name, manufacturer,
                        "此设备的 Windows PnP FriendlyName（厂商仅本地保存；真实硬件型号不变）", true), [property], fallback));
                }
                catch (Exception ex) { warnings.Add($"{category} 第 {index + 1} 个设备读取失败：{ex.Message}"); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    public HardwareDisplayValue Read(HardwareDisplayProperty property)
    {
        ValidateProperty(property);
        if (property.Kind == HardwareDisplayPropertyKind.Registry)
        {
            using var key = Registry.LocalMachine.OpenSubKey(property.Path) ?? throw new IOException("原注册表键不存在");
            uint length = 0;
            var result = RegQueryValueEx(key.Handle, property.ValueName, IntPtr.Zero, out var kind, null, ref length);
            if (result == FileNotFound) return HardwareDisplayValue.Missing;
            if (result != 0 && result != InsufficientBuffer) throw new Win32Exception(result);
            if (length > 1024 * 1024) throw new IOException("显示名称值异常过大");
            var data = new byte[length];
            result = RegQueryValueEx(key.Handle, property.ValueName, IntPtr.Zero, out kind, data, ref length);
            if (result != 0) throw new Win32Exception(result);
            return new(true, (int)kind, data.AsSpan(0, (int)length).ToArray());
        }
        return WithPresentDevice(property, (IntPtr set, ref DeviceInfo device) => ReadPnpProperty(set, ref device, FriendlyName));
    }

    public void Write(HardwareDisplayProperty property, HardwareDisplayValue value)
    {
        ValidateProperty(property);
        if (property.Kind == HardwareDisplayPropertyKind.Registry)
        {
            using var key = Registry.LocalMachine.OpenSubKey(property.Path, writable: true) ?? throw new IOException("原注册表键不存在");
            var result = value.Exists
                ? RegSetValueEx(key.Handle, property.ValueName, 0, (uint)value.Kind, value.Data, (uint)value.Data.Length)
                : RegDeleteValue(key.Handle, property.ValueName);
            if (result != 0 && !(result == FileNotFound && !value.Exists)) throw new Win32Exception(result);
            return;
        }
        WithPresentDevice(property, (IntPtr set, ref DeviceInfo device) =>
        {
            // A null, zero-length buffer deletes a missing-before-edit FriendlyName property.
            if (value.Exists && value.Kind != 1) throw new IOException("PnP FriendlyName 原值类型不是 REG_SZ，无法精确恢复");
            if (!SetupDiSetDeviceRegistryProperty(set, ref device, FriendlyName,
                    value.Exists ? value.Data : null, value.Exists ? (uint)value.Data.Length : 0)) throw Error();
            return true;
        });
    }

    private delegate T DeviceAction<T>(IntPtr set, ref DeviceInfo device);
    private static T WithPresentDevice<T>(HardwareDisplayProperty property, DeviceAction<T> action)
    {
        // Enumerate the present class each time instead of opening an arbitrary/stale Enum registry path.
        foreach (var (_, deviceClass) in DeviceClasses)
        {
            var classGuid = deviceClass;
            var set = SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, 2);
            if (set == new IntPtr(-1)) throw Error();
            try
            {
                for (uint index = 0; ; index++)
                {
                    var device = DeviceInfo.Create();
                    if (!SetupDiEnumDeviceInfo(set, index, ref device))
                    {
                        if (Marshal.GetLastWin32Error() == NoMoreItems) break;
                        throw Error();
                    }
                    var id = new StringBuilder(4096);
                    if (!SetupDiGetDeviceInstanceId(set, ref device, id, id.Capacity, out _)) throw Error();
                    if (id.ToString().Equals(property.Path, StringComparison.OrdinalIgnoreCase)) return action(set, ref device);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
        throw new IOException("选择的设备当前未连接或已移除");
    }

    private static HardwareDisplayValue ReadPnpProperty(IntPtr set, ref DeviceInfo device, uint property)
    {
        if (!SetupDiGetDeviceRegistryProperty(set, ref device, property, out var kind, null, 0, out var required))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == InvalidData) return HardwareDisplayValue.Missing;
            if (error != InsufficientBuffer) throw new Win32Exception(error);
        }
        if (required > 1024 * 1024) throw new IOException("设备名称值异常过大");
        var buffer = new byte[required];
        if (!SetupDiGetDeviceRegistryProperty(set, ref device, property, out kind, buffer, required, out required)) throw Error();
        return new(true, (int)kind, buffer.AsSpan(0, (int)required).ToArray());
    }

    private static void ValidateProperty(HardwareDisplayProperty property)
    {
        if (property.Kind == HardwareDisplayPropertyKind.PnpFriendlyName && property.ValueName == "FriendlyName" && !property.IsManufacturer)
            return; // WithPresentDevice additionally requires a present GPU, monitor, or disk class.
        if (property.Kind == HardwareDisplayPropertyKind.Registry &&
            ((property.Path.StartsWith(ProcessorRoot + "\\", StringComparison.Ordinal) &&
              int.TryParse(property.Path[(ProcessorRoot.Length + 1)..], out _) &&
              property.ValueName == "ProcessorNameString" && !property.IsManufacturer) ||
             (property.Path == BoardPath && ((property.ValueName == "BaseBoardProduct" && !property.IsManufacturer) ||
                                           (property.ValueName == "BaseBoardManufacturer" && property.IsManufacturer))))) return;
        throw new InvalidOperationException("只允许修改配置修改器支持的显示名称字段");
    }

    private static Win32Exception Error() => new(Marshal.GetLastWin32Error());
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public UIntPtr Reserved;
        public static DeviceInfo Create() => new() { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
    }

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(uint relation, [Out] byte[]? buffer, ref uint length);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo device);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DeviceInfo device, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref DeviceInfo device, uint property,
        out uint kind, byte[]? buffer, uint size, out uint required);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiSetDeviceRegistryPropertyW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref DeviceInfo device, uint property, byte[]? buffer, uint size);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueEx(SafeRegistryHandle key, string name, IntPtr reserved, out uint kind, byte[]? data, ref uint size);
    [DllImport("advapi32.dll", EntryPoint = "RegSetValueExW", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueEx(SafeRegistryHandle key, string name, uint reserved, uint kind, byte[] data, uint size);
    [DllImport("advapi32.dll", EntryPoint = "RegDeleteValueW", CharSet = CharSet.Unicode)]
    private static extern int RegDeleteValue(SafeRegistryHandle key, string name);
}
