using PopoTranslation.Core;

namespace PopoTranslation.Core.Tests;

public sealed class TranslationLineBreaksTests
{
    [Theory]
    [InlineData("自然折行只是显示效果", "自然折行只是显示效果")]
    [InlineData("第一行\n第二行", "第一行\r\n第二行")]
    [InlineData("\r\n第一行\r第二行\n\n", "\r\n第一行\r\n第二行\r\n\r\n")]
    [InlineData("一\r\n二\r\n", "一\r\n二\r\n")]
    public void ConvertsOnlyExistingLineBreaksToCrLf(string input, string expected)
    {
        Assert.Equal(expected, TranslationLineBreaks.ToWindows(input));
    }

    [Theory]
    [InlineData("第一行\n\n第二行\n", "line one\r\n\r\nline two\r\n", true)]
    [InlineData("单行", "one line", true)]
    [InlineData("第一行\n\n第二行\n", "line one\nline two\n\n", false)]
    [InlineData("第一行\n第二行", "line one\nline two\n", false)]
    [InlineData("a\n \nb", "x\n\ny", true)]
    public void SameLineStructureComparesLineCountAndBlankLines(string source, string translated, bool expected)
    {
        Assert.Equal(expected, TranslationLineBreaks.SameLineStructure(source, translated));
    }
}
