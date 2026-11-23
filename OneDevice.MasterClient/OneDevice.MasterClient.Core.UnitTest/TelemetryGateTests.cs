using Microsoft.Extensions.Time.Testing;
using OneDevice.MasterClient.Core.Runtime;

namespace OneDevice.MasterClient.Core.UnitTest;

public class TelemetryGateTests
{
    private sealed class Fixture : IDisposable
    {
        public readonly FakeTimeProvider Time = new(DateTimeOffset.Parse("2026-11-23T12:00:00Z"));
        public readonly TelemetryGate Gate;
        public int KeepAlives;
        public int Changes;

        public Fixture()
        {
            Gate = new TelemetryGate(() => KeepAlives++, Time);
            Gate.Changed += (_, _) => Changes++;
        }

        public void Dispose() => Gate.Dispose();
    }

    [Fact]
    public void Initially_NotStreaming()
    {
        using var f = new Fixture();
        Assert.False(f.Gate.IsActive);
        Assert.Null(f.Gate.ExpiresAt);
    }

    [Fact]
    public void Start_StreamsUntilLeaseRunsOut_ThenStopsByItself()
    {
        using var f = new Fixture();
        Assert.True(f.Gate.Start(TimeSpan.FromSeconds(60)));
        Assert.True(f.Gate.IsActive);

        f.Time.Advance(TimeSpan.FromSeconds(59));
        Assert.True(f.Gate.IsActive);

        f.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(f.Gate.IsActive);
        Assert.Null(f.Gate.ExpiresAt);
        Assert.Equal(2, f.Changes); // started, expired
    }

    [Fact]
    public void Renewal_ExtendsLease_AndIsNotAFreshStart()
    {
        using var f = new Fixture();
        f.Gate.Start(TimeSpan.FromSeconds(60));
        f.Time.Advance(TimeSpan.FromSeconds(50));
        Assert.False(f.Gate.Start(TimeSpan.FromSeconds(60))); // renewal

        f.Time.Advance(TimeSpan.FromSeconds(50)); // 100 s after the first start
        Assert.True(f.Gate.IsActive);
        f.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(f.Gate.IsActive);
    }

    [Fact]
    public void Stop_EndsStreamingImmediately()
    {
        using var f = new Fixture();
        f.Gate.Start();
        f.Gate.Stop();
        Assert.False(f.Gate.IsActive);
        f.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, f.KeepAlives);
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(60, 60)]
    [InlineData(3600, 600)]
    public void Lease_IsClamped(int requested, int expected)
    {
        Assert.Equal(TimeSpan.FromSeconds(expected), TelemetryGate.Clamp(TimeSpan.FromSeconds(requested)));
    }

    [Fact]
    public void UnchangedValue_GetsKeepAliveEvery10s_WhileStreaming()
    {
        using var f = new Fixture();
        f.Gate.Start(TimeSpan.FromSeconds(60));
        f.Gate.NoteSent(); // latest value sent at start

        f.Time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(0, f.KeepAlives);
        f.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, f.KeepAlives);
        f.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(2, f.KeepAlives);

        f.Time.Advance(TimeSpan.FromSeconds(40)); // lease ends at 60 s
        Assert.False(f.Gate.IsActive);
        var atExpiry = f.KeepAlives;
        f.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(atExpiry, f.KeepAlives); // nothing after the lease
    }

    [Fact]
    public void ChangingValue_SuppressesKeepAlive()
    {
        using var f = new Fixture();
        f.Gate.Start(TimeSpan.FromSeconds(60));
        for (var i = 0; i < 30; i++) // value changes every second
        {
            f.Gate.NoteSent();
            f.Time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(0, f.KeepAlives);
    }
}
