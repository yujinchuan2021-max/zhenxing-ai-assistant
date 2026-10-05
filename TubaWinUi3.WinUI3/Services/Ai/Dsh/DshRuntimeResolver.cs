// ZXAI A16：dsh 运行时解析 —— 优先应用私有运行时，其次本机 Hermes 目录（开发期），最后 PATH。
// 依据 zxai-docs/dsh-protocol-reference-2026-09-21.md「独立运行」：
//   · 绝对路径启动 <独立 node.exe> <独立 dsh>/lib/bin.js --profile acp --patch ...
//   · child env 设 DSH_HOME=<应用管理的数据根>（profile/会话/skill 投影都在其下，不碰用户 ~/.dsh）
//   · 不可共享 Hermes profile 模块镜像；发布应锁定实际验证过的 Node/dsh 与传递依赖、离线验证启动。
// 开发期兜底 Hermes node 目录只是「能找到 dsh」的手段；正式发布包把 runtime\ 一并带上即走第 1 分支。

using System.Text.Json;

namespace TubaWinUi3.Services.Ai.Dsh;

public static class DshRuntimeResolver
{
    /// <summary>运行时来源（诊断用）。</summary>
    public enum Source { AppRuntime, HermesDev, Path }

    public sealed record Resolved(Source Origin, string? NodeExe, string? BinJs, string VersionNote)
    {
        /// <summary>是否为独立（node + bin.js）启动方式。</summary>
        public bool IsStandalone => NodeExe is not null && BinJs is not null;
    }

    /// <summary>
    /// 解析可用的 dsh 运行时。
    /// appBaseDir 缺省为应用自身目录（发布包内含 runtime\node.exe + runtime\dsh\lib\bin.js）。
    /// </summary>
    public static Resolved Resolve(string? appBaseDir = null)
    {
        var baseDir = appBaseDir ?? AppContext.BaseDirectory;

        // ① 应用私有运行时（发布形态；A16 目标形态）
        var node = Path.Combine(baseDir, "runtime", "node.exe");
        var binJs = Path.Combine(baseDir, "runtime", "dsh", "lib", "bin.js");
        if (File.Exists(node) && File.Exists(binJs))
            return new Resolved(Source.AppRuntime, node, binJs, ReadDshVersion(Path.Combine(baseDir, "runtime", "dsh")));

        // ② 开发期：Hermes 自带的 node 环境里全局装了 @deepseek-ai/dsh（历史原因）
        var hermesNode = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "hermes", "node");
        var hermesNodeExe = Path.Combine(hermesNode, "node.exe");
        var hermesBinJs = Path.Combine(hermesNode, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        if (File.Exists(hermesNodeExe) && File.Exists(hermesBinJs))
            return new Resolved(Source.HermesDev, hermesNodeExe, hermesBinJs,
                ReadDshVersion(Path.Combine(hermesNode, "node_modules", "@deepseek-ai", "dsh")));

        // ③ PATH 兜底（cmd 解析）
        return new Resolved(Source.Path, null, null, "");
    }

    /// <summary>dsh 安装目录的 package.json version 字段（版本锁定诊断用；读不到返回空）。</summary>
    internal static string ReadDshVersion(string dshDir)
    {
        try
        {
            var pkg = Path.Combine(dshDir, "package.json");
            if (!File.Exists(pkg)) return "";
            using var doc = JsonDocument.Parse(File.ReadAllText(pkg));
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
        }
        catch { return ""; }
    }
}
