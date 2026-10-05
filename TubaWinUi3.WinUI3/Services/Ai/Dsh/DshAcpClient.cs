using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TubaWinUi3.Services.Ai.Dsh;

/// <summary>
/// DeepSeek Harness ACP 客户端：起 `dsh --profile acp` 子进程，
/// 通过 stdio 的 newline-delimited JSON-RPC 2.0 驱动（Agent Client Protocol）。
/// 【ZXAI】换核心基线：协议蓝图已实测（initialize → session/new → session/prompt →
/// session/update 流式 → stopReason）。本类只负责进程与协议，不含 UI。
/// </summary>
public sealed class DshAcpClient : IAsyncDisposable
{
    private readonly Process _proc;
    private readonly StreamWriter _stdin;
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _nextId;
    private volatile bool _disposed;

    /// <summary>所有 session/update 通知（ACP 语义更新，流式）。</summary>
    public event Action<DshAcpUpdate>? SessionUpdate;

    /// <summary>子进程 stderr 行（诊断日志）。</summary>
    public event Action<string>? StderrLine;

    /// <summary>握手结果：agentInfo + capabilities。</summary>
    public JsonObject? AgentInfo { get; private set; }

    /// <summary>子进程是否仍运行。</summary>
    public bool IsRunning => !_disposed && !_proc.HasExited;

    private DshAcpClient(Process proc)
    {
        _proc = proc;
        _stdin = proc.StandardInput;
        _stdoutPump = Task.Run(PumpStdoutAsync);
        _stderrPump = Task.Run(PumpStderrAsync);
    }

    /// <summary>
    /// 启动 dsh ACP 子进程。apiKey 通过环境变量注入（绝不落盘/绝不入日志）。
    /// workspace 为 agent 的默认工作目录。
    /// </summary>
    public static DshAcpClient Start(string apiKey, string workspace, string? dshPath = null)
        => Start(new DshLaunchConfig
        {
            Env = new Dictionary<string, string> { ["DEEPSEEK_API_KEY"] = apiKey },
        }, workspace, dshPath);

    /// <summary>
    /// 【A07/A16】以启动配置启动：patch（provider/model 覆盖）+ child env（Key 等）。
    /// patch 路径经参数传入（--patch 依据协议参考）；env 直接进子进程环境，绝不落盘。
    /// </summary>
    public static DshAcpClient Start(DshLaunchConfig launch, string workspace, string? dshPath = null)
    {
        var patchArg = launch.PatchPath is null ? "" : $" --patch \"{launch.PatchPath}\"";
        // 【A16】运行时解析：优先独立 node+dsh（发布形态）；显式 dshPath 或解析不到时回退 cmd 解析。
        var runtime = DshRuntimeResolver.Resolve();
        var effectiveDsh = dshPath ?? launch.DshPathOverride;   // 【测试】假 ACP 覆盖优先于运行时解析
        string startFile, startArgs;
        if (effectiveDsh is not null || !runtime.IsStandalone)
        {
            startFile = "cmd.exe";
            // 【A07 返修】cmd /c 的引号规则：命令串超过一对引号时会回退"剥离首尾引号"的旧规则，
            // `${--patch "..."}` 会让整串被剥坏（生产路径同样会挂）。用外层再套一对引号的
            // 标准写法（/c ""prog" args"）确保内层引号被完整保留。
            startArgs = $"/c \"\"{effectiveDsh ?? "dsh"}\" --profile acp{patchArg}\"";
        }
        else
        {
            startFile = runtime.NodeExe!;
            startArgs = $"\"{runtime.BinJs}\" --profile acp{patchArg}";
        }
        var psi = new ProcessStartInfo
        {
            FileName = startFile,
            Arguments = startArgs,
            WorkingDirectory = workspace,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var kv in launch.Env)
            psi.Environment[kv.Key] = kv.Value;
        // 【A16】DSH_HOME：应用管理的 dsh 数据根（profile/会话隔离，不碰用户 ~/.dsh；
        // 内置 acp profile 首次启动自动初始化）。
        if (launch.DataDir is not null)
            psi.Environment["DSH_HOME"] = Path.Combine(launch.DataDir, DshLaunchConfig.DshHomeDirName);
        // 【权限全开】本产品定位=排障助手：需要访问临时目录/系统日志/驱动/注册表等，
        // 沙箱与审批一律放开（dsh 内置支持：danger-full-access = sandbox 全开 + approval never）。
        psi.Environment["DSH_PERMISSION_MODE"] = "danger-full-access";
        // Node 在 Windows 下输出 UTF-8
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        var proc = Process.Start(psi) ?? throw new InvalidOperationException(MiscTexts.T("无法启动 dsh 子进程"));
        return new DshAcpClient(proc);
    }

    // ---------- 协议：请求/响应 ----------

    private async Task<JsonObject> RequestAsync(string method, JsonObject? prms, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = tcs;

        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (prms is not null) msg["params"] = prms;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stdin.WriteLineAsync(msg.ToJsonString()).ConfigureAwait(false);
            await _stdin.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        var result = await tcs.Task.ConfigureAwait(false);

        if (result["error"] is JsonObject err)
            throw new DshAcpException($"{err["code"]}: {err["message"]}", method);
        return result["result"] as JsonObject ?? new JsonObject();
    }

    /// <summary>ACP initialize 握手。返回 agentCapabilities。</summary>
    public async Task<JsonObject> InitializeAsync(CancellationToken ct = default)
    {
        var result = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = 1,
            ["clientCapabilities"] = new JsonObject(),
        }, ct).ConfigureAwait(false);
        AgentInfo = result;
        return result;
    }

    /// <summary>建新会话（cwd + 可选 MCP 服务器）。返回 sessionId。</summary>
    public async Task<string> NewSessionAsync(string cwd, JsonArray? mcpServers = null, CancellationToken ct = default)
    {
        var result = await RequestAsync("session/new", new JsonObject
        {
            ["cwd"] = cwd,
            ["mcpServers"] = mcpServers ?? new JsonArray(),
        }, ct).ConfigureAwait(false);
        return result["sessionId"]?.GetValue<string>()
            ?? throw new DshAcpException(MiscTexts.T("session/new 未返回 sessionId"), "session/new");
    }

    /// <summary>恢复已有会话。返回 sessionId。</summary>
    public async Task<string> ResumeSessionAsync(string sessionId, string cwd, JsonArray? mcpServers = null, CancellationToken ct = default)
        => (await ResumeSessionWithOptionsAsync(sessionId, cwd, mcpServers, ct).ConfigureAwait(false)).SessionId;

    /// <summary>
    /// 【A07】恢复已有会话并返回完整结果（含 configOptions——历史会话的 logged 模型等设置在这里，
    /// resume 不会自动应用当前 patch 的模型；调用方需按返回的 configOptions 用
    /// session/set_config_option 显式设置，不得猜内部编码）。
    /// </summary>
    public async Task<(string SessionId, JsonObject Result)> ResumeSessionWithOptionsAsync(
        string sessionId, string cwd, JsonArray? mcpServers = null, CancellationToken ct = default)
    {
        var result = await RequestAsync("session/resume", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["cwd"] = cwd,
            ["mcpServers"] = mcpServers ?? new JsonArray(),
        }, ct).ConfigureAwait(false);
        return (result["sessionId"]?.GetValue<string>() ?? sessionId, result);
    }

    /// <summary>
    /// 【A07】会话配置项设置（如 model）。value 必须原样取自服务返回的 configOptions，
    /// 不要自行拼造内部编码。返回服务响应（为空对象时表示仅确认）。
    /// </summary>
    public async Task<JsonObject> SetConfigOptionAsync(string sessionId, string configId, object value, CancellationToken ct = default)
    {
        var result = await RequestAsync("session/set_config_option", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["configId"] = configId,
            ["value"] = System.Text.Json.Nodes.JsonValue.Create(value),
        }, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// 发一轮消息，流式 update 经 SessionUpdate 事件推送；返回 stopReason（end_turn 等）。
    /// </summary>
    public async Task<string> PromptAsync(string sessionId, string text,
        IReadOnlyList<(byte[] Bytes, string MediaType)>? images = null, CancellationToken ct = default)
    {
        // 【A12 审计修复】图片按 ACP 格式发送：{type:"image",data:"规范base64",mimeType}。
        // 纯图片 prompt（text 空）为合法输入；图文顺序保持（先文后图）。
        // 注意：仅在 initialize.agentCapabilities.promptCapabilities.image=true 的连接上使用
        // （由 DshSession 在连接时协商并把关）。
        var prompt = new JsonArray();
        if (!string.IsNullOrEmpty(text))
            prompt.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        if (images is not null)
        {
            foreach (var (bytes, mt) in images)
            {
                prompt.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["data"] = Convert.ToBase64String(bytes),
                    ["mimeType"] = mt,
                });
            }
        }

        var result = await RequestAsync("session/prompt", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = prompt,
        }, ct).ConfigureAwait(false);
        return result["stopReason"]?.GetValue<string>() ?? "unknown";
    }

    /// <summary>
    /// 【A02 审计修复】取消当前任务。
    /// ACP 的 session/cancel 在服务端注册为 notification handler（无 id）；
    /// 以 request 发送不会进入取消处理器（离线实测 cancelHandlerInvoked=false）。
    /// 返回 true 仅当通知确实已写入 stdio——写失败时不触发 CancellationSent、
    /// 不向上报告成功（审计复核：原实现 catch 吞异常后仍发成功事件）。
    /// </summary>
    public async Task<bool> CancelAsync(string sessionId)
    {
        var sent = await SendNotificationAsync("session/cancel",
            new JsonObject { ["sessionId"] = sessionId }).ConfigureAwait(false);
        if (sent) CancellationSent?.Invoke();
        return sent;
    }

    /// <summary>最近一次 session/cancel 通知确实写入 stdio 后触发（测试与诊断用）。</summary>
    public event Action? CancellationSent;

    /// <summary>发送 JSON-RPC 通知（无 id，服务端不应答）。返回是否写入成功。</summary>
    private async Task<bool> SendNotificationAsync(string method, JsonObject? prms)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (prms is not null) msg["params"] = prms;

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _stdin.WriteLineAsync(msg.ToJsonString()).ConfigureAwait(false);
            await _stdin.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            StderrLine?.Invoke($"[通知发送失败] {method}: {ex.Message}");
            return false;
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>关闭会话。</summary>
    public async Task CloseSessionAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            await RequestAsync("session/close", new JsonObject { ["sessionId"] = sessionId }, ct)
                .ConfigureAwait(false);
        }
        catch { }
    }

    // ---------- stdio 泵 ----------

    private async Task PumpStdoutAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var line = await _proc.StandardOutput.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                Dispatch(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StderrLine?.Invoke($"[stdout-pump] {ex.Message}"); }
        finally { FailAllPending(new DshAcpException(MiscTexts.T("dsh 子进程连接已关闭"), "stdio")); }
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var line = await _proc.StandardError.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line)) StderrLine?.Invoke(line);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void Dispatch(string line)
    {
        JsonObject? obj;
        try { obj = JsonNode.Parse(line) as JsonObject; }
        catch { StderrLine?.Invoke($"[协议] 非 JSON 行：{line[..Math.Min(200, line.Length)]}"); return; }
        if (obj is null) return;

        // 响应（有 id、无 method）
        if (obj["id"] is JsonNode idNode && obj["method"] is null)
        {
            var id = idNode.GetValue<int>();
            TaskCompletionSource<JsonObject>? tcs;
            lock (_pending) { _pending.Remove(id, out tcs); }
            tcs?.TrySetResult(obj);
            return;
        }

        // server→client 请求（有 id 且有 method）：必须应答（如 session/request_permission）
        if (obj["id"] is JsonNode reqId && obj["method"] is JsonNode reqMethod)
        {
            _ = HandleServerRequestAsync(reqId, reqMethod.GetValue<string>(), obj["params"] as JsonObject);
            return;
        }

        // 通知（有 method）
        var method = obj["method"]?.GetValue<string>();
        if (method == "session/update")
        {
            var update = DshAcpUpdate.Parse(obj["params"] as JsonObject);
            if (update is not null) SessionUpdate?.Invoke(update);
        }
        else if (method is not null)
        {
            StderrLine?.Invoke($"[通知] {method}");
        }
    }

    /// <summary>
    /// server→client 请求应答。【ZXAI】本产品定位=排障助手（DSH_PERMISSION_MODE=danger-full-access），
    /// request_permission 一律自动允许（全权限策略）；其余未知请求回空对象（宽松）。
    /// </summary>
    private async Task HandleServerRequestAsync(JsonNode id, string method, JsonObject? prms)
    {
        StderrLine?.Invoke($"[请求] {method}");
        JsonNode? resultNode;
        if (method == "session/request_permission")
        {
            string? optionId = null;
            if (prms?["options"] is JsonArray options)
            {
                foreach (var o in options)
                {
                    if (o is not JsonObject oo) continue;
                    var oid = oo["optionId"]?.GetValue<string>() ?? "";
                    var nm = (oo["name"]?.GetValue<string>() ?? "").ToLowerInvariant();
                    var kind = (oo["kind"]?.GetValue<string>() ?? "").ToLowerInvariant();
                    if (oid.Contains("allow", StringComparison.OrdinalIgnoreCase) ||
                        nm.Contains("allow") || kind.Contains("allow"))
                    { optionId = oid; break; }
                }
                optionId ??= (options.FirstOrDefault() as JsonObject)?["optionId"]?.GetValue<string>();
            }
            // 【A10 审计修复】按当前 ACP SDK 结构：outcome 为嵌套对象
            // {outcome:{outcome:"selected", optionId:"..."}}——扁平结构会被 schema 校验拒绝、
            // 服务端按 rejected 消费（离线探针确认）。
            resultNode = new JsonObject
            {
                ["outcome"] = new JsonObject
                {
                    ["outcome"] = "selected",
                    ["optionId"] = optionId ?? "allow",
                },
            };
            StderrLine?.Invoke($"[请求] 自动允许（全权限策略）→ {optionId}");
        }
        else
        {
            resultNode = new JsonObject();
        }

        var resp = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = resultNode,
        };
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _stdin.WriteLineAsync(resp.ToJsonString()).ConfigureAwait(false);
            await _stdin.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { StderrLine?.Invoke($"[请求应答失败] {ex.Message}"); }
        finally { _writeLock.Release(); }
    }

    private void FailAllPending(Exception ex)
    {
        List<TaskCompletionSource<JsonObject>> all;
        lock (_pending) { all = _pending.Values.ToList(); _pending.Clear(); }
        foreach (var tcs in all) tcs.TrySetException(ex);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _stdin.Close(); } catch { }
        try
        {
            if (!_proc.HasExited)
            {
                _proc.Kill(entireProcessTree: true);
                await _proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        catch { }
        try { await _stdoutPump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        try { await _stderrPump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _proc.Dispose();
        _cts.Dispose();
        _writeLock.Dispose();
    }
}

/// <summary>ACP 协议异常。</summary>
public sealed class DshAcpException : Exception
{
    public string Method { get; }
    public DshAcpException(string message, string method) : base(message) => Method = method;
}
