using System.Reflection;
using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Tests;

/// <summary>
/// 【A04 事务性回归】Git 技能导入：校验后才提交 / 复制失败回滚 / 备份隔离 / 同 Id 更新。
///
/// 隔离约定：SkillsDirOverride 指向每个测试独立的临时根（<c>%TEMP%\zxai-skill-txn-*</c>），
/// 技能目录与备份目录都在该根之下，绝不触碰真实 <c>%LOCALAPPDATA%\TubaWinUi3\Skills</c>；
/// git 不需要（用 ImportFromClonedDirectory 直接驱动「克隆完成之后」的流水线）。
/// 与技能注册表相关测试同集合串行（静态状态）。
/// </summary>
[Collection("AgentSkillRegistry")]
public sealed class UserSkillImportTransactionalTests : IDisposable
{
    private static readonly FieldInfo SkillsField =
        typeof(AgentSkillRegistry).GetField("_skills", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void ClearRegistry() => ((List<AgentSkill>)SkillsField.GetValue(null)!).Clear();

    private readonly string _root;         // 隔离临时根
    private readonly string _skillsDir;    // _root\Skills（本轮测试的技能目录）

    public UserSkillImportTransactionalTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zxai-skill-txn-" + Guid.NewGuid().ToString("N")[..8]);
        _skillsDir = Path.Combine(_root, "Skills");
        Directory.CreateDirectory(_skillsDir);

        ClearRegistry();
        UserSkillLoader.ResetSourceOwnershipForTest();
        UserSkillLoader.SkillsDirOverride = _skillsDir;
    }

    public void Dispose()
    {
        UserSkillLoader.SkillsDirOverride = null;
        UserSkillLoader.ForceStagedCopyForTest = false;
        UserSkillLoader.CopyFaultInjector = null;
        UserSkillLoader.ResetSourceOwnershipForTest();
        ClearRegistry();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    // ───────────────────────── ⑤ 测试隔离 ─────────────────────────

    [Fact]
    public void Isolation_SkillsDirAndBackupRoot_AreInsideTempRoot()
    {
        Assert.Equal(Path.GetFullPath(_skillsDir), Path.GetFullPath(UserSkillLoader.SkillsDir));

        // 绝不是真实技能目录（也不在其子树内）
        var real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TubaWinUi3", "Skills");
        Assert.False(Path.GetFullPath(UserSkillLoader.SkillsDir)
            .StartsWith(Path.GetFullPath(real) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        // 备份根目录：在隔离根内，且不在技能扫描范围内
        Assert.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar,
            Path.GetFullPath(UserSkillLoader.BackupRootDir) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(Path.GetFullPath(UserSkillLoader.BackupRootDir)
            .StartsWith(Path.GetFullPath(_skillsDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    // ─────────────────── ① 提交前验证（校验后才提交） ───────────────────

    [Fact]
    public void CloneWithNoValidSkill_IsRejected_BeforeAnyChange()
    {
        var target = Path.Combine(_skillsDir, "team-skills");
        WriteSkill(Path.Combine(target, "old.skill.json"), "user_old", "旧提示词");
        Assert.Equal(1, UserSkillLoader.LoadAll());

        // 克隆内容：损坏 JSON + 缺必填字段 → 有效技能集合为空
        var clone = NewCloneDir();
        File.WriteAllText(Path.Combine(clone, "broken.skill.json"), "{ not json");
        File.WriteAllText(Path.Combine(clone, "half.skill.json"), """{"id":"half","displayName":"缺片段"}""");
        Assert.Empty(UserSkillLoader.ParseSkillsIn(clone));

        var (ok, msg) = UserSkillLoader.ImportFromClonedDirectory(clone, target);

        Assert.False(ok);
        Assert.Contains("无法解析", msg);
        // 磁盘：旧目录旧文件原样，克隆内容一个字节都没落盘
        Assert.True(File.Exists(Path.Combine(target, "old.skill.json")));
        Assert.False(File.Exists(Path.Combine(target, "broken.skill.json")));
        Assert.False(File.Exists(Path.Combine(target, "half.skill.json")));
        // 注册表：旧条目与内存 prompt 不变
        Assert.NotNull(AgentSkillRegistry.Find("user_old"));
        Assert.Equal("旧提示词", AgentSkillRegistry.Find("user_old")!.SystemPromptFragment);
        // 无半成品、无备份残留
        AssertSkillsDirOnly(target);
        AssertNoBackupLeftover();
    }

    [Fact]
    public void CloneWithoutSkillFiles_IsRejected()
    {
        var target = Path.Combine(_skillsDir, "team-skills");
        Directory.CreateDirectory(target);
        var clone = NewCloneDir();
        File.WriteAllText(Path.Combine(clone, "README.md"), "仓库里没有技能文件");

        var (ok, msg) = UserSkillLoader.ImportFromClonedDirectory(clone, target);

        Assert.False(ok);
        Assert.Contains("未找到", msg);
        Assert.Empty(AgentSkillRegistry.All);
        Assert.True(Directory.Exists(target));
        Assert.Empty(Directory.GetFileSystemEntries(target));
        AssertNoBackupLeftover();
    }

    // ───────────── ② 跨卷复制事务：复制中断 → 回滚旧技能 ─────────────

    [Fact]
    public void StagedCopyFailure_RollsBack_OldSkillsIntact()
    {
        var target = Path.Combine(_skillsDir, "repo");
        WriteSkill(Path.Combine(target, "a.skill.json"), "user_a", "旧 A 提示词");
        WriteSkill(Path.Combine(target, "b.skill.json"), "user_b", "旧 B 提示词");
        Assert.Equal(2, UserSkillLoader.LoadAll());

        var clone = NewCloneDir();
        WriteSkill(Path.Combine(clone, "a.skill.json"), "user_a", "新 A 提示词");
        WriteSkill(Path.Combine(clone, "sub", "b.skill.json"), "user_b", "新 B 提示词");
        WriteSkill(Path.Combine(clone, "sub", "c.skill.json"), "user_c", "新 C 提示词");

        // 强制走跨卷「暂存复制」分支；复制 1 个文件后注入故障（模拟跨卷复制中断）
        UserSkillLoader.ForceStagedCopyForTest = true;
        UserSkillLoader.CopyFaultInjector = copied => copied == 1 ? new IOException("模拟跨卷复制中断") : null;

        var (ok, msg) = UserSkillLoader.ImportFromClonedDirectory(clone, target);

        Assert.False(ok);
        Assert.Contains("模拟跨卷复制中断", msg);
        Assert.Contains("已回滚", msg);
        // 旧技能目录完整：两个旧文件、内容仍是旧提示词；半成品没混进去
        Assert.Equal("旧 A 提示词", ReadPrompt(Path.Combine(target, "a.skill.json")));
        Assert.Equal("旧 B 提示词", ReadPrompt(Path.Combine(target, "b.skill.json")));
        Assert.False(Directory.Exists(Path.Combine(target, "sub")));
        // 内存 prompt 仍是旧值（注册表未被半成品污染）
        Assert.Equal("旧 A 提示词", AgentSkillRegistry.Find("user_a")!.SystemPromptFragment);
        Assert.Equal("旧 B 提示词", AgentSkillRegistry.Find("user_b")!.SystemPromptFragment);
        Assert.Null(AgentSkillRegistry.Find("user_c"));
        // 克隆源未被破坏（提交前绝不删源）
        Assert.True(File.Exists(Path.Combine(clone, "sub", "c.skill.json")));
        // 无 .incoming-* 半成品 / 无备份残留
        AssertSkillsDirOnly(target);
        AssertNoBackupLeftover();
    }

    // ─────────────────── ③ 备份隔离（不被扫描加载） ───────────────────

    [Fact]
    public void LegacyBackupInsideSkills_IsNotLoadedAsSkill()
    {
        // 旧实现遗留：备份目录落在 Skills 内（repo.old-xxxx）
        WriteSkill(Path.Combine(_skillsDir, "repo.old-abc123", "ghost.skill.json"), "ghost_skill", "备份里的旧技能");
        // 暂存提交目录（.incoming-*）与显式备份名（.backup-*）同理
        WriteSkill(Path.Combine(_skillsDir, "repo.incoming-dead", "half.skill.json"), "incoming_skill", "复制到一半");
        WriteSkill(Path.Combine(_skillsDir, ".backup-x", "b.skill.json"), "backup_skill", "显式备份");

        Assert.Equal(0, UserSkillLoader.LoadAll());
        Assert.Null(AgentSkillRegistry.Find("ghost_skill"));
        Assert.Null(AgentSkillRegistry.Find("incoming_skill"));
        Assert.Null(AgentSkillRegistry.Find("backup_skill"));

        // 排除规则不误伤正常目录
        WriteSkill(Path.Combine(_skillsDir, "real-repo", "ok.skill.json"), "real_skill", "正常技能");
        Assert.Equal(1, UserSkillLoader.LoadAll());
        Assert.NotNull(AgentSkillRegistry.Find("real_skill"));

        // 成功提交：备份落在扫描范围外，并且提交后即清理
        var target = Path.Combine(_skillsDir, "repo");
        WriteSkill(Path.Combine(target, "old.skill.json"), "user_old", "v1");
        Assert.Equal(1, UserSkillLoader.LoadAll());

        var clone = NewCloneDir();
        WriteSkill(Path.Combine(clone, "new.skill.json"), "user_new", "v2");
        var (ok, msg) = UserSkillLoader.ImportFromClonedDirectory(clone, target);

        Assert.True(ok, msg);
        Assert.Equal("v2", AgentSkillRegistry.Find("user_new")!.SystemPromptFragment);
        Assert.False(Path.GetFullPath(UserSkillLoader.BackupRootDir)
            .StartsWith(Path.GetFullPath(_skillsDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        AssertNoBackupLeftover();
    }

    // ─────────────── ④ 同 Id 更新（磁盘 / 内存 / 返回结果一致） ───────────────

    [Fact]
    public void SameIdReimport_UpdatesMemory_KeepsOtherSourcesAndBuiltIns()
    {
        // 内置（或手动注册）技能：绝不能被用户技能的刷新牵连
        AgentSkillRegistry.Register(new AgentSkill
        {
            Id = "builtin_skill",
            DisplayName = "内置技能",
            Glyph = "\uE721",
            Description = "内置",
            SystemPromptFragment = "内置提示词",
        });

        var repoA = Path.Combine(_skillsDir, "repo-a");
        var repoB = Path.Combine(_skillsDir, "other-repo");
        WriteSkill(Path.Combine(repoA, "a.skill.json"), "user_a", "A 提示词 v1");
        WriteSkill(Path.Combine(repoB, "b.skill.json"), "user_b", "B 提示词 v1");
        Assert.Equal(2, UserSkillLoader.LoadAll());
        Assert.Equal("A 提示词 v1", AgentSkillRegistry.Find("user_a")!.SystemPromptFragment);

        // 内容未变的重复扫描：幂等（保留原实例，不计入更新）
        var (sameLoaded, sameReplaced) = UserSkillLoader.LoadAllDetailed();
        Assert.Equal(0, sameLoaded);
        Assert.Equal(0, sameReplaced);

        // 同一来源、同 Id、prompt 变了 → 内存 prompt 必须跟着变，且不重复注册
        WriteSkill(Path.Combine(repoA, "a.skill.json"), "user_a", "A 提示词 v2");
        var (loaded, replaced) = UserSkillLoader.LoadAllDetailed();
        Assert.Equal(1, loaded);
        Assert.Equal(1, replaced);
        Assert.Equal("A 提示词 v2", AgentSkillRegistry.Find("user_a")!.SystemPromptFragment);
        Assert.Single(AgentSkillRegistry.All, s => s.Id == "user_a");
        Assert.Equal("B 提示词 v1", AgentSkillRegistry.Find("user_b")!.SystemPromptFragment);      // 其他来源保留
        Assert.Equal("内置提示词", AgentSkillRegistry.Find("builtin_skill")!.SystemPromptFragment); // 内置保留

        // 走完整导入流程：磁盘、内存 prompt、返回结果三者一致
        var clone = NewCloneDir();
        WriteSkill(Path.Combine(clone, "a.skill.json"), "user_a", "A 提示词 v3");
        var (ok, msg) = UserSkillLoader.ImportFromClonedDirectory(clone, repoA);

        Assert.True(ok, msg);
        Assert.Contains("同 Id 更新", msg);
        Assert.Equal("A 提示词 v3", ReadPrompt(Path.Combine(repoA, "a.skill.json")));              // 磁盘
        Assert.Equal("A 提示词 v3", AgentSkillRegistry.Find("user_a")!.SystemPromptFragment);       // 内存
        Assert.Single(AgentSkillRegistry.All, s => s.Id == "user_a");                               // 不重复
        Assert.Equal("B 提示词 v1", AgentSkillRegistry.Find("user_b")!.SystemPromptFragment);       // 其他来源保留
        Assert.Equal("内置提示词", AgentSkillRegistry.Find("builtin_skill")!.SystemPromptFragment);  // 内置保留
        AssertNoBackupLeftover();
    }

    [Fact]
    public void ReimportWithBuiltInIdConflict_SkipsWithoutTouchingBuiltIn()
    {
        AgentSkillRegistry.Register(new AgentSkill
        {
            Id = "builtin_skill",
            DisplayName = "内置技能",
            Glyph = "\uE721",
            Description = "内置",
            SystemPromptFragment = "内置提示词",
        });

        var target = Path.Combine(_skillsDir, "clash-repo");
        Directory.CreateDirectory(target);
        var clone = NewCloneDir();
        WriteSkill(Path.Combine(clone, "clash.skill.json"), "builtin_skill", "冒名提示词");

        var (ok, msg) = UserSkillLoader.ImportFromClonedDirectory(clone, target);

        Assert.False(ok);                                   // 全冲突：如实失败
        Assert.Contains("冲突", msg);
        Assert.Equal("内置提示词", AgentSkillRegistry.Find("builtin_skill")!.SystemPromptFragment);
        Assert.Single(AgentSkillRegistry.All);
        // 【A04 返修】提交前预判「无可接纳项」：不提交——冒名文件不入盘（旧行为会先落盘再报失败）
        Assert.False(File.Exists(Path.Combine(target, "clash.skill.json")));
        AssertNoBackupLeftover();
    }

    [Fact]
    public void ReimportOverNonEmptyRepo_AllConflicting_KeepsOldRepoIntact()
    {
        AgentSkillRegistry.Register(new AgentSkill
        {
            Id = "builtin_skill",
            DisplayName = "内置技能",
            Glyph = "\uE721",
            Description = "内置",
            SystemPromptFragment = "内置提示词",
        });

        // 非空旧仓库：先导入一个有效技能（user_old 属于 clash-repo）
        var target = Path.Combine(_skillsDir, "clash-repo");
        var first = NewCloneDir();
        WriteSkill(Path.Combine(first, "old.skill.json"), "user_old", "旧仓库提示词");
        var (ok1, msg1) = UserSkillLoader.ImportFromClonedDirectory(first, target);
        Assert.True(ok1, msg1);
        Assert.Equal("旧仓库提示词", AgentSkillRegistry.Find("user_old")!.SystemPromptFragment);
        Assert.Equal("旧仓库提示词", ReadPrompt(Path.Combine(target, "old.skill.json")));

        // 新仓库：全部 Id 与内置冲突（无可接纳项）
        var second = NewCloneDir();
        WriteSkill(Path.Combine(second, "new.skill.json"), "builtin_skill", "冒名提示词");
        var (ok2, msg2) = UserSkillLoader.ImportFromClonedDirectory(second, target);

        Assert.False(ok2, msg2);
        Assert.Contains("冲突", msg2);
        // 旧目录/旧文件原样保留（不允许"先删旧、再报失败"）
        Assert.True(File.Exists(Path.Combine(target, "old.skill.json")));
        Assert.Equal("旧仓库提示词", ReadPrompt(Path.Combine(target, "old.skill.json")));
        Assert.False(File.Exists(Path.Combine(target, "new.skill.json")));
        // 旧技能内存条目原样保留、未新增冒名条目
        Assert.Equal("旧仓库提示词", AgentSkillRegistry.Find("user_old")!.SystemPromptFragment);
        Assert.Equal("内置提示词", AgentSkillRegistry.Find("builtin_skill")!.SystemPromptFragment);
        Assert.Equal(2, AgentSkillRegistry.All.Count(s => s.Id is "user_old" or "builtin_skill"));
        AssertNoBackupLeftover();
    }

    [Fact]
    public void Reimport_BuiltInIdCaseVariantOnly_RejectedAtPrecheck_OldRepoIntact()
    {
        AgentSkillRegistry.Register(new AgentSkill
        {
            Id = "builtin_skill",
            DisplayName = "内置技能",
            Glyph = "\uE721",
            Description = "内置",
            SystemPromptFragment = "内置提示词",
        });

        // 非空旧仓库：user_old 属于 clash-repo
        var target = Path.Combine(_skillsDir, "clash-repo");
        var first = NewCloneDir();
        WriteSkill(Path.Combine(first, "old.skill.json"), "user_old", "旧仓库提示词");
        var (ok1, msg1) = UserSkillLoader.ImportFromClonedDirectory(first, target);
        Assert.True(ok1, msg1);

        // 新仓库只有一个【大小写变体】Id（BUILTIN_SKILL）。旧实现的预检用大小写敏感的
        // AgentSkillRegistry.Find 会放行；提交后正式加载（忽略大小写）又拒绝全部新项并 orphan 清理，
        // 导致旧有效技能丢失。修复后必须【在预检阶段】就被拒绝、旧目录/内存零改动。
        var second = NewCloneDir();
        WriteSkill(Path.Combine(second, "new.skill.json"), "BUILTIN_SKILL", "冒名提示词");
        var (ok2, msg2) = UserSkillLoader.ImportFromClonedDirectory(second, target);

        Assert.False(ok2, msg2);
        Assert.Contains("冲突", msg2);
        // 旧目录/旧文件/旧内存原样保留，新内容未就位
        Assert.True(File.Exists(Path.Combine(target, "old.skill.json")));
        Assert.Equal("旧仓库提示词", ReadPrompt(Path.Combine(target, "old.skill.json")));
        Assert.False(File.Exists(Path.Combine(target, "new.skill.json")));
        Assert.Equal("旧仓库提示词", AgentSkillRegistry.Find("user_old")!.SystemPromptFragment);
        Assert.Equal("内置提示词", AgentSkillRegistry.Find("builtin_skill")!.SystemPromptFragment);
        AssertNoBackupLeftover();
    }

    [Fact]
    public void Reimport_OnlyExcludedBackupDirs_RejectedAtPrecheck_OldRepoIntact()
    {
        // 非空旧仓库：user_old 属于 clash-repo
        var target = Path.Combine(_skillsDir, "clash-repo");
        var first = NewCloneDir();
        WriteSkill(Path.Combine(first, "old.skill.json"), "user_old", "旧仓库提示词");
        var (ok1, msg1) = UserSkillLoader.ImportFromClonedDirectory(first, target);
        Assert.True(ok1, msg1);

        // 新仓库：技能文件【全部】位于扫描排除目录（.backup-x/）——预检必须与加载端同语义：
        // 视为无有效内容，整体拒绝；旧目录/内存零改动（旧实现的 ParseSkillsIn 不排除备份目录，
        // 会把该文件算作有效技能并放行提交，随后正式加载又不扫描它 → 旧技能被删）。
        var second = NewCloneDir();
        WriteSkill(Path.Combine(second, ".backup-x", "new.skill.json"), "user_new", "备份里的提示词");
        var (ok2, msg2) = UserSkillLoader.ImportFromClonedDirectory(second, target);

        Assert.False(ok2, msg2);
        Assert.Contains("备份", msg2);
        // 旧技能全保；备份目录内容没有进入正式目录
        Assert.True(File.Exists(Path.Combine(target, "old.skill.json")));
        Assert.Equal("旧仓库提示词", ReadPrompt(Path.Combine(target, "old.skill.json")));
        Assert.Equal("旧仓库提示词", AgentSkillRegistry.Find("user_old")!.SystemPromptFragment);
        Assert.False(Directory.Exists(Path.Combine(target, ".backup-x")));
        AssertNoBackupLeftover();
    }

    // ───────────────────────────── 工具 ─────────────────────────────

    private string NewCloneDir()
    {
        var dir = Path.Combine(_root, "clones", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteSkill(string path, string id, string prompt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            $$"""{"id":"{{id}}","displayName":"技能 {{id}}","description":"测试技能","systemPromptFragment":"{{prompt}}","triggerKeywords":["{{id}}"]}""");
    }

    private static string? ReadPrompt(string path)
        => UserSkillLoader.FromJson(File.ReadAllText(path))?.SystemPromptFragment;

    /// <summary>技能目录下只应有这些子目录（无 .incoming-* / .old-* 半成品）。</summary>
    private void AssertSkillsDirOnly(params string[] expectedDirs)
    {
        var actual = Directory.GetDirectories(_skillsDir).Select(Path.GetFullPath)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        var expected = expectedDirs.Select(Path.GetFullPath)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(expected, actual);
    }

    /// <summary>备份根目录（若已创建）必须是空的——成功提交即清理，失败回滚即移回。</summary>
    private static void AssertNoBackupLeftover()
    {
        var root = UserSkillLoader.BackupRootDir;
        if (Directory.Exists(root)) Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }
}
