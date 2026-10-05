using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TubaWinUi3.Services;

namespace TubaWinUi3.Services.Ai;

/// <summary>
/// AI 提供商配置存储：管理 ai_providers.json（DeepSeek 预设 + 用户自定义），
/// 以及当前选中的提供商/模型（聊天界面切换即持久化到这里）。
/// 兼容旧版扁平配置：首次加载时把 AiApiEndpoint / AiModelName / AiApiKey 迁移进自定义提供商。
///
/// ZXAI 2026-09-19：枕星图吧AI助手只保留 DeepSeek 一家预设——用户自己去
/// platform.deepseek.com 申请 Key 填进来即可；默认自动配置 deepseek-flash。
/// 旧版内置的「小图吧自带模型」/「小米 MiMo」/「OpenCode Zen」在首次加载时清除。
/// </summary>
public static class AiProviderStore
{
    public const string CustomProviderId = "custom";
    public const string DeepSeekProviderId = "deepseek";
    /// <summary>ZXAI：遗留标识——已移除的本地模型预设 id（清理旧配置用）。</summary>
    public const string LegacyLocalProviderId = "local";
    /// <summary>ZXAI：遗留标识——已移除的内置本地模型预设 id（清理旧配置用）。</summary>
    public const string LegacyLocalBuiltinProviderId = "local-builtin";

    /// <summary>DeepSeek 默认模型标识：deepseek-flash（DeepSeek 官端接受的标识，V4.1）。</summary>
    public const string DeepSeekDefaultModel = "deepseek-flash";

    // --- 旧版内置预设（仅用于存量配置清理，不再作为可选项提供）---
    internal const string LegacyMiMoProviderId = "mimo";
    internal const string LegacyOpenCodeZenProviderId = "opencode";

    private sealed class StoreFile
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("selectedProviderId")] public string SelectedProviderId { get; set; } = DeepSeekProviderId;
        [JsonPropertyName("selectedModelId")] public string SelectedModelId { get; set; } = "";
        /// <summary>ZXAI：「只留 DeepSeek」存量清理一次性标记（之后用户手动切换不再被干预）。</summary>
        [JsonPropertyName("zxDeepseekOnlyMigrated")] public bool ZxDeepseekOnlyMigrated { get; set; }
        [JsonPropertyName("providers")] public List<AiProvider> Providers { get; set; } = [];
    }

    private static readonly object _lock = new();
    private static StoreFile? _cache;
    private const string ProtectedKeyPrefix = "dpapi:v1:";
    private static readonly byte[] KeyEntropy = Encoding.UTF8.GetBytes("TubaWinUi3.AiProviderStore.v1");
    // A protected key copied from another Windows account cannot be decrypted here.
    // Keep its original ciphertext until the user enters a replacement key.
    private static readonly Dictionary<string, string> _unreadableKeys = new(StringComparer.Ordinal);

    /// <summary>测试用存储路径覆盖（null = 使用真实数据目录）。</summary>
    internal static string? StoragePathOverride { get; set; }

    /// <summary>测试用旧配置读取器覆盖（默认读 AppSettings）。</summary>
    internal static Func<string, string?> LegacyGet { get; set; } = AppSettings.Get;

    private static string FilePath =>
        StoragePathOverride ?? ConfigManager.GetAiProvidersPath();

    /// <summary>全部提供商（已加载的副本，修改后需调用 <see cref="Save"/>）。</summary>
    public static IReadOnlyList<AiProvider> GetProviders()
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _cache!.Providers;
        }
    }

    public static AiProvider? GetProvider(string id)
        => GetProviders().FirstOrDefault(p => p.Id == id);

    /// <summary>密文来自另一 Windows 账户或已损坏；原密文保留，用户需重新填写此提供商的 Key。</summary>
    public static bool NeedsKeyReentry(string providerId)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _unreadableKeys.ContainsKey(providerId);
        }
    }

    public static string SelectedProviderId
    {
        get
        {
            lock (_lock)
            {
                EnsureLoaded();
                return _cache!.SelectedProviderId;
            }
        }
    }

    /// <summary>当前选中的提供商（不存在时回退 DeepSeek 预设）。</summary>
    public static AiProvider SelectedProvider
    {
        get
        {
            var id = SelectedProviderId;
            return GetProvider(id) ?? GetProvider(DeepSeekProviderId) ?? GetProviders()[0];
        }
    }

    /// <summary>当前选中的模型 Id（不存在于模型列表时回退提供商默认模型）。</summary>
    public static string SelectedModelId
    {
        get
        {
            lock (_lock)
            {
                EnsureLoaded();
                return ResolveSelectedModel(_cache!.SelectedProviderId, _cache.SelectedModelId);
            }
        }
    }

    /// <summary>切换选中的提供商与模型（null 模型 = 使用该提供商默认模型）。</summary>
    public static void SetSelected(string providerId, string? modelId = null)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var provider = GetProvider(providerId);
            if (provider is null) return;
            _cache!.SelectedProviderId = providerId;
            _cache.SelectedModelId = modelId ?? provider.DefaultModel;
            Save();
        }
    }

    /// <summary>One settings selection becomes the global active model and this provider's next-use default.</summary>
    public static void SetGlobalModel(string providerId, string modelId)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var provider = _cache!.Providers.FirstOrDefault(p => p.Id == providerId);
            if (provider is null || !provider.Models.Any(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))) return;
            provider.DefaultModel = modelId;
            _cache.SelectedProviderId = provider.Id;
            _cache.SelectedModelId = modelId;
            Save();
        }
    }

    /// <summary>新建一个空的自定义提供商（地址/模型全部留空，由用户自行填写），并选中它。</summary>
    public static AiProvider AddCustomProvider(string? name = null)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var id = CustomProviderId;
            var n = 1;
            while (_cache!.Providers.Any(p => p.Id == id))
                id = $"{CustomProviderId}-{++n}";

            var provider = new AiProvider
            {
                Id = id,
                Name = name ?? MiscTexts.TSub($"自定义 {n}"),
                BaseUrl = "",
                IsPreset = false,
                EndpointLocked = false,
                DefaultModel = "",
                Models = [],
            };
            _cache.Providers.Add(provider);
            _cache.SelectedProviderId = id;
            _cache.SelectedModelId = "";
            Save();
            return provider;
        }
    }

    /// <summary>恢复提供商的预设默认（模型列表/默认模型/地址），保留 API Key；自定义提供商则完全清空。</summary>
    public static void ResetProviderDefaults(string providerId)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var provider = GetProvider(providerId);
            if (provider is null) return;

            var preset = CreatePreset(providerId);
            if (preset is null)
            {
                // 自定义提供商：完全清空（地址、模型），由用户自行填写
                provider.BaseUrl = "";
                provider.Models = [];
                provider.DefaultModel = "";
            }
            else
            {
                var key = provider.ApiKey;
                provider.Name = preset.Name;
                provider.BaseUrl = preset.BaseUrl;
                provider.EndpointLocked = preset.EndpointLocked;
                provider.KeyHintUrl = preset.KeyHintUrl;
                provider.DefaultModel = preset.DefaultModel;
                provider.Models = preset.Models;
                provider.IsPreset = true;
                provider.ApiKey = key;
            }

            _cache!.SelectedModelId = ResolveSelectedModel(providerId, _cache.SelectedModelId);
            Save();
        }
    }

    /// <summary>解析当前选中提供商的实际请求配置（endpoint/model/key）；可能含空值，由调用方校验，不回退其他服务。</summary>
    public static (string Endpoint, string Model, string ApiKey) GetSelectedConfig()
    {
        var (_, endpoint, model, key) = GetSelectedSnapshot();
        return (endpoint, model, key);
    }

    internal static (string ProviderId, string Endpoint, string Model, string Key) GetSelectedSnapshot()
    {
        lock (_lock)
        {
            EnsureLoaded();
            var id = _cache!.SelectedProviderId;
            var provider = _cache.Providers.FirstOrDefault(p => p.Id == id)
                ?? _cache.Providers.FirstOrDefault(p => p.Id == DeepSeekProviderId) ?? _cache.Providers[0];
            return (provider.Id, provider.BaseUrl?.Trim() ?? "", ResolveSelectedModel(provider.Id, _cache.SelectedModelId),
                provider.ApiKey?.Trim() ?? "");
        }
    }

    /// <summary>
    /// 【R2 2026-09-25】提供商是否已具备可发送配置：服务地址与 API Key 均非空。
    /// 自定义提供商「只填其一」一律按未配置处理——空地址不得把用户 Key 打到默认地址，
    /// 有地址也不得注入内置默认 Key；本应用不真正支持无 Key 端点（不送假凭据，引导填 Key）。
    /// </summary>
    public static bool IsProviderReady(AiProvider provider)
        => !string.IsNullOrWhiteSpace(provider.BaseUrl) && !string.IsNullOrWhiteSpace(provider.ApiKey);

    /// <summary>保存用户主动编辑的 Key，包括清空；明确编辑时不再保留旧的不可解密密文。</summary>
    public static void SetApiKey(string providerId, string? apiKey)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var provider = GetProvider(providerId);
            if (provider is null) return;
            provider.ApiKey = apiKey?.Trim() ?? "";
            _unreadableKeys.Remove(providerId);
            Save();
        }
    }

    /// <summary>保存到磁盘（失败静默，与 AppSettings 一致）。</summary>
    public static void Save()
    {
        lock (_lock)
        {
            if (_cache is null) return;
            string? temporaryPath = null;
            try
            {
                var dir = Path.GetDirectoryName(FilePath)!;
                Directory.CreateDirectory(dir);
                var node = JsonSerializer.SerializeToNode(_cache, JsonOpts)!.AsObject();
                var providers = node["providers"]!.AsArray();
                foreach (var entry in providers)
                {
                    var provider = entry!.AsObject();
                    var id = provider["id"]?.GetValue<string>() ?? "";
                    var key = provider["apiKey"]?.GetValue<string>() ?? "";
                    if (key.Length > 0)
                    {
                        var protectedBytes = ProtectedData.Protect(
                            Encoding.UTF8.GetBytes(key), KeyEntropy, DataProtectionScope.CurrentUser);
                        provider["apiKey"] = ProtectedKeyPrefix + Convert.ToBase64String(protectedBytes);
                        _unreadableKeys.Remove(id);
                    }
                    else if (_unreadableKeys.TryGetValue(id, out var original))
                    {
                        provider["apiKey"] = original;
                    }
                }

                // A same-directory replacement avoids truncating a usable provider file on a failed write.
                temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporaryPath, node.ToJsonString(JsonOpts));
                File.Move(temporaryPath, FilePath, overwrite: true);
                temporaryPath = null;

                // The old flat setting is only cleared after the protected provider file is on disk.
                if (StoragePathOverride is null && _cache.Providers.Any(p => !string.IsNullOrEmpty(p.ApiKey)))
                {
                    AppSettings.Remove("AiApiKey");
                    AppSettings.Flush();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AiProviderStore] Save failed: {ex.GetType().Name}");
            }
            finally
            {
                if (temporaryPath is not null)
                {
                    try { File.Delete(temporaryPath); } catch { }
                }
            }
        }
    }

    /// <summary>清空缓存（测试用 / 数据目录切换后）。</summary>
    public static void InvalidateCache()
    {
        lock (_lock)
        {
            _cache = null;
            _unreadableKeys.Clear();
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static void EnsureLoaded()
    {
        if (_cache is not null) return;

        // 【A13 审计修复】旧版把 ai_providers.json 固定写在 AppData——
        // 目录切换后先做一次性兼容迁移（仅目标缺失时，保留源文件）
        if (StoragePathOverride is null)
            ConfigManager.MigrateLegacyFileIfMissing("ai_providers.json");

        StoreFile? file = null;
        try
        {
            if (File.Exists(FilePath))
                file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(FilePath), JsonOpts);
        }
        catch { }

        if (file is not null && file.Providers.Count > 0)
        {
            _cache = file;
            var needsProtection = false;
            foreach (var provider in file.Providers)
            {
                var storedKey = provider.ApiKey ?? "";
                if (storedKey.StartsWith(ProtectedKeyPrefix, StringComparison.Ordinal))
                {
                    try
                    {
                        var protectedBytes = Convert.FromBase64String(storedKey[ProtectedKeyPrefix.Length..]);
                        provider.ApiKey = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                            protectedBytes, KeyEntropy, DataProtectionScope.CurrentUser));
                    }
                    catch (Exception ex) when (ex is CryptographicException or FormatException)
                    {
                        _unreadableKeys[provider.Id] = storedKey;
                        provider.ApiKey = "";
                    }
                }
                else if (storedKey.Length > 0)
                {
                    needsProtection = true; // Upgrade a legacy plaintext key on first load.
                }
            }
            EnsureDeepSeekOnlyMigration();
            RemoveLegacyLocalProviders();
            if (needsProtection) Save();
            return;
        }

        _cache = new StoreFile();
        foreach (var id in new[] { DeepSeekProviderId })
        {
            if (CreatePreset(id) is { } preset)
                _cache.Providers.Add(preset);
        }

        // 旧版扁平配置迁移 → 恢复为一个自定义提供商（AppSettings 键：AiApiEndpoint / AiModelName / AiApiKey）
        var legacyEndpoint = LegacyGet("AiApiEndpoint")?.Trim() ?? "";
        var legacyModel = LegacyGet("AiModelName")?.Trim() ?? "";
        var legacyKey = LegacyGet("AiApiKey")?.Trim() ?? "";
        if (legacyEndpoint.Length > 0 || legacyModel.Length > 0 || legacyKey.Length > 0)
        {
            var custom = new AiProvider
            {
                Id = CustomProviderId,
                Name = MiscTexts.T("自定义"),
                BaseUrl = legacyEndpoint,
                IsPreset = false,
                EndpointLocked = false,
                DefaultModel = legacyModel,
                Models = [],
            };
            if (legacyKey.Length > 0) custom.ApiKey = legacyKey;
            if (legacyModel.Length > 0) custom.Models.Add(new AiModelOption(legacyModel));
            _cache.Providers.Insert(0, custom);

            _cache.SelectedProviderId = CustomProviderId;
            _cache.SelectedModelId = legacyModel;
        }
        else
        {
            // 全新安装：默认 DeepSeek + deepseek-flash（用户只需填入自己的 API Key）
            _cache.SelectedProviderId = DeepSeekProviderId;
            _cache.SelectedModelId = DeepSeekDefaultModel;
        }

        _cache.ZxDeepseekOnlyMigrated = true;
        Save();
    }

    /// <summary>
    /// ZXAI 存量配置一次性清理：移除旧版内置的「小图吧自带模型」（地址留空的 custom 记录）/
    /// 「小米 MiMo」/「OpenCode Zen」，确保 DeepSeek 预设存在且 deepseek-flash 可用；
    /// 选中的提供商被清掉时自动归位 DeepSeek + deepseek-flash。
    /// </summary>
    private static void EnsureDeepSeekOnlyMigration()
    {
        if (_cache!.ZxDeepseekOnlyMigrated) return;
        _cache.ZxDeepseekOnlyMigrated = true;

        _cache.Providers.RemoveAll(p =>
            p.Id == LegacyMiMoProviderId
            || p.Id == LegacyOpenCodeZenProviderId
            || (p.Id == CustomProviderId
                && string.IsNullOrWhiteSpace(p.BaseUrl)
                && (p.Name.Contains("自带") || string.IsNullOrWhiteSpace(p.ApiKey))));

        var deepseek = _cache.Providers.FirstOrDefault(p => p.Id == DeepSeekProviderId);
        if (deepseek is null)
        {
            deepseek = CreatePreset(DeepSeekProviderId)!;
            _cache.Providers.Insert(0, deepseek);
        }
        if (!deepseek.Models.Any(m => m.Id.Equals(DeepSeekDefaultModel, StringComparison.OrdinalIgnoreCase)))
            deepseek.Models.Insert(0, new AiModelOption(DeepSeekDefaultModel, "DeepSeek Flash"));
        if (string.IsNullOrWhiteSpace(deepseek.DefaultModel))
            deepseek.DefaultModel = DeepSeekDefaultModel;

        var selected = _cache.Providers.FirstOrDefault(p => p.Id == _cache.SelectedProviderId);
        if (selected is null)
        {
            _cache.SelectedProviderId = DeepSeekProviderId;
            _cache.SelectedModelId = DeepSeekDefaultModel;
        }
        else if (selected.Id == DeepSeekProviderId
            && !selected.Models.Any(m => m.Id.Equals(_cache.SelectedModelId, StringComparison.OrdinalIgnoreCase)))
        {
            _cache.SelectedModelId = DeepSeekDefaultModel;
        }

        Save();
    }

    private static AiProvider? CreatePreset(string id)
    {
        switch (id)
        {
            case DeepSeekProviderId:
                return new AiProvider
                {
                    Id = id,
                    Name = "DeepSeek",
                    BaseUrl = "https://api.deepseek.com",
                    IsPreset = true,
                    EndpointLocked = true,
                    KeyHintUrl = "https://platform.deepseek.com/api_keys",
                    DefaultModel = DeepSeekDefaultModel,
                    Models =
                    [
                        new AiModelOption(DeepSeekDefaultModel, "DeepSeek Flash"),
                        new AiModelOption("deepseek-v4-pro", "DeepSeek V4 Pro"),
                    ],
                };
            default:
                return null;
        }
    }

    /// <summary>ZXAI：把运行时发现的模型（如本地 LM Studio /v1/models 列表）登记进提供商，
    /// 使动态选择的模型可被 ResolveSelectedModel 持久化（幂等）。</summary>
    public static void RegisterModel(string providerId, string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return;
        lock (_lock)
        {
            EnsureLoaded();
            var provider = GetProvider(providerId);
            if (provider is null) return;
            if (provider.Models.Any(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))) return;
            provider.Models.Add(new AiModelOption(modelId));
            Save();
        }
    }

    /// <summary>ZXAI：清理已移除的本地模型预设（local / local-builtin），选中项归位 DeepSeek。</summary>
    private static void RemoveLegacyLocalProviders()
    {
        var removed = _cache!.Providers.RemoveAll(p =>
            p.Id == LegacyLocalProviderId || p.Id == LegacyLocalBuiltinProviderId);
        if (removed == 0) return;
        if (_cache.SelectedProviderId == LegacyLocalProviderId ||
            _cache.SelectedProviderId == LegacyLocalBuiltinProviderId)
        {
            _cache.SelectedProviderId = DeepSeekProviderId;
            _cache.SelectedModelId = DeepSeekDefaultModel;
        }
        Save();
    }

    private static string ResolveSelectedModel(string providerId, string? selectedModelId)
    {
        var provider = GetProvider(providerId) ?? GetProvider(DeepSeekProviderId);
        if (provider is null) return "";

        if (!string.IsNullOrWhiteSpace(selectedModelId) &&
            provider.Models.Any(m => m.Id.Equals(selectedModelId, StringComparison.OrdinalIgnoreCase)))
        {
            return selectedModelId;
        }

        if (!string.IsNullOrWhiteSpace(provider.DefaultModel) &&
            provider.Models.Any(m => m.Id.Equals(provider.DefaultModel, StringComparison.OrdinalIgnoreCase)))
        {
            return provider.DefaultModel;
        }

        return provider.Models.FirstOrDefault()?.Id ?? "";
    }
}
