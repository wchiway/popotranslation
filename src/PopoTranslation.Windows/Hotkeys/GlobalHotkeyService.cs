using System.Runtime.InteropServices;
using PopoTranslation.Windows.Messaging;

namespace PopoTranslation.Windows.Hotkeys;

[Flags]
public enum HotkeyModifiers : uint { Alt = 1, Control = 2, Shift = 4, Windows = 8 }
public enum HotkeyAction { Selection, WholeControl, TripleSpace }
public sealed record Hotkey(HotkeyModifiers Modifiers, uint VirtualKey);
public sealed record HotkeyRegistrationResult(bool Success, int ErrorCode = 0);

public interface IHotkeyNative
{
    bool Register(nint window, int id, uint modifiers, uint key, out int error);
    bool Unregister(nint window, int id);
}

public sealed class GlobalHotkeyService : IDisposable
{
    private const uint WmHotkey = 0x0312, ModNoRepeat = 0x4000;
    private readonly NativeMessageWindow _window;
    private readonly IHotkeyNative _native;
    private readonly Dictionary<HotkeyAction, (int Id, Hotkey Hotkey)> _registrations = new();
    private readonly HashSet<int> _registeredIds = new();
    private int _nextId = 1;
    private bool _disposed;
    public event Action<HotkeyAction>? Triggered;

    public GlobalHotkeyService(NativeMessageWindow window, IHotkeyNative? native = null)
    {
        _window = window;
        _native = native ?? new Win32HotkeyNative();
        _window.Message += OnMessage;
    }

    public HotkeyRegistrationResult Configure(HotkeyAction action, Hotkey hotkey)
    {
        ArgumentNullException.ThrowIfNull(hotkey);
        if (hotkey.VirtualKey is 0 or > 255 || ((uint)hotkey.Modifiers & ~15u) != 0)
            return new(false, 87);
        return _window.Invoke<HotkeyRegistrationResult>(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_registrations.TryGetValue(action, out var existing) && existing.Hotkey == hotkey)
                return new(true);
            if (_nextId > 0xBFFF) return new(false, 8);
            var id = _nextId++;
            if (!_native.Register(_window.Handle, id, (uint)hotkey.Modifiers | ModNoRepeat, hotkey.VirtualKey, out var error))
                return new(false, error);
            _registeredIds.Add(id);
            if (_registrations.TryGetValue(action, out existing) && _native.Unregister(_window.Handle, existing.Id))
                _registeredIds.Remove(existing.Id);
            _registrations[action] = (id, hotkey);
            return new(true);
        });
    }

    // New combinations are registered before any existing registration is released.
    // Existing IDs can be reassigned, allowing the two actions to be swapped without a gap.
    public HotkeyRegistrationResult ConfigurePair(Hotkey selection, Hotkey wholeControl)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(wholeControl);
        if (selection == wholeControl || !IsValid(selection) || !IsValid(wholeControl)) return new(false, 87);
        return _window.Invoke<HotkeyRegistrationResult>(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var requested = new Dictionary<HotkeyAction, Hotkey>
            {
                [HotkeyAction.Selection] = selection,
                [HotkeyAction.WholeControl] = wholeControl
            };
            var next = new Dictionary<HotkeyAction, (int Id, Hotkey Hotkey)>();
            var added = new List<int>();
            foreach (var entry in requested)
            {
                var existing = _registrations.Values.FirstOrDefault(x => x.Hotkey == entry.Value);
                if (existing.Hotkey is not null)
                {
                    next.Add(entry.Key, existing);
                    continue;
                }
                var error = 8;
                if (_nextId > 0xBFFF || !_native.Register(_window.Handle, _nextId,
                        (uint)entry.Value.Modifiers | ModNoRepeat, entry.Value.VirtualKey, out error))
                {
                    foreach (var addedId in added)
                        if (_native.Unregister(_window.Handle, addedId)) _registeredIds.Remove(addedId);
                    return new(false, error);
                }
                var id = _nextId++;
                _registeredIds.Add(id);
                added.Add(id);
                next.Add(entry.Key, (id, entry.Value));
            }
            var retained = next.Values.Select(x => x.Id).ToHashSet();
            foreach (var old in _registrations.Values)
                if (!retained.Contains(old.Id) && _native.Unregister(_window.Handle, old.Id))
                    _registeredIds.Remove(old.Id);
            _registrations.Clear();
            foreach (var entry in next) _registrations.Add(entry.Key, entry.Value);
            return new(true);
        });
    }

    public bool Clear() => _window.Invoke(() =>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _registrations.Clear();
        var success = true;
        foreach (var id in _registeredIds.ToArray())
        {
            if (_native.Unregister(_window.Handle, id)) _registeredIds.Remove(id);
            else success = false;
        }
        return success;
    });

    private static bool IsValid(Hotkey hotkey) => hotkey.VirtualKey is > 0 and <= 255 &&
        ((uint)hotkey.Modifiers & ~15u) == 0;

    private void OnMessage(uint message, nint wParam, nint lParam)
    {
        if (message != WmHotkey || _disposed) return;
        foreach (var pair in _registrations)
            if (pair.Value.Id == (int)wParam) { Triggered?.Invoke(pair.Key); break; }
    }

    public void Dispose()
    {
        _window.Invoke(() =>
        {
            if (_disposed) return 0;
            _disposed = true;
            _window.Message -= OnMessage;
            foreach (var id in _registeredIds) _native.Unregister(_window.Handle, id);
            _registeredIds.Clear();
            _registrations.Clear();
            return 0;
        });
    }

    private sealed class Win32HotkeyNative : IHotkeyNative
    {
        public bool Register(nint window, int id, uint modifiers, uint key, out int error)
        {
            var success = RegisterHotKey(window, id, modifiers, key);
            error = success ? 0 : Marshal.GetLastWin32Error();
            return success;
        }
        public bool Unregister(nint window, int id) => UnregisterHotKey(window, id);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(nint window, int id);
    }
}
