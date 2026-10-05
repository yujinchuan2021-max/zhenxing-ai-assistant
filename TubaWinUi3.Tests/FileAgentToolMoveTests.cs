using TubaWinUi3.Services.Agent;

namespace TubaWinUi3.Tests;

/// <summary>
/// move_file / copy_file 的写沙箱校验回归（审计 A14）：
/// 移动 = 删除源 + 写目的地，故源与目的地都必须通过 <see cref="FileSandbox.ValidateWrite"/>；
/// 复制不删除源文件，源保持只读校验的原有语义（勿与移动对齐）。
/// 全部在临时目录内做纯逻辑验证，不触碰真实用户文件；受保护目录用例只做路径校验，不落盘。
/// </summary>
public class FileAgentToolMoveTests : IDisposable
{
    private readonly string _dir;

    public FileAgentToolMoveTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "zxai-move-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string CreateFile(string name, string content = "hello")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string PathInDir(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Move_WriteProtectedExtensionSource_Rejected_SourceIntact()
    {
        // .exe 属沙箱写保护扩展名：读校验放行（旧实现只查这个 → 保护被绕过），写校验必须拒绝
        var src = CreateFile("payload.exe", "not a real executable, just text");
        var dst = PathInDir("payload-moved.txt");

        Assert.Null(FileSandbox.ValidateRead(src));
        Assert.NotNull(FileSandbox.ValidateWrite(src));

        var result = FileAgentTool.MoveFile(src, dst, "回归测试");

        Assert.StartsWith("错误：", result);
        Assert.Contains("安全", result);
        Assert.True(File.Exists(src), "写保护的源文件不应被移动/删除");
        Assert.False(File.Exists(dst), "校验失败时不应创建目的地文件");
    }

    [Fact]
    public void Move_ProtectedRootSource_RejectedBeforeExistenceCheck()
    {
        // 受保护目录（Windows 目录）下的源：路径校验先于存在性判断拦下，全程不做文件 IO
        var root = Environment.GetEnvironmentVariable("SystemRoot");
        if (string.IsNullOrWhiteSpace(root)) return;

        var src = Path.Combine(root, "zxai-move-probe", "probe-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        var dst = PathInDir("probe-moved.txt");

        var result = FileAgentTool.MoveFile(src, dst, "回归测试");

        Assert.StartsWith("错误：", result);
        Assert.Contains("沙箱", result);
        Assert.DoesNotContain("不存在", result);
        Assert.False(File.Exists(dst));
    }

    [Fact]
    public void Move_PlainTextFile_Succeeds()
    {
        var src = CreateFile("note.txt", "content-42");
        var dst = Path.Combine(_dir, "sub", "note-moved.txt");

        // 环境异常（如 TEMP 落在受保护根目录）时本用例前提不成立：软跳过，与 FileSandboxTests 同风格
        if (FileSandbox.ValidateWrite(src) is not null) return;

        var result = FileAgentTool.MoveFile(src, dst, "回归测试");

        Assert.Contains("已移动", result);
        Assert.False(File.Exists(src), "正常文件移动后源应消失");
        Assert.True(File.Exists(dst));
        Assert.Equal("content-42", File.ReadAllText(dst));
    }

    [Fact]
    public void Move_WriteProtectedDestination_Rejected_SourceIntact()
    {
        var src = CreateFile("note2.txt", "x");
        var dst = PathInDir("note2.exe");

        var result = FileAgentTool.MoveFile(src, dst, "回归测试");

        Assert.StartsWith("错误：", result);
        Assert.True(File.Exists(src));
        Assert.False(File.Exists(dst));
    }

    [Fact]
    public void Copy_WriteProtectedSource_StillAllowed_DestinationStillChecked()
    {
        // 复制的原语义：源只做只读校验——写保护扩展名的源仍可复制（与移动区分，勿改）
        var src = CreateFile("payload-copy.exe", "text payload");
        var dstOk = PathInDir("payload-copy-out.txt");

        var copied = FileAgentTool.CopyFile(src, dstOk, "回归测试");

        Assert.Contains("已复制", copied);
        Assert.True(File.Exists(src), "复制不得删除源文件");
        Assert.True(File.Exists(dstOk));

        // 目的地仍走写校验：写保护扩展名的目的地被拒
        var dstBad = PathInDir("payload-copy-out.exe");
        var rejected = FileAgentTool.CopyFile(src, dstBad, "回归测试");

        Assert.StartsWith("错误：", rejected);
        Assert.False(File.Exists(dstBad));
    }
}
