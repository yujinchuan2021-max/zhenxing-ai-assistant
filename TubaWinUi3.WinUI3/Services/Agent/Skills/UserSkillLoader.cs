using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace TubaWinUi3.Services.Agent;

/// <summary>
/// ZXAI：用户自定义技能加载器。
/// 目录 <c>%LocalAppData%\TubaWinUi3\Skills\</c> 下的 <c>*.skill.json</c>（含子目录递归）
/// 在启动与导入时自动注册到 <see cref="AgentSkillRegistry"/>；支持从文件导入与从 Git 仓库导入。
///
/// 技能文件格式（字段宽松：camelCase/PascalCase 均可）：
/// <code>
/// {
///   "id": "my_skill",              // 必填，字母数字/_/-
///   "displayName": "我的技能",      // 必填
///   "glyph": "\uE9D9",            // 可选，Segoe Fluent 字形
///   "description": "一句话简介",     // 必填
///   "systemPromptFragment": "…",  // 必填，完整指导片段（按需注入）
///   "triggerKeywords": ["关键词"]  // 可选，命中即强制注入强指令
/// }
/// </code>
///
/// 【A04 事务性】Git 导入 = 克隆到独立临时目录 → 提交前校验（路径安全 + 至少解析出一个有效技能）
/// → 事务提交（旧目录备份到扫描范围外 → 新内容整体就位 → 失败回滚，旧技能绝不丢）
/// → 按来源刷新注册（同一来源重新导入时同 Id 替换旧条目）。
/// </summary>
public static class UserSkillLoader
{
    /// <summary>测试注入点（null = 用真实目录）。</summary>
    internal static string? SkillsDirOverride { get; set; }

    /// <summary>【A04 测试钩子】强制走「暂存复制 → 重命名提交」分支（同卷测试也能覆盖跨卷事务路径）。</summary>
    internal static bool ForceStagedCopyForTest { get; set; }

    /// <summary>【A04 测试钩子】跨卷复制第 N 个文件（N = 已复制数量，0 基）前注入故障：返回非 null 则抛出。
    /// 设置后自动改走暂存复制分支——用于验证「复制一个文件后失败 → 旧技能完整保留」。</summary>
    internal static Func<int, Exception?>? CopyFaultInjector { get; set; }

    public static string SkillsDir
    {
        get
        {
            // 测试注入点优先（内部钩子；生产/GUI 永不设置——隔离判定保持生效）。
            var injected = SkillsDirOverride;
            if (!string.IsNullOrWhiteSpace(injected))
            {
                Directory.CreateDirectory(injected);
                return injected;
            }

            // 【GUI 隔离】测试模式：技能目录落隔离根（不读写真实用户技能目录）。
            if (DataRoots.EffectiveTestRoot is { } testTr)
            {
                var testSkills = Path.Combine(testTr, "Skills");
                Directory.CreateDirectory(testSkills);
                return testSkills;
            }

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TubaWinUi3", "Skills");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// 【A04】备份根目录：必须落在技能扫描范围（<see cref="SkillsDir"/> 子树）之外——
    /// 默认是 SkillsDir 的同级 <c>SkillsBackup</c>，解析异常时回退系统临时目录。
    /// 这样 LoadAll 递归扫描永远不会把备份里的旧技能当有效技能读进来。
    /// </summary>
    internal static string BackupRootDir
    {
        get
        {
            var skills = Path.GetFullPath(SkillsDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Path.GetDirectoryName(skills);
            var root = string.IsNullOrEmpty(parent)
                ? Path.Combine(Path.GetTempPath(), "TubaWinUi3-SkillsBackup")
                : Path.Combine(parent, "SkillsBackup");

            // 防御：备份根目录若仍落在 Skills 子树内（父目录解析异常等），一律改用系统临时目录
            var inside = (Path.GetFullPath(root) + Path.DirectorySeparatorChar)
                .StartsWith(skills + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            return inside ? Path.Combine(Path.GetTempPath(), "TubaWinUi3-SkillsBackup") : root;
        }
    }

    /// <summary>扫描技能目录（递归 *.skill.json）并注册；返回本轮注册的技能数。</summary>
    public static int LoadAll() => LoadAllDetailed().Loaded;

    /// <summary>
    /// 【A04】扫描技能目录并注册，返回（本轮注册数，其中同 Id 更新数）。
    ///
    /// 同 Id 不再一律跳过——按「来源」更新（来源 = 技能文件所在的 Skills 顶层子目录；
    /// Skills 根目录下的技能文件归为空来源）：
    ///   · 同一来源重新导入：内容变化的同 Id 条目替换旧值（内存 prompt 同步为新内容，不重复注册）；
    ///   · 内容未变的条目保留原实例（重复扫描幂等，不产生无谓的对象替换）；
    ///   · 其他来源与内置技能一律不动；与内置/他人 Id 冲突的仍然跳过（先到者优先）；
    ///   · 磁盘上已不存在任何技能文件的来源，其内存条目一并清理。
    /// 单个文件损坏不影响其余；备份（.old-*）、暂存提交（.incoming-*）、显式备份（.backup-）不参与扫描。
    /// </summary>
    internal static (int Loaded, int Replaced) LoadAllDetailed()
    { lock (AgentSkillRegistry.Sync) return LoadAllCore(); }

    private static (int Loaded, int Replaced) LoadAllCore()
    {
        var loaded = 0;
        var replaced = 0;
        try
        {
            var root = Path.GetFullPath(SkillsDir);
            ResetOwnershipIfRootChanged(root);

            var files = EnumerateSkillFiles(root);   // 【A04】与预检同一套收集规则

            // ① 解析并按来源分组（损坏 / 缺必填字段的文件跳过）
            var bySource = new Dictionary<string, List<AgentSkill>>(StringComparer.OrdinalIgnoreCase);
            var fileSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var source = SourceKeyOf(root, file);
                fileSources.Add(source);

                AgentSkill? skill;
                try { skill = FromJson(File.ReadAllText(file)); }
                catch { continue; }
                if (skill is null) continue;

                if (!bySource.TryGetValue(source, out var list)) bySource[source] = list = [];
                list.Add(skill);
            }

            // ② 磁盘上已不存在任何文件的来源 → 清掉其内存条目（技能目录被删除/改名）
            foreach (var orphan in _ownedBySource.Keys.ToList())
            {
                if (fileSources.Contains(orphan)) continue;
                foreach (var id in TakeOwned(orphan)) RemoveFromRegistry(id);
            }

            // ③ 逐来源刷新：内容未变的保留原实例；变化/新增的替换或注册
            var removedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in bySource.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                var skills = bySource[source];
                if (skills.Count == 0) continue;                       // 全损坏：保留旧条目，不静默卸载

                var parsed = new Dictionary<string, AgentSkill>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in skills) parsed.TryAdd(s.Id, s);       // 同来源内重复 Id：首个生效

                // 该来源此前拥有的条目：文件消失 → 移除；内容变化 → 先移除（下面注册新值）；内容未变 → 保留
                foreach (var id in TakeOwned(source))
                {
                    if (!parsed.TryGetValue(id, out var incoming))
                    {
                        RemoveFromRegistry(id);
                        continue;
                    }
                    var existing = FindById(id);
                    if (existing is null) continue;
                    if (SameContent(existing, incoming))
                    {
                        Own(source, id);
                        continue;
                    }
                    if (RemoveFromRegistry(id)) removedIds.Add(id);
                }

                // 注册本来源的有效技能：Id 被内置/其他来源占用则跳过（不改动别人的条目）
                foreach (var (id, skill) in parsed)
                {
                    if (FindById(id) is not null) continue;
                    AgentSkillRegistry.Register(skill);
                    Own(source, id);
                    loaded++;
                    if (removedIds.Contains(id)) replaced++;
                }
            }
        }
        catch { }
        return (loaded, replaced);
    }

    /// <summary>解析技能 JSON（宽松字段名；必需字段缺失或 JSON 非法返回 null）。</summary>
    public static AgentSkill? FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return FromJsonCore(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AgentSkill? FromJsonCore(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        string? S(params string[] names)
        {
            foreach (var n in names)
                if (root.TryGetProperty(n, out var el) && el.ValueKind == JsonValueKind.String)
                    return el.GetString();
            return null;
        }

        var id = S("id", "Id");
        var name = S("displayName", "DisplayName", "name", "Name");
        var desc = S("description", "Description");
        var fragment = S("systemPromptFragment", "SystemPromptFragment", "prompt", "Prompt", "instructions", "Instructions");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(desc) || string.IsNullOrWhiteSpace(fragment))
            return null;

        var safeId = new string(id!.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());
        if (safeId.Length == 0) return null;

        var triggers = new List<string>();
        foreach (var n in new[] { "triggerKeywords", "TriggerKeywords", "triggers", "Triggers" })
        {
            if (root.TryGetProperty(n, out var el) && el.ValueKind == JsonValueKind.Array)
            {
                triggers.AddRange(el.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!)
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
                break;
            }
        }

        return new AgentSkill
        {
            Id = safeId,
            DisplayName = name!,
            Glyph = S("glyph", "Glyph") ?? "\uE9D9",
            Description = desc!,
            SystemPromptFragment = fragment!,
            TriggerKeywords = triggers.ToArray(),
        };
    }

    /// <summary>从文件导入：校验 → 复制到技能目录 → 刷新注册。</summary>
    public static (bool Ok, string Message) ImportFromFile(string filePath)
    {
        try
        {
            var skill = FromJson(File.ReadAllText(filePath));
            if (skill is null)
                return (false, MiscTexts.T("不是有效的技能文件（需含 id / displayName / description / systemPromptFragment）"));

            var target = Path.Combine(SkillsDir, Path.GetFileName(filePath));
            if (!Path.GetFullPath(filePath).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Copy(filePath, target, true);

            LoadAll();
            return AgentSkillRegistry.All.Any(s => s.Id == skill.Id)
                ? (true, MiscTexts.TSub($"已导入并加载「{skill.DisplayName}」"))
                : (false, MiscTexts.TSub($"已复制文件，但 Id「{skill.Id}」与现有技能重复，未加载"));
        }
        catch (Exception ex) { return (false, MiscTexts.T("导入失败：") + ex.Message); }
    }

    /// <summary>
    /// 【A04 审计修复】从 Git 仓库导入（git clone --depth 1 → 扫描注册）。需系统已装 git。
    /// 安全约束：
    ///   ① 仓库名严格校验（仅字母/数字/._-；拒绝 "."、".." 等特殊目录名——原实现
    ///      `Path.GetFileNameWithoutExtension("..")` 得到 "."，目标等于 Skills 根目录，且
    ///      在克隆前就先 Directory.Delete(target, true) 整个技能库）；
    ///   ② 目标必须是 SkillsDir 的严格子目录（GetFullPath 规范化后前缀校验）；
    ///   ③ 先克隆到独立临时目录，克隆完成后校验出有效技能集合，全部通过才提交；
    ///   ④ 失败一律保留旧技能——绝不预先删除目标目录（提交阶段见 CommitValidatedClone）。
    /// </summary>
    public static async Task<(bool Ok, string Message)> ImportFromGitAsync(string repoUrl, CancellationToken ct = default)
    {
        string? staging = null;
        try
        {
            if (string.IsNullOrWhiteSpace(repoUrl) ||
                !(repoUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) || repoUrl.StartsWith("git@")))
                return (false, MiscTexts.T("请输入 git 仓库地址（http(s):// 或 git@ 开头）"));

            // ① 仓库名严格校验
            if (!TryComputeRepoName(repoUrl, out var name, out var nameError))
                return (false, nameError);

            // ② 目标必须是 Skills 的严格子目录
            if (!TryResolveSafeTarget(name, out var target, out var targetError))
                return (false, targetError);

            // ③ 先克隆到独立临时目录（现有技能目录零改动）
            staging = Path.Combine(Path.GetTempPath(), "zxai-skill-import", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(staging);
            var cloneTarget = Path.Combine(staging, "repo");

            var psi = new ProcessStartInfo("git", $"clone --depth 1 \"{repoUrl}\" \"{cloneTarget}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (false, MiscTexts.T("无法启动 git（请确认已安装 git 并加入 PATH）"));
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0)
            {
                var err = (await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
                return (false, MiscTexts.T("git clone 失败：") + (err.Length > 200 ? err[..200] : err) + MiscTexts.T("（旧技能未改动）"));
            }

            // ④ 校验 → 提交 → 按来源刷新注册（见 ImportFromClonedDirectory）
            return ImportFromClonedDirectory(cloneTarget, target);
        }
        catch (Exception ex) { return (false, MiscTexts.T("导入失败：") + ex.Message); }
        finally
        {
            // 清理暂存（成功时 cloneTarget 已被移走，staging 只剩空目录）
            if (staging is not null && Directory.Exists(staging))
            {
                try { Directory.Delete(staging, true); } catch { }
            }
        }
    }

    /// <summary>
    /// 【A04】克隆完成后的流水线：提交前校验 → 事务提交 → 按来源刷新注册。
    /// internal：测试可用本地目录直接驱动全流程（无需 git / 网络）。
    /// </summary>
    internal static (bool Ok, string Message) ImportFromClonedDirectory(string cloneTarget, string target)
    {
        try
        {
            // ① 提交前验证：解析克隆内容，构建「有效技能集合」。
            //    一个都无效（无技能文件 / JSON 损坏 / 缺必填字段）→ 整体放弃：
            //    磁盘上的旧目录与内存注册表都保持不变。
            var valid = ParseSkillsIn(cloneTarget);
            if (valid.Count == 0)
            {
                var anyRaw = Directory.EnumerateFiles(cloneTarget, "*.skill.json", SearchOption.AllDirectories).Any();
                if (!anyRaw)
                    return (false, MiscTexts.T("仓库中未找到 *.skill.json 技能文件，已放弃导入（旧技能保留）"));
                if (EnumerateSkillFiles(cloneTarget).Count == 0)
                    return (false, MiscTexts.T("仓库中的技能文件全部位于备份/暂存类目录（.backup- / .old- / .incoming-），")
                         + MiscTexts.T("不参与扫描，已放弃导入（旧技能保留）"));
                return (false, MiscTexts.T("仓库中的技能文件均无法解析（JSON 非法或缺 id / displayName / description / systemPromptFragment），")
                     + MiscTexts.T("已放弃导入（旧技能未改动）"));
            }

            // ② 【A04 返修·第二轮】提交前「最终可接纳集合」与正式加载（LoadAllDetailed）完全同语义：
            //    · 收集排除：ParseSkillsIn 已经走 EnumerateSkillFiles（与加载端同一套规则）；
            //    · 去重：同 Id 首个生效（与加载端 parsed.TryAdd 一致——文件序、OrdinalIgnoreCase）；
            //    · 判重/归属：大小写不敏感（CanAccept → FindById + IsOwnedBySource）。
            //      旧实现用大小写敏感的 AgentSkillRegistry.Find 预检："BUILTIN_SKILL" 这类变体会绕过预检，
            //      提交后又被正式加载（忽略大小写）拒绝 → 旧有效技能被删除且内存条目被 orphan 清理。本函数修掉该不一致。
            var deduped = new List<AgentSkill>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in valid)
                if (seenIds.Add(s.Id)) deduped.Add(s);

            var sourceName = Path.GetFileName(target);
            var acceptable = deduped.Where(s => CanAccept(s.Id, sourceName)).ToList();
            if (acceptable.Count == 0)
                return (false, MiscTexts.T("新仓库的全部技能 Id 都与内置/其他来源的技能冲突（含大小写变体），已放弃导入（旧技能保留）"));

            // ③ 事务提交（两阶段·第一阶段）：备份（扫描范围外）→ 新内容整体就位；
            //    备份【保留到正式加载验证通过之后】（CommitValidatedClone 不再立即删除）。
            var (commitOk, commitMessage, backup) = CommitValidatedClone(cloneTarget, target);
            if (!commitOk) return (false, commitMessage);

            // ④ 刷新注册：同一来源的同 Id 条目会被替换（磁盘、内存 prompt、返回结果三者一致）
            var (loaded, replaced) = LoadAllDetailed();
            if (loaded > 0)
            {
                TryDeleteDirectory(backup);   // 第二阶段：验证通过，删除备份
                return (true, MiscTexts.TSub($"已从仓库导入 {loaded} 个技能") + (replaced > 0 ? MiscTexts.TSub($"（其中 {replaced} 个为同 Id 更新）") : ""));
            }

            // 【A04 返修】内容完全未变（去重集合的所有条目均已在注册且内容一致）→ "已是最新"成功。
            if (deduped.All(s =>
                {
                    var ex = FindById(s.Id);
                    return ex is not null && SameContent(ex, s);
                }))
            {
                TryDeleteDirectory(backup);
                return (true, MiscTexts.T("已是最新：仓库内容与当前技能一致，无需更新"));
            }

            // ⑤ 【A04 返修·第二轮】abort 路径：正式加载未接纳任何新项且并非"已是最新"——
            //    恢复旧目录（回滚磁盘）并重扫（恢复旧内存条目）。
            //    绝不留下「新内容被拒绝 + 旧技能已丢失」的状态。
            var restored = Rollback(target, backup);
            LoadAllDetailed();   // 磁盘已恢复旧内容：重建旧来源的内存注册
            return (false, restored
                ? MiscTexts.T("导入未生效：新内容全部不可接纳，已回滚，旧技能原样保留")
                : MiscTexts.TSub($"导入未生效，旧技能完整保留在备份目录：{backup}，请手动移回 {target}"));
        }
        catch (Exception ex) { return (false, MiscTexts.T("导入失败：") + ex.Message); }
    }

    /// <summary>
    /// 【A04 统一语义·第二轮】枚举 root 下应参与扫描的技能文件——与正式加载（LoadAllDetailed）
    /// 使用【同一套】排除规则与排序。预检与加载共用本函数，杜绝「预检看到的内容 ≠ 加载看到的内容」。
    /// </summary>
    internal static List<string> EnumerateSkillFiles(string root)
    {
        root = Path.GetFullPath(root);
        return Directory.EnumerateFiles(root, "*.skill.json", SearchOption.AllDirectories)
            .Where(f => !IsExcludedFromScan(root, f))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 【A04】解析目录里的全部 *.skill.json（递归；与加载端同一套扫描排除规则），
    /// 返回有效技能集合（损坏/缺字段的跳过；备份/暂存目录里的文件不参与）。
    /// </summary>
    internal static List<AgentSkill> ParseSkillsIn(string dir)
    {
        var list = new List<AgentSkill>();
        try
        {
            foreach (var file in EnumerateSkillFiles(dir))
            {
                try
                {
                    var skill = FromJson(File.ReadAllText(file));
                    if (skill is not null) list.Add(skill);
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    /// <summary>【A04】仓库名严格校验：仅字母/数字/._-，且不能是 "." / ".." / 空白。</summary>
    internal static bool TryComputeRepoName(string repoUrl, out string name, out string error)
    {
        name = "";
        error = "";
        var last = repoUrl.TrimEnd('/').Split('/').Last();
        if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            last = last[..^4];
        last = last.Trim();

        if (string.IsNullOrWhiteSpace(last) || last is "." or "..")
        {
            error = MiscTexts.T("仓库名不合法：不能为空、. 或 ..");
            return false;
        }
        if (last.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !last.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            error = MiscTexts.T("仓库名不合法：只允许字母 / 数字 / . _ -");
            return false;
        }
        name = last;
        return true;
    }

    /// <summary>【A04】目标路径必须是 SkillsDir 的严格子目录（规范化后前缀校验）。</summary>
    internal static bool TryResolveSafeTarget(string name, out string target, out string error)
    {
        target = "";
        error = "";
        var root = Path.GetFullPath(SkillsDir) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(SkillsDir, name));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            error = MiscTexts.T("目标路径越界（必须是技能目录的子目录），已拒绝");
            return false;
        }
        target = full;
        return true;
    }

    /// <summary>
    /// 【A04】事务提交：旧目录先改名备份到扫描范围外 → 新内容整体就位 → 成功删备份 / 失败回滚。
    /// 失败后保证：target 恢复为旧内容（或不存在），暂存目录与备份均已清理或明确告知位置。
    /// </summary>
    /// <summary>【A04 两阶段】提交克隆内容。成功时【保留备份】随返回值交给调用方，
    /// 由调用方在正式加载验证通过后再删除（失败则回滚恢复）。</summary>
    private static (bool Ok, string Message, string? Backup) CommitValidatedClone(string cloneTarget, string target)
    {
        string? backup = null;
        if (Directory.Exists(target))
        {
            try
            {
                backup = AllocateBackupPath(target);
                Directory.Move(target, backup);            // 同卷 rename：备份一步完成，旧技能零丢失窗口
            }
            catch (Exception ex)
            {
                return (false, MiscTexts.T("导入失败：无法备份现有技能目录（旧技能未改动）：") + ex.Message, null);
            }
        }

        try
        {
            MoveOrCopyDirectory(cloneTarget, target);
        }
        catch (Exception ex)
        {
            var restored = Rollback(target, backup);
            return (false, restored
                ? MiscTexts.TSub($"导入失败：{ex.Message}（已回滚，旧技能未改动）")
                : MiscTexts.TSub($"导入失败：{ex.Message}（旧技能完整保留在备份目录：{backup}，请手动移回 {target}）"), null);
        }

        // 【A04 两阶段】成功路径不在此删除备份——调用方确认正式加载接纳后再删。
        return (true, "", backup);
    }

    /// <summary>【A04】分配备份路径（备份根目录在技能扫描范围外 + 目标名.old-随机串）。</summary>
    private static string AllocateBackupPath(string target)
    {
        var root = BackupRootDir;
        Directory.CreateDirectory(root);
        return Path.Combine(root, Path.GetFileName(target) + ".old-" + Guid.NewGuid().ToString("N")[..6]);
    }

    /// <summary>【A04】回滚：清掉可能残留的半成品新目录 → 从备份恢复旧技能。
    /// 返回是否成功把旧内容恢复到 target（false = 旧内容仍完整躺在备份目录，需要人工处理）。</summary>
    private static bool Rollback(string target, string? backup)
    {
        TryDeleteDirectory(target);
        if (backup is null) return true;                   // 原本没有旧目录：无旧数据可丢
        try
        {
            Directory.Move(backup, target);
            return Directory.Exists(target);
        }
        catch { return false; }
    }

    /// <summary>
    /// 【A04】把已校验的克隆目录「就位」到 target。
    /// 同卷：直接 rename（原子）。跨卷：先在目标卷建临时目录完成全部复制，再 rename 提交——
    /// 复制中途失败时 target 尚未出现（旧技能在备份处，由调用方恢复），绝不留下半成品 target。
    /// </summary>
    private static void MoveOrCopyDirectory(string source, string dest)
    {
        if (!ForceStagedCopyForTest && CopyFaultInjector is null)
        {
            try
            {
                Directory.Move(source, dest);
                return;
            }
            catch (IOException) { /* 跨卷（或同卷异常）→ 走暂存复制提交 */ }
        }

        var incoming = dest + ".incoming-" + Guid.NewGuid().ToString("N")[..6];
        try
        {
            CopyDirectoryRecursive(source, incoming, new int[1]);
            Directory.Move(incoming, dest);                // 目标卷上复制完毕后才提交（rename 原子）
        }
        catch
        {
            TryDeleteDirectory(incoming);                  // 清理复制到一半的新目录
            throw;
        }

        // 源是临时克隆，删不掉不影响已提交结果（外层 finally 还会再清一次）
        TryDeleteDirectory(source);
    }

    /// <summary>递归复制目录；<paramref name="copied"/>[0] = 已复制文件数（供测试钩子定位注入点）。</summary>
    private static void CopyDirectoryRecursive(string source, string dest, int[] copied)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
        {
            var fault = CopyFaultInjector?.Invoke(copied[0]);
            if (fault is not null) throw fault;
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
            copied[0]++;
        }
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectoryRecursive(dir, Path.Combine(dest, Path.GetFileName(dir)), copied);
    }

    /// <summary>删除目录（不存在 / 失败均静默；null 安全）。</summary>
    private static void TryDeleteDirectory(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    // ─────────────────────── 扫描范围 / 来源台账 ───────────────────────

    /// <summary>【A04】扫描排除：备份（.old-*）、暂存提交（.incoming-*）、显式备份（.backup-*）
    /// ——旧实现遗留的 Skills 内备份目录必须不被当成有效技能加载。</summary>
    private static bool IsExcludedFromScan(string root, string filePath)
    {
        var rel = Path.GetRelativePath(root, filePath);
        foreach (var seg in rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            // 【A04 返修】实际生成名是 `<repo>.old-xxxx` / `<repo>.incoming-xxxx`（点段在名称中间），
            // 此前用 StartsWith(".old-") 检查永远不命中——改为 Contains 匹配同一条命名规则。
            if (seg.Contains(".old-", StringComparison.OrdinalIgnoreCase) ||
                seg.Contains(".backup-", StringComparison.OrdinalIgnoreCase) ||
                seg.Contains(".incoming-", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>技能文件的「来源」= 相对 Skills 根目录的第一段目录名（根目录下的文件归为空来源）。
    /// 同一来源重新导入时，该来源此前注册的条目会被更新/替换。</summary>
    private static string SourceKeyOf(string root, string filePath)
    {
        var rel = Path.GetRelativePath(root, filePath);
        var idx = rel.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        return idx < 0 ? "" : rel[..idx];
    }

    /// <summary>
    /// 【A04 统一语义】Id 当前是否可被 source 接纳：新 Id（可注册）或本来源旧条目（可替换）。
    /// 大小写不敏感——与正式加载（LoadAllDetailed 的 FindById 判重）完全一致。
    /// </summary>
    internal static bool CanAccept(string id, string source)
        => FindById(id) is null || IsOwnedBySource(source, id);

    /// <summary>【A04】判断某 Id 当前是否由指定来源（相对第一段目录名）拥有。</summary>
    private static bool IsOwnedBySource(string source, string id)
    {
        return _ownedBySource.TryGetValue(source, out var ids) &&
               ids.Contains(id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>来源台账：来源 → 该来源当前拥有的技能 Id（只记本加载器注册成功的条目）。</summary>
    private static readonly Dictionary<string, List<string>> _ownedBySource = new(StringComparer.OrdinalIgnoreCase);
    private static string? _ownershipRoot;

    /// <summary>根目录变化（测试换注入目录 / 应用换技能目录）时清空台账，避免跨根串账。</summary>
    private static void ResetOwnershipIfRootChanged(string root)
    {
        if (string.Equals(_ownershipRoot, root, StringComparison.OrdinalIgnoreCase)) return;
        _ownershipRoot = root;
        _ownedBySource.Clear();
    }

    /// <summary>取出并清空某来源的台账（返回其此前拥有的 Id 列表）。</summary>
    private static List<string> TakeOwned(string source)
    {
        if (_ownedBySource.TryGetValue(source, out var ids))
        {
            _ownedBySource.Remove(source);
            return ids;
        }
        return [];
    }

    private static void Own(string source, string id)
    {
        if (!_ownedBySource.TryGetValue(source, out var ids)) _ownedBySource[source] = ids = [];
        ids.Add(id);
    }

    /// <summary>【测试钩子】清空来源台账（不影响注册表本身）。</summary>
    internal static void ResetSourceOwnershipForTest()
    {
        _ownedBySource.Clear();
        _ownershipRoot = null;
    }

    // ─────────────────────── 注册表条目更新 ───────────────────────

    private static readonly FieldInfo? RegistrySkillsField =
        typeof(AgentSkillRegistry).GetField("_skills", BindingFlags.NonPublic | BindingFlags.Static);

    /// <summary>
    /// 【A04】从注册表移除一个 Id（同 Id 更新必须替换旧条目，否则内存里仍是旧 prompt）。
    /// AgentSkillRegistry 暂无移除 API，按注册表既有测试同款方式访问其私有列表；
    /// 反射不可用时静默降级为「不更新」（退回旧行为：重复 Id 跳过）。
    /// </summary>
    private static bool RemoveFromRegistry(string id)
    {
        if (RegistrySkillsField?.GetValue(null) is not List<AgentSkill> list) return false;
        var idx = list.FindIndex(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return false;
        list.RemoveAt(idx);
        return true;
    }

    private static AgentSkill? FindById(string id)
        => AgentSkillRegistry.All.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>内容是否完全一致（用于「重复扫描幂等」判定——一致就不动内存里的原实例）。</summary>
    private static bool SameContent(AgentSkill a, AgentSkill b)
        => a.DisplayName == b.DisplayName
           && a.Glyph == b.Glyph
           && a.Description == b.Description
           && a.SystemPromptFragment == b.SystemPromptFragment
           && (a.TriggerKeywords ?? []).SequenceEqual(b.TriggerKeywords ?? []);
}
