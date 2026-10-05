using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TubaWinUi3.Models;

namespace TubaWinUi3.Services;

/// <summary>
/// 邀请码与朋友端「一键加入」脚本的生成。
/// 邀请码是一段能直接粘进 QQ / 微信的紧凑字符串（TBG1: + Base64Url），
/// 内含对方地址、端口、游戏名，以及可选的短期授权密钥。
/// </summary>
public static partial class GameTunnelInvite
{
    public const string CodePrefix = "TBG1:";

    /// <summary>脚本里的 Tailscale 安装包地址（latest 由官方 302 到当前版本）。</summary>
    public const string InstallerUrlTemplate = "https://pkgs.tailscale.com/stable/tailscale-setup-latest-{0}.msi";

    static GameTunnelInvite()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
    }

    // ══════════════════════ 邀请码 ══════════════════════

    private sealed class InvitePayload
    {
        public string h { get; set; } = "";
        public int p { get; set; }
        public string? g { get; set; }
        public string? k { get; set; }
        public string? n { get; set; }
        public long e { get; set; }
        public string? r { get; set; }
    }

    public static string Encode(InviteInfo info)
    {
        var payload = new InvitePayload
        {
            h = info.Host,
            p = info.Port,
            g = info.Game,
            k = string.IsNullOrWhiteSpace(info.AuthKey) ? null : info.AuthKey.Trim(),
            n = string.IsNullOrWhiteSpace(info.HostName) ? null : info.HostName.Trim(),
            e = info.ExpiresAt,
            r = info.Protocol switch
            {
                GameTunnelProtocol.Udp => "udp",
                GameTunnelProtocol.TcpAndUdp => "both",
                _ => "tcp"
            }
        };

        var json = JsonSerializer.Serialize(payload, PayloadOptions);
        return CodePrefix + ToBase64Url(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// 解析邀请码。同时兼容三种粘贴内容：
    /// 纯邀请码、带说明文字的整条邀请消息、以及手写的「IP:端口」。
    /// </summary>
    public static InviteInfo? Decode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();

        var index = value.IndexOf(CodePrefix, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var rest = value[(index + CodePrefix.Length)..];
            var token = new string(rest.TakeWhile(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '=').ToArray());
            return DecodeToken(token);
        }

        return TryParsePlainAddress(value);
    }

    private static InviteInfo? DecodeToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        try
        {
            var json = Encoding.UTF8.GetString(FromBase64Url(token));
            var payload = JsonSerializer.Deserialize<InvitePayload>(json, PayloadOptions);
            if (payload is null) return null;
            if (!IsValidAddress(payload.h) || !GameTunnelCatalog.IsValidPort(payload.p)) return null;

            return new InviteInfo
            {
                Host = payload.h.Trim(),
                Port = payload.p,
                Game = payload.g ?? "",
                AuthKey = payload.k,
                HostName = payload.n,
                ExpiresAt = payload.e,
                Protocol = payload.r switch
                {
                    "udp" => GameTunnelProtocol.Udp,
                    "both" => GameTunnelProtocol.TcpAndUdp,
                    _ => GameTunnelProtocol.Tcp
                }
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>「100.1.2.3:25565」这种手输地址也直接支持。</summary>
    private static InviteInfo? TryParsePlainAddress(string value)
    {
        var match = AddressRegex().Match(value);
        if (!match.Success) return null;

        var host = match.Groups[1].Value.Trim();
        if (!IsValidAddress(host)) return null;
        if (!int.TryParse(match.Groups[2].Value, out var port) || !GameTunnelCatalog.IsValidPort(port)) return null;

        return new InviteInfo { Host = host, Port = port, Game = "" };
    }

    [GeneratedRegex(@"^\s*([0-9A-Za-z\.\-]{7,64})\s*[:\uff1a]\s*(\d{2,5})\s*$")]
    private static partial Regex AddressRegex();

    public static bool IsValidAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var host = value.Trim();

        if (TailscaleService.IsTailnetAddress(host)) return true;

        // 也允许普通 IP / 主机名（比如自定义网络里的地址）
        if (System.Net.IPAddress.TryParse(host, out _)) return true;

        return host.Length is >= 2 and <= 253
               && host.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_')
               && host.Contains('.');
    }

    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded
        };
        return Convert.FromBase64String(padded);
    }

    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // ══════════════════════ 邀请文案 ══════════════════════

    /// <summary>对方复制发给朋友的整段文字。</summary>
    public static string BuildInviteText(InviteInfo info, string? inviteCode, bool hasScript)
    {
        var builder = new StringBuilder();
        builder.AppendLine(GameTunnelTexts.T("【图吧工具箱 · 联机邀请】"));
        if (!string.IsNullOrWhiteSpace(info.Game)) builder.AppendLine(GameTunnelTexts.TSub($"游戏：{info.Game}"));
        builder.AppendLine(GameTunnelTexts.TSub($"联机地址：{info.Address}"));
        builder.AppendLine();

        if (info.ExpiresAtLocal is { } expires)
        {
            builder.AppendLine(GameTunnelTexts.TSub($"（邀请码有效期到 {expires:HH:mm}，过期后让我重新生成）"));
            builder.AppendLine();
        }

        var step = 1;

        if (info.HasAuthKey && inviteCode is { Length: > 0 })
        {
            builder.AppendLine(GameTunnelTexts.TSub($"{step}) 装了图吧工具箱：打开「游戏联机助手」→ 我要加入 → 粘贴下面这行邀请码"));
            builder.AppendLine(inviteCode);
            builder.AppendLine(GameTunnelTexts.T("   （不用你登录 Tailscale，也不用等我批准——粘贴后直接加入我的网络）"));
            builder.AppendLine();
            step++;
        }

        if (info.HasAuthKey && hasScript)
        {
            builder.AppendLine(GameTunnelTexts.TSub($"{step}) 没装工具箱：双击我发你的「一键加入联机.cmd」，它会自动装好并连上我"));
            builder.AppendLine(GameTunnelTexts.T("   （和邀请码一样是直接加入，不需要任何人同意）"));
            builder.AppendLine();
            step++;
        }

        if (!info.HasAuthKey)
        {
            builder.AppendLine(GameTunnelTexts.TSub($"{step}) 你已经和我处在同一个 Tailscale 网络里：直接打开游戏就行"));
            builder.AppendLine(GameTunnelTexts.T("   （如果我们不在同一个网络，光有地址是连不上的——跟我说一声，我改成发邀请码给你）"));
            builder.AppendLine();
            step++;
        }

        builder.AppendLine(GameTunnelTexts.TSub($"连接后在游戏里填：{info.Address}"));
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    // ══════════════════════ 朋友端一键加入脚本 ══════════════════════

    public static string ScriptFileName(string gameName)
    {
        var safe = Sanitize(gameName);
        return $"{safe}-一键加入联机.cmd";
    }

    internal static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "游戏";
        var builder = new StringBuilder();
        foreach (var c in name.Trim())
        {
            builder.Append(Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' ? '_' : c);
        }
        var result = builder.ToString().Trim().Trim('.');
        return result.Length == 0 ? "游戏" : result.Length > 40 ? result[..40] : result;
    }

    /// <summary>
    /// 生成朋友端 .cmd 脚本内容（GBK 落盘，中文 Windows 的 cmd 才不会乱码）。
    /// 流程：自动提权 → 没有 Tailscale 就下载并静默安装 → 用授权密钥加入对方的网络 → 等待分配地址 → 打印游戏内要填的地址。
    /// </summary>
    public static string BuildScript(InviteInfo info)
    {
        var authKey = TailscaleService.NormalizeAuthKey(info.AuthKey) ?? "";
        var gameName = string.IsNullOrWhiteSpace(info.Game) ? "游戏联机" : info.Game!;

        return $$"""
@echo off
setlocal EnableExtensions
title 图吧工具箱 · 一键加入联机
cd /d "%~dp0"

net session >nul 2>&1
if errorlevel 1 (
    echo 正在申请管理员权限...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

set "GAME={{gameName}}"
set "HOSTADDR={{info.Host}}"
set "PORT={{info.Port}}"
set "AUTHKEY={{authKey}}"
set "TSEXE=%ProgramFiles%\Tailscale\tailscale.exe"
set "TSIPN=%ProgramFiles%\Tailscale\tailscale-ipn.exe"

cls
echo ============================================================
echo   图吧工具箱 · 一键加入联机
echo ------------------------------------------------------------
echo   游戏：%GAME%
echo   主机：%HOSTADDR%:%PORT%
echo ============================================================
echo.

if exist "%TSEXE%" goto :has_tailscale

echo [1/3] 下载 Tailscale 安装包（约 38MB，视网速可能需要一两分钟）
set "MSIDIR=%TEMP%\tuba-tailscale"
set "MSI=%MSIDIR%\tailscale-setup.msi"
mkdir "%MSIDIR%" 2>nul
if exist "%MSI%" del /f /q "%MSI%" >nul 2>&1

set "ARCH=amd64"
if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" set "ARCH=arm64"
set "URL=https://pkgs.tailscale.com/stable/tailscale-setup-latest-%ARCH%.msi"

set "TRIES=0"
:download
set /a TRIES+=1
echo     第 %TRIES% 次尝试...
if exist "%SystemRoot%\System32\curl.exe" (
    "%SystemRoot%\System32\curl.exe" -L --fail --silent --show-error --connect-timeout 20 -o "%MSI%" "%URL%"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -Command "try { Invoke-WebRequest -Uri '%URL%' -OutFile '%MSI%' -UseBasicParsing } catch { exit 1 }"
)
for %%F in ("%MSI%") do if %%~zF GTR 5000000 goto :downloaded
if %TRIES% GEQ 3 goto :download_failed
echo     下载没有完成，5 秒后重试...
timeout /t 5 /nobreak >nul
goto :download

:download_failed
echo.
echo   [失败] 没能自动下载 Tailscale。
echo   请手动打开 https://tailscale.com/download/windows 下载安装，
echo   装好后再双击运行本脚本即可（本脚本会自动跳过下载）。
echo.
pause
exit /b 1

:downloaded
echo     下载完成。

echo [2/3] 正在静默安装 Tailscale...
start /wait "" msiexec /i "%MSI%" /qn /norestart
set "WAIT=0"
:wait_cli
if exist "%TSEXE%" goto :has_tailscale
set /a WAIT+=1
if %WAIT% GEQ 40 goto :install_failed
timeout /t 1 /nobreak >nul
goto :wait_cli

:install_failed
echo.
echo   [失败] 安装似乎没有完成。
echo   请手动安装后再运行本脚本：https://tailscale.com/download/windows
echo.
pause
exit /b 1

:has_tailscale
rem 托盘客户端必须常驻。没有它，tailscaled 只在命令行进程活着的那几十毫秒里连接，
rem 命令一退出立刻断开——登录/入网都会「看起来成功然后又弹回未连接」。
tasklist /FI "IMAGENAME eq tailscale-ipn.exe" 2>nul | find /i "tailscale-ipn.exe" >nul
if not errorlevel 1 goto :tray_ready
if not exist "%TSIPN%" goto :tray_ready
echo     正在启动 Tailscale 客户端（托盘常驻，之后不要退出它）...
explorer.exe "%TSIPN%"
timeout /t 4 /nobreak >nul
:tray_ready
if "%AUTHKEY%"=="" goto :manual_login

echo [3/3] 加入对方的联机网络（不会修改本机 DNS 设置）
echo     如果你本来登录了别的 Tailscale 网络，这次会切换过去，
echo     之后可以用托盘图标里的账号菜单随时切回来。
set "TRIES=0"
:join
set /a TRIES+=1
"%TSEXE%" up --auth-key=%AUTHKEY% --accept-dns=false --unattended
if not errorlevel 1 goto :wait_ip
if %TRIES% GEQ 3 goto :join_failed
echo     加入失败，5 秒后重试...
timeout /t 5 /nobreak >nul
goto :join

:join_failed
echo.
echo   [失败] 没能加入联机网络。常见原因：
echo     - 邀请码已经过期或被对方撤销（让对方重新生成一个）
echo     - 本机时间不准（同步一下系统时间再试）
echo     - 网络受限（安全软件 / 代理拦截了 Tailscale）
echo.
pause
exit /b 1

:manual_login
echo [3/3] 这个邀请没有附带自动登录密钥，请手动登录：
echo     1. 在任务栏右下角找到 Tailscale 图标，右键 -^> Log in
echo     2. 按提示完成登录（浏览器会自动打开）
echo     3. 登录完成后回到本窗口，按任意键继续
pause >nul

:wait_ip
echo     正在等待分配联机地址...
set "WAIT=0"
:ip_loop
set "MYIP="
for /f "usebackq delims=" %%i in (`"%TSEXE%" ip -4 2^>nul`) do set "MYIP=%%i"
if defined MYIP goto :ready
set /a WAIT+=1
if %WAIT% GEQ 40 goto :ip_timeout
timeout /t 1 /nobreak >nul
goto :ip_loop

:ip_timeout
echo.
echo   [失败] 没有拿到联机地址，请确认 Tailscale 已登录并处于 Connected 状态。
echo.
pause
exit /b 1

:ready
echo     本机联机地址：%MYIP%
echo     正在测试与对方的连接...
"%TSEXE%" ping --c 2 --timeout 5s %HOSTADDR% > "%TEMP%\tuba-ping.txt" 2>&1
findstr /i "pong" "%TEMP%\tuba-ping.txt" >nul
if errorlevel 1 (
    echo     暂时没通——对方可能还没进入游戏，先打开游戏试试
) else (
    echo     连接测试成功
)
del "%TEMP%\tuba-ping.txt" >nul 2>&1

cls
echo ============================================================
echo   全部就绪！
echo ============================================================
echo.
echo   现在打开游戏，在这里填地址：
echo        %HOSTADDR%:%PORT%
echo.
echo   这个窗口关掉也不影响联机（Tailscale 会一直在后台运行）。
echo   想退出联机时，右键任务栏的 Tailscale 图标选择 Disconnect 即可。
echo.
echo ============================================================
echo.
pause
exit /b 0
""";
    }

    /// <summary>把脚本写到指定路径：GBK 且不带 BOM，换行统一成 CRLF。</summary>
    public static string WriteScript(string content, string targetPath)
    {
        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

        var encoding = GetGbkEncoding();
        File.WriteAllBytes(targetPath, encoding.GetBytes(ToCrlf(content)));
        return targetPath;
    }

    /// <summary>
    /// 批处理必须是 CRLF：cmd.exe 遇到裸 LF 会把命令行拆碎成「'o' 不是内部或外部命令」这种碎片。
    /// 脚本正文来自源码里的多行字符串，源码存成 LF 时正文也就是 LF，所以落盘前统一一次。
    /// </summary>
    internal static string ToCrlf(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    /// <summary>写到本工具自己的脚本目录，返回路径。</summary>
    public static string WriteScriptToDataDir(InviteInfo info)
    {
        var path = Path.Combine(GameTunnelCatalog.GetScriptsDir(), ScriptFileName(info.Game));
        return WriteScript(BuildScript(info), path);
    }

    private static Encoding GetGbkEncoding()
    {
        try { return Encoding.GetEncoding(936); }
        catch { return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false); }
    }

    /// <summary>给界面用的脚本体积估算文案。</summary>
    public static string DescribeScriptStaleness() => GameTunnelTexts.T("脚本里带的是本次邀请的密钥，过期后需要重新生成。");
}
