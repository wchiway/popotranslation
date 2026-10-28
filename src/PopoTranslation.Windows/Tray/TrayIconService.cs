using System.ComponentModel;
using System.Runtime.InteropServices;
using PopoTranslation.Windows.Messaging;

namespace PopoTranslation.Windows.Tray;

public enum TrayAction { OpenSettings, TogglePause, Exit, OpenResult }

public sealed class TrayIconService : IDisposable
{
    private const uint CallbackMessage = 0x8002, WmRButtonUp = 0x0205, WmLButtonDoubleClick = 0x0203;
    private readonly NativeMessageWindow _window;
    private readonly uint _taskbarCreated;
    private NotifyIconData _icon;
    private bool _disposed;
    private bool _paused;
    public event Action<TrayAction>? ActionRequested;

    public TrayIconService(NativeMessageWindow window, nint iconHandle, string tooltip = "PopoTranslation")
    {
        if (iconHandle == 0) throw new ArgumentException("A valid icon handle is required.", nameof(iconHandle));
        _window = window;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        if (_taskbarCreated == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _icon = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window.Handle, Id = 1,
            Flags = 0x1 | 0x2 | 0x4, CallbackMessage = CallbackMessage, Icon = iconHandle,
            Tip = tooltip.Length <= 127 ? tooltip : tooltip[..127], Info = "", InfoTitle = ""
        };
        _window.Invoke(() =>
        {
            if (!ShellNotifyIcon(0, ref _icon)) throw new Win32Exception("无法创建通知区域图标。");
            _window.Message += OnMessage;
            return 0;
        });
    }

    public void SetPaused(bool paused) => _window.Invoke(() => { _paused = paused; return 0; });

    // Callers must supply only generic, non-sensitive status text.
    public bool ShowNotification(string title, string message) => _window.Invoke(() =>
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var notification = _icon;
        notification.Flags = 0x10;
        notification.InfoTitle = title.Length <= 63 ? title : title[..63];
        notification.Info = message.Length <= 255 ? message : message[..255];
        notification.InfoFlags = 0x1;
        return ShellNotifyIcon(1, ref notification);
    });

    public bool NotifyResultAvailable() => ShowNotification("翻译已完成", "点击查看译文并手动应用。");

    private void OnMessage(uint message, nint wParam, nint lParam)
    {
        if (_disposed) return;
        if (message == _taskbarCreated) { ShellNotifyIcon(0, ref _icon); return; }
        if (message != CallbackMessage || (uint)wParam != _icon.Id) return;
        if ((uint)lParam == WmLButtonDoubleClick) ActionRequested?.Invoke(TrayAction.OpenSettings);
        if ((uint)lParam == 0x0405) ActionRequested?.Invoke(TrayAction.OpenResult); // NIN_BALLOONUSERCLICK
        if ((uint)lParam == WmRButtonUp) ShowMenu();
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            AppendMenu(menu, 0, 1, "打开设置");
            AppendMenu(menu, 0, 2, _paused ? "恢复" : "暂停");
            AppendMenu(menu, 0, 3, "退出");
            GetCursorPos(out var point);
            SetForegroundWindow(_window.Handle);
            var command = TrackPopupMenu(menu, 0x0100 | 0x0002, point.X, point.Y, 0, _window.Handle, 0);
            if (command is >= 1 and <= 3) ActionRequested?.Invoke((TrayAction)(command - 1));
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        _window.Invoke(() =>
        {
            if (_disposed) return 0;
            _disposed = true;
            _window.Message -= OnMessage;
            ShellNotifyIcon(2, ref _icon);
            return 0;
        });
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
}
