using System.Text.Json;
using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

/// <summary>Pure clarification parsing. No model requests, GUI, configuration, or tool actions.</summary>
public sealed class ChatChoiceQuestionTests
{
    private static string Payload(int count = 3) => JsonSerializer.Serialize(new
    {
        schema = 1,
        question = "你目前能正常使用这项服务吗？",
        options = Enumerable.Range(0, count).Select(i => new
        {
            id = "answer_" + i, label = "选项" + i, answer = "我的回答是选项" + i,
        }).ToArray(),
    });
    private static string Contract(string? payload = null) => "一句必要背景。\n```choice-question\n" + (payload ?? Payload()) + "\n```";

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(6)]
    public void CompletedBoundedQuestionCapturesExactNaturalAnswers(int count)
    {
        var question = ChatChoiceQuestion.TryCapture(Contract(Payload(count)));
        Assert.NotNull(question);
        Assert.False(question.IsTextFallback);
        Assert.Equal(count, question.Options.Count);
        Assert.Equal("你目前能正常使用这项服务吗？", question.Question);
        Assert.All(question.Options, x =>
        {
            Assert.StartsWith("answer_", x.Id);
            Assert.Equal("我的回答是选项" + x.Id[7..], x.Answer);
        });
    }

    [Fact]
    public void SurroundingTextAndCopyRemainReadableWithoutProtocolJson()
    {
        var original = Contract() + "\nAfter";
        var prompt = Assert.IsType<ChatChoicePrompt>(ChatChoiceQuestion.TryCapture(original));
        Assert.Equal("一句必要背景。\nAfter", ChatChoiceQuestion.GetVisibleContent(original));
        var copied = ChatChoiceQuestion.CopyWithChoices(original, prompt);
        Assert.Contains("一句必要背景。", copied);
        Assert.Contains("After", copied);
        Assert.Contains(prompt.Question, copied);
        foreach (var option in prompt.Options) Assert.Contains(option.Label, copied);
        Assert.DoesNotContain("schema", copied);
        Assert.DoesNotContain("choice-question", copied);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void OutOfRangeNumberOfOptionsCannotProduceButtons(int count)
    {
        var original = Contract(Payload(count));
        Assert.Null(ChatChoiceQuestion.TryCapture(original));
        Assert.Equal(original, ChatChoiceQuestion.GetVisibleContent(original));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"schema\":1}")]
    [InlineData("{\"schema\":\"1\",\"question\":\"Q?\",\"options\":[]}")]
    [InlineData("{\"schema\":2,\"question\":\"Q?\",\"options\":[]}")]
    [InlineData("{\"schema\":1.0,\"question\":\"Q?\",\"options\":[]}")]
    [InlineData("{\"schema\":1,\"question\":\"Q?\",\"options\":{}}")]
    [InlineData("{\"schema\":1,\"question\":\"Q?\",\"options\":[],\"command\":\"install\"}")]
    [InlineData("{\"schema\":1,\"schema\":1,\"question\":\"Q?\",\"options\":[]}")]
    public void InvalidSchemaCannotProduceButtonsAndRemainsVisible(string payload)
    {
        var original = Contract(payload);
        Assert.Null(ChatChoiceQuestion.TryCapture(original));
        Assert.Equal(original, ChatChoiceQuestion.GetVisibleContent(original));
    }

    [Theory]
    [InlineData("id", "answer_0")]
    [InlineData("label", "选项0")]
    [InlineData("answer", "我的回答是选项0")]
    public void DuplicateIdsLabelsOrAnswersAreRejected(string field, string value)
    {
        var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Payload())!;
        var options = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(map["options"].GetRawText())!;
        options[1][field] = value;
        Assert.Null(ChatChoiceQuestion.TryCapture(Contract(JsonSerializer.Serialize(new
        {
            schema = 1, question = "Q?", options,
        }))));
    }

    [Theory]
    [InlineData("id", "Uppercase")]
    [InlineData("id", "with spaces")]
    [InlineData("label", "")]
    [InlineData("label", "line\nbreak")]
    [InlineData("answer", "```powershell install```")]
    [InlineData("answer", "https://example.com/")]
    [InlineData("answer", "<button>Install</button>")]
    public void OptionValuesAreShortPlainTextNotLinksOrCode(string field, string value)
    {
        var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Payload())!;
        var options = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(map["options"].GetRawText())!;
        options[1][field] = value;
        Assert.Null(ChatChoiceQuestion.TryCapture(Contract(JsonSerializer.Serialize(new
        {
            schema = 1, question = "Q?", options,
        }))));
    }

    [Fact]
    public void ActionOrExtraFieldsInOptionsAreRejected()
    {
        var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Payload())!;
        var options = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(map["options"].GetRawText())!;
        options[0]["command"] = "install";
        Assert.Null(ChatChoiceQuestion.TryCapture(Contract(JsonSerializer.Serialize(new
        {
            schema = 1, question = "Q?", options,
        }))));
    }

    [Theory]
    [InlineData("```choice-question\n{}\n```")]
    [InlineData("```choice-quest")]
    [InlineData("```model-preference\n{\"schema\":1}\n```")]
    [InlineData("```model-pref")]
    [InlineData("```toolflow-items\nApp | software | | | |\n```")]
    [InlineData("```toolflow-opt")]
    public void MixedOrPartialCompetingProtocolsCannotOfferQuestionButtons(string other)
    {
        Assert.Null(ChatChoiceQuestion.TryCapture(Contract() + "\n" + other));
        Assert.Null(ChatChoiceQuestion.TryCapture(other + "\n" + Contract()));
    }

    [Fact]
    public void EveryValidStreamingPrefixIsHiddenButNotActionable()
    {
        var payload = Payload();
        const string prefix = "背景。\n```choice-question\n";
        for (var length = 0; length <= payload.Length; length++)
        {
            var original = prefix + payload[..length];
            Assert.Null(ChatChoiceQuestion.TryCapture(original));
            Assert.Equal("背景。\n", ChatChoiceQuestion.GetVisibleContent(original));
        }
        Assert.Equal("背景。\n", ChatChoiceQuestion.GetVisibleContent(prefix + payload + "\n`"));
        Assert.Equal("背景。\n", ChatChoiceQuestion.GetVisibleContent(prefix + payload + "\n``"));
        Assert.Equal("背景。\n", ChatChoiceQuestion.GetVisibleContent("背景。\n```choice-quest"));
    }

    [Fact]
    public void OrdinaryCodeQuotedAndIndentedExamplesAreNotProtocols()
    {
        string[] examples =
        [
            "````text\n" + Contract() + "\n````",
            "~~~markdown\n" + Contract() + "\n~~~",
            string.Join("\n", Contract().Split('\n').Select(x => "    " + x)),
            string.Join("\n", Contract().Split('\n').Select(x => "> " + x)),
            "An example: ```choice-question {schema:1}```",
        ];
        foreach (var example in examples)
        {
            Assert.Null(ChatChoiceQuestion.TryCapture(example));
            Assert.False(ChatChoiceQuestion.HasQuestionProtocol(example));
            Assert.Equal(example, ChatChoiceQuestion.GetVisibleContent(example));
        }
    }

    [Fact]
    public void ScreenshotNetworkQuestionBecomesThreeOptionsWithoutDuplicatingTheProseList()
    {
        const string original = "顶级模型这条路线仍需确认账号与网络。\n你目前能正常登录并使用 OpenAI / ChatGPT 这类海外服务吗？\n- 能正常用\n- 打不开或没有可用账号\n- 不确定\n回一个就行；这条确认后我就直接给你三套完整方案对比。";
        var question = Assert.IsType<ChatChoicePrompt>(ChatChoiceQuestion.TryCapture(original));
        Assert.True(question.IsTextFallback);
        Assert.Equal(new[] { "能正常用", "打不开或没有可用账号", "不确定" }, question.Options.Select(x => x.Label));
        Assert.Equal("打不开或没有可用账号", question.Options[1].Answer);
        Assert.Equal("顶级模型这条路线仍需确认账号与网络。", ChatChoiceQuestion.GetQuestionVisibleContent(original, question));
        Assert.Equal(original, ChatChoiceQuestion.GetVisibleContent(original)); // Streaming prose is not guessed.
        var copied = ChatChoiceQuestion.CopyWithChoices(original, question);
        Assert.DoesNotContain("回一个就行", copied);
        Assert.Contains("1. 能正常用", copied);
        Assert.Null(ChatChoiceQuestion.TryCapture(original, allowTextFallback: false));
    }

    [Fact]
    public void EnglishAndCrLfPlainQuestionsKeepTheirOwnLanguageAndAnswer()
    {
        const string original = "Can you use this service?\r\n- Yes\r\n- No\r\n- Not sure\r\nChoose one.";
        var question = Assert.IsType<ChatChoicePrompt>(ChatChoiceQuestion.TryCapture(original));
        Assert.Equal("Can you use this service?", question.Question);
        Assert.Equal("Not sure", question.Options[2].Answer);
        Assert.Equal("", ChatChoiceQuestion.GetQuestionVisibleContent(original, question));
    }

    [Theory]
    [InlineData("怎么安装软件？\n- 下载官方安装包\n- 打开安装程序\n- 完成安装")]
    [InlineData("How do I install it?\n- Download\n- Run\n- Finish")]
    [InlineData("可以按下面步骤安装吗？\n- 下载官方安装包\n- 运行安装程序\n- 完成安装")]
    [InlineData("Can I install it using these steps?\n- Download the installer\n- Run the installer\n- Finish installation")]
    [InlineData("推荐以下工具：\n- Godot\n- Blender\n- Audacity")]
    [InlineData("你使用哪个平台？\n1. Windows\n2. macOS")]
    [InlineData("你使用哪个平台？\n- Windows | software\n- macOS | software")]
    [InlineData("你使用哪个平台？\n- Windows\n- macOS\n然后我会安装软件并开始制作。")]
    [InlineData("你使用哪个平台？\n- Windows\n- macOS\n你的预算是多少？")]
    [InlineData("你的目标是什么？我需要先了解。\n你使用哪个平台？\n- Windows\n- macOS")]
    [InlineData("    你使用哪个平台？\n    - Windows\n    - macOS")]
    [InlineData("> 你使用哪个平台？\n> - Windows\n> - macOS")]
    [InlineData("你使用哪个平台？\n- [Windows](https://example.com)\n- macOS")]
    [InlineData("- 已知目标\n你使用哪个平台？\n- Windows\n- macOS")]
    public void StepsRecommendationsMultipleQuestionsAndExamplesRemainPlainText(string original)
    {
        Assert.Null(ChatChoiceQuestion.TryCapture(original));
        Assert.Equal(original, ChatChoiceQuestion.GetVisibleContent(original));
    }

    [Fact]
    public void ExplicitPlatformAlternativesKeepTheirLabelsWithoutYesNoGuessing()
    {
        var question = Assert.IsType<ChatChoicePrompt>(ChatChoiceQuestion.TryCapture(
            "你使用哪个平台？\n- Windows\n- macOS\n- Linux"));
        Assert.Equal(new[] { "Windows", "macOS", "Linux" }, question.Options.Select(x => x.Label));
    }

    [Fact]
    public void BoundsPreventLargePayloadOrMessageFromBecomingQuestions()
    {
        var original = Contract(new string(' ', ChatChoiceQuestion.MaxPayloadChars) + Payload());
        Assert.Null(ChatChoiceQuestion.TryCapture(original));
        Assert.Equal(original, ChatChoiceQuestion.GetVisibleContent(original));
        Assert.Null(ChatChoiceQuestion.TryCapture(new string('a', ChatChoiceQuestion.MaxMessageChars) + Contract()));
        var longLabelPayload = JsonSerializer.Serialize(new
        {
            schema = 1, question = "Q?", options = new[]
            {
                new { id = "one", label = new string('a', 81), answer = "One" },
                new { id = "two", label = "Two", answer = "Two" },
            },
        });
        Assert.Null(ChatChoiceQuestion.TryCapture(Contract(longLabelPayload)));
    }
}
