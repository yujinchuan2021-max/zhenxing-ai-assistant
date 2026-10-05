using System.Collections.Generic;

namespace TubaWinUi3.Services;

/// <summary>
/// 官网目录块显示层本地化。zh 串为稳定显示源（目录 Id/URL 不在此表），
/// 仅在显示时经 T/TSub 转换；搜索/匹配仍走原始 Name/Url。
/// </summary>
public static class OfficialWebsiteTexts
{
    private static readonly Dictionary<string, string> EnMap = new()
    {
        ["IntelliJ/PyCharm 等 IDE"] = "IntelliJ/PyCharm and other IDEs",
        ["Microsoft .NET 官方"] = "Microsoft .NET official",
        ["JavaScript 运行时"] = "JavaScript runtime",
        ["全球最大的 PC 游戏平台"] = "The world's largest PC gaming platform",
        ["《原神》《崩坏》系列开发商"] = "Developer of Genshin Impact and Honkai series",
        ["显卡驱动与 GeForce"] = "Graphics drivers & GeForce",
        ["CPU/GPU 驱动与软件"] = "CPU/GPU drivers & software",
        ["Windows 11 下载"] = "Windows 11 download",
        [".NET 运行时与 SDK"] = ".NET runtime & SDK",
        ["网易免费远程控制与游戏串流"] = "NetEase free remote control & game streaming",
        ["无 DRM 数字游戏商店"] = "DRM-free digital game store",
        ["Nintendo 任天堂"] = "Nintendo",
        ["Microsoft 官网"] = "Microsoft official site",
        ["Office 套件在线版"] = "Office suite online",
        ["微软游戏主机与订阅服务"] = "Microsoft consoles & subscription",
        ["手机/笔记本/智能硬件"] = "Phones/laptops/smart devices",
        ["微软 Edge 浏览器"] = "Microsoft Edge browser",
        ["WinRAR 中文官网"] = "WinRAR official site",
        ["Ubisoft 育碧"] = "Ubisoft",
        ["网易出品的游戏加速器"] = "Game booster by NetEase",
        ["Google 浏览器"] = "Google browser",
        ["索尼游戏主机与商店"] = "Sony consoles & store",
        ["主板/显卡/笔记本"] = "Motherboards/GPUs/laptops",
        ["手机/笔记本/平板"] = "Phones/laptops/tablets",
        ["手机/存储/显示器"] = "Phones/storage/monitors",
        ["360 安全浏览器"] = "360 Secure Browser",
        ["Opera 浏览器"] = "Opera browser",
        ["DevOps 平台"] = "DevOps platform",
        ["Python 官方"] = "Python official",
        [".NET 包管理器"] = ".NET package manager",
        ["小黑盒游戏加速器"] = "Xiaoheihe game booster",
        ["显卡/主板/存储"] = "GPUs/motherboards/storage",
        ["机械硬盘/SSD"] = "HDDs/SSDs",
        ["系统镜像官方下载"] = "Official OS image downloads",
        ["轻量级代码编辑器"] = "Lightweight code editor",
        ["阿里企业协同办公"] = "Alibaba enterprise collaboration",
        ["字节跳动协同办公"] = "ByteDance collaboration",
        ["国内代码托管平台"] = "Chinese code hosting platform",
        ["EA 游戏官方"] = "EA games official",
        ["游戏社区与下载"] = "Game community & downloads",
        ["雷神游戏加速器"] = "Leigod game booster",
        ["迅游网游加速器"] = "Xunyou game accelerator",
        ["奇游手游加速器"] = "Qiyou mobile game accelerator",
        ["海豚游戏加速器"] = "Haitun game booster",
        ["玲珑游戏加速器"] = "Linglong game booster",
        ["显卡/迷你主机"] = "GPUs/mini PCs",
        ["笔记本/台式机"] = "Laptops/desktops",
        ["游戏本/台式机"] = "Gaming laptops/desktops",
        ["高端游戏 PC"] = "High-end gaming PCs",
        [".NET 下载"] = ".NET downloads",
        ["360 浏览器"] = "360 Browser",
        ["搜狗高速浏览器"] = "Sogou Explorer",
        ["笔记与知识管理"] = "Notes & knowledge management",
        ["微信 PC 版"] = "WeChat for PC",
        ["B 站视频平台"] = "Bilibili video platform",
        ["阿里云计算平台"] = "Alibaba Cloud platform",
        ["腾讯云计算平台"] = "Tencent Cloud platform",
        ["电脑检测与评分"] = "PC checkup & scoring",
        ["处理器信息检测"] = "CPU info tool",
        ["每周免费游戏"] = "Weekly free games",
        ["腾讯游戏平台"] = "Tencent gaming platform",
        ["暴雪游戏官方"] = "Blizzard official",
        ["育碧游戏官方"] = "Ubisoft official",
        ["网易旗下游戏"] = "NetEase games",
        ["UU 加速器"] = "UU Booster",
        ["小黑盒加速器"] = "Xiaoheihe booster",
        ["处理器与驱动"] = "CPUs & drivers",
        ["显卡/SSD"] = "GPUs/SSDs",
        ["微软中国官网"] = "Microsoft China official",
        ["微软应用商店"] = "Microsoft Store",
        ["微软 IDE"] = "Microsoft IDE",
        ["金山办公套件"] = "Kingsoft Office",
        ["腾讯企业通讯"] = "Tencent enterprise messaging",
        ["在线视频会议"] = "Online video meetings",
        ["免费压缩软件"] = "Free archiver",
        ["在线协作文档"] = "Online collaborative docs",
        ["微博社交平台"] = "Weibo social platform",
        ["在线视频平台"] = "Online video platform",
        ["游戏直播平台"] = "Game streaming platform",
        ["代码托管平台"] = "Code hosting platform",
        ["显卡信息检测"] = "GPU info tool",
        ["远程桌面控制"] = "Remote desktop control",
        ["远程控制软件"] = "Remote control software",
        ["远程桌面软件"] = "Remote desktop software",
        ["开源远程桌面"] = "Open-source remote desktop",
        ["任天堂官方"] = "Nintendo official",
        ["游戏加速器"] = "Game boosters",
        ["雷神加速器"] = "Leigod booster",
        ["迅游加速器"] = "Xunyou booster",
        ["奇游加速器"] = "Qiyou booster",
        ["海豚加速器"] = "Haitun booster",
        ["玲珑加速器"] = "Linglong booster",
        ["主板/显卡"] = "Motherboards/GPUs",
        ["显卡/主板"] = "GPUs/motherboards",
        ["A 卡显卡"] = "AMD Radeon GPUs",
        ["键鼠/外设"] = "Keyboards/mice/peripherals",
        ["内存/存储"] = "Memory/storage",
        ["火狐浏览器"] = "Firefox browser",
        ["夸克浏览器"] = "Quark browser",
        ["搜狗浏览器"] = "Sogou browser",
        ["文本编辑器"] = "Text editor",
        ["QQ 下载"] = "QQ download",
        ["短视频平台"] = "Short-video platform",
        ["网易云音乐"] = "NetEase Cloud Music",
        ["QQ 音乐"] = "QQ Music",
        ["芒果 TV"] = "Mango TV",
        ["容器化平台"] = "Container platform",
        ["虚拟机软件"] = "Virtual machine software",
        ["UU 远程"] = "UU Remote",
        ["游戏平台"] = "Gaming platforms",
        ["暴雪战网"] = "Battle.net",
        ["网易游戏"] = "NetEase Games",
        ["硬件厂商"] = "Hardware vendors",
        ["机械革命"] = "MECHREVO",
        ["游戏外设"] = "Gaming peripherals",
        ["微软系统"] = "Microsoft software",
        ["办公效率"] = "Productivity",
        ["企业微信"] = "WeCom",
        ["腾讯会议"] = "Tencent Meeting",
        ["腾讯文档"] = "Tencent Docs",
        ["下载工具"] = "Download tools",
        ["百度网盘"] = "Baidu Netdisk",
        ["阿里云盘"] = "Aliyun Drive",
        ["夸克网盘"] = "Quark Drive",
        ["社交娱乐"] = "Social & entertainment",
        ["哔哩哔哩"] = "Bilibili",
        ["腾讯视频"] = "Tencent Video",
        ["开发工具"] = "Dev tools",
        ["检测工具"] = "Checkup tools",
        ["驱动人生"] = "Driver Life",
        ["驱动管理"] = "Driver manager",
        ["驱动精灵"] = "DriverGenius",
        ["远程工具"] = "Remote tools",
        ["米哈游"] = "miHoYo",
        ["七彩虹"] = "Colorful",
        ["蓝宝石"] = "Sapphire",
        ["游戏本"] = "Gaming laptops",
        ["外星人"] = "Alienware",
        ["金士顿"] = "Kingston",
        ["浏览器"] = "Browsers",
        ["爱奇艺"] = "iQIYI",
        ["阿里云"] = "Alibaba Cloud",
        ["腾讯云"] = "Tencent Cloud",
        ["鲁大师"] = "LuMaster",
        ["向日葵"] = "Sunlogin",
        ["华硕"] = "ASUS",
        ["微星"] = "MSI",
        ["技嘉"] = "GIGABYTE",
        ["华擎"] = "ASRock",
        ["铭瑄"] = "MAXSUN",
        ["影驰"] = "GALAX",
        ["索泰"] = "ZOTAC",
        ["联想"] = "Lenovo",
        ["惠普"] = "HP",
        ["戴尔"] = "Dell",
        ["宏碁"] = "Acer",
        ["神舟"] = "Hasee",
        ["小米"] = "Xiaomi",
        ["荣耀"] = "HONOR",
        ["三星"] = "Samsung",
        ["罗技"] = "Logitech",
        ["雷蛇"] = "Razer",
        ["西数"] = "WD",
        ["希捷"] = "Seagate",
        ["夸克"] = "Quark",
        ["钉钉"] = "DingTalk",
        ["飞书"] = "Feishu",
        ["迅雷"] = "Xunlei",
        ["微信"] = "WeChat",
        ["微博"] = "Weibo",
        ["抖音"] = "Douyin",
        ["快手"] = "Kuaishou",
        ["优酷"] = "Youku",
        ["斗鱼"] = "Douyu",
        ["虎牙"] = "Huya",
        ["{matches.Count} 个结果"] = "{matches.Count} results",
        ["{sites.Count} 个网站"] = "{sites.Count} sites",
        ["复制链接"] = "Copy link",
    };

    /// <summary>整串翻译。</summary>
    public static string T(string zh)
    {
        if (LocalizationService.CurrentLanguage != "en-US") return zh;
        return EnMap.TryGetValue(zh, out var en) ? en : zh;
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
