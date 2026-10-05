using System.Collections.Generic;

namespace TubaWinUi3.Services;

/// <summary>
/// 电子文盲测试块显示层本地化。题干/选项/解析为维护内容，显示时经 T/TSub 转换；
/// 答题判定（索引/分值/洗牌）不依赖文本，数据键保持中文。
/// </summary>
public static class DigitalLiteracyTexts
{
    private static readonly Dictionary<string, string> EnMap = new()
    {
        ["系统卸载入口会调用软件自带的卸载程序并清理注册表；Geek/HiBit 等专业卸载工具原理相同，还能强制卸载、扫描残留，对付顽固软件更彻底（本工具箱「其他工具」分类就内置了 HiBit Uninstaller）。删图标、删文件夹只是删文件不算卸载。"] = "The system uninstall entry runs the app's own uninstaller and cleans the registry; tools like Geek/HiBit do the same but can force-remove and scan leftovers — better for stubborn apps (HiBit Uninstaller is bundled in this toolbox under 'Other tools'). Deleting the icon or folder just deletes files — that is not uninstalling.",
        ["软件应默认装在 Program Files，或自建专用子目录（如 D:\\Apps\\软件名），文件集中便于统一管理和卸载；装到桌面或盘符根目录只会污染目录结构，D 盘根目录也并不会带来「I/O 隔离」之类的性能收益。"] = "Software belongs in Program Files by default, or a dedicated subfolder you create (e.g. D:\\Apps\\AppName) so files stay together for easy management and removal; installing to the desktop or a drive root only pollutes things, and D:\\ root brings no 'I/O isolation' performance gains.",
        ["Windows Defender 已足够日常防护。但涉及网银、敏感数据等场景，额外安装一款可靠的专业杀软（如 ESET、卡巴斯基）是合理的安全策略。注意：同时装多款杀软会互相冲突。"] = "Windows Defender is enough for daily protection. For online banking or sensitive work, adding one reliable paid AV (e.g. ESET, Kaspersky) is reasonable. Note: multiple AVs at once conflict with each other.",
        ["正确率 {percentage:F0}%  ·  答对 {_totalScore / PointsPerQuestion} / {TotalQuestions} 题"] = "Accuracy {percentage:F0}%  ·  {_totalScore / PointsPerQuestion} / {TotalQuestions} correct",
        ["通过 设置 → 应用 → 已安装的应用 卸载；顽固软件可用 Geek Uninstaller、HiBit Uninstaller 等专业工具深度清理"] = "Uninstall via Settings → Apps → Installed apps; for stubborn software use deep-clean tools like Geek Uninstaller or HiBit Uninstaller",
        [".exe 是 Windows 平台的 PE（Portable Executable）可执行格式，Android 使用 ELF/APK，架构完全不同。"] = ".exe is Windows' PE (Portable Executable) format; Android uses ELF/APK — entirely different architectures.",
        ["第 {_currentQuestion + 1} 题 / 共 {TotalQuestions} 题  ·  当前得分 {_totalScore} 分"] = "Question {_currentQuestion + 1} / {TotalQuestions}  ·  Score {_totalScore}",
        ["Steam 官网是 store.steampowered.com，「Steam 管家」等均为第三方仿冒软件，可能携带捆绑或木马。"] = "Steam's site is store.steampowered.com; 'Steam Butler' and similar are third-party fakes that may carry bundles or trojans.",
        ["核显性能远弱于独显：视频线插在主板接口上会走核显输出，游戏自然掉帧。装机后务必把显示器接在独立显卡的 DP/HDMI 接口上。"] = "Integrated graphics are far weaker: a cable in the motherboard ports outputs via iGPU, so games drop frames. Always plug the monitor into the discrete GPU's DP/HDMI port.",
        ["运行 sfc /scannow 修复系统文件，再用 DISM /Online /Cleanup-Image 还原组件存储"] = "Run sfc /scannow to repair system files, then DISM /Online /Cleanup-Image to restore the component store",
        [".exe 是 Windows PE（Portable Executable）格式，Android/iOS 系统无法运行"] = ".exe is the Windows PE (Portable Executable) format; Android/iOS cannot run it",
        ["电子邮箱（如 xxx@qq.com）基于 SMTP/IMAP 协议，是互联网基础通信工具，几乎所有网络服务都需要。"] = "Email (e.g. xxx@qq.com) runs on SMTP/IMAP and is a fundamental internet service nearly every online account needs.",
        ["在 PowerShell 中执行 Get-AppxPackage | Remove-AppxPackage 卸载"] = "In PowerShell, run Get-AppxPackage | Remove-AppxPackage to uninstall",
        ["扩展名标识文件类型和关联程序。.docx 是基于 Office Open XML 标准的 Word 文档格式。"] = "Extensions indicate the file type and associated app. .docx is a Word document based on the Office Open XML standard.",
        ["压缩文件需用解压工具处理。推荐 7-Zip（开源免费）或 WinRAR，Windows 11 也自带右键解压。"] = "Archives need an extractor. 7-Zip (open source) or WinRAR are recommended; Windows 11 also extracts via right-click.",
        ["正确做法是用任务管理器（Ctrl+Shift+Esc）定位瓶颈，区分 CPU/内存/磁盘/GPU 哪个是瓶颈。"] = "Use Task Manager (Ctrl+Shift+Esc) to find the bottleneck — is it CPU, memory, disk or GPU?",
        ["你的电脑基础知识亟需加强！别担心，每个人都是从零开始的。建议从基础操作学起，多练习常用功能，慢慢就能上手了。"] = "Your computer basics need work! Don't worry — everyone starts from zero. Begin with basic operations and practice common features; you'll get the hang of it.",
        ["PC 游戏基于 x86 架构，手机是 ARM 架构，ISA 不兼容无法直接运行。可通过云游戏串流方案实现。"] = "PC games target x86 while phones are ARM; the ISAs are incompatible, so they can't run directly. Cloud gaming streaming is the way.",
        ["第三方装机工具常捆绑推广甚至植入后门。微软官方 Media Creation Tool 可制作纯净启动盘。"] = "Third-party install tools often bundle promos or backdoors. Microsoft's official Media Creation Tool makes a clean boot USB.",
        ["你有一些电脑基础，但还有很多需要学习的地方。建议多了解系统操作、安全知识和常用技巧，告别电子文盲指日可待！"] = "You have some PC basics but still plenty to learn. Study system operations, security and common tips — bidding farewell to digital illiteracy is within reach!",
        ["使用默认的 Program Files 路径或在非系统盘创建专用子目录（如 D:\\Apps\\软件名）"] = "Use the default Program Files path, or create a dedicated subfolder on another drive (e.g. D:\\Apps\\AppName)",
        ["十指分工盲打是标准打字姿势，各手指负责固定键位区域，基准键位定位是 touch typing 的核心。"] = "Ten-finger touch typing is the standard: each finger owns fixed key areas, and home-row positioning is its core.",
        ["电子邮箱（Email）是基于 SMTP/IMAP 协议的网络通信地址，可收发邮件、注册账号、接收验证码"] = "Email is a network address built on SMTP/IMAP for sending mail, registering accounts and receiving verification codes",
        ["只要连成一条线，说明你是电子文盲。\n共 25 道选择题，满分 100 分，测测你的电脑基础知识水平！"] = "Connect the dots and you're digitally illiterate.\n25 multiple-choice questions, 100 points — test your PC basics!",
        ["视频线插在了主板背部的集成显卡接口上，画面实际由核显输出，应插在独立显卡的 DP/HDMI 输出接口"] = "The cable is plugged into the motherboard's integrated output so the iGPU renders; use the discrete GPU's DP/HDMI port instead",
        ["第 {_currentQuestion + 1} 题（{PointsPerQuestion} 分）"] = "Question {_currentQuestion + 1} ({PointsPerQuestion} pts)",
        ["装到 C:\\ProgramData 目录，该目录对所有用户账户可见且具有 SYSTEM 级权限"] = "Install into C:\\ProgramData — visible to all accounts with SYSTEM-level rights",
        ["这是典型的电商骗局，「i9级」不等于真正的 i9，需查看 CPU 具体型号和 CPU-Z 参数"] = "This is a classic e-commerce scam: 'i9-class' is not a real i9; check the exact CPU model and CPU-Z specs",
        ["按 Win+Shift+S 调用系统截图工具框选区域，或按 Print Screen 截取全屏"] = "Press Win+Shift+S to snip a region, or Print Screen for the full screen",
        ["磁力链接是 BT 协议的资源标识，需专用 BT 客户端（如 qBittorrent）解析下载。"] = "Magnet links are BitTorrent resource identifiers; a BT client (e.g. qBittorrent) is needed to resolve and download.",
        ["Alt+= 可快速插入 SUM 求和公式，配合自动填充（拖拽单元格右下角）可批量处理多列数据。"] = "Alt+= quickly inserts SUM; drag the fill handle to batch-process multiple columns.",
        ["「i9级」「军工级」是商家营销话术，实际可能是淘汰服务器拆机件。务必确认 CPU 具体型号。"] = "'i9-class' and 'military-grade' are marketing talk — often retired server parts. Always confirm the exact CPU model.",
        ["1TB 通常指硬盘存储容量。目前主流电脑内存为 16~64GB，1TB 内存属于服务器级别。"] = "1TB usually means storage. Mainstream PCs have 16–64GB of RAM; 1TB of memory is server territory.",
        ["CPU 性能取决于架构、代数、核心数等综合因素，不能仅凭 i3/i5/i7 的品牌前缀判断。"] = "CPU performance depends on architecture, generation and core count — not just the i3/i5/i7 brand prefix.",
        ["从微软官网下载 Media Creation Tool 制作官方启动 U 盘，确保镜像纯净"] = "Download Media Creation Tool from Microsoft's official site to make a clean boot USB",
        ["恭喜你！你对电脑基础知识掌握得很好，完全不是电子文盲！你已经超越了绝大多数用户，继续保持！"] = "Congratulations! You have solid PC basics — definitely not digitally illiterate! You're ahead of most users; keep it up!",
        ["PC 与移动端 ISA 架构不同，可通过云游戏平台（GeForce NOW 等）串流游玩"] = "PC and mobile use different ISAs; cloud gaming platforms (GeForce NOW, etc.) stream games to your phone",
        ["市面上的「加速器」多为伪优化。真正提速应从减少启动项、升级 SSD/内存、重装系统入手。"] = "Most 'boosters' are fake optimization. Real speedup comes from cutting startup items, upgrading SSD/RAM, or reinstalling.",
        ["使用 qBittorrent 等 BT 客户端导入磁力链接，通过 DHT 网络获取元数据"] = "Use a BT client like qBittorrent to import the magnet link and fetch metadata via DHT",
        ["「任意键」指键盘上任意一个按键，并非某个特定按键。该提示源自 DOS 时代的交互设计。"] = "'Any key' means any key on the keyboard, not a specific one. The prompt dates back to the DOS era.",
        ["访问 store.steampowered.com 官网下载 Steam 客户端安装包"] = "Go to store.steampowered.com and download the official Steam client installer",
        ["下载站的「高速下载」通常是捆绑安装器，会附带大量推广软件。应优先从软件官网获取安装包。"] = "'High-speed download' buttons are usually bundled installers with lots of promo software. Get installers from official sites first.",
        ["选中数据区域下方单元格，按 Alt+= 快捷键插入 SUM 函数，配合自动填充批量处理"] = "Select the cell below your data, press Alt+= to insert SUM, then fill down",
        ["显卡的 PhysX 物理加速没有开启，现代游戏引擎必须依赖 PhysX 才能渲染画面"] = "The GPU's PhysX acceleration is off; modern game engines must use PhysX to render",
        ["文件扩展名，表示这是一个基于 Office Open XML 标准的 Word 文档"] = "A file extension indicating a Word document based on the Office Open XML standard",
        ["把 magnet 链接转换为 HTTP 链接，用 wget 或 curl 命令行下载"] = "Convert the magnet link to an HTTP link and download with wget or curl",
        ["日常使用 Defender 即可；若有网银操作、敏感办公等需求，可加装一款专业杀软"] = "Defender is fine for daily use; for online banking or sensitive work, add one professional AV",
        ["Win+Shift+S 可以框选任意区域截图并自动复制到剪贴板，远比手机拍照清晰。"] = "Win+Shift+S captures any region and copies it to the clipboard — far clearer than photographing the screen.",
        ["Ctrl+E 是段落居中对齐的快捷键，也可在「开始」选项卡的段落组中点击居中按钮。"] = "Ctrl+E centers the paragraph; you can also click the center button in the Paragraph group of the Home tab.",
        ["来路不明的 .exe 文件可能携带木马。应先用杀毒软件扫描，确认来源可信后再执行。"] = ".exe files from unknown sources may carry trojans. Scan with AV and verify the source before running.",
        ["下载 Switch 模拟器运行，因为 Switch 也是 ARM 架构的移动设备"] = "Download a Switch emulator — the Switch is ARM-based too, so it will run",
        ["用 PowerShell 的 Add-Type 截图 API 编写脚本自动化截图"] = "Write a PowerShell script with the Add-Type screenshot API to automate capture",
        ["加速器通过修改 Windows 的 NTFS 分区簇大小来提升磁盘 I/O 性能"] = "The booster resizes the NTFS cluster size to boost disk I/O performance",
        ["C 盘剩余空间不足导致虚拟内存（Page File）无法扩展，清理 C 盘即可"] = "Low C: free space prevents the page file from expanding; just clean up C:",
        ["网盘文件应先保存到自己网盘再下载到本地。「在线解压」可能受限且无法保证完整性。"] = "Save netdisk files to your own drive first, then download. 'Online unzip' can be limited and can't guarantee integrity.",
        ["Windows 家庭版没有独显驱动的完整授权，需要升级专业版才能发挥独显性能"] = "Windows Home lacks full authorization for discrete GPU drivers; upgrade to Pro for full performance",
        ["需看具体代数和型号，如 i3-14100 多核性能可超过老款 i7-7700"] = "It depends on generation and model — e.g. an i3-14100 can beat an older i7-7700 in multi-core",
        ["部署 ESET NOD32 + 卡巴斯基双引擎交叉扫描，确保零日威胁检测率"] = "Deploy ESET NOD32 + Kaspersky dual engines for cross-scanning and zero-day detection",
        ["装到桌面方便快速启动，Windows 的 Shell 文件夹机制会自动管理"] = "Put it on the desktop for quick launch; Windows' Shell folder mechanism manages it automatically",
        ["你收到一个磁力链接（magnet:?xt=...），想下载对应资源，应该？"] = "You received a magnet link (magnet:?xt=...). To download the resource you should:",
        ["手机的 ARM 处理器缺少 x86 指令集的微码支持，无法解析 PE 格式"] = "The phone's ARM CPU lacks x86 microcode support and can't parse PE files",
        ["在 Microsoft Store 搜索 Steam 下载 UWP 版本"] = "Search Steam in the Microsoft Store and download the UWP version",
        ["裸机运行即可，现代 Windows 的内核隔离（VBS）已提供硬件级防护"] = "Just run bare-metal; modern Windows VBS already provides hardware-level protection",
        ["所有 i7 一定比 i5 强，因为 i7 的 L3 缓存更大、线程数更多"] = "All i7s always beat i5s — bigger L3 cache and more threads",
        ["是文件的 MIME Type 标识，用于 HTTP 传输时的内容类型协商"] = "It's the file's MIME Type, used for HTTP content negotiation",
        ["通过 WSL 的 unzip 命令行工具解压，兼容性比 GUI 工具更好"] = "Unzip via WSL's command-line unzip tool for better compatibility than GUI tools",
        ["大部分「加速器」是噱头，真正有效的是优化启动项、升级 SSD/内存等硬件"] = "Most 'boosters' are gimmicks; what works is trimming startup items and upgrading SSD/RAM",
        ["通过 DISM++ 直接将 WIM 镜像释放到硬盘分区，跳过安装向导更快"] = "Use DISM++ to apply the WIM image straight to the drive — skipping the installer is faster",
        ["搜索「Steam 管家」下载，它整合了 Steam 加速和游戏管理功能"] = "Search and download 'Steam Butler' — it bundles Steam acceleration and game management",
        ["电商上看到「军工级主板 + i9级CPU」整机只卖1999元，你应该？"] = "You see a 'military-grade motherboard + i9-class CPU' PC for only ¥1999. You should:",
        ["显示器刷新率只有 60Hz，拖累了显卡的渲染帧数，需要更换高刷新率屏幕"] = "The 60Hz monitor refresh rate caps the GPU's frames — replace it with a high-refresh display",
        ["打开任务管理器查看 CPU、内存、磁盘、GPU 占用率，定位高占用进程"] = "Open Task Manager and check CPU/memory/disk/GPU usage to find the heavy process",
        ["下载第三方 Ghost 镜像（如雨林木风），已预装常用软件省去配置时间"] = "Download a third-party Ghost image with common software preinstalled to skip setup",
        ["将数据导入 Power Query 编辑器，通过 M 语言编写聚合查询"] = "Import the data into Power Query and write aggregation queries in M language",
        ["第 {i + 1} 题：{Questions[i].Question}"] = "Q{i + 1}: {Questions[i].Question}",
        ["使用 Vulkan 渲染层适配，部分 3A 大作已支持移动端原生运行"] = "Use a Vulkan translation layer — some AAA titles now run natively on mobile",
        ["你收到一个 report.docx 文件，「.docx」是什么意思？"] = "You received report.docx — what does '.docx' mean?",
        ["把磁力链接当成普通网址粘贴到浏览器地址栏，浏览器会自动解析并下载文件"] = "Paste the magnet link into the browser address bar; the browser will resolve and download it",
        ["对比一下同价位的 Cinebench R23 跑分，性价比确实很高"] = "Compare Cinebench R23 scores at the same price — great value indeed",
        ["显卡显存容量，对应的是 RTX 4090 级别的 1TB 显存版本"] = "The GPU's VRAM — the 1TB version is RTX 4090 class",
        ["把 .zip 后缀改成 .txt 用记事本打开，查看压缩包内部结构"] = "Rename .zip to .txt and open it in Notepad to view the archive contents",
        ["在搜索引擎中搜索该磁力链接对应的 .torrent 种子文件再下载"] = "Search the web for the .torrent file matching the magnet link, then download it",
        ["使用「老毛桃」PE 工具箱，内置了万能驱动和系统优化脚本，一键部署"] = "Use the 'LaoMaoTao' PE toolkit with universal drivers and one-click scripted deployment",
        ["保持警惕，先用杀毒软件扫描确认安全后再运行，优先从可信渠道获取软件"] = "Stay cautious: scan with AV before running, and prefer trusted sources",
        ["使用 7-Zip、WinRAR 或系统自带功能右键解压到指定目录"] = "Right-click and extract to the target folder with 7-Zip, WinRAR or the built-in tool",
        ["用浏览器直接打开 .zip，Chrome 内置了 ZIP 解码器"] = "Open the .zip directly in the browser — Chrome has a built-in ZIP decoder",
        ["点击「在线解压」直接预览，百度网盘服务端会实时解压并推流到浏览器"] = "Click 'Online unzip' to preview; Baidu's servers decompress and stream it to the browser",
        ["手机芯片性能已接近 PC，直接下载 PC 版安装包就能安装运行"] = "Phone chips are nearly as fast as PCs now; just install the PC package directly",
        ["点击「高速下载」，它使用了 P2SP 多线程加速协议，速度更快"] = "Click 'High-speed download' — it uses the P2SP multi-threaded protocol for faster speeds",
        ["先查一下这块主板的 VRM 供电相数，军工级通常 12 相以上"] = "Check the motherboard's VRM phases — military-grade usually means 12 or more",
        ["你的电脑有独立显卡，但玩游戏帧数很低、画面卡顿，可能的原因是？"] = "Your PC has a discrete GPU but games run at low FPS and stutter. The likely cause is:",
        ["把桌面图标拖到回收站，Windows 会自动触发关联的卸载程序"] = "Drag the desktop icon to the Recycle Bin and Windows will trigger the uninstaller",
        ["用手机对着屏幕拍照，手机摄像头的 HDR 算法可以补偿屏幕反光"] = "Photograph the screen with your phone — its HDR algorithm compensates for glare",
        ["用空格键逐个敲入半角空格，通过等宽字体的字符宽度对齐到页面中心"] = "Type half-width spaces one by one to center the title via monospace character widths",
        ["用沙箱（Sandboxie）运行，即使有病毒也不会影响宿主系统"] = "Run it in a sandbox (Sandboxie) — even with a virus the host stays safe",
        ["必须精确找到标着「Any Key」的物理按键，否则会中断安装"] = "Find the exact physical key labeled 'Any Key', or the install will abort",
        ["你想在手机上玩 PC 端的《赛博朋克2077》，正确做法是？"] = "You want to play Cyberpunk 2077 (PC) on your phone. The right way is:",
        ["只看单核主频和 IPC 指标就行，多核性能对日常使用影响不大"] = "Just check single-core clock and IPC — multi-core barely matters in daily use",
        ["装到 D 盘根目录，利用独立分区的 I/O 隔离提升读写性能"] = "Install to D:\\ root and use the separate partition's I/O isolation for faster reads/writes",
        ["直接删除安装文件夹，再用 CCleaner 清理注册表残留项"] = "Delete the install folder, then use CCleaner to clean registry leftovers",
        ["正确，加速器通过清理注册表碎片和优化内存分配提升系统响应速度"] = "Correct — boosters speed up the system by cleaning registry fragmentation and optimizing memory allocation",
        ["邮箱就是手机号码的网络化映射，通过 SMS 网关实现邮件收发"] = "Email is just a network mapping of your phone number via an SMS gateway",
        ["手机的 SELinux 安全策略阻止了未签名二进制文件的执行"] = "The phone's SELinux policy blocks execution of unsigned binaries",
        ["在网盘搜索「Steam 免安装绿色版」压缩包，解压即可使用"] = "Search netdisks for a 'portable Steam' archive and just unzip it",
        ["安装360安全卫士+鲁大师+驱动精灵，形成多层主动防御体系"] = "Install 360 Total Security + LuMaster + DriverGenius for multi-layer active defense",
        ["内存（RAM）容量，1TB DDR5 已经是消费级旗舰配置"] = "Memory (RAM) — 1TB of DDR5 is a consumer flagship configuration",
        ["使用 VLOOKUP 函数配合 IF 条件判断实现动态求和"] = "Use VLOOKUP with IF conditions for dynamic summing",
        ["你用手机下载了一个 .exe 文件，提示无法打开，原因是？"] = "You downloaded an .exe on your phone and it won't open because:",
        ["十指分工定位基准键位（ASDF/JKL;），实现盲打输入"] = "Ten fingers on the home row (ASDF/JKL;) for touch typing",
        ["单手操作即可，人体工学研究表明过度分工反而增加腱鞘炎风险"] = "One hand is enough — ergonomics show excessive division raises tendinitis risk",
        ["使用 IDM 接管下载，它会自动识别页面中的真实下载链接"] = "Let IDM take over — it detects the real download link automatically",
        ["商家说「这台电脑 1TB」，这里的 1TB 通常指的是？"] = "The seller says 'this PC is 1TB'. Usually that means:",
        ["加速器能超频 CPU 和 GPU，相当于免费提升硬件性能"] = "Boosters overclock the CPU and GPU — free hardware performance",
        ["通过百度网盘的 WebDAV 接口挂载为本地磁盘直接读取"] = "Mount Baidu Netdisk via WebDAV as a local drive and read directly",
        ["选中文字后按 Ctrl+E 或点击段落组的居中对齐按钮"] = "Select the text and press Ctrl+E, or click the center-align button in the Paragraph group",
        ["插入一个单列表格，将标题放入单元格并设置单元格水平居中"] = "Insert a one-column table, put the title in the cell and set horizontal centering",
        ["进入安全模式卸载最近安装的驱动程序，回滚到上一个还原点"] = "Boot into Safe Mode to remove the recent driver and roll back to a restore point",
        ["只要文件带有有效的数字签名就绝对安全，无需扫描直接运行"] = "If the file has a valid digital signature it's absolutely safe — no scan needed",
        ["邮箱是一种即时通讯工具，功能类似微信但使用异步消息队列"] = "Email is an instant messenger like WeChat but with async message queues",
        ["邮箱是电脑本地的用户账户凭证，存储在 SAM 数据库中"] = "Email is a local user account credential stored in the SAM database",
        ["找到「普通下载」或直接去软件官网下载，避免捆绑安装器"] = "Use 'Normal download' or go to the official site to avoid bundled installers",
        ["硬盘（存储）容量，主流电脑内存通常为 16~32GB"] = "Hard drive (storage) capacity — mainstream PCs usually have 16–32GB RAM",
        ["你想把 Word 文档中的标题居中对齐，正确做法是？"] = "You want to center the title in a Word document. The right way is:",
        ["在标尺上拖动左缩进和右缩进标记到对称位置实现视觉居中"] = "Drag the left and right indent markers on the ruler to symmetric positions",
        ["用计算器算好后手动输入结果，避免公式导致文件体积膨胀"] = "Compute it on a calculator and type the result to avoid bloating the file with formulas",
        ["别人通过网盘给你分享了一个 .exe 文件，你应该？"] = "Someone shared an .exe with you via a netdisk. You should:",
        ["用语音输入替代键盘，属于更先进的 HCI 交互方式"] = "Use voice input instead of the keyboard — a more advanced HCI method",
        ["等待系统倒计时结束后自动跳过，属于非交互式安装流程"] = "Wait for the countdown and let it skip — it's a non-interactive installer",
        ["按键盘上任意一个键（空格、回车等均可）触发安装继续"] = "Press any key (space, Enter, etc.) to continue the installation",
        ["这是 DOS 时代的遗留指令，现代系统可以直接忽略"] = "This is a DOS-era legacy prompt; modern systems can ignore it",
        ["指的是 NVMe SSD 的 TBW 写入寿命指标"] = "It refers to the NVMe SSD's TBW endurance rating",
        ["使用远程桌面连接到自己的电脑，然后在远程会话中截图"] = "Connect via Remote Desktop to your own PC and screenshot inside the session",
        ["使用油猴脚本绕过限速，配合 IDM 多线程加速下载"] = "Use a userscript to bypass the speed limit plus IDM multithreading to speed up",
        ["Excel 中需要对一列数字求和，最高效的方法是？"] = "In Excel you need to sum a column of numbers. The most efficient way is:",
        ["用两根食指配合 Shift 切换大小写，效率最高"] = "Use two index fingers with Shift for capitals — most efficient",
        ["赶紧下单，军工级用料意味着更高的稳定性和耐久度"] = "Order immediately — military-grade materials mean higher stability and durability",
        ["你要从百度网盘下载别人分享的文件，正确做法是？"] = "You want to download a file someone shared on Baidu Netdisk. The right way is:",
        ["先保存到自己的网盘，再通过客户端下载到本地磁盘"] = "Save it to your own netdisk first, then download via the client",
        ["你要重装 Windows 系统，正确的做法是？"] = "You're going to reinstall Windows. The right way is:",
        ["你要解压一个 .zip 压缩包，应该怎么做？"] = "You need to extract a .zip archive. What should you do?",
        ["有人说「电脑加速器能让电脑变快」，这种说法？"] = "Someone says 'PC boosters make your computer faster'. This claim is:",
        ["文件在下载过程中因网络丢包导致二进制校验失败"] = "The download failed its binary check due to packet loss during transfer",
        ["看 TDP 功耗就行，功耗越高代表性能越强"] = "Just look at TDP — higher power means stronger performance",
        ["你想截取屏幕上的内容发给朋友，正确做法是？"] = "You want to capture your screen and send it to a friend. The right way is:",
        ["安装软件时弹出「按任意键继续」，你应该？"] = "An installer shows 'Press any key to continue'. You should:",
        ["你要下载 Steam 平台，应该怎么做？"] = "You want to download the Steam platform. What should you do?",
        ["是文件的哈希校验后缀，用于验证文件完整性"] = "It's the file's hash checksum suffix used to verify integrity",
        ["你要卸载一个不再使用的软件，正确做法是？"] = "You want to uninstall software you no longer use. The right way is:",
        ["以下关于「电子邮箱」的描述，最准确的是？"] = "Which description of 'email' is the most accurate?",
        ["你正在学习电脑打字，正确的指法习惯是？"] = "You're learning to type. The correct finger habit is:",
        ["新电脑到手后，关于安全防护软件你应该？"] = "A new PC arrives. Regarding security software you should:",
        ["在下载站看到「高速下载」按钮，你应该？"] = "You see a 'High-speed download' button on a download site. You should:",
        ["关于 CPU 性能对比，正确的理解是？"] = "About comparing CPU performance, the correct understanding is:",
        ["说明该文件经过了 DRM 数字版权加密"] = "It means the file is DRM-protected",
        ["直接运行，网盘平台已经做了文件安全扫描"] = "Just run it — the netdisk already scanned the file for safety",
        ["复制下载链接到迅雷，利用离线下载加速"] = "Copy the download link into Xunlei to use offline acceleration",
        ["安装软件时，安装路径应该怎么选？"] = "When installing software, how should you choose the install path?",
        ["电脑运行变卡，正确的排查思路是？"] = "Your PC is getting slow. The right troubleshooting approach is:",
        ["/ {MaxScore} 分"] = "/ {MaxScore} pts",
        ["60 ~ 79 分"] = "60 – 79",
        ["电子文盲等级测试"] = "Digital Literacy Test",
        ["✅ 回答正确！"] = "✅ Correct!",
        ["📊 评分标准"] = "📊 Grading",
        ["≥ 80 分"] = "≥ 80",
        ["普通电子文盲"] = "Regular digital illiterate",
        ["超级电子文盲"] = "Super digital illiterate",
        ["< 60 分"] = "< 60",
        ["❌ 回答错误"] = "❌ Wrong answer",
        ["📋 答题详情"] = "📋 Details",
        ["电脑高手"] = "PC expert",
        ["开始测试"] = "Start test",
        ["查看结果"] = "View result",
        ["重新测试"] = "Retake test",
        ["下一题"] = "Next question",
    };

    /// <summary>整串翻译。</summary>
    public static string T(string zh)
    {
        if (LocalizationService.CurrentLanguage != "en-US") return zh;
        return EnMap.TryGetValue(zh, out var en) ? en : zh;
    }

    /// <summary>运行时拼接文本：模板键正则捕获（一次性绑定，不重扫描插入值）；普通键先不做路径串替换。</summary>
    public static string TSub(string text)
    {
        if (LocalizationService.CurrentLanguage != "en-US" || string.IsNullOrEmpty(text)) return text;
        if (_templates.TryTranslate(text, out var translated)) return translated;
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
