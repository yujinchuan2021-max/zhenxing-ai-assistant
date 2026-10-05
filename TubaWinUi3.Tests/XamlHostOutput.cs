using System;
using System.Linq;

namespace TubaWinUi3.Tests;

/// <summary>
/// 【主审补正 B】XamlHostRunner 子进程输出的纯解析/判定函数（独立可测）。
///
/// 协议（与 TubaWinUi3.XamlHostRunner/Program.cs 一致）：
///   CASE|&lt;name&gt;|PASS            用例通过
///   CASE|&lt;name&gt;|FAIL|&lt;原因&gt;    用例失败
///   SUMMARY|&lt;passed&gt;|&lt;total&gt;   汇总
///   HOST_DONE                    正常完成标记
///   HOST_FAIL|&lt;原因&gt;            宿主初始化失败（exit=33）
///
/// 判定规则（任一不满足即失败——"宿主崩溃/输出不全不得被吞"）：
///   1) 必须存在 CASE|&lt;name&gt;| 行，且结果为 PASS；
///   2) PASS 时必须存在 HOST_DONE；
///   3) PASS 时 SUMMARY 必须存在且 passed==total 且 total&gt;=1；
///   4) PASS 时进程必须以 exit=0 正常退出（PASS 后非零退出/崩溃一律判失败）。
/// </summary>
internal static class XamlHostOutput
{
    public sealed record Result(bool Ok, string Details);

    public static Result Parse(string stdout, int exitCode, string caseName)
    {
        var lines = (stdout ?? string.Empty)
            .Split('\n')
            .Select(l => l.Trim())
            .ToArray();

        // 1) 用例结果行
        var line = lines.FirstOrDefault(l => l.StartsWith($"CASE|{caseName}|", StringComparison.Ordinal));
        if (line is null)
        {
            var hostFail = lines.FirstOrDefault(l => l.StartsWith("HOST_FAIL|", StringComparison.Ordinal));
            return new(false, hostFail is null
                ? $"未产出用例结果行（exit={exitCode}）"
                : $"宿主初始化失败：{hostFail}（exit={exitCode}）");
        }

        var parts = line.Split('|');
        var casePass = parts.Length >= 3 && parts[2] == "PASS";
        if (!casePass)
        {
            var reason = parts.Length >= 4 ? parts[3] : "无原因";
            return new(false, $"CASE {caseName} FAIL：{reason}（宿主 exit={exitCode}）");
        }

        // 2) 完成标记
        var hostDone = lines.Contains("HOST_DONE");
        if (!hostDone)
            return new(false, $"CASE {caseName} 报 PASS 但输出缺少 HOST_DONE 完成标记（exit={exitCode}）——按协议判失败");

        // 3) SUMMARY 一致性
        var summaryLine = lines.FirstOrDefault(l => l.StartsWith("SUMMARY|", StringComparison.Ordinal));
        var summaryOk = false;
        if (summaryLine is not null)
        {
            var sp = summaryLine.Split('|');
            summaryOk = sp.Length >= 3
                && int.TryParse(sp[1], out var passed)
                && int.TryParse(sp[2], out var total)
                && total >= 1 && passed == total;
        }
        if (!summaryOk)
            return new(false, $"CASE {caseName} 报 PASS 但 SUMMARY 缺失或与结果不一致（{(summaryLine ?? "SUMMARY 缺失")}，exit={exitCode}）——按协议判失败");

        // 4) 退出码（PASS 后非零退出 = 崩溃/部分失败，按协议判失败）
        if (exitCode != 0)
            return new(false, $"CASE {caseName} 报 PASS 但宿主以非零码退出（exit={exitCode}）——按协议判失败");

        return new(true, "");
    }
}
