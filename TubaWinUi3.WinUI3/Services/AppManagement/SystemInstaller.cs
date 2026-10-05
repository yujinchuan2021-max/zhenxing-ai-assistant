using System.Diagnostics;
using System.IO;
using System.Text;

namespace TubaWinUi3.Services.AppManagement;

/// <summary>
/// 系统级安装器（v0.3）：通过 winget 把软件安装到系统（用户级优先，全局可用），
/// 受控白名单 + 装后自动刷新进程 PATH（新装的软件立即可用）+ 自动登记应用中心。
/// 与沙箱安装（SandboxInstaller）的区别：这是"给用户用的正式安装"，软件进系统 PATH；
/// 沙箱仅用于隔离/试用场景。
/// </summary>
internal static class SystemInstaller
{
    private static readonly Dictionary<string, (string WingetId, string Name, string ProbeExe)> Targets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["python"] = ("Python.Python.3.12", "Python 3.12", "python.exe"),
        ["node"] = ("OpenJS.NodeJS.LTS", "Node.js LTS", "node.exe"),
        ["git"] = ("Git.Git", "Git", "git.exe"),
        ["vscode"] = ("Microsoft.VisualStudioCode", "Visual Studio Code", "code.cmd"),
        ["godot"] = ("GodotEngine.GodotEngine", "Godot", "godot.exe"),
        ["dotnet"] = ("Microsoft.DotNet.SDK.8", ".NET SDK 8", "dotnet.exe"),
        ["7zip"] = ("7zip.7zip", "7-Zip", "7z.exe"),
        ["ollama"] = ("Ollama.Ollama", "Ollama", "ollama.exe"),
        ["cmake"] = ("Kitware.CMake", "CMake", "cmake.exe"),
        // —— ZXAI 2026-09-19：AI 工具卡「一键安装」白名单扩展 ——
        ["cherry-studio"] = ("kangfenmao.CherryStudio", "Cherry Studio", "CherryStudio.exe"),
        ["chatbox"] = ("Bin-Huang.Chatbox", "ChatBox", "Chatbox.exe"),
        ["jan"] = ("Jan.Jan", "Jan", "jan.exe"),
        ["lmstudio"] = ("ElementLabs.LMStudio", "LM Studio", "LM Studio.exe"),
        ["blender"] = ("BlenderFoundation.Blender", "Blender", "blender.exe"),
        ["gimp"] = ("GIMP.GIMP", "GIMP", "gimp-2.10.exe"),
        ["krita"] = ("KDE.Krita", "Krita", "krita.exe"),
        ["inkscape"] = ("Inkscape.Inkscape", "Inkscape", "inkscape.exe"),
        ["obs"] = ("OBSProject.OBSStudio", "OBS Studio", "obs64.exe"),
        ["audacity"] = ("Audacity.Audacity", "Audacity", "audacity.exe"),
        ["handbrake"] = ("HandBrake.HandBrake", "HandBrake", "HandBrake.exe"),
        ["kdenlive"] = ("KDE.Kdenlive", "Kdenlive", "kdenlive.exe"),
        ["shotcut"] = ("Meltytech.Shotcut", "Shotcut", "shotcut.exe"),
        ["potplayer"] = ("Daum.PotPlayer", "PotPlayer", "PotPlayerMini64.exe"),
        ["everything"] = ("voidtools.Everything", "Everything", "Everything.exe"),
        ["notepadpp"] = ("Notepad++.Notepad++", "Notepad++", "notepad++.exe"),
        ["sumatrapdf"] = ("SumatraPDF.SumatraPDF", "SumatraPDF", "SumatraPDF.exe"),
        ["libreoffice"] = ("TheDocumentFoundation.LibreOffice", "LibreOffice", "soffice.exe"),
        ["wps"] = ("Kingsoft.WPSOffice", "WPS Office", "wps.exe"),
        ["powertoys"] = ("Microsoft.PowerToys", "PowerToys", "PowerToys.exe"),
        ["notion"] = ("Notion.Notion", "Notion", "Notion.exe"),
        ["obsidian"] = ("Obsidian.Obsidian", "Obsidian", "Obsidian.exe"),
        ["sublime"] = ("SublimeHQ.SublimeText.4", "Sublime Text", "sublime_text.exe"),
        ["neovim"] = ("Neovim.Neovim", "Neovim", "nvim.exe"),
        ["dbeaver"] = ("DBeaver.DBeaver.Community", "DBeaver", "dbeaver.exe"),
        ["postman"] = ("Postman.Postman", "Postman", "Postman.exe"),
        ["docker"] = ("Docker.DockerDesktop", "Docker Desktop", "Docker Desktop.exe"),
        ["wireshark"] = ("WiresharkFoundation.Wireshark", "Wireshark", "Wireshark.exe"),
        ["mobaxterm"] = ("Mobatek.MobaXterm", "MobaXterm", "MobaXterm.exe"),
        ["heidisql"] = ("HeidiSQL.HeidiSQL", "HeidiSQL", "heidisql.exe"),
        ["winmerge"] = ("WinMerge.WinMerge", "WinMerge", "WinMergeU.exe"),
        ["sourcetree"] = ("Atlassian.Sourcetree", "Sourcetree", "Sourcetree.exe"),
        ["vs-community"] = ("Microsoft.VisualStudio.2022.Community", "Visual Studio 社区版", "devenv.exe"),
        ["unityhub"] = ("Unity.UnityHub", "Unity Hub", "Unity Hub.exe"),
        ["epic"] = ("EpicGames.EpicGamesLauncher", "Epic Games 启动器", "EpicGamesLauncher.exe"),
        ["tiled"] = ("Tiled.Tiled", "Tiled", "tiled.exe"),
        ["gdevelop"] = ("GDevelop.GDevelop", "GDevelop", "GDevelop.exe"),
        ["lmms"] = ("LMMS.LMMS", "LMMS", "lmms.exe"),
        ["musescore"] = ("Musescore.Musescore", "MuseScore", "MuseScore4.exe"),
        ["reaper"] = ("Cockos.REAPER", "REAPER", "reaper.exe"),
        ["mp3tag"] = ("FlorianHeidenreich.Mp3tag", "MP3tag", "Mp3tag.exe"),
        ["foobar2000"] = ("PeterPawlowski.foobar2000", "foobar2000", "foobar2000.exe"),
        ["nanazip"] = ("M2Team.NanaZip", "NanaZip", "NanaZip.exe"),
        ["bandizip"] = ("Bandisoft.Bandizip", "Bandizip", "Bandizip.exe"),
        // —— 第二轮实测校正 + 扩容（2026-09-19）——
        ["cursor"] = ("Anysphere.Cursor", "Cursor", "Cursor.exe"),
        // The existing package is the international edition; preserve its official discovery identity.
        ["trae"] = ("ByteDance.Trae", "Trae", "Trae.exe"),
        ["codebuddy"] = ("Tencent.CodeBuddy", "腾讯 CodeBuddy", "CodeBuddy.exe"),
        ["qoder"] = ("Alibaba.Qoder", "Qoder", "Qoder.exe"),
        ["windsurf"] = ("Codeium.Windsurf", "Windsurf", "Windsurf.exe"),
        ["comfyui"] = ("Comfy.ComfyUI-Desktop", "ComfyUI", "ComfyUI.exe"),
        ["openshot"] = ("OpenShot.OpenShot", "OpenShot", "openshot-qt.exe"),
        ["paintnet"] = ("dotPDN.PaintDotNet", "Paint.NET", "paintdotnet.exe"),
        ["cakewalk"] = ("BandLab.Cakewalk", "Cakewalk", "Cakewalk.exe"),
        ["wiztree"] = ("AntibodySoftware.WizTree", "WizTree", "WizTree.exe"),
        ["xmind"] = ("Xmind.Xmind", "XMind", "XMind.exe"),
        ["wsl"] = ("Microsoft.WSL", "WSL2（Linux 子系统）", "wsl.exe"),
        ["ditto"] = ("Ditto.Ditto", "Ditto", "Ditto.exe"),
        ["autohotkey"] = ("AutoHotkey.AutoHotkey", "AutoHotkey", "AutoHotkey64.exe"),
        // —— 第三轮：366 卡全量 winget 排查后接入（2026-09-19）——
        ["claude-desktop"] = ("Anthropic.Claude", "Claude 桌面版", "Claude.exe"),
        ["claude-code"] = ("Anthropic.ClaudeCode", "Claude Code", "claude.exe"),
        ["codex"] = ("OpenAI.Codex", "Codex CLI", "codex.exe"),
        ["kimi"] = ("MoonshotAI.Kimi", "Kimi 桌面版", "Kimi.exe"),
        ["perplexity"] = ("Perplexity.Perplexity", "Perplexity 桌面版", "Perplexity.exe"),
        ["poe"] = ("Quora.Poe", "Poe 桌面版", "Poe.exe"),
        ["monica"] = ("ButterflyEffect.Monica", "Monica", "Monica.exe"),
        ["open-webui"] = ("OpenWebUI.OpenWebUI", "Open WebUI", "open-webui.exe"),
        ["workbuddy"] = ("Tencent.WorkBuddy", "WorkBuddy", "WorkBuddy.exe"),
        ["kiro"] = ("Amazon.Kiro", "Kiro", "Kiro.exe"),
        ["zed"] = ("ZedIndustries.Zed", "Zed", "zed.exe"),
        ["warp"] = ("Warp.Warp", "Warp", "warp.exe"),
        ["langflow"] = ("Langflow.Langflow", "LangFlow", "langflow.exe"),
        ["replit"] = ("Replit.Replit", "Replit", "Replit.exe"),
        ["opencode"] = ("SST.opencode", "OpenCode", "opencode.exe"),
        ["cc-switch"] = ("farion1231.CC-Switch", "CC Switch", "cc-switch.exe"),
        ["amp"] = ("Sourcegraph.Amp", "Amp", "amp.exe"),
        ["doubao"] = ("ByteDance.Doubao", "豆包", "Doubao.exe"),
        ["feishu-desktop"] = ("ByteDance.Feishu", "飞书", "Feishu.exe"),
        ["jianying"] = ("ByteDance.JianyingPro", "剪映专业版", "JianyingPro.exe"),
        ["bcut"] = ("Bilibili.Bcut", "必剪", "Bcut.exe"),
        ["capcut"] = ("ByteDance.CapCut", "CapCut", "CapCut.exe"),
        ["figma"] = ("Figma.Figma", "Figma", "Figma.exe"),
        ["canva"] = ("Canva.Canva", "Canva", "Canva.exe"),
        ["qwen-app"] = ("Alibaba.Qwen", "千问", "Qwen.exe"),
        ["chatglm"] = ("ZhipuAI.ChatGLM", "智谱清言", "ChatGLM.exe"),
        ["youdao-translate"] = ("Youdao.YoudaoTranslate", "有道翻译", "YoudaoTranslate.exe"),
        ["eudic"] = ("EuSoft.Eudic", "欧路词典", "Eudic.exe"),
        ["dida"] = ("Appest.Dida", "滴答清单", "Dida.exe"),
        ["gaoding"] = ("Gaoding.Gaoding", "稿定设计", "Gaoding.exe"),
        ["iflyrec"] = ("iFlytek.iFlyRecSI", "讯飞听见", "iFlyRec.exe"),
        ["yuque"] = ("Alibaba.Yuque", "语雀", "Yuque.exe"),
        ["kdocs"] = ("Kingsoft.KDocs", "金山文档", "Kdocs.exe"),
        ["mubu"] = ("Shilihu.Mubu", "幕布", "Mubu.exe"),
        ["anki"] = ("Anki.Anki", "Anki", "anki.exe"),
        ["deepl"] = ("DeepL.DeepL", "DeepL", "DeepL.exe"),
        ["grammarly"] = ("Grammarly.Grammarly", "Grammarly", "Grammarly.exe"),
        ["languagetool"] = ("Learneo.LanguageTool", "LanguageTool", "LanguageTool.exe"),
        ["effie"] = ("7S2P.Effie", "Effie", "Effie.exe"),
        ["marktext"] = ("MarkText.MarkText", "MarkText", "marktext.exe"),
        ["zettlr"] = ("Zettlr.Zettlr", "Zettlr", "Zettlr.exe"),
        ["joplin"] = ("Joplin.Joplin", "Joplin", "Joplin.exe"),
        ["typora"] = ("appmakes.Typora", "Typora", "Typora.exe"),
        ["pixso"] = ("Bosyun.Pixso", "Pixso", "Pixso.exe"),
        ["mastergo"] = ("JinweiZhiguang.MasterGo", "MasterGo", "MasterGo.exe"),
        ["upscayl"] = ("Upscayl.Upscayl", "Upscayl", "Upscayl.exe"),
        ["waifu2x"] = ("Tenpi.Waifu2xGUI", "Waifu2x GUI", "Waifu2x.exe"),
        ["clipstudio"] = ("Celsys.ClipStudioPaint", "Clip Studio Paint", "CLIPStudioPaint.exe"),
        ["pixelorama"] = ("OramaInteractive.Pixelorama", "Pixelorama", "Pixelorama.exe"),
        ["blockbench"] = ("JannisX11.Blockbench", "Blockbench", "Blockbench.exe"),
        ["materialmaker"] = ("RodZill4.MaterialMaker", "Material Maker", "Material Maker.exe"),
        ["meshlab"] = ("CNRISTI.MeshLab", "MeshLab", "meshlab.exe"),
        ["twine"] = ("ChrisKlimas.Twine", "Twine", "Twine.exe"),
        ["renpy"] = ("RenPy.RenPySDK", "Ren'Py", "renpy.exe"),
        ["ags"] = ("AGSProjectTeam.AdventureGameStudio", "Adventure Game Studio", "AGSEditor.exe"),
        ["solar2d"] = ("Corona.Solar2D", "Solar2D", "Solar2D.exe"),
        ["opentoonz"] = ("DWANGO.OpenToonz", "OpenToonz", "OpenToonz.exe"),
        ["losslesscut"] = ("ch.LosslessCut", "LosslessCut", "LosslessCut.exe"),
        ["subtitleedit"] = ("Nikse.SubtitleEdit", "Subtitle Edit", "SubtitleEdit.exe"),
        ["vrew"] = ("VoyagerX.Vrew", "Vrew", "Vrew.exe"),
        ["filmora"] = ("Wondershare.Filmora", "Filmora", "Filmora.exe"),
        ["descript"] = ("Descript.Descript", "Descript", "Descript.exe"),
        ["splayer"] = ("Shooter.SPlayer", "SPlayer", "SPlayer.exe"),
        ["ocenaudio"] = ("Ocenaudio.Ocenaudio", "Ocenaudio", "ocenaudio.exe"),
        ["goldwave"] = ("GoldWave.GoldWave", "GoldWave", "GoldWave.exe"),
        ["surgext"] = ("SurgeSynth.SurgeXT", "Surge XT", "surge-xt.exe"),
        ["voicemeeter"] = ("VB-Audio.Voicemeeter", "Voicemeeter", "voicemeeter.exe"),
        ["acestudio"] = ("ACEStudio.ACEStudio", "ACE Studio", "ACE Studio.exe"),
        ["snipaste"] = ("liule.Snipaste", "Snipaste", "Snipaste.exe"),
        ["quicker"] = ("LiErHeXun.Quicker", "Quicker", "Quicker.exe"),
        ["utools"] = ("Yuanli.uTools", "uTools", "uTools.exe"),
        ["rider"] = ("JetBrains.Rider", "JetBrains Rider", "rider64.exe"),
        ["m365copilot"] = ("Microsoft.365Copilot", "Microsoft 365 Copilot", "Copilot.exe"),
        ["apifox"] = ("Ruihu.Apifox", "Apifox", "Apifox.exe"),
        // —— ZXAI 2026-09-22：用户点名补录（winget 有官方包但之前漏接）——
        ["yuanbao"] = ("Tencent.Yuanbao", "腾讯元宝", "Yuanbao.exe"),
        ["github-desktop"] = ("GitHub.GitHubDesktop", "GitHub Desktop", "GitHubDesktop.exe"),
        ["windows-terminal"] = ("Microsoft.WindowsTerminal", "Windows Terminal", "wt.exe"),
        ["devin"] = ("CognitionAI.DevinDesktop", "Devin", "Devin.exe"),
        ["clion"] = ("JetBrains.CLion", "CLion", "clion64.exe"),
        ["bruno"] = ("Bruno.Bruno", "Bruno", "Bruno.exe"),
        ["fork"] = ("Fork.Fork", "Fork", "Fork.exe"),
        ["goldendict"] = ("GoldenDict.GoldenDict", "GoldenDict", "GoldenDict.exe"),
    };

    public static IEnumerable<string> KnownTargets => Targets.Keys.Concat(PipTargets.Keys);

    /// <summary>Describe the exact allow-listed variant; a shared product name is not an installation identity.</summary>
    internal static IReadOnlyDictionary<string, string> GetTargetDescriptions()
        => KnownTargets.ToDictionary(key => key, key =>
            key == "trae" ? "Trae 国际版；ByteDance.Trae；桌面入口；首次登录需要海外网络；不是国内版安装目标"
            : TryGetToolAccessMetadata(key, out var target)
                ? $"{target.Name}{(key is "claude-code" or "opencode" ? " CLI" : "")}；{(target.IsGui ? "桌面入口" : "非桌面安装项")}" : key,
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Only existing pip installation targets depend on the fixed Python target.</summary>
    internal static IReadOnlyList<string> GetInstallDependencies(string key)
        => PipTargets.ContainsKey((key ?? "").Trim()) ? ["python"] : [];

    internal sealed record ToolAccessMetadata(string TargetKey, string Name, bool IsGui,
        IReadOnlyList<string> ExecutableNames);

    // A positive list: an executable alone does not make a CLI, service or SDK a desktop app.
    private static readonly HashSet<string> GuiAccessTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "godot", "pixelorama", "tiled", "audacity", "blender", "vscode", "unityhub", "gdevelop",
        "cherry-studio", "chatbox", "jan", "lmstudio", "gimp", "krita", "inkscape", "obs",
        "handbrake", "kdenlive", "shotcut", "potplayer", "everything", "notepadpp", "sumatrapdf",
        "libreoffice", "wps", "notion", "obsidian", "sublime", "dbeaver", "postman", "docker",
        "wireshark", "heidisql", "winmerge", "sourcetree", "vs-community", "epic", "musescore",
        "reaper", "lmms", "github-desktop", "cursor", "trae", "codebuddy", "qoder", "windsurf", "7zip", "claude-desktop",
        // Existing desktop targets and their fixed EXE names are already declared in Targets.
        "figma", "canva", "cc-switch", "powertoys", "mobaxterm",
        "mp3tag", "foobar2000", "nanazip", "bandizip", "comfyui", "openshot", "paintnet",
        "cakewalk", "wiztree", "xmind", "ditto", "kimi", "perplexity", "poe", "monica",
        "workbuddy", "kiro", "zed", "warp", "replit", "doubao", "feishu-desktop", "jianying",
        "bcut", "capcut", "qwen-app", "chatglm", "youdao-translate", "eudic", "dida", "gaoding",
        "iflyrec", "yuque", "kdocs", "mubu", "anki", "deepl", "grammarly", "languagetool",
        "effie", "marktext", "zettlr", "joplin", "typora", "pixso", "mastergo", "upscayl",
        "waifu2x", "clipstudio", "blockbench", "materialmaker", "meshlab", "twine", "renpy",
        "ags", "solar2d", "opentoonz", "losslesscut", "subtitleedit", "vrew", "filmora",
        "descript", "splayer", "ocenaudio", "goldwave", "surgext", "voicemeeter", "acestudio",
        "snipaste", "quicker", "utools", "rider", "m365copilot", "apifox", "yuanbao",
        "windows-terminal", "devin", "clion", "bruno", "fork", "goldendict",
    };

    internal static bool TryGetToolAccessMetadata(string? targetKey, out ToolAccessMetadata metadata)
    {
        var key = (targetKey ?? "").Trim().ToLowerInvariant();
        if (key == "winrar")
        {
            // Access to a verified existing provider does not add an automatic installation target.
            metadata = new(key, "WinRAR", true, ["WinRAR.exe"]);
            return true;
        }
        if (Targets.TryGetValue(key, out var target))
        {
            string[] names = key switch
            {
                "vscode" => ["Code.exe"], // code.cmd is the CLI wrapper, not the desktop entry.
                "godot" => ["godot.exe", "Godot_v*_win64.exe", "Godot_v*_win32.exe"],
                "7zip" => ["7zFM.exe"], // Opening the application uses the GUI, never its CLI.
                _ => [target.ProbeExe],
            };
            metadata = new(key, target.Name, GuiAccessTargets.Contains(key), names);
            return true;
        }
        if (PipTargets.TryGetValue(key, out var pip))
        {
            metadata = new(key, pip.Name, false, [pip.ProbeCmd + ".exe"]);
            return true;
        }
        metadata = null!;
        return false;
    }

    /// <summary>Read-only access lookup. Registered paths require executable identity checks; never runs a target.</summary>
    internal static Task<string?> ResolveToolAccessPathAsync(string targetKey, CancellationToken ct)
        => Task.Run(() => PipTargets.ContainsKey(targetKey.Trim())
            ? ProbeInstalledPipTool(targetKey, ct)?.ExecutablePath : ResolveToolAccessPath(targetKey, ct), ct);

    /// <summary>Read-only OS/path evidence for preparation. WinRAR is a reuse provider, not an install target.</summary>
    internal static Task<ToolFlows.InstalledToolEvidence?> ProbeInstalledToolAsync(string targetKey, CancellationToken ct)
        => Task.Run(() => ProbeInstalledTool(targetKey, ct), ct);

    internal static ToolFlows.InstalledToolEvidence? ProbeInstalledTool(string targetKey, CancellationToken ct)
    {
        var key = (targetKey ?? "").Trim().ToLowerInvariant();
        if (PipTargets.ContainsKey(key)) return ProbeInstalledPipTool(key, ct);
        ToolAccessMetadata metadata;
        if (key == "winrar") metadata = new(key, "WinRAR", true, ["WinRAR.exe"]);
        else if (!TryGetToolAccessMetadata(key, out metadata)) return null;
        if (key == "7zip") metadata = metadata with { ExecutableNames = ["7z.exe", "7zFM.exe"] };
        var hasOsRegistration = false;
        string? registeredVersion = null;
        var path = ResolveToolAccessPath(key, ct, metadata, requireExecutable: true, onOsRegistration: version =>
        {
            hasOsRegistration = true;
            registeredVersion = version;
        });
        if (path is null)
            return hasOsRegistration && CanUseOsRegistrationWithoutExecutable(key)
                ? new ToolFlows.InstalledToolEvidence(key, metadata.Name, Version: registeredVersion) : null;
        var directory = Path.GetDirectoryName(path)!;
        string? version = null;
        if (EnvironmentProbe.UsesSharedRuntimeDiscovery(key))
        {
            if (!EnvironmentProbe.TryValidateRuntimeCandidate(key, path, out var runtimeVersion)) return null;
            version = runtimeVersion;
        }
        else
        {
            try { version = FileVersionInfo.GetVersionInfo(path).ProductVersion; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return new ToolFlows.InstalledToolEvidence(key, metadata.Name, path, version,
            key == "7zip" && File.Exists(Path.Combine(directory, "7z.exe")),
            key == "7zip" && File.Exists(Path.Combine(directory, "7z.dll")));
    }

    internal static bool CanUseOsRegistrationWithoutExecutable(string targetKey)
    {
        var key = targetKey.Trim().ToLowerInvariant();
        return key is not ("7zip" or "winrar" or "claude-desktop" or "claude-code") && !EnvironmentProbe.UsesSharedRuntimeDiscovery(key);
    }

    private static string? ResolveToolAccessPath(string targetKey, CancellationToken ct,
        ToolAccessMetadata? suppliedMetadata = null, bool requireExecutable = false,
        Action<string?>? onOsRegistration = null)
    {
        var metadata = suppliedMetadata;
        if (metadata is null && !TryGetToolAccessMetadata(targetKey, out metadata)) return null;
        string? location = null;
        bool IsUsableRuntimeCandidate(string path) => metadata.TargetKey is "claude-desktop" or "claude-code"
            ? InstalledToolReuse.MatchesProductIdentity(metadata, ReadProductName(path))
            : !EnvironmentProbe.UsesSharedRuntimeDiscovery(metadata.TargetKey)
                || EnvironmentProbe.TryValidateRuntimeCandidate(metadata.TargetKey, path, out _);
        string? Probe(string directory, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory)) return null;
            foreach (var name in metadata.ExecutableNames)
            {
                var hit = FindFileRecursive(directory, name, depth);
                if (hit is not null && File.Exists(hit) && IsUsableRuntimeCandidate(hit)) return hit;
            }
            return null;
        }

        // Share the application center's existing registry. A record is only a search hint:
        // verify the fixed executable name and that file's own product identity before reuse.
        var records = SoftwareRegistry.Load();
        var registered = EnvironmentProbe.UsesSharedRuntimeDiscovery(metadata.TargetKey)
            ? EnvironmentProbe.FindRegisteredRuntimeCandidate(metadata.TargetKey, records, ExpandRegisteredPath)
            : InstalledToolReuse.FindRegisteredPath(metadata, records, ExpandRegisteredPath, ReadProductName);
        if (registered is not null && IsUsableRuntimeCandidate(registered)) return registered;

        IEnumerable<string> ExpandRegisteredPath(string path)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path)) return [path];
            return Directory.Exists(path) && Probe(path, 3) is { } executable ? [executable] : [];
        }
        static string? ReadProductName(string path)
        {
            try { return FileVersionInfo.GetVersionInfo(path).ProductName; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        // Portable packages are scoped to the fixed package ID, never searched across other packages.
        if (Targets.TryGetValue(metadata.TargetKey, out var target))
        {
            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Packages");
            try
            {
                if (Directory.Exists(packages))
                    foreach (var directory in Directory.EnumerateDirectories(packages, target.WingetId + "_*"))
                    {
                        location ??= directory;
                        if (Probe(directory, 3) is { } hit) return hit;
                    }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var paths = new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine }
            .SelectMany(scope => (Environment.GetEnvironmentVariable("PATH", scope) ?? "").Split(Path.PathSeparator))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in paths)
        {
            var pathDirectory = directory.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(pathDirectory)) continue;
            if (Probe(pathDirectory, 0) is { } hit) return hit;
            if (metadata.TargetKey == "vscode" && Path.GetFileName(pathDirectory).Equals("bin", StringComparison.OrdinalIgnoreCase)
                && Path.GetDirectoryName(pathDirectory) is { } parent && Probe(parent, 0) is { } editor)
                return editor;
        }

        // Use OS installation metadata and fixed display names, not SoftwareRegistry's editable Path.
        foreach (var (hive, view) in new[]
        {
            (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64),
            (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32),
            (Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Registry64),
            (Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Registry32),
        })
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    ct.ThrowIfCancellationRequested();
                    using var sub = key.OpenSubKey(name);
                    var display = sub?.GetValue("DisplayName") as string ?? "";
                    if (!InstalledToolReuse.MatchesOsDisplayName(metadata, display)) continue;
                    onOsRegistration?.Invoke(sub?.GetValue("DisplayVersion") as string);
                    var directory = sub?.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(directory) && Path.IsPathFullyQualified(directory) && Directory.Exists(directory))
                    {
                        location ??= directory;
                        if (Probe(directory, 3) is { } hit) return hit;
                    }
                    var icon = (sub?.GetValue("DisplayIcon") as string ?? "").Trim();
                    if (icon.StartsWith('"'))
                    {
                        var closingQuote = icon.IndexOf('"', 1);
                        icon = closingQuote > 1 ? icon[1..closingQuote] : "";
                    }
                    else if (icon.LastIndexOf(',') is var separator && separator > 0 &&
                        int.TryParse(icon[(separator + 1)..], out _)) icon = icon[..separator].Trim();
                    if (Path.IsPathFullyQualified(icon) && File.Exists(icon) && metadata.ExecutableNames.Any(pattern =>
                        System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(icon), ignoreCase: true))
                        && IsUsableRuntimeCandidate(icon))
                        return icon;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
        }
        var fixedDirectories = metadata.TargetKey switch
        {
            "vscode" => new[] { "Microsoft VS Code" },
            "blender" => new[] { "Blender Foundation" },
            "unityhub" => new[] { "Unity Hub", "UnityHub" },
            "python" => new[] { "Python", metadata.Name },
            "node" => new[] { "nodejs", "Node.js" },
            "claude-desktop" => new[] { "Claude", "Claude Desktop", "AnthropicClaude" },
            _ => new[] { metadata.Name },
        };
        var installationRoots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        };
        if (metadata.TargetKey == "claude-desktop")
            installationRoots.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        foreach (var root in installationRoots)
        {
            if (!Path.IsPathFullyQualified(root)) continue;
            foreach (var name in fixedDirectories)
            {
                var directory = Path.Combine(root, name);
                if (!Directory.Exists(directory)) continue;
                location ??= directory;
                if (Probe(directory, 3) is { } hit) return hit;
            }
        }
        if (metadata.TargetKey == "python")
        {
            // Store Python's actual package directory is valid; the WindowsApps root alias is not.
            foreach (var storeRoot in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"),
            })
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!Directory.Exists(storeRoot)) continue;
                    foreach (var package in Directory.EnumerateDirectories(storeRoot, "PythonSoftwareFoundation.Python.*"))
                        if (Probe(package, 3) is { } hit) return hit;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (System.Security.SecurityException) { }
            }
        }
        return requireExecutable ? null : location;
    }

    /// <summary>ZXAI 2026-09-22：pip 类一键安装目标（winget 无包、官方以 pip 分发——如 vLLM；装前自动预检 Python）。</summary>
    private static readonly Dictionary<string, (string PipPackage, string Name, string ProbeCmd)> PipTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vllm"] = ("vllm", "vLLM", "vllm"),
        ["inspect-ai"] = ("inspect-ai", "Inspect", "inspect"),
        ["ragas"] = ("ragas", "RAGAS", "ragas"),
        ["helm"] = ("crfm-helm", "HELM", "helm"),
        ["deepeval"] = ("deepeval", "DeepEval", "deepeval"),
    };

    private static ToolFlows.InstalledToolEvidence? ProbeInstalledPipTool(string targetKey, CancellationToken ct)
    {
        var python = ProbeInstalledTool("python", ct);
        if (python?.ExecutablePath is not { } executable) return null;
        return ProbePipPackageFiles(targetKey, executable, python.Version ?? "",
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ct);
    }

    /// <summary>
    /// File-only package evidence for the same verified Python used by installation. A package's
    /// METADATA establishes installation; only its fixed console EXE establishes an opening entry.
    /// This intentionally does not run Python, import a package or depend on the current PATH.
    /// Tests pass only their own directories and synthetic METADATA files.
    /// </summary>
    internal static ToolFlows.InstalledToolEvidence? ProbePipPackageFiles(string targetKey,
        string verifiedPythonPath, string pythonVersion, string roamingAppData, CancellationToken ct = default)
    {
        var key = (targetKey ?? "").Trim().ToLowerInvariant();
        if (!PipTargets.TryGetValue(key, out var pip) || !Path.IsPathFullyQualified(verifiedPythonPath)
            || !Path.GetFileName(verifiedPythonPath).Equals("python.exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(verifiedPythonPath)) return null;
        var pythonDirectory = Path.GetDirectoryName(verifiedPythonPath)!;
        var installationRoot = Path.GetFileName(pythonDirectory).Equals("Scripts", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(pythonDirectory)! : pythonDirectory;
        var locations = new List<(string Packages, string Scripts)>
        {
            (Path.Combine(installationRoot, "Lib", "site-packages"), Path.Combine(installationRoot, "Scripts")),
        };
        var version = System.Text.RegularExpressions.Regex.Match(pythonVersion.Trim(),
            @"\A(?:v|Python\s+)?(?<major>\d+)\.(?<minor>\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (version.Success && Path.IsPathFullyQualified(roamingAppData)
            && !File.Exists(Path.Combine(installationRoot, "pyvenv.cfg"))
            && !Path.GetFileName(pythonDirectory).Equals("Scripts", StringComparison.OrdinalIgnoreCase))
        {
            var userBase = Path.Combine(roamingAppData, "Python", "Python" + version.Groups["major"].Value + version.Groups["minor"].Value);
            locations.Add((Path.Combine(userBase, "site-packages"), Path.Combine(userBase, "Scripts")));
        }
        foreach (var location in locations)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!IsPlainDirectory(location.Packages)) continue;
                foreach (var directory in Directory.EnumerateDirectories(location.Packages, "*.dist-info",
                    new System.IO.EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    ct.ThrowIfCancellationRequested();
                    var metadata = Path.Combine(directory, "METADATA");
                    try
                    {
                        var info = new FileInfo(metadata);
                        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        var header = ReadHeader(metadata);
                        if (header is null) continue;
                        var names = header.Where(line => line.StartsWith("Name:", StringComparison.OrdinalIgnoreCase)).ToArray();
                        var versions = header.Where(line => line.StartsWith("Version:", StringComparison.OrdinalIgnoreCase)).ToArray();
                        if (names.Length != 1 || versions.Length != 1
                            || NormalizePipName(names[0][5..].Trim()) != NormalizePipName(pip.PipPackage)) continue;
                        var packageVersion = versions[0][8..].Trim();
                        if (!System.Text.RegularExpressions.Regex.IsMatch(packageVersion, @"\A[A-Za-z0-9][A-Za-z0-9.+!_-]{0,119}\z")) continue;
                        string? entry = null;
                        try
                        {
                            if (IsPlainDirectory(location.Scripts))
                            {
                                var candidate = Path.Combine(location.Scripts, pip.ProbeCmd + ".exe");
                                if (File.Exists(candidate) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) == 0) entry = candidate;
                            }
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                        catch (System.Security.SecurityException) { }
                        return new(key, pip.Name, entry, packageVersion);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (System.Security.SecurityException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
        }
        return null;

        static bool IsPlainDirectory(string path)
        {
            if (!Directory.Exists(path)) return false;
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0;
        }
        static string NormalizePipName(string name) => System.Text.RegularExpressions.Regex.Replace(name, @"[-_.]+", "-").ToLowerInvariant();
        static string[]? ReadHeader(string path)
        {
            const int maximumHeaderBytes = 64 * 1024;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var bytes = new MemoryStream();
            var lineBytes = 0;
            var previous = -1;
            while (bytes.Length < maximumHeaderBytes)
            {
                var next = file.ReadByte();
                if (next < 0) return Decode();
                bytes.WriteByte((byte)next);
                if (next == '\n')
                {
                    // Stop at the header/body separator. Large README-style package descriptions
                    // do not affect installation evidence and are never consumed or parsed.
                    if (lineBytes == 0 || lineBytes == 1 && previous == '\r') return Decode();
                    lineBytes = 0;
                }
                else lineBytes++;
                previous = next;
            }
            return null;

            string[] Decode() => Encoding.UTF8.GetString(bytes.ToArray())
                .Split(["\r\n", "\n"], StringSplitOptions.None).TakeWhile(line => line.Length != 0).ToArray();
        }
    }

    public static string DescribeTargets()
        => string.Join("、", Targets.Select(kv => $"{kv.Key}（{kv.Value.Name}）"));

    /// <summary>检测某目标是否已安装（供「一键安装」依赖预检——已装的跳过不重装）。</summary>
    public static async Task<bool> IsInstalledAsync(string key, CancellationToken ct)
    {
        // Exact target only: an existing WinRAR must never imply that 7z.exe is installed.
        if (await ProbeInstalledToolAsync(key, ct) is not null) return true;
        if (PipTargets.TryGetValue(key ?? "", out var p))
        {
            var py = await FindPythonAsync(ct);
            if (py is null) return false;
            var (pc, po) = await RunAsync(py.Value.FileName, $"{py.Value.PrefixArgs}-m pip show {p.PipPackage}", 60_000, ct);
            return pc == 0 && po.Contains("Name:", StringComparison.OrdinalIgnoreCase);
        }
        if (!Targets.TryGetValue(key ?? "", out var t)) return false;
        var (code, output) = await RunAsync("winget", $"list --id {t.WingetId} -e", 90_000, ct);
        return code == 0 && output.Contains(t.WingetId, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<string> InstallAsync(string key, Action<string>? progress, CancellationToken ct)
    {
        if (await ProbeInstalledToolAsync(key, ct) is { } existing)
            return LocalizationService.L("AiFlow_ReuseDetected",
                LocalizationService.CurrentLanguage == LocalizationService.EnglishLanguage
                    ? "Detected {0}; no additional preparation is needed." : "已检测到 {0}；无需重复准备。")
                .Replace("{0}", existing.Name);
        if (PipTargets.TryGetValue(key ?? "", out var p))
            return await InstallPipAsync(p, progress, ct);
        if (!Targets.TryGetValue(key ?? "", out var t))
            return $"未知安装目标：{key}（支持：{DescribeTargets()}）";

        // 1) 已装检测（winget list）
        var (listCode, listOut) = await RunAsync("winget", $"list --id {t.WingetId} -e", 90_000, ct);
        if (listCode == 0 && listOut.Contains(t.WingetId, StringComparison.OrdinalIgnoreCase))
        {
            EnvironmentScopeManager.RefreshProcessPath();
            var existingPath = await FindInstalledPathAsync(t);
            var existingVer = ReadInstalledFileVersion(existingPath);
            SoftwareRegistry.Add(t.Name, existingPath ?? $"winget:{t.WingetId}", existingVer, source: "system",
                note: MiscTexts.TSub($"系统安装 · winget {t.WingetId}"), wingetId: t.WingetId);
            return existingPath is null
                ? MiscTexts.TSub($"{t.Name} 已经装过了（winget 可管理；升级用 winget upgrade --id {t.WingetId}）。")
                : MiscTexts.TSub($"{t.Name} 已经装过了：{existingPath}（{existingVer}）。登记信息已刷新。");
        }

        // 2) 安装：优先 user scope（免 UAC、装到用户目录），不支持时回退默认 scope
        progress?.Invoke($"winget install {t.WingetId}");
        var (code, output) = await RunAsync("winget",
            CreateWingetInstallArguments(key, userScope: true),
            600_000, ct);
        if (code != 0 && LooksLikeScopeUnsupported(output))
        {
            progress?.Invoke(MiscTexts.T("user scope 不受支持，按默认 scope 重试"));
            (code, output) = await RunAsync("winget",
                CreateWingetInstallArguments(key, userScope: false),
                600_000, ct);
        }

        // Installer messages can quote a prior success before a later failure.
        // The runner separately checks actual installation; text never overrides a failed command.
        var ok = InstallCommandSucceeded(code);
        if (!ok)
        {
            var tail = string.Join('\n', output.Split('\n').TakeLast(6)).Trim();
            return MiscTexts.TSub($"❌ {t.Name} 安装失败（winget 退出码 {code}）：\n{tail}");
        }

        // 4) 刷新进程 PATH（注册表最新值注入，装完立即可用）
        EnvironmentScopeManager.RefreshProcessPath();

        // 5) 探测路径与版本 → 登记应用中心
        var exePath = await FindInstalledPathAsync(t);
        var version = ReadInstalledFileVersion(exePath);

        SoftwareRegistry.Add(t.Name, exePath ?? $"winget:{t.WingetId}", version, source: "system",
            note: MiscTexts.TSub($"系统安装 · winget {t.WingetId}"), wingetId: t.WingetId);

        return exePath is null
            ? MiscTexts.TSub($"✅ {t.Name} 安装完成（winget {t.WingetId}）。已登记到应用中心；个别软件需要重开终端才在 PATH 生效。")
            : MiscTexts.TSub($"✅ {t.Name} 安装完成：{exePath}（{version}）。已全局可用并登记到应用中心。");
    }

    /// <summary>ZXAI：pip 类安装（先找 Python → pip show 检测 → pip install → 登记应用中心）。</summary>
    private static async Task<string> InstallPipAsync((string PipPackage, string Name, string ProbeCmd) p, Action<string>? progress, CancellationToken ct)
    {
        var py = await FindPythonAsync(ct);
        if (py is null)
            return MiscTexts.TSub($"❌ {p.Name} 需要 Python 环境：请先安装 Python（可先在工具箱里一键装 Python），装好后重试。");

        progress?.Invoke($"python -m pip show {p.PipPackage}");
        var (showCode, showOut) = await RunAsync(py.Value.FileName, $"{py.Value.PrefixArgs}-m pip show {p.PipPackage}", 60_000, ct);
        if (showCode == 0 && showOut.Contains("Name:", StringComparison.OrdinalIgnoreCase))
        {
            var ver0 = ExtractPipVersion(showOut);
            SoftwareRegistry.Add(p.Name, $"pip:{p.PipPackage}", ver0, source: "system", note: MiscTexts.TSub($"pip 安装 · {p.PipPackage}"));
            return MiscTexts.TSub($"{p.Name} 已经装过了（pip {p.PipPackage} {ver0}）。升级：python -m pip install -U {p.PipPackage}");
        }

        progress?.Invoke(MiscTexts.TSub($"python -m pip install {p.PipPackage}（从官方源下载，体积较大，请耐心等待）"));
        var (code, output) = await RunAsync(py.Value.FileName, $"{py.Value.PrefixArgs}-m pip install {p.PipPackage}", 1_800_000, ct);
        var ok = InstallCommandSucceeded(code);
        if (!ok)
        {
            var tail = string.Join('\n', output.Split('\n').TakeLast(6)).Trim();
            return MiscTexts.TSub($"❌ {p.Name} 安装失败（pip 退出码 {code}）：\n{tail}");
        }

        var (_, showOut2) = await RunAsync(py.Value.FileName, $"{py.Value.PrefixArgs}-m pip show {p.PipPackage}", 60_000, ct);
        var ver = ExtractPipVersion(showOut2);
        EnvironmentScopeManager.RefreshProcessPath();
        SoftwareRegistry.Add(p.Name, $"pip:{p.PipPackage}", ver, source: "system", note: MiscTexts.TSub($"pip 安装 · {p.PipPackage}"));
        return MiscTexts.TSub($"✅ {p.Name} 安装完成（pip {p.PipPackage} {ver}）。已登记到应用中心。");
    }

    private static string ExtractPipVersion(string pipShowOutput)
        => pipShowOutput.Split('\n').FirstOrDefault(l => l.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))?.Split(':').LastOrDefault()?.Trim() ?? "";

    private static async Task<(string FileName, string PrefixArgs)?> FindPythonAsync(CancellationToken ct)
    {
        // Match preparation's verified file discovery. A fresh PATH, launcher alias or unrelated
        // Python environment must not choose a different installation than the subsequent probe.
        var python = await ProbeInstalledToolAsync("python", ct);
        return python?.ExecutablePath is { } path ? (path, "") : null;
    }

    private static bool LooksLikeScopeUnsupported(string output)
        => output.Contains("scope", StringComparison.OrdinalIgnoreCase)
           && (output.Contains("not supported", StringComparison.OrdinalIgnoreCase)
               || output.Contains("不支持", StringComparison.Ordinal));

    /// <summary>Arguments come from the same fixed target map as installation; no URL, name or command inference.</summary>
    internal static string CreateWingetInstallArguments(string targetKey, bool userScope)
    {
        if (!Targets.TryGetValue(targetKey, out var target))
            throw new ArgumentException("Unknown fixed winget installation target.", nameof(targetKey));
        return $"install --id {target.WingetId} -e" + (userScope ? " --scope user" : "")
            + " --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";
    }

    internal static bool InstallCommandSucceeded(int exitCode) => exitCode == 0;

    /// <summary>Read metadata without launching the installed program, including GUI apps that ignore --version.</summary>
    internal static string ReadInstalledFileVersion(string? path)
        => ReadInstalledFileVersion(path, File.Exists, candidate =>
        {
            var info = FileVersionInfo.GetVersionInfo(candidate);
            return string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion : info.ProductVersion;
        });

    internal static string ReadInstalledFileVersion(string? path, Func<string, bool> fileExists,
        Func<string, string?> readVersion)
    {
        if (string.IsNullOrWhiteSpace(path) || !fileExists(path)) return "";
        try
        {
            return (readVersion(path) ?? "").Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or ArgumentException or NotSupportedException)
        {
            return "";
        }
    }

    private static async Task<string?> FindInstalledPathAsync((string WingetId, string Name, string ProbeExe) t)
    {
        // ① winget 包缓存目录（portable 包首选：目录名以包 Id 开头）——避免命中宿主 PATH 里的同名程序
        try
        {
            var wingetPackages = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(wingetPackages))
            {
                foreach (var dir in Directory.GetDirectories(wingetPackages, t.WingetId + "_*"))
                {
                    var hit = string.IsNullOrEmpty(t.ProbeExe)
                        ? FindFirstLooseExe(dir, 3)
                        : FindFileRecursive(dir, t.ProbeExe, 3);
                    if (hit is not null) return hit;
                }
                // 有的包在 Packages 根下以别的命名放了一个顶层可执行
                var rootHit = FindFileRecursive(wingetPackages, t.ProbeExe, 3);
                if (rootHit is not null) return rootHit;
            }
        }
        catch { }

        // ② where（进程 PATH 已刷新）+ .cmd 变体（无 ProbeExe 的目标跳过）
        if (!string.IsNullOrEmpty(t.ProbeExe))
        foreach (var exe in new[] { t.ProbeExe, t.ProbeExe.Replace(".exe", ".cmd") })
        {
            var (code, output) = await RunAsync("where.exe", exe, 15_000, CancellationToken.None);
            var path = output.Split('\n').Select(x => x.Trim())
                .FirstOrDefault(x => x.Length > 2 && x.Contains(":\\"));
            if (code == 0 && path is not null) return path;
        }

        // 注册表 Uninstall 列表兜底（按 DisplayName 模糊匹配）
        try
        {
            foreach (var (hive, subPath) in new[]
            {
                (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Microsoft.Win32.Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            })
            {
                using var key = hive.OpenSubKey(subPath);
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    using var sub = key.OpenSubKey(name);
                    var display = sub?.GetValue("DisplayName") as string ?? "";
                    if (display.Contains(t.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        var loc = sub?.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(loc) && Directory.Exists(loc)) return loc;
                    }
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>宽松找主程序：递归浅找第一个"不像卸载/更新器"的 exe（无 ProbeExe 的目标用）。</summary>
    private static string? FindFirstLooseExe(string root, int maxDepth)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*.exe", SearchOption.TopDirectoryOnly))
            {
                var n = Path.GetFileName(f).ToLowerInvariant();
                if (n.Contains("uninstall") || n.Contains("update") || n.Contains("setup") ||
                    n.Contains("maintenancetool") || n.Contains("crash")) continue;
                return f;
            }
            if (maxDepth <= 0) return null;
            foreach (var d in Directory.GetDirectories(root))
            {
                var hit = FindFirstLooseExe(d, maxDepth - 1);
                if (hit is not null) return hit;
            }
        }
        catch { }
        return null;
    }

    private static string? FindFileRecursive(string root, string fileName, int maxDepth)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, fileName, SearchOption.TopDirectoryOnly)) return f;
            if (maxDepth <= 0) return null;
            foreach (var d in Directory.GetDirectories(root))
            {
                var hit = FindFileRecursive(d, fileName, maxDepth - 1);
                if (hit is not null) return hit;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Exact identity preflight and post-check; registration removal remains a separate user action.</summary>
    public static async Task<string> UninstallAsync(string wingetId, CancellationToken ct)
        => (await UninstallRegisteredPackageAsync(wingetId, ct)).Message;

    internal static Task<RegisteredPackageInspection> InspectRegisteredPackageAsync(string wingetId, CancellationToken ct)
        => CreateRegisteredPackageOperations().InspectAsync(wingetId, ct);

    internal static Task<RegisteredPackageUninstallResult> UninstallRegisteredPackageAsync(string wingetId, CancellationToken ct)
        => CreateRegisteredPackageOperations().UninstallAsync(wingetId, ct);

    private static RegisteredPackageOperations CreateRegisteredPackageOperations() => new(
        (verb, id, ct) => RunCommandAsync(CreatePackageManagementCommand(verb, id), verb == "list" ? 30_000 : 600_000, ct),
        (id, ct) => Task.Run(() => ReadRegisteredPackageEvidence(id, ct), ct));

    internal static ProcessStartInfo CreatePackageManagementCommand(string verb, string id)
    {
        if (verb is not ("list" or "uninstall") || !RegisteredPackageOperations.IsPackageId(id))
            throw new ArgumentException("Invalid exact package operation.");
        var start = new ProcessStartInfo { FileName = "winget" };
        foreach (var arg in new[] { verb, "--id", id, "--exact", "--disable-interactivity" })
            start.ArgumentList.Add(arg);
        return start;
    }

    private static LocalPackagePresence ReadRegisteredPackageEvidence(string wingetId, CancellationToken ct)
    {
        var records = SoftwareRegistry.Load().Where(r => string.Equals(r.WingetId, wingetId, StringComparison.OrdinalIgnoreCase)).ToArray();
        var allRecordedPathsMissing = records.Length > 0;
        var readFailed = false;
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            var path = AppCenterActionPresentation.NormalizeLocalPath(record.Path);
            // A winget: pseudo-path, invalid path or unavailable drive cannot prove that software is absent.
            if (path is null || !Directory.Exists(Path.GetPathRoot(path))) { allRecordedPathsMissing = false; continue; }
            try { _ = File.GetAttributes(path); return LocalPackagePresence.Present; }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { readFailed = true; }
        }

        var target = Targets.FirstOrDefault(pair => string.Equals(pair.Value.WingetId, wingetId, StringComparison.OrdinalIgnoreCase));
        if (target.Key is null || !TryGetToolAccessMetadata(target.Key, out var metadata))
            return readFailed ? LocalPackagePresence.CheckFailed : LocalPackagePresence.Unknown;
        try
        {
            if (ResolveToolAccessPath(target.Key, ct, metadata, requireExecutable: true) is not null)
                return LocalPackagePresence.Present;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { readFailed = true; }

        // Look in both user/machine and 32/64-bit registration views without changing scope or launching an uninstaller.
        foreach (var (hive, view) in new[]
        {
            (Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Registry64),
            (Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Registry32),
            (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64),
            (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32),
        })
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    ct.ThrowIfCancellationRequested();
                    using var sub = uninstall.OpenSubKey(name);
                    if (sub?.GetValue("DisplayName") is string display && InstalledToolReuse.MatchesOsDisplayName(metadata, display))
                        return LocalPackagePresence.Present;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { readFailed = true; }
        }
        return readFailed ? LocalPackagePresence.CheckFailed : allRecordedPathsMissing
            ? LocalPackagePresence.RecordedPathMissing : LocalPackagePresence.Unknown;
    }

    /// <summary>检查登记的 winget 包是否有可用更新。返回 {wingetId: 新版本}（版本可能为空串=有更新但解析失败）。</summary>
    public static async Task<Dictionary<string, string>> CheckUpgradesAsync(IEnumerable<string> wingetIds, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ids = wingetIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0) return result;
        try
        {
            var (_, output) = await RunAsync("winget", "upgrade --include-unknown --disable-interactivity", 120_000, ct);
            foreach (var id in ids)
            {
                string? hitLine = null;
                foreach (var line in output.Split('\n'))
                {
                    if (line.Contains(id, StringComparison.OrdinalIgnoreCase))
                    {
                        hitLine = line.Trim();
                        break;
                    }
                }
                if (hitLine is null) continue;
                // 行格式: Name Id Version Available Source —— 从行尾解析（名称可能含空格）
                var m = System.Text.RegularExpressions.Regex.Match(hitLine, @"(\S+)\s+(\S+)\s+winget\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                result[id] = m.Success ? m.Groups[2].Value : "";
            }
        }
        catch { }
        return result;
    }

    /// <summary>升级 winget 包（装完刷新探测与登记版本）。</summary>
    public static async Task<string> UpgradeAsync(string wingetId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(wingetId)) return MiscTexts.T("升级失败：缺少 winget 包 Id");
        var (code, output) = await RunAsync("winget", $"upgrade --id {wingetId} -e --accept-package-agreements --accept-source-agreements --disable-interactivity", 900_000, ct);
        var ok = code == 0
                 || output.Contains("Successfully installed", StringComparison.OrdinalIgnoreCase)
                 || output.Contains("已成功安装", StringComparison.Ordinal)
                 || output.Contains("already installed", StringComparison.OrdinalIgnoreCase)
                 || output.Contains("已经安装", StringComparison.Ordinal);
        if (!ok)
        {
            var tail = string.Join('\n', output.Split('\n').TakeLast(6)).Trim();
            return MiscTexts.TSub($"❌ 升级失败（winget 退出码 {code}）：\n{tail}");
        }
        EnvironmentScopeManager.RefreshProcessPath();

        var target = Targets.Values.FirstOrDefault(t => string.Equals(t.WingetId, wingetId, StringComparison.OrdinalIgnoreCase));
        var name = target.Name ?? wingetId;
        var probe = target.WingetId is null ? (WingetId: wingetId, Name: name, ProbeExe: "") : target;
        var exePath = string.IsNullOrEmpty(probe.ProbeExe) ? null : await FindInstalledPathAsync(probe);
        var version = ReadInstalledFileVersion(exePath);
        SoftwareRegistry.Add(name, exePath ?? $"winget:{wingetId}", version, source: "system",
            note: MiscTexts.TSub($"系统安装 · winget {wingetId}"), wingetId: wingetId);
        return exePath is null
            ? MiscTexts.TSub($"✅ 已升级（winget {wingetId}），登记信息已刷新。")
            : MiscTexts.TSub($"✅ 已升级：{name} → {version}（{exePath}）");
    }

    private static Task<(int Code, string Output)> RunAsync(string fileName, string arguments, int timeoutMs, CancellationToken ct)
        => RunCommandAsync(new ProcessStartInfo { FileName = fileName, Arguments = arguments }, timeoutMs, ct);

    internal static Task AwaitCommandCompletionAsync(Task completion, CancellationToken deadline)
        => completion.WaitAsync(deadline);

    /// <summary>Bound process exit and both redirected pipes by one deadline. Tests use only synthetic child commands.</summary>
    internal static async Task<(int Code, string Output)> RunCommandAsync(ProcessStartInfo psi, int timeoutMs, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = psi.StandardErrorEncoding = Encoding.UTF8;
            using var proc = Process.Start(psi);
            if (proc is null) return (-1, MiscTexts.T("(无法启动进程)"));
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            var completion = Task.WhenAll(proc.WaitForExitAsync(timeoutCts.Token), stdoutTask, stderrTask);
            try
            {
                // A child can inherit stdout/stderr and outlive its parent. Process exit alone
                // is therefore insufficient; pipe reads must obey the same time limit.
                await AwaitCommandCompletionAsync(completion, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                        await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    }
                }
                catch { }
                return (-1, MiscTexts.T(ct.IsCancellationRequested ? "(执行已取消)" : "(执行超时)"));
            }
            finally
            {
                // A deadline may finish before an underlying pipe completion. Observe any
                // later fault without extending the deadline or leaving it unobserved.
                _ = completion.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return (proc.ExitCode, string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}");
        }
        catch (Exception ex)
        {
            return (-1, MiscTexts.TSub($"(执行异常：{ex.Message})"));
        }
    }
}
