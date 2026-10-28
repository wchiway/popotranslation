namespace PopoTranslation.Windows.Hotkeys;

// Pure sequence logic, kept separate so it can be tested without installing a hook.
public sealed class TripleSpaceDetector
{
    public const uint SpaceVirtualKey = 0x20;
    public const int DefaultIntervalMilliseconds = 700;
    private readonly long _interval;
    private long _lastSpaceAt;
    private int _count;
    private bool _spaceDown;

    public TripleSpaceDetector(int intervalMilliseconds = DefaultIntervalMilliseconds)
    {
        if (intervalMilliseconds is < 100 or > 5000) throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds));
        _interval = intervalMilliseconds;
    }

    public bool Process(uint virtualKey, bool isDown, long timestampMilliseconds)
    {
        if (isDown)
        {
            if (virtualKey != SpaceVirtualKey) { Reset(); return false; }
            if (_spaceDown) return false;
            _spaceDown = true;
            if (_count > 0 && timestampMilliseconds - _lastSpaceAt > _interval) _count = 0;
            _lastSpaceAt = timestampMilliseconds;
            _count++;
            if (_count < 3) return false;
            _count = 0;
            return true;
        }

        if (virtualKey == SpaceVirtualKey) _spaceDown = false;
        else Reset();
        return false;
    }

    public void Reset()
    {
        _count = 0;
        _spaceDown = false;
        _lastSpaceAt = 0;
    }
}
