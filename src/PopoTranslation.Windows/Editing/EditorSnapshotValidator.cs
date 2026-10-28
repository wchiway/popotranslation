namespace PopoTranslation.Windows.Editing;

public static class EditorSnapshotValidator
{
    public static bool Matches(EditorSnapshot expected, EditorSnapshot actual) =>
        expected.WindowHandle != 0 && expected.WindowHandle == actual.WindowHandle &&
        expected.ProcessId > 0 && expected.ProcessId == actual.ProcessId &&
        expected.ProcessStartTimeUtcTicks > 0 && expected.ProcessStartTimeUtcTicks == actual.ProcessStartTimeUtcTicks &&
        !string.IsNullOrEmpty(expected.ElementRuntimeId) && expected.ElementRuntimeId == actual.ElementRuntimeId &&
        expected.Mode == actual.Mode && expected.DocumentText == actual.DocumentText &&
        expected.SelectionStart == actual.SelectionStart && expected.SelectionEnd == actual.SelectionEnd;
}
