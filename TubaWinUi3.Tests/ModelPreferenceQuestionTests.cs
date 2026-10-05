using System.Xml.Linq;
using TubaWinUi3.Services;
using WinUI3Localizer;

namespace TubaWinUi3.Tests;

/// <summary>Pure protocol and resource checks; no chat requests, settings writes or GUI startup.</summary>
public class ModelPreferenceQuestionTests
{
    private static string Contract(string body = "{\"schema\":1}") =>
        "请选择模型使用偏好。\n```model-preference\n" + body + "\n```";

    [Fact]
    public void CompleteContractAndJsonWhitespaceAreAccepted()
    {
        Assert.True(ModelPreferenceQuestion.TryCapture(Contract()));
        Assert.True(ModelPreferenceQuestion.TryCapture("```model-preference\n{\"schema\":1}\n```"));
        Assert.True(ModelPreferenceQuestion.TryCapture("Before\r\n  ```model-preference \r\n { \"schema\" : 1 } \r\n  ```\r\nAfter"));
        Assert.True(ModelPreferenceQuestion.TryCapture(Contract("{\"sche\\u006da\":1}")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[{\"schema\":1}]")]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("{\"schema\":0}")]
    [InlineData("{\"schema\":2}")]
    [InlineData("{\"schema\":1.0}")]
    [InlineData("{\"schema\":1e0}")]
    [InlineData("{\"schema\":\"1\"}")]
    [InlineData("{\"schema\":true}")]
    [InlineData("{\"schema\":null}")]
    [InlineData("{\"schema\":{\"schema\":1}}")]
    [InlineData("{\"Schema\":1}")]
    [InlineData("{\"schema\":1,\"id\":\"paid_top\"}")]
    [InlineData("{\"schema\":1,\"schema\":1}")]
    [InlineData("{\"schema\":1,\"sche\\u006da\":1}")]
    [InlineData("{\"schema\":1,}")]
    [InlineData("{\"schema\":1} {\"schema\":1}")]
    [InlineData("{\"schema\":1}//comment")]
    public void InvalidContractsCannotOfferChoicesAndRemainReadable(string body)
    {
        var original = Contract(body);
        Assert.False(ModelPreferenceQuestion.TryCapture(original));
        Assert.Equal(original, ModelPreferenceQuestion.GetVisibleContent(original));
    }

    [Fact]
    public void InvalidFormatNoticesRecognizeRootQuestionTagsButNotOrdinaryExamples()
    {
        Assert.True(ModelPreferenceQuestion.HasQuestionProtocol(Contract()));
        Assert.True(ModelPreferenceQuestion.HasQuestionProtocol(Contract("{\"schema\":2}")));
        Assert.True(ModelPreferenceQuestion.HasQuestionProtocol("```model-pref"));
        Assert.True(ModelPreferenceQuestion.HasQuestionProtocol(Contract(new string('x', ModelPreferenceQuestion.MaxMessageChars))));
        Assert.False(ModelPreferenceQuestion.HasQuestionProtocol("An ordinary answer."));
        Assert.False(ModelPreferenceQuestion.HasQuestionProtocol("```toolflow-items\nApp | software | | | |\n```"));
        Assert.False(ModelPreferenceQuestion.HasQuestionProtocol("````text\n" + Contract() + "\n````"));
        Assert.False(ModelPreferenceQuestion.HasQuestionProtocol(string.Join("\n", Contract().Split('\n').Select(line => "    " + line))));
        Assert.False(ModelPreferenceQuestion.HasQuestionProtocol(string.Join("\n", Contract().Split('\n').Select(line => "> " + line))));
    }
    [Fact]
    public void MissingAndUnclosedBlocksCannotOfferChoices()
    {
        Assert.False(ModelPreferenceQuestion.TryCapture(""));
        Assert.False(ModelPreferenceQuestion.TryCapture("Just a question about model costs."));
        Assert.False(ModelPreferenceQuestion.TryCapture(Contract()[..^3]));
        Assert.False(ModelPreferenceQuestion.TryCapture("```model-preference"));
        Assert.False(ModelPreferenceQuestion.TryCapture("```model-pref"));
    }

    [Theory]
    [InlineData("```model-preference\n{\"schema\":1}\n```")]
    [InlineData("```model-preference\n{\"schema\":2}\n```")]
    [InlineData("```model-preference\n")]
    [InlineData("```model-pref")]
    [InlineData("```toolflow-options\n{}\n```")]
    [InlineData("```toolflow-items\nApp | software | | | |\n```")]
    [InlineData("```toolflow-options\n")]
    [InlineData("```toolflow-opt")]
    [InlineData("```toolflow-ite")]
    public void DuplicateMixedAndPartialProtocolSuffixesDefeatUniqueness(string other)
    {
        Assert.False(ModelPreferenceQuestion.TryCapture(Contract() + "\n" + other));
        Assert.False(ModelPreferenceQuestion.TryCapture(other + "\n" + Contract()));
    }

    [Theory]
    [InlineData("```json\n{\"schema\":1}\n```")]
    [InlineData("~~~model-preference\n{\"schema\":1}\n~~~")]
    [InlineData("````model-preference\n{\"schema\":1}\n````")]
    [InlineData("```MODEL-PREFERENCE\n{\"schema\":1}\n```")]
    [InlineData("```model-preference extra\n{\"schema\":1}\n```")]
    [InlineData("`model-preference {\"schema\":1}`")]
    public void OnlyTheSpecifiedFenceLabelAndMarkerAreAccepted(string original)
    {
        Assert.False(ModelPreferenceQuestion.TryCapture(original));
        Assert.Equal(original, ModelPreferenceQuestion.GetVisibleContent(original));
    }

    [Fact]
    public void ProtocolExamplesInsideOrdinaryCodeOrQuotedBlocksArePreserved()
    {
        var question = Contract();
        string[] examples =
        [
            "````text\n" + question + "\n````",
            "~~~markdown\n" + question + "\n~~~",
            "```text\n" + question + "\n```",
            string.Join("\n", question.Split('\n').Select(line => "    " + line)),
            string.Join("\n", question.Split('\n').Select(line => "> " + line)),
            "An inline example: ```model-preference {\"schema\":1}```",
        ];
        foreach (var example in examples)
        {
            Assert.False(ModelPreferenceQuestion.TryCapture(example));
            Assert.Equal(example, ModelPreferenceQuestion.GetVisibleContent(example));
        }
        var ordinary = "```json\n{\"ordinary\":true}\n```\n";
        Assert.True(ModelPreferenceQuestion.TryCapture(ordinary + question));
        Assert.StartsWith(ordinary, ModelPreferenceQuestion.GetVisibleContent(ordinary + question));
    }

    [Fact]
    public void MessageAndPayloadLengthsAreBounded()
    {
        var exactPayload = "{\"schema\":1}".PadRight(ModelPreferenceQuestion.MaxPayloadChars - 1);
        Assert.True(ModelPreferenceQuestion.TryCapture(Contract(exactPayload)));
        var oversizedPayload = Contract(exactPayload + " ");
        Assert.False(ModelPreferenceQuestion.TryCapture(oversizedPayload));
        Assert.Equal(oversizedPayload, ModelPreferenceQuestion.GetVisibleContent(oversizedPayload));
        var question = Contract();
        var exactMessage = new string('a', ModelPreferenceQuestion.MaxMessageChars - question.Length - 1) + "\n" + question;
        Assert.Equal(ModelPreferenceQuestion.MaxMessageChars, exactMessage.Length);
        Assert.True(ModelPreferenceQuestion.TryCapture(exactMessage));
        Assert.False(ModelPreferenceQuestion.TryCapture("a" + exactMessage));
        Assert.Equal("a" + exactMessage, ModelPreferenceQuestion.GetVisibleContent("a" + exactMessage));
    }

    [Fact]
    public void ValidQuestionIsHiddenWithoutChangingTheSurroundingText()
    {
        const string original = "Before\r\n```model-preference\r\n{\"schema\":1}\r\n```\r\nAfter";
        Assert.Equal("Before\r\nAfter", ModelPreferenceQuestion.GetVisibleContent(original));
        Assert.Equal("请选择模型使用偏好。\n", ModelPreferenceQuestion.GetVisibleContent(Contract()));
        Assert.Contains("\"schema\":1", original);
    }

    [Fact]
    public void EveryValidStreamingPayloadPrefixIsHiddenButNeverCaptured()
    {
        const string before = "请选一个偏好。\n";
        const string payload = "{ \"schema\" : 1 }";
        for (var length = 0; length <= payload.Length; length++)
        {
            var original = before + "```model-preference\n" + payload[..length];
            Assert.False(ModelPreferenceQuestion.TryCapture(original));
            Assert.Equal(before, ModelPreferenceQuestion.GetVisibleContent(original));
        }
        Assert.Equal(before, ModelPreferenceQuestion.GetVisibleContent(before + "```model-preference\n" + payload + "\n`"));
        Assert.Equal(before, ModelPreferenceQuestion.GetVisibleContent(before + "```model-preference\n" + payload + "\n``"));
        const string escapedPayload = "{\"sche\\u006da\":1}";
        for (var length = 0; length <= escapedPayload.Length; length++)
            Assert.Equal(before, ModelPreferenceQuestion.GetVisibleContent(before + "```model-preference\n" + escapedPayload[..length]));
        Assert.Equal(before, ModelPreferenceQuestion.GetVisibleContent(before + "```model-pref"));
        Assert.Equal(before, ModelPreferenceQuestion.GetVisibleContent(before + "```model-preference"));
    }

    [Theory]
    [InlineData("{\"schema\":2")]
    [InlineData("{\"schema\":1,")]
    [InlineData("{\"s chema\":1}")]
    [InlineData("{\"schema\":1.0")]
    [InlineData("{\"schema\":\"1")]
    [InlineData("{\"other\":1")]
    [InlineData("[]")]
    [InlineData("not JSON")]
    public void InvalidStreamingPayloadsStayReadableAndCannotBeClicked(string payload)
    {
        var original = "```model-preference\n" + payload;
        Assert.False(ModelPreferenceQuestion.TryCapture(original));
        Assert.Equal(original, ModelPreferenceQuestion.GetVisibleContent(original));
    }

    [Fact]
    public void ChoicesHaveFourStableIdsAndLocalAnswerCarriesItsHardwarePrerequisite()
    {
        var choices = ModelPreferenceQuestion.Choices;
        Assert.Equal(new[] { "paid_top", "paid_value", "local", "recommend" }, choices.Select(x => x.Id));
        Assert.All(choices, x =>
        {
            Assert.False(string.IsNullOrWhiteSpace(x.Label));
            Assert.False(string.IsNullOrWhiteSpace(x.Answer));
            Assert.DoesNotContain("```", x.Answer);
            Assert.DoesNotContain("\"schema\"", x.Answer);
        });
        var local = choices.Single(x => x.Id == "local");
        Assert.False(string.IsNullOrWhiteSpace(local.Hint));
        Assert.True(local.Answer.Contains("内存", StringComparison.Ordinal) || local.Answer.Contains("RAM", StringComparison.Ordinal));
        Assert.True(local.Answer.Contains("显卡", StringComparison.Ordinal) || local.Answer.Contains("graphics card", StringComparison.Ordinal));
        Assert.True(local.Answer.Contains("显存", StringComparison.Ordinal) || local.Answer.Contains("VRAM", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChoicesAndCopyTextRefreshWhenTheResourceLanguageChangesWithoutWritingSettings()
    {
        var strings = Path.Combine(FontSingleSourceTests.RepoRoot, "TubaWinUi3.WinUI3", "Strings");
        ILocalizer localizer = LocalizerBuilder.IsLocalizerAlreadyBuilt ? Localizer.Get() : await new LocalizerBuilder()
            .AddStringResourcesFolderForLanguageDictionaries(strings)
            .SetOptions(options => options.DefaultLanguage = "zh-CN")
            .Build();
        try
        {
            await localizer.SetLanguage("zh-CN");
            var chinese = ModelPreferenceQuestion.Choices;
            AssertCurrentResources(chinese, ReadResources(strings, "zh-CN"));
            var chineseCopy = ModelPreferenceQuestion.CopyWithChoices(Contract());
            Assert.Contains("内存", chinese.Single(x => x.Id == "local").Answer);
            Assert.Contains("显卡", chinese.Single(x => x.Id == "local").Answer);
            Assert.Contains("显存", chinese.Single(x => x.Id == "local").Answer);

            await localizer.SetLanguage("en-US");
            var english = ModelPreferenceQuestion.Choices;
            AssertCurrentResources(english, ReadResources(strings, "en-US"));
            Assert.Equal(chinese.Select(x => x.Id), english.Select(x => x.Id));
            Assert.All(chinese.Zip(english), pair =>
            {
                Assert.NotEqual(pair.First.Label, pair.Second.Label);
                Assert.NotEqual(pair.First.Answer, pair.Second.Answer);
            });
            var englishLocal = english.Single(x => x.Id == "local");
            Assert.Contains("RAM", englishLocal.Answer);
            Assert.Contains("graphics card", englishLocal.Answer);
            Assert.Contains("VRAM", englishLocal.Answer);
            var englishCopy = ModelPreferenceQuestion.CopyWithChoices(Contract());
            Assert.NotEqual(chineseCopy, englishCopy);
            Assert.DoesNotContain("\"schema\"", englishCopy);
            Assert.DoesNotContain("model-preference", englishCopy);
            Assert.DoesNotContain("```", englishCopy);
            foreach (var choice in english) Assert.Contains(choice.Label, englishCopy);
            Assert.Contains(englishLocal.Hint!, englishCopy);
            Assert.Equal("可以付费，并使用顶级模型", chinese[0].Label);
        }
        finally
        {
            await localizer.SetLanguage("zh-CN");
        }
    }

    private static Dictionary<string, string> ReadResources(string strings, string language) =>
        XDocument.Load(Path.Combine(strings, language, "Resources.resw")).Root!.Elements("data")
            .ToDictionary(x => x.Attribute("name")!.Value, x => x.Element("value")!.Value, StringComparer.Ordinal);

    private static void AssertCurrentResources(IReadOnlyList<ModelPreferenceChoice> choices,
        IReadOnlyDictionary<string, string> resources)
    {
        string[] names = ["PaidTop", "PaidValue", "Local", "Recommend"];
        for (var i = 0; i < names.Length; i++)
        {
            Assert.Equal(resources["Ai_ModelPreference_" + names[i]], choices[i].Label);
            Assert.Equal(resources["Ai_ModelPreference_" + names[i] + "Answer"], choices[i].Answer);
        }
        Assert.Equal(resources["Ai_ModelPreference_LocalHint"], choices[2].Hint);
        var copied = ModelPreferenceQuestion.CopyWithChoices("Visible body");
        Assert.Contains("Visible body", copied);
        Assert.Contains(resources["Ai_ModelPreference_Title"], copied);
        Assert.Contains(resources["Ai_ModelPreference_CopyHint"], copied);
    }
}
