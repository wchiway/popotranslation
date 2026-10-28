using System.Runtime.InteropServices;
using PopoTranslation.Windows.Diagnostics;

namespace PopoTranslation.Windows.Input;

internal static class KeyboardSimulator
{
    private const ushort VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12;
    private const ushort VkLWin = 0x5B, VkRWin = 0x5C, VkSpace = 0x20;
    private const ushort VkA = 0x41, VkC = 0x43, VkV = 0x56;
    private const uint KeyeventfKeyup = 0x0002;

    public static bool AreModifiersDown() =>
        IsDown(VkShift) || IsDown(VkControl) || IsDown(VkMenu) || IsDown(VkLWin) || IsDown(VkRWin);

    // The triple-space hook fires on keydown. Give shortcut keys time to clear
    // before injecting another chord; low-level hooks can affect key-state reporting.
    public static bool WaitForShortcutKeysReleased(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (!AreModifiersDown() && !IsDown(VkSpace))
            {
                Thread.Sleep(60);
                return true;
            }
            Thread.Sleep(10);
        }
        return !AreModifiersDown() && !IsDown(VkSpace);
    }

    public static bool IsSpaceDown() => IsDown(VkSpace);

    public static bool SendSelectAll() => SendCombo(VkControl, VkA);
    public static bool SendCopy() => SendCombo(VkControl, VkC);
    public static bool SendPaste() => SendCombo(VkControl, VkV);
    public static bool SendTerminalCopy() => SendCombo(VkControl, VkShift, VkC);

    private static bool SendCombo(params ushort[] keys)
    {
        if (keys.Length == 0) return false;
        var inputs = new Input[keys.Length * 2];
        for (var i = 0; i < keys.Length; i++)
            inputs[i] = Key(keys[i], up: false);
        for (var i = 0; i < keys.Length; i++)
            inputs[keys.Length + i] = Key(keys[keys.Length - 1 - i], up: true);
        var count = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        var error = count == inputs.Length ? 0 : Marshal.GetLastWin32Error();
        DiagnosticLog.Write($"event=send-input requested={inputs.Length} sent={count} error={error}");
        return count == inputs.Length;
    }

    private static Input Key(ushort key, bool up) => new()
    {
        Type = 1,
        Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? KeyeventfKeyup : 0 } }
    };

    private static bool IsDown(ushort key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParamL, ParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(ushort key);
}
