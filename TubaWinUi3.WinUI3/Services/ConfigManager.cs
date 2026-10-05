using System.IO.Compression;
using System.Text.Json;

namespace TubaWinUi3.Services;

public enum ConfigLocation
{
    AppData,
    AppRoot,
    Custom
}

public static class ConfigManager
{
    private static readonly object _lock = new();
    private static string? _cachedDataDir;
    private static ConfigLocation? _cachedLocation;
    /// <summary>两个缓存解析时对应的隔离根（EffectiveTestRoot）：隔离根变化自动失效，
    /// 杜绝旧根缓存被新根复用（2026-09-23 测试串扰事故根因之一）。</summary>
    private static string? _cacheRootTag;

    /// <summary>
    /// 生产 AppData 数据目录（= %LocalAppData%\TubaWinUi3）。
    /// 【GUI 隔离】隔离模式下经 <see cref="DataRoots.PickBaseDir"/> 整体改道隔离根
    /// （GetDataDir / GetConfigLocation / ResolveCustomDataDir / MigrateData 目标解析共用），
    /// 绝不作为真实 %LocalAppData% 的别名参与任何写操作。
    /// </summary>
    private static string AppDataDir => DataRoots.PickBaseDir(
        DataRoots.EffectiveTestRoot,
        RuntimeHelper.GetLocalAppDataRoot());

    private static readonly string AppRootDir = Path.Combine(
        ToolCatalog.AppDirectory, "Data");

    /// <summary>配置位置标记目录：生产 = 应用目录 Data；【GUI 隔离】隔离模式 = 隔离根（不写应用目录）。</summary>
    private static string LocationMarkerDir => DataRoots.EffectiveTestRoot ?? AppRootDir;

    private const string CustomLocationFile = ".config_location";
    private const string CustomPathPrefix = "Custom:";

    public static string GetDataDir()
    {
        lock (_lock)
        {
            InvalidateCachesIfRootChanged();
            if (_cachedDataDir is not null) return _cachedDataDir;

            if (DataRoots.EffectiveTestRoot is { } testRoot)
            {
                // 【GUI 隔离】隔离模式：位置标记（.config_location）只从隔离根读取
                // （LocationMarkerDir = 隔离根），属于"隔离态自我配置"——尊重它
                // （测试的 Custom 位置隔离 / 隔离实例自身的切换）；无标记时一律落隔离根。
                // 任何分支都绝不使用真实 %LOCALAPPDATA% 数据目录。
                var isolatedLocation = GetConfigLocation();
                _cachedDataDir = isolatedLocation switch
                {
                    ConfigLocation.AppRoot => AppRootDir,
                    ConfigLocation.Custom => ResolveCustomDataDir(),
                    _ => testRoot
                };
                return _cachedDataDir;
            }

            var location = GetConfigLocation();
            _cachedDataDir = location switch
            {
                ConfigLocation.AppRoot => AppRootDir,
                ConfigLocation.Custom => ResolveCustomDataDir(),
                _ => AppDataDir
            };
            return _cachedDataDir;
        }
    }

    /// <summary>隔离根（EffectiveTestRoot）变化时清空位置/数据根缓存；须在 _lock 内调用。</summary>
    private static void InvalidateCachesIfRootChanged()
    {
        if (string.Equals(_cacheRootTag, DataRoots.EffectiveTestRoot, StringComparison.OrdinalIgnoreCase)) return;
        _cachedDataDir = null;
        _cachedLocation = null;
        _cacheRootTag = DataRoots.EffectiveTestRoot;
    }

    public static ConfigLocation GetConfigLocation()
    {
        lock (_lock)
        {
            InvalidateCachesIfRootChanged();
            if (_cachedLocation is not null) return _cachedLocation.Value;

            try
            {
                var markerPath = Path.Combine(LocationMarkerDir, CustomLocationFile);
                if (File.Exists(markerPath))
                {
                    var content = File.ReadAllText(markerPath).Trim();
                    if (content.StartsWith(CustomPathPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        _cachedLocation = ConfigLocation.Custom;
                        return ConfigLocation.Custom;
                    }
                    if (content.Equals("AppRoot", StringComparison.OrdinalIgnoreCase))
                    {
                        _cachedLocation = ConfigLocation.AppRoot;
                        return ConfigLocation.AppRoot;
                    }
                }
            }
            catch { }

            _cachedLocation = ConfigLocation.AppData;
            return ConfigLocation.AppData;
        }
    }

    public static string? GetCustomPath()
    {
        try
        {
            var markerPath = Path.Combine(LocationMarkerDir, CustomLocationFile);
            if (File.Exists(markerPath))
            {
                var content = File.ReadAllText(markerPath).Trim();
                if (content.StartsWith(CustomPathPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return content[CustomPathPrefix.Length..];
                }
            }
        }
        catch { }
        return null;
    }

    private static string ResolveCustomDataDir()
    {
        var customPath = GetCustomPath();
        if (string.IsNullOrWhiteSpace(customPath)) return AppDataDir;
        var expanded = PathResolver.ExpandPath(customPath);
        if (Path.IsPathRooted(expanded)) return expanded;
        return Path.Combine(ToolCatalog.AppDirectory, expanded);
    }

    public static bool SetConfigLocation(ConfigLocation location, string? customPath = null)
    {
        try
        {
            Directory.CreateDirectory(LocationMarkerDir);
            var markerPath = Path.Combine(LocationMarkerDir, CustomLocationFile);

            if (location == ConfigLocation.Custom)
            {
                if (string.IsNullOrWhiteSpace(customPath)) return false;
                File.WriteAllText(markerPath, CustomPathPrefix + customPath.Trim());
            }
            else if (location == ConfigLocation.AppRoot)
            {
                File.WriteAllText(markerPath, "AppRoot");
            }
            else
            {
                if (File.Exists(markerPath)) File.Delete(markerPath);
            }

            lock (_lock)
            {
                _cachedDataDir = null;
                _cachedLocation = null;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string GetSettingsPath() => Path.Combine(GetDataDir(), "settings.json");
    public static string GetAiProvidersPath() => Path.Combine(GetDataDir(), "ai_providers.json");

    /// <summary>
    /// 【A13 审计修复】旧版配置文件的兼容迁移：仅在【目标缺失且源存在】时，
    /// 把旧 AppData 位置的文件复制到当前数据目录（保留源文件，绝不删除/覆盖）。
    /// 旧版即使使用 AppRoot/自定义数据目录，engine.json / project-profile.json /
    /// ai_providers.json 也固定写在 %LOCALAPPDATA%\TubaWinUi3——目录切换后旧档案需要可见。
    /// 目标已存在时一律不动（不覆盖新数据）。
    /// </summary>
    public static void MigrateLegacyFileIfMissing(string fileName)
    {
        // 【GUI 隔离】测试模式禁用旧数据迁移——空隔离目录绝不从真实 %LOCALAPPDATA% 复制
        // （否则会把真实 API Key 配置/mcp 队列等带进测试实例）。
        if (DataRoots.ShouldSkipLegacyMigration) return;

        MigrateFileIfMissing(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TubaWinUi3", fileName),
            Path.Combine(GetDataDir(), fileName));
    }

    /// <summary>
    /// 迁移核心（internal 供隔离测试）：仅当【目标缺失 且 源存在 且 路径不同】时复制。
    /// 返回是否执行了复制。（保留源文件，绝不覆盖已有目标。）
    /// </summary>
    internal static bool MigrateFileIfMissing(string sourcePath, string targetPath)
    {
        try
        {
            if (File.Exists(targetPath) || !File.Exists(sourcePath)) return false;
            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                return false;
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(sourcePath, targetPath);
            return true;
        }
        catch { return false; }
    }
    public static string GetFavoritesPath() => Path.Combine(GetDataDir(), "favorites.json");
    public static string GetLaunchHistoryPath() => Path.Combine(GetDataDir(), "launch_history.json");
    public static string GetPopupSettingsPath() => Path.Combine(GetDataDir(), "popup_settings.json");
    public static string GetSensorDumpPath() => Path.Combine(GetDataDir(), "sensor_dump.txt");
    public static string GetSkippedVersionPath() => Path.Combine(GetDataDir(), "skipped_version.txt");
    public static string GetIconCacheDir() => Path.Combine(GetDataDir(), "IconCache");
    public static string GetBackgroundsDir() => Path.Combine(GetDataDir(), "Backgrounds");
    public static string GetMetadataDir() => Path.Combine(GetDataDir(), "Metadata");
    public static string GetDownloadQueuePath() => Path.Combine(GetDataDir(), "download_queue.json");

    public static string GetDataSize()
    {
        try
        {
            var dir = GetDataDir();
            if (!Directory.Exists(dir)) return "0 B";
            long size = 0;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { size += new FileInfo(file).Length; } catch { }
            }
            if (size >= 1L << 30) return $"{(double)size / (1L << 30):F2} GB";
            if (size >= 1L << 20) return $"{(double)size / (1L << 20):F1} MB";
            if (size >= 1L << 10) return $"{(double)size / (1L << 10):F1} KB";
            return $"{size} B";
        }
        catch { return MiscTexts.T("未知"); }
    }

    /// <summary>
    /// 迁移目标解析（可测纯函数）。返回目标目录；null = 拒绝。
    /// 【GUI 隔离】隔离态规则：AppRoot 一律拒绝（应用目录不是隔离态合法数据落点）；
    /// 其余目标（含 Custom 展开结果）必须位于隔离根内，否则拒绝。生产态（testRoot=null）维持原语义。
    /// </summary>
    internal static string? ResolveMigrationTarget(
        ConfigLocation targetLocation, string? customPath, string? testRoot,
        string appDirectory, string appRootDir, string appDataDir)
    {
        string targetDir;
        if (targetLocation == ConfigLocation.Custom)
        {
            if (string.IsNullOrWhiteSpace(customPath)) return null;
            var expanded = PathResolver.ExpandPath(customPath);
            targetDir = Path.IsPathRooted(expanded) ? expanded : Path.Combine(appDirectory, expanded);
        }
        else
        {
            targetDir = targetLocation == ConfigLocation.AppRoot ? appRootDir : appDataDir;
        }

        if (string.IsNullOrWhiteSpace(targetDir)) return null;

        if (testRoot is not null)
        {
            if (targetLocation == ConfigLocation.AppRoot) return null;
            if (!IsUnderOrEqual(testRoot, targetDir)) return null;
        }
        return targetDir;
    }

    private static bool IsUnderOrEqual(string root, string path)
    {
        try
        {
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return p.Equals(r, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool MigrateData(ConfigLocation targetLocation, bool migrate, string? customPath = null)
    {
        var sourceDir = GetDataDir();
        var oldDataDir = sourceDir;

        // 【GUI 隔离 · fail-closed】目标的解析与校验必须发生在任何创建/复制/删除之前：隔离态下
        // 目标越出隔离根（真实 AppData / 应用目录 / 任意外部路径）→ 直接拒绝，零文件系统效果。
        var targetDir = ResolveMigrationTarget(
            targetLocation, customPath, DataRoots.EffectiveTestRoot,
            ToolCatalog.AppDirectory, AppRootDir, AppDataDir);
        if (targetDir is null) return false;

        if (string.Equals(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase)) return true;

        // 隔离态附加约束：禁止"把隔离根搬进自身子目录"（复制后删除源会把复制结果一并删除，自毁）。
        if (DataRoots.EffectiveTestRoot is not null && migrate && IsUnderOrEqual(sourceDir, targetDir))
            return false;

        try
        {
            if (migrate && Directory.Exists(sourceDir))
            {
                Directory.CreateDirectory(targetDir);

                var excludeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "IconCache", "Metadata" };
                var excludeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sensor_dump.txt" };

                foreach (var file in Directory.EnumerateFiles(sourceDir))
                {
                    var name = Path.GetFileName(file);
                    if (excludeFiles.Contains(name)) continue;
                    var dest = Path.Combine(targetDir, name);
                    File.Copy(file, dest, true);
                }

                foreach (var dir in Directory.EnumerateDirectories(sourceDir))
                {
                    var name = Path.GetFileName(dir);
                    if (excludeDirs.Contains(name)) continue;
                    var destDir = Path.Combine(targetDir, name);
                    CopyDirectory(dir, destDir);
                }

                try { Directory.Delete(sourceDir, true); } catch { }
            }

            if (!SetConfigLocation(targetLocation, customPath)) return false;

            if (migrate)
            {
                try { RewritePathsInDataDir(targetDir, oldDataDir); } catch { }
            }

            return true;
        }
        catch { return false; }
    }

    public static void RewritePathsInDataDir(string dataDir, string? oldDataDir = null)
    {
        oldDataDir ??= dataDir;

        try
        {
            var favoritesPath = Path.Combine(dataDir, "favorites.json");
            if (File.Exists(favoritesPath))
            {
                var json = File.ReadAllText(favoritesPath);
                var paths = JsonSerializer.Deserialize<List<string>>(json);
                if (paths is not null)
                {
                    var rewritten = paths.Select(p => PathResolver.MakeRelative(p)).ToList();
                    File.WriteAllText(favoritesPath, JsonSerializer.Serialize(rewritten));
                }
            }
        }
        catch { }

        try
        {
            var historyPath = Path.Combine(dataDir, "launch_history.json");
            if (File.Exists(historyPath))
            {
                var json = File.ReadAllText(historyPath);
                var records = JsonSerializer.Deserialize<List<LaunchRecord>>(json);
                if (records is not null)
                {
                    foreach (var r in records)
                    {
                        r.Path = PathResolver.MakeRelative(r.Path);
                    }
                    File.WriteAllText(historyPath, JsonSerializer.Serialize(records));
                }
            }
        }
        catch { }

        try
        {
            var settingsPath = Path.Combine(dataDir, "settings.json");
            if (File.Exists(settingsPath))
            {
                var json = File.ReadAllText(settingsPath);
                var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (settings is not null)
                {
                    var pathKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "BackgroundImagePath", "HttpDownloadPath"
                    };

                    var changed = false;
                    foreach (var key in pathKeys)
                    {
                        if (settings.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val))
                        {
                            var rewritten = PathResolver.MakeRelative(val);
                            if (rewritten != val)
                            {
                                settings[key] = rewritten;
                                changed = true;
                            }
                        }
                    }

                    if (changed)
                    {
                        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings));
                    }
                }
            }
        }
        catch { }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), true);
        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
    }

    public static async Task<bool> ExportConfigAsync(string outputPath)
    {
        try
        {
            var dataDir = GetDataDir();
            if (!Directory.Exists(dataDir)) return false;

            var excludeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "IconCache", "Metadata" };
            var excludeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sensor_dump.txt" };

            if (File.Exists(outputPath)) File.Delete(outputPath);

            await Task.Run(() =>
            {
                using var zip = ZipFile.Open(outputPath, ZipArchiveMode.Create);
                foreach (var file in Directory.EnumerateFiles(dataDir))
                {
                    var name = Path.GetFileName(file);
                    if (excludeFiles.Contains(name)) continue;
                    zip.CreateEntryFromFile(file, name, CompressionLevel.Optimal);
                }
                foreach (var dir in Directory.EnumerateDirectories(dataDir))
                {
                    var dirName = Path.GetFileName(dir);
                    if (excludeDirs.Contains(dirName)) continue;
                    foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        var relative = file.Substring(dataDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        zip.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
                    }
                }
            });

            return true;
        }
        catch { return false; }
    }

    public static async Task<bool> ImportConfigAsync(string zipPath)
    {
        try
        {
            var dataDir = GetDataDir();
            // 【安全·先验后建】数据根自身的创建推迟到全部 entry 通过验证之后（见下方 plans 循环后）：
            // 隔离数据根起初不存在时，被拒绝的恶意包不得留下任何目录（2026-09-23 复核退回第 4 项）。
            var rootFull = Path.GetFullPath(dataDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // 【安全·两阶段导入】先验证全部 entry（rooted / 盘符 / ".." 段 / 规范化后的严格根边界 /
            // 已存在组件的重解析点），全部通过后才执行任何创建、覆盖或解压——绝不允许写到一半
            // 才发现恶意条目。此校验对隔离导入与生产导入同样生效（旧实现的越界写入是安全漏洞）。
            var ok = await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(zipPath);
                var plans = new List<(string DestPath, bool IsDirectory, ZipArchiveEntry Entry)>();
                foreach (var entry in archive.Entries)
                {
                    var name = entry.FullName;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var relative = name.Replace('/', Path.DirectorySeparatorChar);
                    if (Path.IsPathRooted(relative) || relative.Contains(':'))
                        return false;

                    var rawSegments = relative.Split(
                        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                        StringSplitOptions.RemoveEmptyEntries);
                    if (rawSegments.Any(segment => segment == ".."))
                        return false;

                    string destFull;
                    try { destFull = Path.GetFullPath(Path.Combine(rootFull, relative)); }
                    catch { return false; }

                    if (!destFull.Equals(rootFull, StringComparison.OrdinalIgnoreCase) &&
                        !destFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        return false;

                    if (PathChainHasReparsePoint(rootFull, destFull))
                        return false;

                    plans.Add((destFull, string.IsNullOrEmpty(entry.Name), entry));
                }

                // 【安全·先验后建】全部条目验证通过后才执行第一次写入——包括数据根自身：
                // 「根起初不存在 + 合法条目在前 + 恶意条目在后」必须整包拒绝且不留下任何目录。
                Directory.CreateDirectory(rootFull);

                foreach (var plan in plans)
                {
                    if (plan.IsDirectory)
                    {
                        Directory.CreateDirectory(plan.DestPath);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(plan.DestPath)!);
                    try { if (File.Exists(plan.DestPath)) File.Delete(plan.DestPath); } catch { }
                    plan.Entry.ExtractToFile(plan.DestPath);
                }
                return true;
            });

            if (!ok) return false;

            InvalidateAllCaches();
            return true;
        }
        catch { return false; }
    }

    /// <summary>【安全】导入目标路径链（root 之下已存在的组件）不得经过 junction/symlink；无法判定视为不安全。</summary>
    private static bool PathChainHasReparsePoint(string rootFull, string destFull)
    {
        try
        {
            var relative = Path.GetRelativePath(rootFull, destFull);
            if (relative.Length == 0 || relative == ".") return false;
            var acc = rootFull;
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (segment.Length == 0 || segment == ".") continue;
                acc = Path.Combine(acc, segment);
                try
                {
                    var attrs = File.GetAttributes(acc);
                    if ((attrs & FileAttributes.ReparsePoint) != 0) return true;
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            return false;
        }
        catch { return true; }
    }

    public static void InvalidateAllCaches()
    {
        AppSettings.InvalidateCache();
        FavoritesService.InvalidateCache();
        LaunchHistoryService.InvalidateCache();
        ToolCatalog.OnToolsChanged();
    }

    private const int CurrentPathMigrationVersion = 1;

    public static void AutoMigratePathsIfNeeded()
    {
        try
        {
            var dataDir = GetDataDir();
            if (!Directory.Exists(dataDir)) return;

            var markerPath = Path.Combine(dataDir, ".path_migration_done");
            if (File.Exists(markerPath)) return;

            RewritePathsInDataDir(dataDir);

            try
            {
                File.WriteAllText(markerPath, CurrentPathMigrationVersion.ToString());
            }
            catch { }

            AppSettings.InvalidateCache();
            FavoritesService.InvalidateCache();
            LaunchHistoryService.InvalidateCache();
        }
        catch { }
    }
}
