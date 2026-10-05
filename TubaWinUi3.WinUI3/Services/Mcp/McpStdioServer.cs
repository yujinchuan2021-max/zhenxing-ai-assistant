using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TubaWinUi3.Services.Agent;
using TubaWinUi3.Services.AppManagement;

namespace TubaWinUi3.Services.Mcp;

/// <summary>
/// MCP（Model Context Protocol）stdio 服务器 v0（ZXAI 2026-09-19）。
/// 让 Claude Code / Cursor 等外部 AI Agent 以子进程方式调用工具箱能力：
///   TubaWinUi3.exe --mcp-stdio        （不显示主窗口，stdin/stdout 全部用于协议）
/// 传输：行分隔 JSON-RPC 2.0（UTF-8）。**stdout 只允许协议消息**，日志一律走 AgentDebugLog。
/// 安全：stdio 无网络端口暴露，天然规避本地端口被网页劫持（DNS rebinding）的风险；
/// 后续若增加 SSE / Streamable HTTP 传输，必须携带令牌鉴权后再对外监听。
/// </summary>
internal static class McpStdioServer
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // 本地管道协议，中文不转义（可读性优先；无 XSS 面）
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private const string ServerName = "zhenxing-tubatools";
    private static string ServerVersion => UpdateService.CurrentVersion.ToString(3);

    /// <summary>MCP 确认流队列目录（与主应用 McpConfirmWatcher 共用；见 McpConfirmWatcher 注释）。
    /// 【GUI 隔离】测试模式走隔离根队列，绝不读写真实 %LocalAppData%\TubaWinUi3\mcp-queue：
    /// 隔离实例不启动 McpConfirmWatcher（见 App.OnLaunchedCore），队列里没有 .app-alive 心跳，
    /// 确认类工具调用会立即返回"应用未运行"而不是干等 55 秒超时。</summary>
    private static string QueueDir => DataRoots.TestRoot is { } testRoot
        ? Path.Combine(testRoot, "mcp-queue")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TubaWinUi3", "mcp-queue");

    public static async Task RunAsync(bool readOnly = false)
    {
        using var stdin = new StreamReader(Console.OpenStandardInput(), Utf8NoBom);
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), Utf8NoBom) { AutoFlush = false };
        AgentDebugLog.Info("[Mcp] stdio server started");

        while (true)
        {
            string? line;
            try { line = await stdin.ReadLineAsync(); }
            catch { break; }
            if (line is null) break;                    // 客户端关闭管道 → 退出
            if (string.IsNullOrWhiteSpace(line)) continue;

            string? response = null;
            try { response = await HandleLineAsync(line, readOnly); }
            catch (Exception ex) { AgentDebugLog.Info($"[Mcp] handle failed: {ex}"); }

            if (response is not null)
            {
                await stdout.WriteAsync(response);
                await stdout.WriteAsync('\n');
                await stdout.FlushAsync();
            }
        }

        AgentDebugLog.Info("[Mcp] stdio server stopped");
    }

    internal static async Task<string?> HandleLineAsync(string line, bool readOnly = false)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(line); }
        catch { return ErrorResponse(null, -32700, "Parse error"); }
        if (root is null) return ErrorResponse(null, -32700, "Parse error");

        var id = root["id"]?.DeepClone();               // number / string / null
        var hasId = root["id"] is not null;
        var method = root["method"]?.GetValue<string>();

        if (string.IsNullOrEmpty(method))
            return hasId ? ErrorResponse(id, -32600, "Invalid Request") : null;

        // 通知（无 id 或 notifications/*）不需要响应
        if (!hasId || method.StartsWith("notifications/", StringComparison.Ordinal)) return null;

        switch (method)
        {
            case "initialize":
                return ResultResponse(id, new JsonObject
                {
                    ["protocolVersion"] = root["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject { ["listChanged"] = false },
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = ServerName,
                        ["version"] = ServerVersion,
                    },
                    ["instructions"] = readOnly
                        ? "枕星图吧AI助手客户端只读查询：现有工具目录、CLI使用说明、官方资讯摘要与来源链接。没有文章全文，不执行或安装软件。"
                        : "枕星图吧AI助手工具箱：工具目录、官方资讯、硬件遥测与开发环境查询。",
                });

            case "ping":
                return ResultResponse(id, new JsonObject());

            case "tools/list":
                var tools = ClientContentTools.BuildToolList();
                if (!readOnly) foreach (var tool in BuildToolList()) tools.Add(tool?.DeepClone());
                return ResultResponse(id, new JsonObject { ["tools"] = tools });

            case "tools/call":
                return await CallToolAsync(id, root["params"], readOnly);

            default:
                return ErrorResponse(id, -32601, $"Method not found: {method}");
        }
    }

    // ---------- 工具实现 ----------

    private static async Task<string?> CallToolAsync(JsonNode? id, JsonNode? p, bool readOnly)
    {
        var name = p?["name"]?.GetValue<string>();
        var args = p?["arguments"] as JsonObject;
        if (string.IsNullOrEmpty(name)) return ErrorResponse(id, -32602, "缺少工具名（params.name）");

        try
        {
            if (readOnly && !ClientContentTools.IsContentTool(name))
                throw new ArgumentException("This client bridge exposes read-only content tools only.");
            var text = ClientContentTools.IsContentTool(name) ? await ClientContentTools.CallAsync(name, args) : name switch
            {
                "get_system_telemetry" => await GetSystemTelemetryAsync(),
                "query_storage_health" => QueryStorageHealth(),
                "manage_environment" => await ManageEnvironmentAsync(args),
                "install_software" => await InstallSoftwareAsync(args),
                "uninstall_software" => await UninstallSoftwareAsync(args),
                _ => throw new ArgumentException($"未知工具：{name}"),
            };
            return ResultResponse(id, new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                ["isError"] = false,
            });
        }
        catch (Exception ex)
        {
            return ResultResponse(id, new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"执行失败：{ex.Message}" }),
                ["isError"] = true,
            });
        }
    }

    private static async Task<string> GetSystemTelemetryAsync()
    {
        var hw = await SystemAgentTool.GetHardwareInfoAsync(CancellationToken.None);
        var sys = SystemAgentTool.GetSystemInfo();
        var disk = SystemAgentTool.DiskUsage();
        return $"# 硬件信息\n{hw}\n# 系统信息\n{sys}\n{disk}";
    }

    private static string QueryStorageHealth()
    {
        // PowerShell 桥接：Get-PhysicalDisk（健康/介质类型）+ Get-Volume（容量），输出 JSON。
        // 后续版本接入 smartctl（需管理员权限 + 合规内置 smartctl.exe）。
        var script = string.Join("; ",
            "$disks = Get-PhysicalDisk | Select-Object DeviceId,FriendlyName,MediaType,BusType,@{n='SizeGB';e={[math]::Round($_.Size/1GB,1)}},HealthStatus,OperationalStatus",
            "$vols = Get-Volume | Where-Object DriveLetter | Select-Object DriveLetter,FileSystemLabel,FileSystem,@{n='SizeGB';e={[math]::Round($_.Size/1GB,1)}},@{n='FreeGB';e={[math]::Round($_.SizeRemaining/1GB,1)}}",
            "ConvertTo-Json -InputObject @{disks=$disks; volumes=$vols} -Depth 4");
        var output = RunProcess("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"");
        return $"存储健康探测（物理磁盘/卷，只读）：\n{output.Trim()}";
    }

    private static async Task<string> ManageEnvironmentAsync(JsonObject? args)
    {
        var action = args?["action"]?.GetValue<string>() ?? "verify";
        var target = args?["target"]?.GetValue<string>()?.Trim().ToLowerInvariant();

        if (action == "install")
        {
            if (string.IsNullOrEmpty(target))
                return $"action=install 需要 target 参数。可选：{SystemInstaller.DescribeTargets()}";
            var (ok, msg) = await RequestConfirmationAsync("install_software", args);
            return ok ? msg : $"❌ 未安装：{msg}";
        }
        if (action == "remove")
        {
            return "卸载请改用 uninstall_software 工具（参数 winget_id，需用户在主应用弹窗中确认）。";
        }

        // verify：探测常见开发环境（与「应用中心」页面共用 EnvironmentProbe 同一套逻辑）
        var entries = EnvironmentProbe.KnownKeys
            .Select(EnvironmentProbe.ProbeOne)
            .Where(e => target is null || e.Key == target)
            .ToList();

        var sb = new StringBuilder("本机开发环境探测：\n");
        foreach (var e in entries)
        {
            sb.AppendLine(e.Found
                ? $"- {e.Label}: 已安装 {e.Version} ｜ 路径 {e.Path}"
                : $"- {e.Label}: 未安装");
        }
        return sb.ToString();
    }

    /// <summary>安装（MCP 侧=请求+等待确认，实际安装在主应用内执行）。</summary>
    private static async Task<string> InstallSoftwareAsync(JsonObject? args)
    {
        var target = args?["target"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "";
        if (string.IsNullOrEmpty(target))
            return $"缺少参数 target。可选（键名）：{SystemInstaller.DescribeTargets()}";
        var (ok, msg) = await RequestConfirmationAsync("install_software", args);
        return ok ? msg : $"❌ 未安装：{msg}";
    }

    private static async Task<string> UninstallSoftwareAsync(JsonObject? args)
    {
        var wingetId = args?["winget_id"]?.GetValue<string>()?.Trim() ?? "";
        if (string.IsNullOrEmpty(wingetId))
            return "缺少参数 winget_id（可以先调用 manage_environment verify 或让用户查「应用中心」）。";
        var (ok, msg) = await RequestConfirmationAsync("uninstall_software", args);
        return ok ? msg : $"❌ 未卸载：{msg}";
    }

    /// <summary>MCP 确认流：写 pending 请求 → 等主应用用户确认（≤55 秒）→ (allowed, message)。
    /// 主应用执行结果在 message 里；主应用没运行（心跳过期）则立即返回。</summary>
    private static async Task<(bool Allowed, string Message)> RequestConfirmationAsync(string tool, JsonObject? args, int timeoutMs = 55_000)
    {
        try
        {
            Directory.CreateDirectory(QueueDir);
            var alivePath = Path.Combine(QueueDir, ".app-alive");
            if (!File.Exists(alivePath) ||
                DateTime.UtcNow - File.GetLastWriteTimeUtc(alivePath) > TimeSpan.FromSeconds(45))
            {
                return (false, "『图吧工具箱』应用未运行——请先打开应用，用户需要在弹窗中确认后才会执行。");
            }
        }
        catch { }

        var id = Guid.NewGuid().ToString("N");
        var reqPath = Path.Combine(QueueDir, $"{id}.request.json");
        var respPath = Path.Combine(QueueDir, $"{id}.response.json");
        var payload = new JsonObject
        {
            ["id"] = id,
            ["tool"] = tool,
            ["args"] = args?.DeepClone() ?? new JsonObject(),
            ["createdUtc"] = DateTime.UtcNow.ToString("o"),
            ["pid"] = Environment.ProcessId,
        };
        File.WriteAllText(reqPath, payload.ToJsonString(JsonOpts), Utf8NoBom);
        AgentDebugLog.Info($"[Mcp] confirmation requested: {tool} id={id}");

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(1000);
            if (!File.Exists(respPath)) continue;
            try
            {
                var resp = JsonNode.Parse(File.ReadAllText(respPath, Encoding.UTF8));
                var allowed = resp?["allowed"]?.GetValue<bool>() ?? false;
                var message = resp?["message"]?.GetValue<string>() ?? "";
                try { File.Delete(respPath); } catch { }
                return (allowed, message);
            }
            catch { /* 半写状态：下一轮再试 */ }
        }
        // 超时：保留 request（主应用仍可确认执行，结果留给用户；MCP 调用方拿到"等待超时"）
        return (false, $"等待用户确认超时（{timeoutMs / 1000} 秒）。用户可在『图吧工具箱』弹窗中稍后确认；请勿重复发起。");
    }

    private static JsonArray BuildToolList() =>
    [
        new JsonObject
        {
            ["name"] = "get_system_telemetry",
            ["description"] = "获取本机硬件与系统遥测：CPU / GPU / 内存 / 主板 / 磁盘使用概况（只读）。",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["category"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "可选：all | cpu | gpu | memory | storage（v0 忽略该参数，返回完整信息）",
                    },
                },
            },
        },
        new JsonObject
        {
            ["name"] = "query_storage_health",
            ["description"] = "查询物理磁盘与各卷的健康状态：容量 / HealthStatus / OperationalStatus（只读）。",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
            },
        },
        new JsonObject
        {
            ["name"] = "install_software",
            ["description"] = "请求安装软件到系统（winget，全局可用）。安全设计：会让『枕星图吧AI助手』应用弹出确认框，用户点『允许』后才执行（需要应用正在运行）。可选 target（键名）：python | node | git | vscode | godot | dotnet | 7zip | ollama | cmake。",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["target"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "要安装的软件键名",
                    },
                },
                ["required"] = new JsonArray("target"),
            },
        },
        new JsonObject
        {
            ["name"] = "uninstall_software",
            ["description"] = "请求卸载 winget 安装的软件（用户弹窗确认后执行）。winget_id 例如 OpenJS.NodeJS.LTS。",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["winget_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "winget 包 Id",
                    },
                },
                ["required"] = new JsonArray("winget_id"),
            },
        },
        new JsonObject
        {
            ["name"] = "manage_environment",
            ["description"] = "开发环境管理：action=verify 探测本机已装的 Node / Python / Git / .NET / VS Code；action=install 走与 install_software 相同的用户确认流。",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["action"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("verify", "install", "remove"),
                        ["description"] = "默认 verify",
                    },
                    ["target"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "可选：node | python | git | dotnet | code（不填=全部）",
                    },
                },
            },
        },
    ];

    // ---------- 辅助 ----------

    private static string ResultResponse(JsonNode? id, JsonNode result)
        => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString(JsonOpts);

    private static string ErrorResponse(JsonNode? id, int code, string message)
        => new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        }.ToJsonString(JsonOpts);

    private static string RunProcess(string fileName, string arguments, int timeoutMs = 20000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return "(无法启动进程)";
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return "(执行超时)";
            }
            return string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n[stderr] {stderr}";
        }
        catch (Exception ex)
        {
            return $"(执行异常：{ex.Message})";
        }
    }
}
