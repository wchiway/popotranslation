using System.Runtime.InteropServices;

namespace PopoTranslation.Windows.Diagnostics;

internal static class FocusDiagnostics
{
    public static void Record(string stage)
    {
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (GetGUIThreadInfo(0, ref info))
            DiagnosticLog.Write($"event=focus stage={stage} active={info.Active} focus={info.Focus} caret={info.Caret} flags={info.Flags}");
        else
            DiagnosticLog.Write($"event=focus stage={stage} error={Marshal.GetLastWin32Error()}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int CaretLeft, CaretTop, CaretRight, CaretBottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
}
