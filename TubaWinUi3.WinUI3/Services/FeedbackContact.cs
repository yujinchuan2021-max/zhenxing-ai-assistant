namespace TubaWinUi3.Services;

/// <summary>枕星版的反馈入口。只打开本机邮件客户端，不自动发送内容。</summary>
internal static class FeedbackContact
{
    internal const string Email = "yujinchuan2021@gmail.com";

    internal static Uri CreateDraft(string subject, string body)
    {
        // 邮件客户端对 mailto 长度的支持各异；草稿保持简短，由用户补充后发送。
        const int maxBodyLength = 2500;
        if (body.Length > maxBodyLength)
        {
            var marker = "\n\n" + LocalizationService.L("Feedback_DraftTruncated", "（内容已截断；请在邮件中补充。）");
            var take = Math.Max(0, maxBodyLength - marker.Length);
            if (take > 0 && char.IsHighSurrogate(body[take - 1])) take--;
            body = body[..take] + marker;
        }

        return new Uri($"mailto:{Email}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}");
    }
}
