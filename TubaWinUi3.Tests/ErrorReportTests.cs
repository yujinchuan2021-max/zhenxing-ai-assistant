using System.Text;
using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

/// <summary>邮件 URI 与摘要过滤的纯数据验收；不打开邮件应用、读取日志或创建附件。</summary>
public class ErrorReportTests
{
    [Fact]
    public void FeedbackDraft_HasFixedRecipientAndEscapesQueryFields()
    {
        const string subject = "错误 &cc=other@example.net? # 测试";
        const string body = "步骤：打开设置\n&bcc=other@example.net#fragment";
        var uri = FeedbackContact.CreateDraft(subject, body);
        var query = ReadQuery(uri);

        Assert.Equal("mailto", uri.Scheme);
        Assert.Equal(FeedbackContact.Email, uri.UserInfo + "@" + uri.Host);
        Assert.Equal(2, query.Count);
        Assert.Equal(subject, query["subject"]);
        Assert.Equal(body, query["body"]);
    }

    [Fact]
    public void ErrorDraft_IncludesBasicVersionSummaryAndUserSteps()
    {
        var uri = ErrorReportService.BuildFeedbackDraft(
            "System.InvalidOperationException: window closed", "打开设置后出现错误", "0.1.0", "Windows x64 / .NET 10");
        var body = ReadQuery(uri)["body"];

        Assert.Equal(FeedbackContact.Email, uri.UserInfo + "@" + uri.Host);
        Assert.Contains("0.1.0", body);
        Assert.Contains("Windows x64 / .NET 10", body);
        Assert.Contains("System.InvalidOperationException: window closed", body);
        Assert.Contains("打开设置后出现错误", body);
        Assert.Equal(2, ReadQuery(uri).Count); // 只有主题和正文，无附件或其他收件人参数
    }

    [Theory]
    [InlineData("api_key=secret-key-value", "secret-key-value")]
    [InlineData("{\"apiKey\":\"secret with spaces\"}", "secret with spaces")]
    [InlineData("password='secret with spaces'", "secret with spaces")]
    [InlineData("Authorization: Bearer short-token", "short-token")]
    [InlineData("sk-abcdef1234567890", "sk-abcdef1234567890")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz", "ghp_abcdefghijklmnopqrstuvwxyz")]
    [InlineData("https://private.example.net/session?token=abcd", "private.example.net")]
    [InlineData("customer@private.example.net", "customer@private.example.net")]
    [InlineData("Error at C:\\Users\\Customer Name\\project\\secret.txt", "Customer Name")]
    [InlineData("Error at \\\\private-host\\private-share\\file.txt", "private-host")]
    [InlineData("Error at /home/customer/private.txt", "customer")]
    public void Summary_FiltersObviousCredentialsAndPrivateLocations(string input, string secret)
    {
        var summary = ErrorReportService.SummarizeError(input);

        Assert.DoesNotContain(secret, summary);
        Assert.Contains("[redacted]", summary);
    }

    [Fact]
    public void Summary_StopsBeforeStackTraceAndKeepsReadableError()
    {
        var summary = ErrorReportService.SummarizeError(
            "System.Exception\r\n消息：window closed\r\n堆栈：\r\nat Namespace.Method in C:\\private\\file.cs");

        Assert.Contains("System.Exception", summary);
        Assert.Contains("window closed", summary);
        Assert.DoesNotContain("Namespace.Method", summary);
        Assert.DoesNotContain("file.cs", summary);
    }

    [Fact]
    public void ErrorDraft_FiltersUserStepsAsWellAsErrorSummary()
    {
        var uri = ErrorReportService.BuildFeedbackDraft("API failed: token=error-secret",
            "步骤：password=user-secret", "0.1", "Windows");
        var body = ReadQuery(uri)["body"];

        Assert.DoesNotContain("error-secret", body);
        Assert.DoesNotContain("user-secret", body);
        Assert.Contains("[redacted]", body);
    }

    [Fact]
    public void MissingError_StillCreatesDraftForUserToComplete()
    {
        var uri = ErrorReportService.BuildFeedbackDraft(null, null, "0.1", "Windows");

        Assert.Equal(FeedbackContact.Email, uri.UserInfo + "@" + uri.Host);
        Assert.Contains("0.1", ReadQuery(uri)["body"]);
        Assert.Equal("", ErrorReportService.SummarizeError(null));
    }

    [Fact]
    public void SummaryAndDraft_AreBoundedWithoutSplittingUnicodePairs()
    {
        var input = "System.Exception: " + string.Concat(Enumerable.Repeat("测试😀", 2000));
        var summary = ErrorReportService.SummarizeError(input);
        var body = ReadQuery(FeedbackContact.CreateDraft("error", input))["body"];

        Assert.InRange(summary.Length, 1, ErrorReportService.MaxErrorSummaryLength);
        Assert.InRange(body.Length, 1, 2500);
        Assert.DoesNotContain(summary.EnumerateRunes(), r => r == Rune.ReplacementChar);
        Assert.DoesNotContain(body.EnumerateRunes(), r => r == Rune.ReplacementChar);
    }

    private static Dictionary<string, string> ReadQuery(Uri uri)
        => uri.Query.TrimStart('?').Split('&').ToDictionary(
            field => field[..field.IndexOf('=')],
            field => Uri.UnescapeDataString(field[(field.IndexOf('=') + 1)..]),
            StringComparer.Ordinal);
}
