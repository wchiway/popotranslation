namespace PopoTranslation.Windows.Hotkeys;

// Sequence and suppression logic, kept separate so it can be tested without a hook.
public sealed class TripleSpaceDetector
{
    public const uint SpaceVirtualKey = 0x20;
    public const int DefaultIntervalMilliseconds = 700;
    private readonly long _interval;
    private long _lastSpaceAt;
    private int _count;
    private bool _spaceDown;
    private bool _swallowSpaceUp;
    private bool _locked;

    public TripleSpaceDetector(int intervalMilliseconds = DefaultIntervalMilliseconds)
    {
        if (intervalMilliseconds is < 100 or > 5000) throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds));
        _interval = intervalMilliseconds;
    }

    // Returns whether this physical key event must be swallowed. Only an accepted
    // trigger locks the sequence; unrelated and injected keys are never swallowed.
    public bool Process(uint virtualKey, bool isDown, long timestampMilliseconds,
        bool eligible, bool modifiersDown, bool injected, Func<bool>? trigger)
    {
        if (injected) return false;
        if (virtualKey != SpaceVirtualKey)
        {
            if (!_locked) ResetSequence();
            return false;
        }

        if (!isDown)
        {
            _spaceDown = false;
            var swallow = _swallowSpaceUp;
            _swallowSpaceUp = false;
            return swallow;
        }

        // A repeated keydown belongs to the same press, including its suppression.
        if (_spaceDown)
        {
            if (_swallowSpaceUp) _lastSpaceAt = timestampMilliseconds;
            return _swallowSpaceUp;
        }
        _spaceDown = true;

        if (!eligible || modifiersDown)
        {
            ResetSequence();
            return false;
        }

        if ((_locked || _count > 0) && timestampMilliseconds - _lastSpaceAt > _interval)
            ResetSequence();
        _lastSpaceAt = timestampMilliseconds;
        if (_locked)
        {
            _swallowSpaceUp = true;
            return true;
        }

        if (++_count < 3) return false;
        _count = 0;
        try { _locked = trigger?.Invoke() == true; }
        catch { _locked = false; /* Never cross the hook boundary. */ }
        _swallowSpaceUp = _locked;
        return _locked;
    }

    private void ResetSequence()
    {
        _count = 0;
        _locked = false;
        _lastSpaceAt = 0;
    }
}
