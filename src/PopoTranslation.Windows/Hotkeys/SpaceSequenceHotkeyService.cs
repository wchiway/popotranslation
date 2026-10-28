using System.ComponentModel;
using System.Runtime.InteropServices;
using PopoTranslation.Windows.Automation;
using PopoTranslation.Windows.Input;
using PopoTranslation.Windows.Messaging;

namespace PopoTranslation.Windows.Hotkeys;

// Global low-level space detector. It observes only space key transitions; it does
// not record text or other keys. The third space is swallowed so the shortcut does
// not insert the final separator into the target control.
public sealed class SpaceSequenceHotkeyService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    private const uint LlkhfInjected = 0x10;
    private readonly NativeMessageWindow _window;
    private readonly LowLevelKeyboardProc _callback;
    private readonly TripleSpaceDetector _detector = new();
    private nint _hook;
    private int _disposed;
    // Return true when the application accepted the shortcut (and the third space should be swallowed).
    public event Func<bool>? Triggered;

    public SpaceSequenceHotkeyService(NativeMessageWindow window)
    {
        _window = window;
        _callback = OnKeyboardMessage;
        _window.Invoke(() =>
        {
            _hook = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(null), 0);
            if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            return 0;
        });
    }

    public bool IsInstalled => Volatile.Read(ref _hook) != 0;

    private nint OnKeyboardMessage(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && Volatile.Read(ref _disposed) == 0)
        {
            var message = (uint)wParam;
            var info = Marshal.PtrToStructure<KeyboardHookData>(lParam);
            if ((info.Flags & LlkhfInjected) == 0 &&
                (message is WmKeyDown or WmSysKeyDown) &&
                KeyboardSimulator.AreModifiersDown())
            {
                _detector.Reset();
            }
            else
            {
                var isDown = message is WmKeyDown or WmSysKeyDown;
                var isUp = message is WmKeyUp or WmSysKeyUp;
                if (isDown || isUp)
                {
                    if (isDown && info.VirtualKey == TripleSpaceDetector.SpaceVirtualKey &&
                        (IsOwnProcessForeground() || IsTerminalForeground()))
                    {
                        // A three-space shortcut cannot safely select terminal text because
                        // the spaces would enter the terminal. Use the selection hotkey there.
                        _detector.Reset();
                    }
                    else if (_detector.Process(info.VirtualKey, isDown, Environment.TickCount64))
                    {
                        var handler = Triggered;
                        if (handler is not null)
                        {
                            try { if (handler()) return 1; }
                            catch { /* Never cross the hook boundary. */ }
                        }
                    }
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool IsOwnProcessForeground()
    {
        var window = GetForegroundWindow();
        if (window == 0) return false;
        GetWindowThreadProcessId(window, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    private static bool IsTerminalForeground()
    {
        var window = GetForegroundWindow();
        return window != 0 && TargetApplication.ClassifyWindow(window) == TargetKind.Terminal;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var hook = Interlocked.Exchange(ref _hook, 0);
        if (hook == 0) return;
        try { _window.Invoke(() => { UnhookWindowsHookEx(hook); return 0; }); }
        catch { /* The native message thread may already be gone. */ }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKey, ScanCode, Flags, Time;
        public nint ExtraInfo;
    }

    private delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hook, LowLevelKeyboardProc callback, nint module, uint threadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
