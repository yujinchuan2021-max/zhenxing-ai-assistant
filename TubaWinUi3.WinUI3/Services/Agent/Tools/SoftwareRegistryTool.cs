using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using TubaWinUi3.Services.AppManagement;

namespace TubaWinUi3.Services.Agent;

/// <summary>
/// 应用管理工具组：系统安装（winget）/ 沙箱安装 / 软件登记。
/// 口径：**系统级安装是默认方式**（全局可用）；沙箱安装仅用于隔离 / 试用场景。
/// 装完都要登记进「应用中心」。
/// </summary>
public static class SoftwareRegistryTool
{
    public static void Register()
    {
        // 1. 登记
        AgentToolRegistry.Register(new AgentTool
        {
            Name = "register_installed_software",
            DisplayName = MiscTexts.T("登记已装软件"),
            Glyph = "\uE8F1",
            RequiresConfirmation = false,
            Function = AIFunctionFactory.Create(
                (Func<string, string, string?, string?, string>)RegisterSoftware,
                new AIFunctionFactoryOptions { Name = "register_installed_software" }),
        });

        // 2. 系统安装（默认方式，写操作需确认）
        AgentToolRegistry.Register(new AgentTool
        {
            Name = "install_system_app",
            DisplayName = MiscTexts.T("安装软件到系统"),
            Glyph = "\uE896",
            RequiresConfirmation = true,
            Function = AIFunctionFactory.Create(
                (Func<string, Task<string>>)InstallSystemApp,
                new AIFunctionFactoryOptions { Name = "install_system_app" }),
        });

        // 3. 沙箱安装（隔离 / 试用场景）
        AgentToolRegistry.Register(new AgentTool
        {
            Name = "install_sandbox_app",
            DisplayName = MiscTexts.T("沙箱安装环境"),
            Glyph = "\uE8F1",
            RequiresConfirmation = false,
            Function = AIFunctionFactory.Create(
                (Func<string, Task<string>>)InstallSandboxApp,
                new AIFunctionFactoryOptions { Name = "install_sandbox_app" }),
        });

        // 4. 查询已装（应用中心登记表）
        AgentToolRegistry.Register(new AgentTool
        {
            Name = "list_installed_software",
            DisplayName = MiscTexts.T("查询已装软件"),
            Glyph = "\uE71D",
            RequiresConfirmation = false,
            Function = AIFunctionFactory.Create(
                (Func<string>)ListInstalledSoftware,
                new AIFunctionFactoryOptions { Name = "list_installed_software" }),
        });
    }

    [Description("列出「应用中心」里登记的全部软件（AI 安装 / 系统安装 / 手动登记 / 沙箱）。回答'我装过什么 / 装在哪 / 怎么卸载'类问题时先调用本工具。")]
    public static string ListInstalledSoftware()
    {
        var list = SoftwareRegistry.Load();
        if (list.Count == 0) return "应用中心暂无登记记录。";
        var sb = new StringBuilder($"应用中心已登记 {list.Count} 项：\n");
        foreach (var r in list.OrderByDescending(x => x.InstalledAt))
        {
            var src = r.Source switch
            {
                "ai" => "AI 安装",
                "sandbox" => "沙箱",
                "system" => "系统安装",
                _ => "手动登记",
            };
            sb.Append($"- {r.Name}");
            if (!string.IsNullOrEmpty(r.Version)) sb.Append($" {r.Version}");
            sb.Append($"（{src}）→ {r.Path}");
            if (!string.IsNullOrWhiteSpace(r.WingetId)) sb.Append($" ｜卸载: winget {r.WingetId}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    [Description("通过 winget 把软件安装到系统（★默认安装方式：全局可用，用户命令行/编辑器里能直接用；需要用户确认）。target 可选：python / node / git / vscode / godot / dotnet / 7zip / ollama / cmake。装完自动刷新环境并登记到应用中心。一般 1-10 分钟。")]
    public static async Task<string> InstallSystemApp(string target)
    {
        try
        {
            return await SystemInstaller.InstallAsync(target, progress: null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return $"安装失败：{ex.Message}";
        }
    }

    [Description("把便携版环境安装到应用管理的沙箱目录（隔离目录，不进系统 PATH；可在应用中心一键卸载）。target 可选：python（Python 3.12 绿色版）/ node（Node.js 22 绿色版）。注意：一般安装请走 install_system_app（系统级、全局可用）；本工具仅用于用户要求隔离/试用、或临时任务需要独立运行时的场景。下载较大，执行需要 1-5 分钟。")]
    public static async Task<string> InstallSandboxApp(string target)
    {
        try
        {
            return await SandboxInstaller.InstallAsync(target, progress: null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return $"安装失败：{ex.Message}";
        }
    }

    [Description("把刚安装完成的软件登记到应用中心（以便用户随时查询/定位）。安装类任务收尾时调用；name=软件名，path=安装路径（目录或主程序），version=版本（可选），note=备注（可选：如'Godot 引擎，用于做游戏'）")]
    public static string RegisterSoftware(string name, string path, string? version = null, string? note = null)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
            return "登记失败：缺少 name 或 path 参数";

        try
        {
            var rec = SoftwareRegistry.Add(name, path, version ?? "", source: "ai", note: note ?? "");
            var sb = new StringBuilder();
            sb.Append($"已登记：{rec.Name}");
            if (!string.IsNullOrEmpty(rec.Version)) sb.Append($" {rec.Version}");
            sb.Append($" → {rec.Path}");
            sb.Append("（可在顶栏「应用中心」查看）");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"登记失败：{ex.Message}";
        }
    }
}
