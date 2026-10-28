using PopoTranslation.Core;

namespace PopoTranslation.Core.Tests;

public class TranslationResultTests
{
    [Theory]
    [InlineData(TranslationStatus.Empty)]
    [InlineData(TranslationStatus.Truncated)]
    [InlineData(TranslationStatus.Refused)]
    [InlineData(TranslationStatus.Failed)]
    [InlineData(TranslationStatus.TimedOut)]
    [InlineData(TranslationStatus.Cancelled)]
    public void OnlyCompletedTextCanBeApplied(TranslationStatus status) =>
        Assert.False(new TranslationResult(status, "text", null, null).IsSuccessful);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n ")]
    public void CompletedWithoutTextCannotBeApplied(string? text) =>
        Assert.False(new TranslationResult(TranslationStatus.Completed, text, null, null).IsSuccessful);

    [Theory]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1", "https://DASHSCOPE.aliyuncs.com:443/compatible-mode/v1/", true)]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1", "", true)]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", false)]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1", "https://workspace.cn-beijing.maas.aliyuncs.com/compatible-mode/v1", false)]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1", "https://dashscope.aliyuncs.com/compatible-mode/v2", false)]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1", "https://dashscope.aliyuncs.com/compatible-mode/v1?query=1", false)]
    [InlineData("invalid", "invalid", false)]
    public void QwenCredentialReuseRequiresSameValidAddress(string oldUrl, string newUrl, bool expected) =>
        Assert.Equal(expected, ProviderCredentialPolicy.CanReuse(ApiProtocol.QwenMt, oldUrl, newUrl));

    [Fact]
    public void PreservesWhitespaceAroundValidText()
    {
        var result = new TranslationResult(TranslationStatus.Completed, "  文本\n", null, null);
        Assert.True(result.IsSuccessful);
        Assert.Equal("  文本\n", result.Text);
    }
}
