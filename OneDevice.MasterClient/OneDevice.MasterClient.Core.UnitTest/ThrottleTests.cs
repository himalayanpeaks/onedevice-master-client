using Microsoft.Extensions.Time.Testing;
using OneDevice.MasterClient.Core.Masters;

namespace OneDevice.MasterClient.Core.UnitTest;

public class ThrottleTests
{
    private static (LatestValueThrottle<int> throttle, List<int> sent, FakeTimeProvider time) Create()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-21T12:00:00Z"));
        var sent = new List<int>();
        return (new LatestValueThrottle<int>(TimeSpan.FromSeconds(1), sent.Add, time), sent, time);
    }

    [Fact]
    public void FirstValue_IsSentImmediately()
    {
        var (throttle, sent, _) = Create();
        throttle.Post(1);
        Assert.Equal([1], sent);
    }

    [Fact]
    public void Burst_IsCoalesced_AndLatestValueIsSentAfterInterval()
    {
        var (throttle, sent, time) = Create();
        throttle.Post(1);
        time.Advance(TimeSpan.FromMilliseconds(100));
        throttle.Post(2);
        time.Advance(TimeSpan.FromMilliseconds(100));
        throttle.Post(3);
        Assert.Equal([1], sent);

        time.Advance(TimeSpan.FromMilliseconds(800)); // 1 s after the first send
        Assert.Equal([1, 3], sent);
    }

    [Fact]
    public void AtMostOneMessagePerSecond_At20Hz()
    {
        var (throttle, sent, time) = Create();
        for (var i = 0; i < 100; i++) // 5 s at 20 Hz
        {
            throttle.Post(i);
            time.Advance(TimeSpan.FromMilliseconds(50));
        }

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.InRange(sent.Count, 5, 6);
        Assert.Equal(99, sent[^1]); // the final state is never lost
    }

    [Fact]
    public void AfterQuietPeriod_NextValueIsImmediateAgain()
    {
        var (throttle, sent, time) = Create();
        throttle.Post(1);
        time.Advance(TimeSpan.FromSeconds(5));
        throttle.Post(2);
        Assert.Equal([1, 2], sent);
    }

    [Fact]
    public void Dispose_DropsPendingValue()
    {
        var (throttle, sent, time) = Create();
        throttle.Post(1);
        throttle.Post(2);
        throttle.Dispose();
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal([1], sent);
    }
}
