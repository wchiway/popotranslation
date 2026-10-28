namespace PopoTranslation.Core;

public static class TranslationLineBreaks
{
    // Normalize existing text line breaks only; visual wrapping is not a text character.
    public static string ToWindows(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
    }
}
