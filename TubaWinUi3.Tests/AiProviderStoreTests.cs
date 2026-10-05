using TubaWinUi3.Services;
using TubaWinUi3.Services.Ai;
using System.Text.Json;

namespace TubaWinUi3.Tests;

/// <summary>
/// AI 提供商存储（AiProviderStore / AiService 配置解析）单元测试。
/// 使用临时文件 + 静态路径覆盖，不触碰真实用户数据。
/// </summary>
public class AiProviderStoreTests : IDisposable
{
    private readonly string _path;

    public AiProviderStoreTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TubaAiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "ai_providers.json");
        ResetStore(_path, legacy: null);
    }

    public void Dispose()
    {
        ResetStore(null, legacy: null);
        try
        {
            var dir = Path.GetDirectoryName(_path)!;
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { }
    }

    private static void ResetStore(string? path, Dictionary<string, string>? legacy)
    {
        AiProviderStore.InvalidateCache();
        AiProviderStore.StoragePathOverride = path;
        AiProviderStore.LegacyGet = legacy is null
            ? static _ => null
            : key => legacy.TryGetValue(key, out var v) ? v : null;
    }

    private static void AssertProvider(AiProvider p, string id, string name, string endpoint, bool locked)
    {
        Assert.Equal(id, p.Id);
        Assert.Equal(name, p.Name);
        Assert.Equal(endpoint, p.BaseUrl);
        Assert.Equal(locked, p.EndpointLocked);
        Assert.True(p.IsPreset);
    }

    [Fact]
    public void Defaults_ContainOnlyDeepSeekPreset()
    {
        var providers = AiProviderStore.GetProviders();

        Assert.Single(providers);
        var deepseek = providers[0];
        AssertProvider(deepseek, "deepseek", "DeepSeek", "https://api.deepseek.com", locked: true);
        Assert.Equal("deepseek-flash", deepseek.DefaultModel);
        Assert.Contains(deepseek.Models, m => m.Id == "deepseek-flash");
        Assert.Contains(deepseek.Models, m => m.Id == "deepseek-v4-pro");
        Assert.Equal("https://platform.deepseek.com/api_keys", deepseek.KeyHintUrl);
    }

    [Fact]
    public void Defaults_Unconfigured_SelectsDeepSeekFlash()
    {
        // 全新安装：默认选中 DeepSeek + deepseek-flash（用户只需填自己的 Key）
        Assert.Equal(AiProviderStore.DeepSeekProviderId, AiProviderStore.SelectedProviderId);
        Assert.Equal("deepseek-flash", AiProviderStore.SelectedModelId);
    }

    [Fact]
    public void SaveLoad_RoundTrip()
    {
        var custom = AiProviderStore.AddCustomProvider();
        custom.BaseUrl = "https://my-gateway.example/v1";
        custom.AddModel("my-model-1");
        custom.AddModel("my-model-2");
        custom.DefaultModel = "my-model-2";
        AiProviderStore.Save();

        // 重新加载（清缓存）后内容一致
        ResetStore(_path, legacy: null);
        var reloaded = AiProviderStore.GetProvider(custom.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("https://my-gateway.example/v1", reloaded!.BaseUrl);
        Assert.Equal("my-model-2", reloaded.DefaultModel);
        Assert.Contains(reloaded.Models, m => m.Id == "my-model-1");
        Assert.Contains(reloaded.Models, m => m.Id == "my-model-2");
    }

    [Fact]
    public void Save_KeyIsProtectedForCurrentWindowsUser_AndRoundTrips()
    {
        var deepseek = AiProviderStore.GetProvider("deepseek")!;
        deepseek.ApiKey = "sk-local-test-secret";
        AiProviderStore.Save();

        var stored = File.ReadAllText(_path);
        Assert.DoesNotContain("sk-local-test-secret", stored);
        using (var document = JsonDocument.Parse(stored))
        {
            var key = document.RootElement.GetProperty("providers")[0].GetProperty("apiKey").GetString();
            Assert.StartsWith("dpapi:v1:", key);
        }

        ResetStore(_path, legacy: null);
        Assert.Equal("sk-local-test-secret", AiProviderStore.GetProvider("deepseek")!.ApiKey);
    }

    [Fact]
    public void Load_LegacyPlaintextKey_IsProtectedOnFirstRead()
    {
        File.WriteAllText(_path, """
            {"version":1,"selectedProviderId":"deepseek","selectedModelId":"deepseek-flash","zxDeepseekOnlyMigrated":true,
             "providers":[{"id":"deepseek","name":"DeepSeek","baseUrl":"https://api.deepseek.com",
             "apiKey":"sk-legacy-test-secret","isPreset":true,"endpointLocked":true,
             "defaultModel":"deepseek-flash","models":[{"id":"deepseek-flash"}]}]}
            """);
        ResetStore(_path, legacy: null);

        Assert.Equal("sk-legacy-test-secret", AiProviderStore.GetProvider("deepseek")!.ApiKey);
        var stored = File.ReadAllText(_path);
        Assert.DoesNotContain("sk-legacy-test-secret", stored);
        Assert.Contains("dpapi:v1:", stored);
    }

    [Fact]
    public void SetApiKey_UserClearsKey_RemainsEmptyAfterReload()
    {
        AiProviderStore.SetApiKey("deepseek", "sk-disposable-test-key");
        AiProviderStore.SetApiKey("deepseek", "");
        ResetStore(_path, legacy: null);

        Assert.Empty(AiProviderStore.GetProvider("deepseek")!.ApiKey);
        Assert.False(AiProviderStore.IsProviderReady(AiProviderStore.SelectedProvider));
        using var document = JsonDocument.Parse(File.ReadAllText(_path));
        Assert.Equal("", document.RootElement.GetProperty("providers")[0].GetProperty("apiKey").GetString());
    }

    [Fact]
    public void SetApiKey_ExplicitClear_DoesNotRestoreUnreadableOldCiphertext()
    {
        File.WriteAllText(_path, """
            {"version":1,"selectedProviderId":"deepseek","selectedModelId":"deepseek-flash","zxDeepseekOnlyMigrated":true,
             "providers":[{"id":"deepseek","name":"DeepSeek","baseUrl":"https://api.deepseek.com",
             "apiKey":"dpapi:v1:AAECAwQ=","isPreset":true,"endpointLocked":true,
             "defaultModel":"deepseek-flash","models":[{"id":"deepseek-flash"}]}]}
            """);
        ResetStore(_path, legacy: null);
        Assert.True(AiProviderStore.NeedsKeyReentry("deepseek"));

        AiProviderStore.SetApiKey("deepseek", "");
        Assert.False(AiProviderStore.NeedsKeyReentry("deepseek"));
        ResetStore(_path, legacy: null);
        Assert.Empty(AiProviderStore.GetProvider("deepseek")!.ApiKey);
        Assert.DoesNotContain("dpapi:v1:AAECAwQ=", File.ReadAllText(_path));
    }

    [Fact]
    public void CaptureConnectionTestConfig_UsesSelectedModelInsteadOfProviderDefault()
    {
        var provider = AiProviderStore.GetProvider("deepseek")!;
        AiProviderStore.SetApiKey(provider.Id, "sk-connection-test-key");
        provider.DefaultModel = "deepseek-flash";
        AiProviderStore.SetSelected(provider.Id, "deepseek-v4-pro");

        var captured = AgentEngine.CaptureConnectionTestConfig();

        Assert.Equal("deepseek-v4-pro", captured.Model);
        Assert.Equal("https://api.deepseek.com", captured.Endpoint);
        Assert.Equal("sk-connection-test-key", captured.Key);
        Assert.Equal(AgentEngine.CurrentLaunchFingerprint(), captured.Fingerprint);
        Assert.NotEqual(
            TubaWinUi3.Services.Ai.Dsh.DshLaunchConfig.ComputeFingerprint(provider.Id, provider.DefaultModel, captured.Endpoint, captured.Key),
            captured.Fingerprint);
    }

    [Fact]
    public void UnreadableProtectedKey_IsRetainedUntilUserReplacesIt()
    {
        const string unreadable = "dpapi:v1:AAECAwQ=";
        File.WriteAllText(_path, """
            {"version":1,"selectedProviderId":"deepseek","selectedModelId":"deepseek-flash","zxDeepseekOnlyMigrated":true,
             "providers":[{"id":"deepseek","name":"DeepSeek","baseUrl":"https://api.deepseek.com",
             "apiKey":"dpapi:v1:AAECAwQ=","isPreset":true,"endpointLocked":true,
             "defaultModel":"deepseek-flash","models":[{"id":"deepseek-flash"}]}]}
            """);
        ResetStore(_path, legacy: null);

        var provider = AiProviderStore.GetProvider("deepseek")!;
        Assert.Empty(provider.ApiKey);
        Assert.True(AiProviderStore.NeedsKeyReentry("deepseek"));
        provider.AddModel("another-model");
        AiProviderStore.Save();
        Assert.Contains(unreadable, File.ReadAllText(_path));

        provider.ApiKey = "sk-replacement-test-secret";
        AiProviderStore.Save();
        var stored = File.ReadAllText(_path);
        Assert.DoesNotContain(unreadable, stored);
        Assert.DoesNotContain("sk-replacement-test-secret", stored);
        Assert.False(AiProviderStore.NeedsKeyReentry("deepseek"));
        ResetStore(_path, legacy: null);
        Assert.Equal("sk-replacement-test-secret", AiProviderStore.GetProvider("deepseek")!.ApiKey);
    }

    [Fact]
    public void SetSelected_PersistsAndResolvesDefault()
    {
        AiProviderStore.SetSelected("deepseek");
        Assert.Equal("deepseek", AiProviderStore.SelectedProviderId);
        Assert.Equal("deepseek-flash", AiProviderStore.SelectedModelId);

        AiProviderStore.SetSelected("deepseek", "deepseek-v4-pro");
        Assert.Equal("deepseek-v4-pro", AiProviderStore.SelectedModelId);

        // 重新加载后选中状态仍保留
        ResetStore(_path, legacy: null);
        Assert.Equal("deepseek", AiProviderStore.SelectedProviderId);
        Assert.Equal("deepseek-v4-pro", AiProviderStore.SelectedModelId);
    }

    [Fact]
    public void LegacyMigration_SeedsCustomProvider()
    {
        var legacy = new Dictionary<string, string>
        {
            ["AiApiEndpoint"] = "https://old.example/v1",
            ["AiModelName"] = "old-model",
            ["AiApiKey"] = "sk-old-key",
        };
        ResetStore(_path, legacy);

        var custom = AiProviderStore.GetProvider("custom")!;
        Assert.Equal("https://old.example/v1", custom.BaseUrl);
        Assert.Equal("sk-old-key", custom.ApiKey);
        Assert.Equal("old-model", custom.DefaultModel);
        Assert.Contains(custom.Models, m => m.Id == "old-model");
        Assert.Equal("old-model", AiProviderStore.SelectedModelId);
    }

    [Fact]
    public void LegacyFile_OldPresets_CleanedToDeepSeekOnly()
    {
        // 旧版文件（自带模型 + DeepSeek + MiMo + Zen，选中 Zen 默认免费模型）：
        // 载入后只剩 DeepSeek；DeepSeek 的 Key 保留、deepseek-flash 补进模型列表；
        // 选中项失效自动归位 deepseek + deepseek-flash。
        File.WriteAllText(_path, """
            {"version":1,"selectedProviderId":"opencode","selectedModelId":"deepseek-v4-flash-free","providers":[
              {"id":"custom","name":"小图吧自带模型","baseUrl":"","apiKey":"","isPreset":true,"endpointLocked":false,"defaultModel":"auto","models":[{"id":"auto","label":"自动"}]},
              {"id":"deepseek","name":"DeepSeek","baseUrl":"https://api.deepseek.com","apiKey":"sk-keep","isPreset":true,"endpointLocked":true,"defaultModel":"deepseek-v4-flash","models":[{"id":"deepseek-v4-flash"}]},
              {"id":"mimo","name":"小米 MiMo","baseUrl":"https://api.xiaomimimo.com/v1","apiKey":"","isPreset":true,"endpointLocked":true,"defaultModel":"mimo-v2.5-pro","models":[{"id":"mimo-v2.5-pro"}]},
              {"id":"opencode","name":"OpenCode Zen","baseUrl":"https://opencode.ai/zen/v1","apiKey":"","isPreset":true,"endpointLocked":true,"defaultModel":"deepseek-v4-flash-free","models":[{"id":"deepseek-v4-flash-free"}]}
            ]}
            """);
        ResetStore(_path, legacy: null);

        var providers = AiProviderStore.GetProviders();
        // ZXAI：清退后 = 仅 DeepSeek 预设；旧内置（mimo/opencode/本地模型）全移除
        Assert.Single(providers);
        var deepseek = providers.First(p => p.Id == "deepseek");
        Assert.Equal("sk-keep", deepseek.ApiKey);
        Assert.Contains(deepseek.Models, m => m.Id == "deepseek-flash");
        Assert.DoesNotContain(providers, p => p.Id is "local" or "local-builtin");
        Assert.DoesNotContain(providers, p => p.Id is "mimo" or "opencode");
        Assert.Equal("deepseek", AiProviderStore.SelectedProviderId);
        Assert.Equal("deepseek-flash", AiProviderStore.SelectedModelId);
    }

    [Fact]
    public void LegacyFile_UserCustomEndpoint_Survives()
    {
        // 用户自建的自定义端点（有地址）不属于内置清理范围；选中者不受影响；
        // 缺失的 DeepSeek 预设会被补回。
        File.WriteAllText(_path, """
            {"version":1,"selectedProviderId":"custom-2","selectedModelId":"my-model","providers":[
              {"id":"custom","name":"小图吧自带模型","baseUrl":"","apiKey":"","isPreset":true,"endpointLocked":false,"defaultModel":"auto","models":[{"id":"auto","label":"自动"}]},
              {"id":"custom-2","name":"自定义 2","baseUrl":"https://my.example/v1","apiKey":"sk-mine","isPreset":false,"endpointLocked":false,"defaultModel":"my-model","models":[{"id":"my-model"}]},
              {"id":"mimo","name":"小米 MiMo","baseUrl":"https://api.xiaomimimo.com/v1","apiKey":"","isPreset":true,"endpointLocked":true,"defaultModel":"mimo-v2.5-pro","models":[{"id":"mimo-v2.5-pro"}]}
            ]}
            """);
        ResetStore(_path, legacy: null);

        Assert.Equal("custom-2", AiProviderStore.SelectedProviderId);
        Assert.NotNull(AiProviderStore.GetProvider("custom-2"));
        Assert.Null(AiProviderStore.GetProvider("mimo"));
        Assert.Null(AiProviderStore.GetProvider("custom"));
        Assert.NotNull(AiProviderStore.GetProvider("deepseek"));
    }

    [Fact]
    public void Migration_OneTime_CustomChoiceSticks()
    {
        // 清理只执行一次：之后用户新建的自定义提供商被选中，重启后不再被干预
        var custom = AiProviderStore.AddCustomProvider();
        Assert.Equal(custom.Id, AiProviderStore.SelectedProviderId);

        ResetStore(_path, legacy: null); // 模拟重启

        Assert.Equal(custom.Id, AiProviderStore.SelectedProviderId);
    }

    [Fact]
    public void GetConfig_ResolvesSelectedProvider()
    {
        var deepseek = AiProviderStore.GetProvider("deepseek")!;
        deepseek.ApiKey = "sk-deepseek-test";
        AiProviderStore.Save();
        AiProviderStore.SetSelected("deepseek", "deepseek-flash");

        var (endpoint, model, apiKey) = AiService.GetConfig();
        Assert.Equal("https://api.deepseek.com", endpoint);
        Assert.Equal("deepseek-flash", model);
        Assert.Equal("sk-deepseek-test", apiKey);
    }

    [Fact]
    public void GetConfig_CustomBlank_DoesNotFallBackToBuiltinDefaults()
    {
        // 【R3 核心】空白自定义提供商 = 未配置：请求配置绝不注入旧默认端点/内置 Key/默认模型
        AiProviderStore.AddCustomProvider();
        var (endpoint, model, apiKey) = AiService.GetConfig();
        Assert.Equal("", endpoint);
        Assert.Equal("", apiKey);
        Assert.Equal("", model);
    }

    [Fact]
    public void AddCustomProvider_CreatesUniqueIdAndSelects()
    {
        var first = AiProviderStore.AddCustomProvider();
        Assert.Equal("custom", first.Id);
        Assert.Equal(first.Id, AiProviderStore.SelectedProviderId);
        // 新自定义提供商完全留空（地址/模型/默认模型均空）
        Assert.Equal("", first.BaseUrl);
        Assert.Empty(first.Models);
        Assert.Equal("", first.DefaultModel);

        var second = AiProviderStore.AddCustomProvider();
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(second.Id, AiProviderStore.SelectedProviderId);
        Assert.Equal(3, AiProviderStore.GetProviders().Count); // deepseek + 2 个自定义
    }

    [Fact]
    public void ResetProviderDefaults_RestoresPresetAndKeepsKey()
    {
        var deepseek = AiProviderStore.GetProvider("deepseek")!;
        deepseek.ApiKey = "sk-keep-me";
        deepseek.AddModel("user-added-model");
        deepseek.DefaultModel = "user-added-model";
        AiProviderStore.Save();

        AiProviderStore.ResetProviderDefaults("deepseek");

        deepseek = AiProviderStore.GetProvider("deepseek")!;
        Assert.Equal("sk-keep-me", deepseek.ApiKey);
        Assert.Equal("deepseek-flash", deepseek.DefaultModel);
        Assert.DoesNotContain(deepseek.Models, m => m.Id == "user-added-model");
        Assert.Equal(2, deepseek.Models.Count);
    }

    [Fact]
    public void SelectedModel_FallsBackToProviderDefault()
    {
        AiProviderStore.SetSelected("deepseek", "not-exist-model");
        Assert.Equal("deepseek-flash", AiProviderStore.SelectedModelId);
    }

    // ---------- 接入引导 R2（2026-09-25）：配置完整性判定 / GetConfig 不混用 ----------

    [Fact]
    public void GlobalModelIsSharedByChatAgentNewsAndPersistedDefault()
    {
        AiProviderStore.SetGlobalModel("deepseek", "deepseek-v4-pro");
        Assert.Equal("deepseek-v4-pro", AiProviderStore.SelectedModelId);
        Assert.Equal("deepseek-v4-pro", AiProviderStore.SelectedProvider.DefaultModel);
        Assert.Equal("deepseek-v4-pro", AiService.GetConfig().Model);
        Assert.Equal("deepseek-v4-pro", TubaWinUi3.Services.Ai.AgentEngine.ReadSelectedCredentials().Model);
        AiProviderStore.InvalidateCache();
        Assert.Equal("deepseek-v4-pro", AiProviderStore.SelectedModelId);
        AiProviderStore.SetSelected("deepseek");
        Assert.Equal("deepseek-v4-pro", AiProviderStore.SelectedModelId);
        Assert.Null(TubaWinUi3.Services.AiNews.AiNewsEnricher.CaptureSelectedModel()); // no synthetic Key configured
    }

    [Fact]
    public void CapturedGlobalSnapshotDoesNotMixLaterProviderCredentials()
    {
        var a = AiProviderStore.AddCustomProvider("A"); a.BaseUrl = "https://a.example/v1"; a.ApiKey = "fake-a"; a.AddModel("a-model");
        AiProviderStore.SetGlobalModel(a.Id, "a-model");
        var captured = AiProviderStore.GetSelectedSnapshot();
        var b = AiProviderStore.AddCustomProvider("B"); b.BaseUrl = "https://b.example/v1"; b.ApiKey = "fake-b"; b.AddModel("b-model");
        AiProviderStore.SetGlobalModel(b.Id, "b-model");
        Assert.Equal((a.Id, "https://a.example/v1", "a-model", "fake-a"), captured);
        Assert.Equal((b.Id, "https://b.example/v1", "b-model", "fake-b"), TubaWinUi3.Services.Ai.AgentEngine.ReadSelectedCredentials());
        Assert.NotNull(TubaWinUi3.Services.AiNews.AiNewsEnricher.CaptureSelectedModel()); // captures only; never creates a client
    }

    [Fact]
    public void ConnectionRequestUsesCapturedModelEvenWhenGlobalChoiceChanges()
    {
        AiProviderStore.SetGlobalModel("deepseek", "deepseek-v4-pro");
        var snapshot = AiProviderStore.GetSelectedSnapshot();
        AiProviderStore.SetGlobalModel("deepseek", "deepseek-flash");
        var body = AiService.BuildRequestBody([AiChatMessage.User("synthetic")], 0, false, 10, null, snapshot.Model);
        Assert.Equal("deepseek-v4-pro", body["model"]);
    }

    [Theory]
    [InlineData("AiConfiguration", true)]
    [InlineData("AiApiEndpoint", true)]
    [InlineData("AiAgentEngine", true)]
    [InlineData("AiModelName", true)]
    [InlineData("InterfaceFont", false)]
    [InlineData(null, false)]
    public void AiEntryUsesOneUnifiedViewOtherSettingsRemainComplete(string? key, bool unified)
        => Assert.Equal(unified, TubaWinUi3.Pages.SettingsPage.IsAiConfigurationKey(key));

    [Fact]
    public void IsProviderReady_RequiresBothEndpointAndKey()
    {
        var custom = AiProviderStore.AddCustomProvider();
        custom.BaseUrl = "https://intra.example/v1";
        custom.ApiKey = "sk-user-test-api";
        Assert.True(AiProviderStore.IsProviderReady(custom));

        // 只填其一：一律未配置（空地址不得把 Key 打到默认地址；有地址不得注入内置默认 Key）
        custom.BaseUrl = "";
        Assert.False(AiProviderStore.IsProviderReady(custom));
        custom.BaseUrl = "https://intra.example/v1";
        custom.ApiKey = "";
        Assert.False(AiProviderStore.IsProviderReady(custom));

        // 预设（DeepSeek）：无 Key 也算未配置
        var deepseek = AiProviderStore.GetProvider("deepseek")!;
        Assert.False(AiProviderStore.IsProviderReady(deepseek));
        deepseek.ApiKey = "sk-user-test-api";
        Assert.True(AiProviderStore.IsProviderReady(deepseek));
    }

    [Fact]
    public void GetConfig_CustomKeyWithoutEndpoint_NeverTargetsDefaultEndpoint()
    {
        // 【R2 核心】地址为空 + 已填用户 Key（假配置）：绝不回退默认地址——用户 Key 不得被发到旧内置地址
        var custom = AiProviderStore.AddCustomProvider();
        custom.ApiKey = "sk-user-test-api";
        var (endpoint, _, apiKey) = AiService.GetConfig();

        Assert.Equal("", endpoint);
        Assert.Equal("sk-user-test-api", apiKey);
    }

    [Fact]
    public void GetConfig_CustomEndpointWithoutKey_NeverInjectsDefaultKey()
    {
        // 【R2 核心】有地址 + 无 Key：不得注入内置默认 Key 充当假凭据
        var custom = AiProviderStore.AddCustomProvider();
        custom.BaseUrl = "https://intra.example/v1";
        var (endpoint, _, apiKey) = AiService.GetConfig();

        Assert.Equal("https://intra.example/v1", endpoint);
        Assert.Equal("", apiKey);
    }
}
