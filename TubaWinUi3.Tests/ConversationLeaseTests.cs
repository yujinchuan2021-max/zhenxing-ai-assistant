using TubaWinUi3.Services.Ai;

namespace TubaWinUi3.Tests;

/// <summary>
/// 【主审修复·第六轮】页面级会话写入租约（ConversationLease）测试。
///
/// 覆盖两条主审指出的源码路径缺陷 + builtin 统一归属：
/// 1) 关闭为终态：生成中关窗后，Send finally 迟到回调不得 TryAcquire 重获刚释放的 ID（防幽灵占用）；
/// 2) 会话替换成对：改模型重建（A→B）时释放旧、取得新——关闭只释放当前持有（不泄漏 B）；
/// 3) builtin/dsh 统一：同一 lease 规则，不双持、不覆盖。
/// </summary>
public class ConversationLeaseTests
{
    private static string NewRoot() => "ZTEST-" + Guid.NewGuid().ToString("N");
    private static string NewConv() => "conv-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void EnsureOwned_AcquiresOnce_SecondLeaseRejected()
    {
        var root = NewRoot(); var id = NewConv();
        var a = new ConversationLease(root);
        var b = new ConversationLease(root);
        Assert.True(a.EnsureOwned(id));
        Assert.Equal(id, a.CurrentId);
        Assert.False(b.EnsureOwned(id));               // 第二窗口：拒绝（同一写入者规则）
    }

    [Fact]
    public void EnsureOwned_WhileHoldingOther_ReturnsFalse_NoDoubleHold()
    {
        // 防双持：持有 A 时请求写 B（替换路径未成对）→ 拒写；B 从未被本 lease 登记。
        var root = NewRoot();
        var a = new ConversationLease(root);
        var idA = NewConv(); var idB = NewConv();
        Assert.True(a.EnsureOwned(idA));
        Assert.False(a.EnsureOwned(idB));
        Assert.Equal(idA, a.CurrentId);
        Assert.True(new ConversationLease(root).EnsureOwned(idB));   // B 未被 a 占有
    }

    [Fact]
    public void Replace_MovesOwnership_OldReleased_NewHeld()
    {
        var root = NewRoot();
        var a = new ConversationLease(root);
        var other = new ConversationLease(root);
        var idA = NewConv(); var idB = NewConv();
        Assert.True(a.EnsureOwned(idA));
        Assert.True(a.Replace(idB));                   // 历史恢复：释放旧 + 取得新
        Assert.Equal(idB, a.CurrentId);
        Assert.True(other.EnsureOwned(idA));           // A 已释放 → 其他窗口可接手
        Assert.False(other.EnsureOwned(idB));          // B 归 a
    }

    [Fact]
    public void Replace_TargetHeldByOther_Fails_CurrentUnchanged()
    {
        var root = NewRoot();
        var a = new ConversationLease(root); var b = new ConversationLease(root);
        var idA = NewConv(); var idB = NewConv();
        Assert.True(a.EnsureOwned(idA));
        Assert.True(b.EnsureOwned(idB));
        Assert.False(a.Replace(idB));                  // 目标被他人持有
        Assert.Equal(idA, a.CurrentId);                // 状态不变，不丢当前归属
    }

    [Fact]
    public void Close_IsTerminal_CannotReacquire_And_FreesCurrent()
    {
        // 主审缺陷 1：生成中关窗——Close 后迟到回调（Send finally）不可重获；
        // 释放的 ID 立即可被其他窗口接手（不再幽灵占用）。
        var root = NewRoot();
        var a = new ConversationLease(root);
        var id = NewConv();
        Assert.True(a.EnsureOwned(id));
        a.Close();
        Assert.True(a.IsClosed);
        Assert.Null(a.CurrentId);
        Assert.False(a.EnsureOwned(id));               // 迟到回调：拒绝（不重获、不旧快照覆盖）
        Assert.False(a.Replace(id));                   // 已关闭页面不可再打开历史
        Assert.False(a.CanTake(id));
        var b = new ConversationLease(root);
        Assert.True(b.EnsureOwned(id));                // 其他窗口立即接手 ✓
        a.Close();                                     // 重复关闭：幂等
        Assert.True(b.EnsureOwned(id));                // 幂等重复关闭不影响他人
    }

    [Fact]
    public void ModelRebuild_Scenario_OldReleasedThenNewOwned_CloseFreesCurrent()
    {
        // 主审缺陷 2 模型：持 A → （持有时落盘 A）→ ReleaseCurrent → B 首落盘取得 B；
        // 之后关闭只释放当前（B）——A 已在重建时释放——两者都不泄漏。
        var root = NewRoot();
        var page = new ConversationLease(root);
        var idA = NewConv(); var idB = NewConv();
        Assert.True(page.EnsureOwned(idA));
        page.ReleaseCurrent();                          // 重建：释放旧归属
        Assert.Null(page.CurrentId);
        Assert.True(page.EnsureOwned(idB));             // 新会话首次落盘：取得 B
        Assert.Equal(idB, page.CurrentId);
        page.Close();                                   // 真关闭：只释放当前（B）
        var o1 = new ConversationLease(root);
        var o2 = new ConversationLease(root);
        Assert.True(o1.EnsureOwned(idA));               // A 可接手（重建时已释放）
        Assert.True(o2.EnsureOwned(idB));               // B 可接手（关闭时释放，无失活 token 占用）
    }

    [Fact]
    public void CanTake_Reflects_HoldsAndClosure()
    {
        var root = NewRoot();
        var a = new ConversationLease(root); var b = new ConversationLease(root);
        var id = NewConv();
        Assert.True(a.CanTake(id));                     // 无人持有
        Assert.True(a.EnsureOwned(id));
        Assert.True(a.CanTake(id));                     // 自己持有
        Assert.False(b.CanTake(id));                    // 他人持有 → 预检失败（UI 提示占用）
        a.Close();
        Assert.False(a.CanTake(NewConv()));             // 已关闭页面不可再打开任何历史
    }

    [Fact]
    public void DifferentDataRoots_Isolated()
    {
        var id = NewConv();
        var a = new ConversationLease(NewRoot());
        var b = new ConversationLease(NewRoot());
        Assert.True(a.EnsureOwned(id));
        Assert.True(b.EnsureOwned(id));                 // 不同数据根互不影响
    }
}
