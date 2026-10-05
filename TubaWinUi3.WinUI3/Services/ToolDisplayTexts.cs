using System.Collections.Generic;

namespace TubaWinUi3.Services;

/// <summary>
/// 随包策展文本的显示层翻译（tools.json 说明 / 内置工具名称与描述 / 内置工具类型）。
/// 数据键保持原始（中文）不变；仅用于显示。原始路径、ID、分类、搜索匹配与用户自带说明不经过此处。
/// </summary>
public static class ToolDisplayTexts
{
    /// <summary>tools.json 说明（中文原文，唯一）→ 资源键 ToolDesc_&lt;match&gt;。</summary>
    private static readonly Dictionary<string, string> KeyByDescription = new(StringComparer.Ordinal)
    {
        ["处理器、主板、内存和显卡基础信息查看工具。"] = "ToolDesc_CPU-Z",
        ["CPU 温度实时监控工具，支持每个核心独立温度显示和系统托盘通知。"] = "ToolDesc_CoreTemp",
        ["CPU 缓存到缓存延迟测试工具，测量 L1/L2/L3 缓存间通信延迟。"] = "ToolDesc_C2CLatency",
        ["基于 Intel LINPACK 的 CPU 稳定性测试工具，用于极限烤机和超频验证。"] = "ToolDesc_LinX",
        ["GIMPS 项目分布式计算客户端，广泛用于 CPU 稳定性测试和烤机。"] = "ToolDesc_Prime95",
        ["CPU 圆周率计算性能测试工具，计算 π 的指定位数衡量 CPU 单核性能。"] = "ToolDesc_SuperPI",
        ["CPU 降频监控和功耗控制工具，可解除笔记本 CPU 功耗限制。"] = "ToolDesc_ThrottleStop",
        ["多线程 CPU 性能测试工具，通过计算质数衡量多核计算能力。"] = "ToolDesc_wPrime",
        ["Fritz 国际象棋基准测试，通过象棋算法评估 CPU 多核运算性能。"] = "ToolDesc_xiangqi",
        ["显卡型号、核心参数、显存、传感器和 BIOS 信息查看工具。"] = "ToolDesc_GPU-Z",
        ["显卡驱动彻底卸载工具，安全清除 AMD/NVIDIA/Intel 显卡驱动残留。"] = "ToolDesc_DDU",
        ["DirectX 视频加速（DXVA）硬件解码能力检测工具。"] = "ToolDesc_DXVAChecker",
        ["跨平台 GPU 基准测试工具，支持 OpenGL 压力测试和性能评分。"] = "ToolDesc_GpuTest",
        ["NVIDIA 显卡信息查看和超频工具，可调节电压、频率和风扇曲线。"] = "ToolDesc_nvidiaInspector",
        ["NVIDIA 显卡驱动配置文件编辑器，可修改隐藏驱动设置和 SLI 配置。"] = "ToolDesc_nvidiaProfileInspector",
        ["AMD 显卡驱动程序官方下载入口。"] = "ToolDesc_AMD显卡驱动",
        ["NVIDIA 显卡驱动程序官方下载入口。"] = "ToolDesc_Nvidia显卡驱动",
        ["FurMark 2（64 位）GPU 压力测试和烤机工具，支持 Vulkan 和 OpenGL，用于检测显卡稳定性。"] = "ToolDesc_FurMark_win64",
        ["硬盘 SMART、健康状态、温度和通电时间查看工具。"] = "ToolDesc_CrystalDiskInfo",
        ["硬盘读写速度基准测试工具，测量顺序和随机读写性能。"] = "ToolDesc_CrystalDiskMark",
        ["SSD 专用基准测试工具，测量顺序/随机读写速度和访问时间。"] = "ToolDesc_AS SSD",
        ["ATTO 磁盘基准测试工具，测量不同块大小下的磁盘读写速度。"] = "ToolDesc_ATTO",
        ["磁盘分区、数据恢复、坏道检测和分区表维护工具。数据恢复功能对固态硬盘（SSD）成功率通常较低。"] = "ToolDesc_DiskGenius",
        ["磁盘碎片整理工具，支持按文件/文件夹级别整理碎片。"] = "ToolDesc_Defraggler",
        ["U盘/存储卡容量真实性检测工具，鉴别扩容盘和假容量存储设备。"] = "ToolDesc_H2testw",
        ["低级格式化工具，对磁盘进行彻底的底层擦除和格式化。"] = "ToolDesc_LLFTOOL",
        ["U盘/存储卡扩容检测和速度测试工具，可识别虚假容量存储设备。"] = "ToolDesc_MyDiskTest",
        ["磁盘空间可视化分析工具，以树状图直观展示文件夹占用空间。"] = "ToolDesc_SpaceSniffer",
        ["SSD 固态硬盘在线工具入口，提供 SSD 相关在线检测和优化。"] = "ToolDesc_SSD utils",
        ["SSD 固态硬盘信息查看和健康检测工具。"] = "ToolDesc_SSDZ",
        ["SSD 存储设备基准测试工具，支持多种读写模式性能评估。"] = "ToolDesc_TxBENCH",
        ["U盘读写速度测试工具，检测 U盘实际读写性能。"] = "ToolDesc_URWTEST",
        ["磁盘空间使用统计工具，以彩色方块图展示文件类型和空间占用。"] = "ToolDesc_WinDirStat",
        ["超快磁盘空间分析工具，使用 MFT 快速扫描 NTFS 分区文件占用。"] = "ToolDesc_WizTree",
        ["U盘量产工具，用于 U盘芯片检测和量产修复。"] = "ToolDesc_FlashMaster",
        ["文件数据恢复工具，支持误删除、格式化和分区丢失的数据恢复。注意：固态硬盘（SSD）因 TRIM 自动清理数据，恢复成功率通常较低。"] = "ToolDesc_魔方数据恢复",
        ["内存稳定性测试工具，通过反复读写检测内存错误。"] = "ToolDesc_memtest",
        ["64 位内存稳定性测试工具，支持大容量内存的错误检测。"] = "ToolDesc_MemTest64",
        ["专业版内存测试工具，支持多线程和更全面的内存错误检测。"] = "ToolDesc_memtestpro",
        ["内存 SPD 信息读取工具，查看内存条制造商、时序、频率等详细参数。"] = "ToolDesc_Thaiphoon",
        ["第五代内存稳定性测试工具（TM5），支持多种测试配置和极限压力测试。"] = "ToolDesc_TestMem5",
        ["虚拟内存盘（RAM Disk）创建工具，将部分内存虚拟为高速磁盘。"] = "ToolDesc_魔方内存盘",
        ["AMD Zen 架构内存时序和频率实时查看工具，支持 DDR4/DDR5 时序、子时序和电压监控。"] = "ToolDesc_ZenTimings",
        ["显示器色域检测工具，查看显示器色域覆盖率和面板信息。"] = "ToolDesc_monitorinfo",
        ["UFO Test 在线显示器刷新率和运动模糊测试工具。"] = "ToolDesc_UFO",
        ["微软官方 HDR 校准工具，用于校准显示器 HDR 亮度和色彩表现，通过 Microsoft Store 安装。"] = "ToolDesc_Windows HDR Calibration",
        ["鼠标按键和滚轮测试工具，检测鼠标各按键是否正常响应。"] = "ToolDesc_AresonMouseTest",
        ["键盘按键测试工具，逐键检测键盘每个按键是否正常工作。"] = "ToolDesc_Keyboard Test Utility",
        ["键盘按键重映射工具，可自定义修改键盘按键映射关系。"] = "ToolDesc_KeyTweak",
        ["鼠标回报率检测工具，实时测量鼠标 USB 报告速率（Hz）。"] = "ToolDesc_MOUSERATE",
        ["鼠标性能测试工具，检测鼠标移动轨迹、抖动和按键延迟。"] = "ToolDesc_MouseTester",
        ["鼠标微动故障检测工具，识别鼠标单击变双击的微动老化问题。"] = "ToolDesc_鼠标单击变双击",
        ["在线外设综合测试中心，通过浏览器测试鼠标、键盘和显示器。"] = "ToolDesc_在线外设测试",
        ["系统硬件信息、传感器监控和稳定性测试工具。"] = "ToolDesc_AIDA64",
        ["专业硬件信息读取、传感器监控和日志记录工具。"] = "ToolDesc_HWiNFO",
        ["硬件温度、电压和风扇转速监控工具，实时显示传感器数据。"] = "ToolDesc_HWMonitor",
        ["硬件寄存器读写工具，可访问 PCI、SMBus、Super I/O 等底层硬件信息。"] = "ToolDesc_RWEverything",
        ["系统硬件信息快速查看工具，提供简洁的硬件配置摘要。"] = "ToolDesc_Speccy",
        ["系统实时音频延迟检测工具，分析 DPC/ISR 延迟和硬页面错误，排查音频卡顿和爆音。"] = "ToolDesc_LatencyMon",
        ["笔记本电池容量、循环、损耗和实时状态查看工具。"] = "ToolDesc_BatteryInfoView",
        ["蓝屏崩溃转储分析工具，查看 BSOD 错误代码和导致崩溃的驱动。"] = "ToolDesc_BlueScreenView",
        ["桌面图标位置保存和恢复工具，防止分辨率变化后图标排列混乱。"] = "ToolDesc_DesktopOK",
        ["DirectX 修复工具，自动检测和修复 DirectX 组件缺失或损坏问题。"] = "ToolDesc_DirectX Repair",
        ["Windows 系统精简和优化工具，基于 DISM 的图形化系统管理工具。"] = "ToolDesc_Dism++",
        ["超快文件搜索工具，基于 NTFS USN 日志实现瞬间文件名搜索。"] = "ToolDesc_Everything",
        ["强大的软件卸载工具，支持强制卸载、批量卸载和清理残留注册表。"] = "ToolDesc_HiBit Uninstaller",
        ["GIF 动画录制工具，通过窗口框选区域直接录制 GIF 图片。"] = "ToolDesc_GifCam",
        ["MSI Afterburner 显卡超频监控工具下载入口，支持所有品牌显卡。"] = "ToolDesc_MSIAfterburner",
        ["MSDN I Tell You 在线系统镜像下载入口，提供微软原版系统镜像。"] = "ToolDesc_next_itellyou",
        ["高级进程管理工具，以树状结构显示进程关系和详细系统资源占用。"] = "ToolDesc_Process Explorer",
        ["U盘启动盘制作工具，快速创建可引导 USB 安装盘。"] = "ToolDesc_Rufus",
        ["多系统 U盘启动制作工具，直接拷贝 ISO 文件即可启动，无需反复格式化。"] = "ToolDesc_Ventoy",
        ["微软官方内核级调试器，用于驱动和系统级问题的深度调试分析。"] = "ToolDesc_WinDbg",
        ["Intel Flash Programming Tool 64 位版，用于刷写和备份 Intel 主板 BIOS/ME 固件。"] = "ToolDesc_fptw64",
        ["开源风扇曲线控制工具，支持自定义风扇转速策略和多风扇联动控制。"] = "ToolDesc_FanControl",
        ["微软官方系统增强工具集，包含窗口管理、颜色拾取、批量重命名、快捷键指南等实用功能。"] = "ToolDesc_PowerToys",
        ["USB 设备历史记录查看和管理工具，可查看所有曾连接的 USB 设备详情和供电信息。"] = "ToolDesc_USBDeview",
        ["USB 控制器拓扑结构查看工具，以树状图展示 USB 控制器、集线器和设备层级关系。"] = "ToolDesc_USBTreeView",
        ["物理内存分配详细查看工具，以多种视图展示内存使用、缓存和进程占用。"] = "ToolDesc_RAMMap",
        ["开机启动项和自注册管理工具，可管理所有自启动位置包括驱动、服务和计划任务。"] = "ToolDesc_Autoruns",
        ["实时进程活动监控工具，监控文件、注册表、网络和进程/线程活动，排查系统问题利器。"] = "ToolDesc_Procmon",
        ["磁盘引导扇区管理工具，支持 MBR/GPT 分区表编辑、引导记录备份恢复和 U盘启动制作。"] = "ToolDesc_BOOTICE",
        ["已安装程序移动工具，可将已安装的软件从 C 盘移动到其他分区而不破坏快捷方式和注册表链接。"] = "ToolDesc_FreeMove",
        ["目前最厉害的杀毒软件"] = "ToolDesc_卡巴斯基",
        ["取代 Windows 自带复制和粘贴的高性能文件复制工具"] = "ToolDesc_RoboCopyEx",
        ["360 驱动大师纯净版，自动检测、安装和更新电脑硬件驱动。"] = "ToolDesc_360驱动大师",
        ["光盘刻录与 ISO 制作工具（开源替代 UltraISO）：制作/刻录/复制光盘镜像，基于 cdrtools。"] = "ToolDesc_cdrtfe",
        ["数据恢复与分区修复（开源替代 FinalData）：qphotorec 图形界面恢复丢失文件，TestDisk 修复分区表。"] = "ToolDesc_TestDisk-PhotoRec",
        ["开源硬盘诊断工具：表面扫描逐扇区读取测试、SMART 属性读取、驱动器自检执行。"] = "ToolDesc_HDDScan",
        ["硬盘低级扫描与坏道处理神器：逐扇区读写测试、S.M.A.R.T. 查看、不稳定扇区重映射。"] = "ToolDesc_Victoria",
        ["经典硬盘测试工具（2.55 免费版）：基准测试 / 健康状态 / 错误扫描 / 文件基准。"] = "ToolDesc_HDTune",
        ["权威内存检测工具（PassMark 官方 U盘版）：深度内存稳定性测试，支持 DDR4/DDR5。"] = "ToolDesc_MemTest86",
        ["专业烤机与监控：CPU / 内存 / 显卡 / 电源压力测试，实时电压温度曲线记录。"] = "ToolDesc_OCCT",
        ["GPU 基准测试经典（Unigine 引擎）：DX11 高负载渲染测试与显卡稳定性验证。"] = "ToolDesc_Unigine Heaven",
        ["次世代 GPU 基准测试：4K 优化 / VR 就绪，跨平台显卡跑分标准。"] = "ToolDesc_Unigine Superposition",
        ["专业图形性能基准测试（官方入口）：Time Spy / Fire Strike 显卡跑分标准。"] = "ToolDesc_3DMark",
        ["Maxon 官方 CPU/GPU 渲染性能基准（2024 版入口）：跑分榜公认的 CPU 性能参考。"] = "ToolDesc_CineBench",
        ["经典显示器测试工具：坏点 / 亮点 / 暗线排查、灰阶与响应时间测试。"] = "ToolDesc_DisplayX",
        ["国民级硬件检测与娱乐跑分（官方入口）：配置查看 / 温度监控 / 综合跑分。"] = "ToolDesc_鲁大师",
        ["跨平台性能基准（官方入口）：Geekbench 6 CPU/GPU 跑分，全球榜单可比。"] = "ToolDesc_Geekbench",
        ["RivaTuner Statistics Server 帧率监控（官方入口）：游戏帧率 / 帧时间显示，配合 Afterburner 使用。"] = "ToolDesc_RTSS",
        ["Intel 官方超频调教工具（官方入口）：频率 / 电压 / 功耗墙调节与稳定性验证。"] = "ToolDesc_Intel XTU",
        ["AMD 官方锐龙调教工具（官方入口）：超频 / 降压 / 实时监控，ZEN 平台利器。"] = "ToolDesc_AMD Ryzen Master",
        ["三星固态硬盘官方工具箱（官方入口）：固件更新 / 健康检测 / 性能测试。"] = "ToolDesc_Samsung Magician",
        ["希捷官方硬盘诊断工具（官方入口）：全品牌硬盘健康检测与短/长自检。"] = "ToolDesc_SeaTools",
        ["开源硬件监控（GitHub 官方下载）：温度 / 风扇 / 电压 / 功耗全传感器实时监控。"] = "ToolDesc_LibreHardwareMonitor",
        ["GPU 基准测试经典（Unigine 引擎）：山谷场景高负载渲染，DX11 显卡跑分与稳定性验证。"] = "ToolDesc_Unigine Valley",
    };

    /// <summary>随包策展说明 → 当前语言显示文本；未收录说明（含用户自定义）原样返回。</summary>
    public static string Description(string? description)
        => description is not null && KeyByDescription.TryGetValue(description, out var key)
            ? LocalizationService.L(key, description)
            : description ?? "";

    /// <summary>内置工具名称（按稳定 ID）→ 当前语言显示文本；未收录时回退原值。</summary>
    public static string BuiltinName(string? id, string? fallback)
        => string.IsNullOrEmpty(id) ? (fallback ?? "") : LocalizationService.L("BuiltinName_" + id, fallback ?? "");

    /// <summary>内置工具描述（按稳定 ID）→ 当前语言显示文本；未收录时回退原值。</summary>
    public static string BuiltinDescription(string? id, string? fallback)
        => string.IsNullOrEmpty(id) ? (fallback ?? "") : LocalizationService.L("BuiltinDesc_" + id, fallback ?? "");

    /// <summary>内置工具类型文本（弹窗/后台任务/进度任务/即时操作/内置）→ 当前语言显示文本。</summary>
    public static string BuiltinKind(string? kindText) => kindText switch
    {
        "弹窗" => LocalizationService.L("BuiltinKind_Dialog", "弹窗"),
        "后台任务" => LocalizationService.L("BuiltinKind_BackgroundTask", "后台任务"),
        "进度任务" => LocalizationService.L("BuiltinKind_ProgressTask", "进度任务"),
        "即时操作" => LocalizationService.L("BuiltinKind_InstantAction", "即时操作"),
        "内置" => LocalizationService.L("Common_Builtin", "内置"),
        _ => kindText ?? "",
    };
}
