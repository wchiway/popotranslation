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

    [Fact]
    public void PreservesWhitespaceAroundValidText()
    {
        var result = new TranslationResult(TranslationStatus.Completed, "  文本\n", null, null);
        Assert.True(result.IsSuccessful);
        Assert.Equal("  文本\n", result.Text);
    }
}
