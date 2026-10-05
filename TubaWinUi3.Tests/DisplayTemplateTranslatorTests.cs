using TubaWinUi3.Services;
using Xunit;

namespace TubaWinUi3.Tests;

public sealed class DisplayTemplateTranslatorTests
{
    [Fact]
    public void TranslationBindsReorderedPlaceholdersByName()
    {
        var translator = new DisplayTemplateTranslator(new Dictionary<string, string>
        {
            ["文件 {filename} 需要 {appName}"] = "{appName} is required for {filename}",
        });

        Assert.True(translator.TryTranslate("文件 notes.doc 需要 Word", out var translated));
        Assert.Equal("Word is required for notes.doc", translated);
    }

    [Theory]
    [InlineData("a{x}.png")]
    [InlineData(@"C:\工作\{appName}\a{filename}.png")]
    [InlineData("line1\n{filename}")]
    [InlineData("")]
    public void CapturedContentIsInsertedOnceAndPreserved(string filename)
    {
        var translator = new DisplayTemplateTranslator(new Dictionary<string, string>
        {
            ["文件 {filename} 失败"] = "Failed: {filename}",
        });

        Assert.True(translator.TryTranslate($"文件 {filename} 失败", out var translated));
        Assert.Equal("Failed: " + filename, translated);
    }

    [Fact]
    public void UnknownTextRemainsUnchanged()
    {
        var translator = new DisplayTemplateTranslator(new Dictionary<string, string>
        {
            ["文件 {filename} 失败"] = "Failed: {filename}",
            ["文件"] = "File",
        });

        const string userContent = @"D:\用户文档\文件{filename}.png";
        Assert.False(translator.TryTranslate(userContent, out var translated));
        Assert.Equal(userContent, translated);
    }

    [Fact]
    public void SpecificTemplateWinsOverGenericTemplate()
    {
        var translator = new DisplayTemplateTranslator(new Dictionary<string, string>
        {
            ["错误 {detail}"] = "Error: {detail}",
            ["错误 安装 {filename}：{reason}"] = "Could not install {filename}: {reason}",
        });

        Assert.True(translator.TryTranslate("错误 安装 app.exe：被拒绝", out var translated));
        Assert.Equal("Could not install app.exe: 被拒绝", translated);
    }

    [Fact]
    public void RepeatedPlaceholderUsesSameCapturedValue()
    {
        var translator = new DisplayTemplateTranslator(new Dictionary<string, string>
        {
            ["重试 {name}"] = "Retry {name} (previously {name})",
        });

        Assert.True(translator.TryTranslate("重试 a{name}.png", out var translated));
        Assert.Equal("Retry a{name}.png (previously a{name}.png)", translated);
    }

    [Fact]
    public void LegacyRenamedPlaceholderRetainsPositionalFallback()
    {
        var translator = new DisplayTemplateTranslator(new Dictionary<string, string>
        {
            ["文件 {文件名} 失败"] = "Failed: {filename}",
        });

        Assert.True(translator.TryTranslate("文件 old.doc 失败", out var translated));
        Assert.Equal("Failed: old.doc", translated);
    }
}
