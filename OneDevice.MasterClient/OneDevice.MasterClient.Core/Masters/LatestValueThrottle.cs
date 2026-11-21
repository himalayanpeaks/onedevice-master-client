namespace OneDevice.MasterClient.Core.Masters;

/// <summary>
///     Passes values on at most once per <c>interval</c>, always delivering the latest one: the first value after a
///     quiet period goes out immediately, values arriving faster are coalesced and the newest is sent when the
///     interval has elapsed. Used to limit cloud telemetry to 1 message/s per sensor without losing the final state.
/// </summary>
public sealed class LatestValueThrottle<T> : IDisposable
{
    private readonly Action<T> _emit;
    private readonly TimeSpan _interval;
    private readonly object _sync = new();
    private readonly TimeProvider _time;
    private bool _disposed;
    private bool _hasPending;
    private DateTimeOffset _lastEmit = DateTimeOffset.MinValue;
    private T? _pending;
    private ITimer? _timer;

    public LatestValueThrottle(TimeSpan interval, Action<T> emit, TimeProvider? time = null)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _interval = interval;
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
        _time = time ?? TimeProvider.System;
    }

    public void Post(T value)
    {
        var emitNow = false;
        lock (_sync)
        {
            if (_disposed) return;
            var now = _time.GetUtcNow();
            if (_timer == null && now - _lastEmit >= _interval)
            {
                _lastEmit = now;
                emitNow = true;
            }
            else
            {
                _pending = value;
                _hasPending = true;
                if (_timer == null)
                {
                    var due = _lastEmit + _interval - now;
                    _timer = _time.CreateTimer(_ => Flush(), null, due < TimeSpan.Zero ? TimeSpan.Zero : due,
                        Timeout.InfiniteTimeSpan);
                }
            }
        }

        if (emitNow) _emit(value);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            _hasPending = false;
        }
    }

    private void Flush()
    {
        T value;
        lock (_sync)
        {
            _timer?.Dispose();
            _timer = null;
            if (_disposed || !_hasPending) return;
            value = _pending!;
            _pending = default;
            _hasPending = false;
            _lastEmit = _time.GetUtcNow();
        }

        _emit(value);
    }
}
