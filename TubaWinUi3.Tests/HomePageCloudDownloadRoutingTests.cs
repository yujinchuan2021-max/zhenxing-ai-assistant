using TubaWinUi3.Models;
using TubaWinUi3.Pages;
using TubaWinUi3.Services.CloudTools;

namespace TubaWinUi3.Tests;

public sealed class HomePageCloudDownloadRoutingTests
{
    private static readonly string ToolsRoot = Path.Combine(Path.GetTempPath(), "routing-bundled", "Tools");
    private static readonly string WritableRoot = Path.Combine(Path.GetTempPath(), "routing-writable", "Tools");
    private static readonly CloudToolDefinition CpuZ = new()
    {
        Id = "tool-cpu-z", Name = "CPU-Z", Category = "处理器工具", LegacyPath = "处理器工具/CPUZ",
    };

    [Fact]
    public void ExplicitCloudIdentitySurvivesRenamedCards()
    {
        var card = Card(Path.Combine(ToolsRoot, "unrelated", "app.exe"), cloudId: CpuZ.Id, name: "用户改过的名字");
        Assert.Same(CpuZ, Resolve(card, [CpuZ]));
    }

    [Theory]
    [InlineData("unknown-id")]
    [InlineData("TOOL-CPU-Z")]
    [InlineData("tool-cpu-z ")]
    public void UnknownExplicitIdentityDoesNotFallBackToLegacyPath(string id)
    {
        Assert.Null(Resolve(Card(Path.Combine(ToolsRoot, "处理器工具", "CPUZ", "app.exe"), cloudId: id), [CpuZ]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CuratedCardUsesExactLegacyDirectoryInEitherOwnedRoot(bool writable)
    {
        var root = writable ? WritableRoot : ToolsRoot;
        var card = Card(Path.Combine(root, "处理器工具", "cpuz", "nested", "app.exe"), name: "另一种显示名");
        Assert.Same(CpuZ, Resolve(card, [CpuZ]));
    }

    [Theory]
    [InlineData("CPUZ-extra")]
    [InlineData("CPUZ Backup")]
    [InlineData("NotCPUZ")]
    public void SimilarFolderNameDoesNotAcquireAnotherProductsIdentity(string folder)
    {
        Assert.Null(Resolve(Card(Path.Combine(ToolsRoot, "处理器工具", folder, "app.exe")), [CpuZ]));
    }

    [Fact]
    public void CuratedNameOrDownloadUrlDoesNotIdentifyAProduct()
    {
        var card = Card(Path.Combine(ToolsRoot, "其他工具", "user-choice", "app.exe"), name: "CPU-Z");
        Assert.Null(Resolve(card, [CpuZ]));
    }

    [Fact]
    public void CustomCardsAreNotAbsorbedByTheCloudCatalogue()
    {
        Assert.Null(Resolve(Card(Path.Combine(ToolsRoot, "处理器工具", "CPUZ", "app.exe"), curated: false), [CpuZ]));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void BuiltinAndCommunityIdentitiesAreHandledByTheirOwnEntrypoints(bool builtin, bool community)
    {
        var card = Card(Path.Combine(ToolsRoot, "处理器工具", "CPUZ", "app.exe"), builtin: builtin, community: community);
        Assert.Null(Resolve(card, [CpuZ]));
    }

    [Fact]
    public void PathsOutsideOwnedRootsDoNotMatch()
    {
        var file = Path.Combine(Path.GetTempPath(), "another-app", "Tools", "处理器工具", "CPUZ", "app.exe");
        Assert.Null(Resolve(Card(file), [CpuZ]));
        Assert.Null(Resolve(Card(Path.Combine(ToolsRoot + "-backup", "处理器工具", "CPUZ", "app.exe")), [CpuZ]));
        Assert.Null(Resolve(Card("处理器工具/CPUZ/app.exe"), [CpuZ]));
    }

    [Fact]
    public void DuplicateCatalogueAliasesDoNotPickAnArbitraryProduct()
    {
        var card = Card(Path.Combine(ToolsRoot, "处理器工具", "CPUZ", "app.exe"));
        Assert.Null(Resolve(card, [CpuZ, CpuZ with { Id = "different-product" }]));
        Assert.Null(Resolve(Card(card.Path, cloudId: CpuZ.Id), [CpuZ, CpuZ]));
    }

    [Fact]
    public void InvalidOrEmptyLegacyPathsCannotMatch()
    {
        var card = Card(Path.Combine(ToolsRoot, "处理器工具", "CPUZ", "app.exe"));
        Assert.Null(Resolve(card, [CpuZ with { LegacyPath = "" }]));
        Assert.Null(Resolve(card, [CpuZ with { LegacyPath = "../Tools/处理器工具/CPUZ" }]));
        Assert.Null(Resolve(card, [CpuZ with { LegacyPath = card.Path }]));
    }

    [Fact]
    public void OwnedUnsupportedArchitectureStillRoutesToOwnedAcquisition()
    {
        // No package is preferable to falling back to an unrelated gh:/gc: link.
        Assert.Empty(CpuZ.Packages);
        Assert.Same(CpuZ, Resolve(Card(Path.Combine(ToolsRoot, "处理器工具", "CPUZ", "app.exe")), [CpuZ]));
    }

    private static CloudToolDefinition? Resolve(ToolItem card, IReadOnlyList<CloudToolDefinition> catalog)
        => HomePage.ResolveOwnedCloudDownloadTool(card, catalog, ToolsRoot, WritableRoot);

    private static ToolItem Card(string path, string? cloudId = null, string name = "CPU-Z", bool curated = true,
        bool builtin = false, bool community = false) => new()
    {
        Name = name, Category = "处理器工具", Path = path, RelativePath = "unused", Extension = "待下载",
        DownloadUrl = "gc:Tools/处理器工具/CPUZ", CloudToolId = cloudId,
        IsCatalogCurated = curated, IsBuiltinLink = builtin, IsCommunity = community,
    };
}
