namespace PopoTranslation.Core;

public static class TranslationLineBreaks
{
    // Normalize existing text line breaks only; visual wrapping is not a text character.
    public static string ToWindows(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
    }

    /// <summary>
    /// True when both texts have the same number of lines and blank lines appear at the same positions.
    /// Line break style (CRLF, CR, LF) is ignored. Used to reject translations that would misalign a write-back.
    /// </summary>
    public static bool SameLineStructure(string source, string translated)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(translated);
        var sourceLines = Lines(source);
        var translatedLines = Lines(translated);
        if (sourceLines.Length != translatedLines.Length) return false;
        for (var i = 0; i < sourceLines.Length; i++)
            if (string.IsNullOrWhiteSpace(sourceLines[i]) != string.IsNullOrWhiteSpace(translatedLines[i])) return false;
        return true;
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
