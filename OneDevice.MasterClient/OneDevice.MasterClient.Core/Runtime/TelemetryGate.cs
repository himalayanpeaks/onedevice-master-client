namespace OneDevice.MasterClient.Core.Runtime;

/// <summary>
///     Decides whether process data goes to the cloud. Streaming runs only while someone watches: the website starts
///     it with a lease (<c>startProcessData</c>) and renews it while the sensor page is open; without a renewal it stops
///     by itself when the lease runs out (closed browser tab, lost connection). While streaming, a keep-alive is
///     requested when nothing was sent for <see cref="KeepAliveInterval" />, so an unchanged value still shows as live.
/// </summary>
/// <remarks>
///     Every IoT Hub message counts against the hub's daily quota; streaming 1 message/s around the clock is 86,400
///     messages per master and day. With the gate, an unwatched master sends no process data at all.
/// </remarks>
public sealed class TelemetryGate : IDisposable
{
    public static readonly TimeSpan DefaultLease = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MinLease = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaxLease = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(10);

    private readonly Action _keepAlive;
    private readonly object _sync = new();
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private bool _disposed;
    private DateTimeOffset _lastSent = DateTimeOffset.MinValue;

    /// <param name="keepAlive">Called (on a timer thread) when streaming and nothing was sent for the keep-alive interval.</param>
    public TelemetryGate(Action keepAlive, TimeProvider? time = null)
    {
        _keepAlive = keepAlive ?? throw new ArgumentNullException(nameof(keepAlive));
        _time = time ?? TimeProvider.System;
        _timer = _time.CreateTimer(_ => OnTick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>End of the current lease, null when not streaming.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    public bool IsActive
    {
        get
        {
            lock (_sync) return ExpiresAt is { } end && _time.GetUtcNow() < end;
        }
    }

    /// <summary>Streaming started, renewed or stopped (also when the lease ran out).</summary>
    public event EventHandler? Changed;

    /// <summary>Starts streaming or renews the lease. The lease is clamped to <see cref="MinLease" />..<see cref="MaxLease" />.</summary>
    /// <returns>True when streaming was off before (the caller sends the latest value right away).</returns>
    public bool Start(TimeSpan? lease = null)
    {
        var duration = Clamp(lease ?? DefaultLease);
        bool wasActive;
        lock (_sync)
        {
            if (_disposed) return false;
            var now = _time.GetUtcNow();
            wasActive = ExpiresAt is { } end && now < end;
            ExpiresAt = now + duration;
            ScheduleNextTick(now);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return !wasActive;
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (ExpiresAt == null) return;
            ExpiresAt = null;
            if (!_disposed) _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records that a message went out (resets the keep-alive countdown).</summary>
    public void NoteSent()
    {
        lock (_sync) _lastSent = _time.GetUtcNow();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _timer.Dispose();
        }
    }

    internal static TimeSpan Clamp(TimeSpan lease) =>
        lease < MinLease ? MinLease : lease > MaxLease ? MaxLease : lease;

    private void OnTick()
    {
        bool expired, keepAlive = false;
        lock (_sync)
        {
            if (_disposed || ExpiresAt is not { } end) return;
            var now = _time.GetUtcNow();
            expired = now >= end;
            if (expired) ExpiresAt = null;
            else
            {
                keepAlive = now - _lastSent >= KeepAliveInterval;
                if (keepAlive) _lastSent = now;
                ScheduleNextTick(now);
            }
        }

        if (expired) Changed?.Invoke(this, EventArgs.Empty);
        else if (keepAlive) _keepAlive();
    }

    // Next check: when a keep-alive would be due or the lease ends, whichever comes first.
    private void ScheduleNextTick(DateTimeOffset now)
    {
        var keepAliveDue = _lastSent + KeepAliveInterval - now;
        if (keepAliveDue <= TimeSpan.Zero) keepAliveDue = KeepAliveInterval;
        var leaseDue = ExpiresAt!.Value - now;
        var due = keepAliveDue < leaseDue ? keepAliveDue : leaseDue;
        _timer.Change(due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
    }
}
