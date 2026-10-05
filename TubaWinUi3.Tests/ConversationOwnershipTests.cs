using TubaWinUi3.Services.Ai;

namespace TubaWinUi3.Tests;

/// <summary>
/// 【主审修复·2026-09-22】会话写入归属登记（ConversationOwnershipRegistry）测试。
///
/// 针对主审 14:59 复现的缺陷：主窗口与独立卡片窗口各载入同一会话 ID 后互相覆盖。
/// 规则：同一「数据根 + 会话 ID」同一时刻只允许一个所有者（token）；
/// 释放仅持有者有效且幂等；不同数据根互不冲突。
/// </summary>
public class ConversationOwnershipTests
{
    private static string NewRoot() => "ZTEST-" + Guid.NewGuid().ToString("N");
    private static string NewConv() => "conv-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void Acquire_FirstToken_Succeeds_SecondTokenBlocked()
    {
        var root = NewRoot(); var id = NewConv();
        var a = Guid.NewGuid(); var b = Guid.NewGuid();

        Assert.True(ConversationOwnershipRegistry.TryAcquire(root, id, a));
        Assert.True(ConversationOwnershipRegistry.IsOwnedBy(root, id, a));
        // 第二个窗口尝试同一会话 → 拒绝
        Assert.False(ConversationOwnershipRegistry.TryAcquire(root, id, b));
        Assert.True(ConversationOwnershipRegistry.IsHeldByOther(root, id, b));
        // 持有者自己不算"被他人持有"
        Assert.False(ConversationOwnershipRegistry.IsHeldByOther(root, id, a));
    }

    [Fact]
    public void Acquire_SameToken_IsReentrant()
    {
        var root = NewRoot(); var id = NewConv(); var a = Guid.NewGuid();
        Assert.True(ConversationOwnershipRegistry.TryAcquire(root, id, a));
        Assert.True(ConversationOwnershipRegistry.TryAcquire(root, id, a));   // 重入（同页面重复取得）
        Assert.True(ConversationOwnershipRegistry.IsOwnedBy(root, id, a));
    }

    [Fact]
    public void Release_WrongToken_HasNoEffect()
    {
        var root = NewRoot(); var id = NewConv();
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        ConversationOwnershipRegistry.TryAcquire(root, id, a);

        ConversationOwnershipRegistry.Release(root, id, b);   // 非持有者释放：无效

        Assert.True(ConversationOwnershipRegistry.IsOwnedBy(root, id, a));       // 仍归 a
        Assert.False(ConversationOwnershipRegistry.TryAcquire(root, id, b));     // b 仍拿不到
    }

    [Fact]
    public void Release_ByOwner_Frees_And_IsIdempotent()
    {
        var root = NewRoot(); var id = NewConv();
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        ConversationOwnershipRegistry.TryAcquire(root, id, a);

        ConversationOwnershipRegistry.Release(root, id, a);
        ConversationOwnershipRegistry.Release(root, id, a);   // 重复释放：幂等（不抛）

        Assert.False(ConversationOwnershipRegistry.IsOwnedBy(root, id, a));
        Assert.True(ConversationOwnershipRegistry.TryAcquire(root, id, b));      // 释放后他人可取得
    }

    [Fact]
    public void DifferentDataRoots_DoNotConflict()
    {
        var id = NewConv();
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        // 同一会话 ID 出现在两个不同数据根（ZXAI_DATA_ROOT 隔离）→ 互不影响
        Assert.True(ConversationOwnershipRegistry.TryAcquire(NewRoot(), id, a));
        Assert.True(ConversationOwnershipRegistry.TryAcquire(NewRoot(), id, b));
    }

    [Fact]
    public void EmptyConversationId_IsNoOp()
    {
        var root = NewRoot(); var a = Guid.NewGuid();
        Assert.True(ConversationOwnershipRegistry.TryAcquire(root, "", a));                       // 无名会话无冲突
        Assert.False(ConversationOwnershipRegistry.IsOwnedBy(root, "", a));
        Assert.False(ConversationOwnershipRegistry.IsHeldByOther(root, "", Guid.NewGuid()));
        ConversationOwnershipRegistry.Release(root, "", a);                                       // 不抛
    }

    [Fact]
    public void MainScenario_OldCopyClose_CannotOverwrite_And_ReopenRejected()
    {
        // 复现主审序列的归属语义（单测级）：
        // 1) 主窗口占有历史会话 X；
        // 2) 独立窗口（默认新会话，未占 X）若手动选择 X → 被占用检查阻止（不再产生第二个写入者）；
        // 3) 独立窗口关闭（释放非持有的 X）→ X 仍归主窗口——旧副本不可能覆盖；
        // 4) 主窗口关闭释放后，X 才可被其他窗口取得。
        var root = NewRoot(); var id = NewConv();
        var mainToken = Guid.NewGuid(); var childToken = Guid.NewGuid();

        Assert.True(ConversationOwnershipRegistry.TryAcquire(root, id, mainToken));       // 主窗口占有 X

        Assert.True(ConversationOwnershipRegistry.IsHeldByOther(root, id, childToken));   // 独立窗口 → 阻止加载

        ConversationOwnershipRegistry.Release(root, id, childToken);                      // 独立窗口关闭：无效释放
        Assert.True(ConversationOwnershipRegistry.IsOwnedBy(root, id, mainToken));        // X 仍归主窗口

        ConversationOwnershipRegistry.Release(root, id, mainToken);                       // 主窗口关闭
        Assert.True(ConversationOwnershipRegistry.TryAcquire(root, id, childToken));      // 其后才可被取得
    }
}
