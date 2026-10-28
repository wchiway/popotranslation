using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PopoTranslation.Windows.Messaging;

// Hidden top-level window: TaskbarCreated is broadcast only to top-level windows.
public sealed class NativeMessageWindow : IDisposable
{
    private const uint WmDispatch = 0x8001, WmClose = 0x0010, WmDestroy = 0x0002;
    private readonly ConcurrentQueue<Action> _pending = new();
    private readonly object _lifetime = new();
    private readonly TaskCompletionSource<nint> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly WindowProc _windowProc;
    private int _disposed;
    private uint _threadId;
    public nint Handle { get; private set; }
    public event Action<uint, nint, nint>? Message;

    public NativeMessageWindow()
    {
        _windowProc = WindowProcedure;
        _thread = new Thread(Run) { IsBackground = true, Name = "PopoTranslation native messages" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Handle = _ready.Task.GetAwaiter().GetResult();
    }

    public T Invoke<T>(Func<T> action)
    {
        if (GetCurrentThreadId() == _threadId)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return action();
        }
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _pending.Enqueue(() =>
            {
                if (completion.Task.IsCompleted) return;
                try { completion.TrySetResult(action()); }
                catch (Exception error) { completion.TrySetException(error); }
            });
            if (!PostMessage(Handle, WmDispatch, 0, 0))
            {
                completion.TrySetException(new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
        return completion.Task.GetAwaiter().GetResult();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        var className = "PopoTranslation." + Guid.NewGuid().ToString("N");
        var instance = GetModuleHandle(null);
        var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = instance,
            ClassName = className, WindowProcedure = _windowProc };
        if (RegisterClassEx(ref cls) == 0)
        {
            _ready.TrySetException(new Win32Exception(Marshal.GetLastWin32Error()));
            return;
        }
        var handle = CreateWindowEx(0, className, "PopoTranslation", 0, 0, 0, 0, 0, 0, 0, instance, 0);
        if (handle == 0)
        {
            _ready.TrySetException(new Win32Exception(Marshal.GetLastWin32Error()));
            UnregisterClass(className, instance);
            return;
        }
        Handle = handle;
        _ready.TrySetResult(handle);
        while (GetMessage(out var message, 0, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
        UnregisterClass(className, instance);
    }

    private nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmDispatch)
        {
            while (_pending.TryDequeue(out var action)) action();
            return 0;
        }
        if (message == WmDestroy) { PostQuitMessage(0); return 0; }
        try { Message?.Invoke(message, wParam, lParam); }
        catch { /* Never let managed exceptions cross the Win32 callback boundary. */ }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        lock (_lifetime)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (Handle != 0) PostMessage(Handle, WmClose, 0, 0);
        }
        if (GetCurrentThreadId() != _threadId) _thread.Join();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public WindowProc WindowProcedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName, ClassName;
        public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window;
        public uint Message;
        public nint WParam, LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool UnregisterClass(string className, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint exStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out NativeMessage message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
