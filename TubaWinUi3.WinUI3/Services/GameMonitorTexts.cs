using System.Collections.Generic;

namespace TubaWinUi3.Services;

/// <summary>
/// 游戏监控/覆盖层块显示层本地化。zh 串为业务数据键（指标名/预设名/状态等），
/// 显示时经 T/TSub 转换；数据、窗口标题匹配、导出格式判定不经过此表。
/// </summary>
public static class GameMonitorTexts
{
    private static readonly Dictionary<string, string> EnMap = new()
    {
        ["记录中… 已采样 {_session.SampleCount} 条 ｜ 已用 {FormatClock(elapsed)} ｜ 剩余 {FormatClock(remaining)}"] = "Recording… {_session.SampleCount} samples | elapsed {FormatClock(elapsed)} | left {FormatClock(remaining)}",
        ["记录时长：{GameMonitorRecorder.FormatDuration(meta.DurationSeconds)}"] = "Duration: {GameMonitorRecorder.FormatDuration(meta.DurationSeconds)}",
        ["记录已达 {GameMonitorRecorder.MaxDurationMinutes} 分钟上限，已自动停止并保存："] = "Recording reached the {GameMonitorRecorder.MaxDurationMinutes} min limit; stopped and saved:",
        ["已达 {GameMonitorRecorder.MaxDurationMinutes} 分钟上限，已自动停止并保存："] = "Reached the {GameMonitorRecorder.MaxDurationMinutes} min limit; stopped and saved:",
        ["应用预设「{preset.Name}」将替换当前画布上的 {_widgets.Count} 个组件，确定继续吗？"] = "Applying preset “{preset.Name}” will replace {_widgets.Count} widgets on the canvas. Continue?",
        ["已选择: Windows 桌面 — 覆盖层固定于屏幕设置位置，FPS 显示当前活动窗口"] = "Selected: Windows desktop — the overlay stays at the position you set; FPS shows the active window",
        ["开启后，即使图吧工具箱没有运行，后台服务也会在检测到全屏/无边框游戏时，"] = "Once enabled, even when the toolbox is not running, the background service detects fullscreen/borderless games and",
        ["已扫描到 {_gameWindows.Count} 个窗口，以及桌面目标"] = "Found {_gameWindows.Count} windows plus the desktop target",
        ["已选 {GetSelectedRecordKeys().Count} 项"] = "{GetSelectedRecordKeys().Count} selected",
        ["自动在游戏窗口上显示 FPS、1% Low、帧生成时间等读数。\n\n"] = "Shows FPS, 1% Low, frame times and more on top of your game.\n\n",
        ["该功能与「流氓软件拦截」共用同一个轻量后台服务，随时可以在本页面开关。"] = "Shares the same lightweight background service with Rogue App Blocking; toggle it on this page anytime.",
        ["已整体缩放到 {newPercent:F0}%，重新启动覆盖层即可生效"] = "Scaled to {newPercent:F0}%; restart the overlay to apply",
        ["已应用预设「{preset.Name}」，重新启动覆盖层即可生效"] = "Preset “{preset.Name}” applied; restart the overlay to apply",
        ["确定删除预设「{preset.Name}」吗？此操作不可恢复。"] = "Delete preset “{preset.Name}”? This cannot be undone.",
        ["图层 {_selectedWidget.Layer}"] = "Layer {_selectedWidget.Layer}",
        ["🖥️ Windows 桌面（FPS 跟随活动窗口）"] = "🖥️ Windows desktop (FPS follows the active window)",
        ["将保存画布尺寸与全部组件布局，同名预设会被覆盖："] = "Saves the canvas size and all widget layouts; a preset with the same name will be overwritten:",
        ["已整体缩放到 {newPercent:F0}%"] = "Scaled to {newPercent:F0}%",
        ["已选择: {info.ProcessName}"] = "Selected: {info.ProcessName}",
        ["选择输出目录失败: {ex.Message}"] = "Failed to choose output folder: {ex.Message}",
        ["打开目录失败: {ex.Message}"] = "Failed to open folder: {ex.Message}",
        ["采样点数：{samples.Count}"] = "Samples: {samples.Count}",
        ["预设加载失败: {ex.Message}"] = "Failed to load presets: {ex.Message}",
        ["保存预设失败: {ex.Message}"] = "Failed to save preset: {ex.Message}",
        ["已删除预设「{preset.Name}」"] = "Preset “{preset.Name}” deleted",
        ["组件: {_widgets.Count}"] = "Widgets: {_widgets.Count}",
        ["开头 {headTrim:0.#} 秒"] = "First {headTrim:0.#} s",
        ["结尾 {tailTrim:0.#} 秒"] = "Last {tailTrim:0.#} s",
        ["写入失败: {ex.Message}"] = "Write failed: {ex.Message}",
        ["图层 {widget.Layer}"] = "Layer {widget.Layer}",
        ["输入游戏窗口标题（支持部分匹配）:"] = "Enter a game window title (partial match supported):",
        ["输出目录还不存在，先完成一次记录"] = "The output folder does not exist yet; finish one recording first",
        ["记录进行中，无法修改输出目录"] = "Cannot change the output folder while recording",
        ["请至少勾选一项要记录的指标"] = "Select at least one metric to record",
        ["没有采集到数据，未生成文件"] = "No data was captured; no file was generated",
        ["已保存预设「{name}」"] = "Preset “{name}” saved",
        ["输入窗口标题关键字..."] = "Enter a window title keyword...",
        ["开启游戏后台自动监控？"] = "Enable background game auto-monitoring?",
        ["0.1% Low 图表"] = "0.1% Low chart",
        ["至少要选择一种导出格式"] = "Select at least one export format",
        ["游戏监控 · 记录查看"] = "Game Monitor · Record Viewer",
        ["请至少选择一种导出格式"] = "Please select at least one export format",
        ["画布为空，无法保存预设"] = "The canvas is empty; cannot save a preset",
        ["Windows 桌面"] = "Windows desktop",
        ["请先拖入至少一个组件"] = "Drag at least one widget onto the canvas first",
        ["未运行（后端缺失）"] = "Not running (backend missing)",
        ["1% Low 图表"] = "1% Low chart",
        ["添加自定义游戏窗口"] = "Add custom game window",
        ["自定义预设 {i}"] = "Custom preset {i}",
        ["输入预设名称..."] = "Enter a preset name...",
        ["保存当前布局为预设"] = "Save current layout as preset",
        ["CPU温度 图表"] = "CPU temp chart",
        ["FPS 直播监控"] = "FPS live streaming",
        ["渲染延迟 图表"] = "Render latency chart",
        ["正在写入文件…"] = "Writing file…",
        ["停止记录并保存"] = "Stop and save",
        ["CPU 温度"] = "CPU Temp",
        ["CPU 负载"] = "CPU Load",
        ["CPU 频率"] = "CPU Clock",
        ["CPU 功耗"] = "CPU Power",
        ["CPU 名称"] = "CPU Name",
        ["GPU 温度"] = "GPU Temp",
        ["GPU 负载"] = "GPU Load",
        ["GPU 频率"] = "GPU Clock",
        ["GPU 功耗"] = "GPU Power",
        ["GPU 名称"] = "GPU Name",
        ["FPS 图表"] = "FPS chart",
        ["帧时间 图表"] = "Frame time chart",
        ["选择色块颜色"] = "Pick color-block color",
        ["选择文字颜色"] = "Pick text color",
        ["覆盖层运行中"] = "Overlay running",
        ["记录已保存："] = "Recording saved: ",
        ["数据记录完成"] = "Recording complete",
        ["极简 FPS"] = "Minimal FPS",
        ["CPU 专项"] = "CPU focus",
        ["GPU 专项"] = "GPU focus",
        ["自定义文字"] = "Custom text",
        ["自定义图片"] = "Custom image",
        ["自定义色块"] = "Custom color block",
        ["未选择图片"] = "No image selected",
        ["(自定义)"] = "(Custom)",
        ["停止覆盖层"] = "Stop overlay",
        ["启动覆盖层"] = "Start overlay",
        ["输出目录："] = "Output folder: ",
        ["打开文件夹"] = "Open folder",
        ["网络与磁盘"] = "Network & disk",
        ["内置 · "] = "Built-in · ",
        ["显存使用"] = "VRAM usage",
        ["内存负载"] = "Memory load",
        ["内存使用"] = "Memory used",
        ["磁盘读取"] = "Disk read",
        ["磁盘写入"] = "Disk write",
        ["网络上传"] = "Upload",
        ["网络下载"] = "Download",
        ["渲染延迟"] = "Render latency",
        ["放置组件"] = "Place widget",
        ["(桌面)"] = "(Desktop)",
        ["记录设置"] = "Recording settings",
        ["已保存："] = "Saved: ",
        ["开始记录"] = "Start recording",
        ["标准监控"] = "Standard monitor",
        ["性能全景"] = "Performance panorama",
        ["电竞对战"] = "Esports mode",
        ["内存专项"] = "Memory focus",
        ["应用预设"] = "Apply preset",
        ["删除预设"] = "Delete preset",
        ["运行中"] = "Running",
        ["帧时间"] = "Frame time",
        ["透明黑"] = "Transparent black",
        ["已停止"] = "Stopped",
        ["未开始"] = "Not started",
        ["全功能"] = "Full featured",
        ["双图表"] = "Dual charts",
        ["开启"] = "On",
        ["暂不"] = "Not now",
        ["蓝色"] = "Blue",
        ["青色"] = "Cyan",
        ["绿色"] = "Green",
        ["橙色"] = "Orange",
        ["红色"] = "Red",
        ["黄色"] = "Yellow",
        ["紫色"] = "Purple",
        ["粉色"] = "Pink",
        ["白色"] = "White",
        ["灰色"] = "Gray",
        ["黑色"] = "Black",
        ["取消"] = "Cancel",
        ["添加"] = "Add",
        ["完成"] = "Done",
        ["确定"] = "OK",
        ["应用"] = "Apply",
        ["保存"] = "Save",
        ["删除"] = "Delete",
        ["CPU 温度: "] = "CPU Temp: ",
        ["CPU 负载: "] = "CPU Load: ",
        ["CPU 频率: "] = "CPU Clock: ",
        ["CPU 功耗: "] = "CPU Power: ",
        ["GPU 温度: "] = "GPU Temp: ",
        ["GPU 负载: "] = "GPU Load: ",
        ["GPU 频率: "] = "GPU Clock: ",
        ["GPU 功耗: "] = "GPU Power: ",
        ["渲染延迟 ms"] = "Render latency ms",
        ["帧时间 ms"] = "Frame time ms",
        ["渲染延迟: "] = "Render latency: ",
        ["内存负载: "] = "Memory load: ",
        ["内存使用: "] = "Memory used: ",
        ["磁盘读取: "] = "Disk read: ",
        ["磁盘写入: "] = "Disk write: ",
        ["网络上传: "] = "Upload: ",
        ["网络下载: "] = "Download: ",
        ["帧时间: "] = "Frame time: ",
        ["显存: "] = "VRAM: ",
        ["最大 {mv.Max.Trim()}{unit} · 平均 {mv.Avg.Trim()}{unit} · 最小 {mv.Min.Trim()}{unit}"] = "Max {mv.Max.Trim()}{unit} · Avg {mv.Avg.Trim()}{unit} · Min {mv.Min.Trim()}{unit}",
        ["{chartCount} 张图表 · 每个指标独立纵轴量程 · {_visibleTimes.Count:N0} 个点 · 点击标签增减"] = "{chartCount} charts · independent Y-axis per metric · {_visibleTimes.Count:N0} points · click labels to add/remove",
        [" · P1 {mv.P1.Trim()} · P99 {mv.P99.Trim()} · {mv.Count:N0} 样本"] = " · P1 {mv.P1.Trim()} · P99 {mv.P99.Trim()} · {mv.Count:N0} samples",
        ["还没有记录文件。\n先在「游戏监控」页勾选指标并录制一段，保存后回到这里即可查看。"] = "No recording files yet.\nRecord a session from the Game Monitor page, then come back to view it.",
        ["点击下方指标标签添加图表（一个指标一张图，最多同时 {MaxCharts} 个）"] = "Click a metric label below to add a chart (one chart per metric, up to {MaxCharts} at a time)",
        ["开头 {_view.HeadTrimmedSeconds:0.#} s"] = "First {_view.HeadTrimmedSeconds:0.#} s",
        ["结尾 {_view.TailTrimmedSeconds:0.#} s"] = "Last {_view.TailTrimmedSeconds:0.#} s",
        ["最多同时显示 {MaxCharts} 个指标，请先取消一个再添加"] = "Up to {MaxCharts} metrics at a time; remove one before adding another",
        ["最大 {headline.Max.Trim()}{unit}"] = "Max {headline.Max.Trim()}{unit}",
        ["正在解析 {Path.GetFileName(path)}"] = "Parsing {Path.GetFileName(path)}",
        ["共 {_fileItems.Count} 个记录文件"] = "{_fileItems.Count} recording files",
        ["未选择指标。\n点击上方指标标签即可添加对应的图表。"] = "No metric selected.\nClick a metric label above to add its chart.",
        ["已加载 {view.Data.FileName}"] = "Loaded {view.Data.FileName}",
        ["（已达 {MaxCharts} 张上限）"] = "(limit of {MaxCharts} reached)",
        ["打开目录失败：{ex.Message}"] = "Failed to open folder: {ex.Message}",
        ["解析失败：{ex.Message}"] = "Parse failed: {ex.Message}",
        ["抽稀步长 {_view.Step}"] = "Downsample step {_view.Step}",
        ["达到 2 小时上限，已自动截断"] = "2-hour limit reached; automatically truncated",
        ["这份记录里没有可展示的指标"] = "This recording has no displayable metrics",
        ["该指标在这份记录里没有数据"] = "No data for this metric in this recording",
        ["输出目录下还没有记录文件"] = "No recording files in the output folder",
        ["当前分组没有可展示的指标"] = "The current group has no displayable metrics",
        ["没有可展示的数据"] = "No data to display",
        ["点击移除这张图表"] = "Click to remove this chart",
        ["点击添加这张图表"] = "Click to add this chart",
        ["未找到记录文件"] = "Recording file not found",
        ["已剔除无效数据"] = "Invalid data removed",
        ["FPS 进程"] = "FPS process",
        ["正在解析…"] = "Parsing…",
        ["解析失败"] = "Parse failed",
        ["目标窗口"] = "Target window",
        ["记录时长"] = "Duration",
        ["采样间隔"] = "Sample interval",
        ["本组指标"] = "Group metrics",
        ["全部指标"] = "All metrics",
        ["采样点"] = "Samples",
        ["全部"] = "All",
        ["开始"] = "Start",
        ["时长"] = "Duration",
        ["间隔"] = "Interval",
        ["样本"] = "Samples",
        ["指标"] = "Metric",
        ["格式"] = "Format",
        ["备注"] = "Notes",
        ["图表"] = "Chart",
        ["内存"] = "Memory",
        ["磁盘"] = "Disk",
        ["网络"] = "Network",
        ["电池"] = "Battery",
        ["FPS：平均 "] = "FPS: avg ",
        [" / 最低 "] = " / min ",
        ["记录已达 "] = "Recording reached ",
        ["已达 "] = "Reached ",
        [" 分钟上限，已自动停止并保存："] = " min limit; stopped and saved:",
        ["（已剔除"] = " (excluded ",
        ["的无效数据）"] = " invalid data)",
        ["FPS"] = "FPS",
        ["开启后悬浮窗每 45 秒沿小圆轨迹缓慢漂移几像素，避免监控内容长时间静止灼烧 OLED 屏幕；关闭后立即回到原位"] = "When on, the overlay drifts a few pixels along a small circular path every 45 seconds so static content doesn't burn into an OLED screen; turning it off returns it to place immediately",
        ["、"] = ", ",
        ["{(int)ts.TotalHours} 小时 {ts.Minutes} 分 {ts.Seconds} 秒"] = "{(int)ts.TotalHours} h {ts.Minutes} min {ts.Seconds} s",
        ["{ts.Minutes} 分 {ts.Seconds} 秒"] = "{ts.Minutes} min {ts.Seconds} s",
        ["磁盘温度"] = "Disk temp",
        ["电池电量"] = "Battery level",
        ["电池功率"] = "Battery power",
    };

    /// <summary>整串翻译。</summary>
    public static string T(string zh)
    {
        if (LocalizationService.CurrentLanguage != "en-US") return zh;
        return EnMap.TryGetValue(zh, out var en) ? en : zh;
    }

    /// <summary>标签（单位）显示：en 半角括号。</summary>
    public static string TPair(string label, string unit)
    {
        var l = T(label);
        return LocalizationService.CurrentLanguage == "en-US" ? l + " (" + T(unit) + ")" : l + "（" + unit + "）";
    }

    /// <summary>运行时拼接文本：模板键正则捕获；普通键长词优先子串替换。</summary>
    public static string TSub(string text)
    {
        if (LocalizationService.CurrentLanguage != "en-US" || string.IsNullOrEmpty(text)) return text;
        if (_templates.TryTranslate(text, out var translated)) return translated;
        // 含路径分隔符或换行的文本（输出路径/文件名/外部消息）不做自由子串替换，避免改写用户数据。
        if (!text.Contains('\\') && !text.Contains('\r') && !text.Contains('\n'))
        {
            foreach (var kv in _plain)
            {
                if (text.Contains(kv.Key, System.StringComparison.Ordinal))
                    text = text.Replace(kv.Key, kv.Value, System.StringComparison.Ordinal);
            }
        }
        return text;
    }

    private static readonly DisplayTemplateTranslator _templates = new(EnMap);

    private static readonly System.Collections.Generic.KeyValuePair<string, string>[] _plain =
        System.Linq.Enumerable.ToArray(
            System.Linq.Enumerable.OrderByDescending(
                System.Linq.Enumerable.Where(EnMap, kv => !kv.Key.Contains('{')), kv => kv.Key.Length));

}
