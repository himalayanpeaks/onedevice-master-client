using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using OneDevice.Framework.Libs.Announcer;
using OneDevice.Framework.Libs.Validator;
using OneDevice.Master.IoLink.Products;
using OneDevice.MasterClient.Core.Cloud;
using OneDevice.MasterClient.Core.Configuration;
using OneDevice.MasterClient.Core.Masters;
using OneDevice.MasterClient.Core.Runtime;
using static OneDevice.Master.IoLink.Products.Definition;

namespace OneDevice.MasterClient.Core.UnitTest;

public class MasterRuntimeTests
{
    /// <summary>Cloud connector whose connect completes only when the test says so.</summary>
    private sealed class FakeCloud : ICloudConnector
    {
        public readonly TaskCompletionSource AllowConnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<ProcessDataMessage> Messages = [];
        public readonly List<MasterReport> Reports = [];

        public CloudConnectionState State { get; private set; } = CloudConnectionState.Disconnected;
        public string? LastError => null;
        public string? DeviceId => "test-device";
        public event EventHandler? StateChanged;

        public async Task ConnectAsync(ICloudCommandHandler commands, CancellationToken ct)
        {
            await AllowConnect.Task.WaitAsync(ct);
            State = CloudConnectionState.Connected;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task SendProcessDataAsync(ProcessDataMessage message, CancellationToken ct)
        {
            if (State == CloudConnectionState.Connected)
                lock (Messages) Messages.Add(message);
            return Task.CompletedTask;
        }

        public Task ReportStateAsync(MasterReport report, CancellationToken ct)
        {
            if (State == CloudConnectionState.Connected)
                lock (Reports) Reports.Add(report);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Drop()
        {
            State = CloudConnectionState.Connecting;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Restore()
        {
            State = CloudConnectionState.Connected;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static (MasterRuntime runtime, FakeCloud cloud, Func<DataTunnel<InternalDataHAL>.DataEventHandler?> pd)
        Create(TimeProvider? time = null)
    {
        var hal = new Mock<IMasterHAL>();
        DataTunnel<InternalDataHAL>.DataEventHandler? pd = null;
        hal.SetupProperty(h => h.SensorPortNumber);
        hal.Setup(h => h.AttachToProcessDataEvent(It.IsAny<DataTunnel<InternalDataHAL>.DataEventHandler>()))
            .Callback<DataTunnel<InternalDataHAL>.DataEventHandler>(h => pd = h);
        hal.Setup(h => h.Open(It.IsAny<string>(), It.IsAny<IValidator>()))
            .Returns(OneDevice.Module.Definition.ConnectionError.NoError);
        hal.Setup(h => h.ConnectSensorWithMaster(It.IsAny<t_eTargetMode>())).Returns(t_eInternal_Return_Codes.RETURN_OK);
        var dpp = Convert.FromHexString("0022211B11C300000130070800000000");
        byte len = 16, ok = 0;
        hal.Setup(h => h.ReadRecord(0, 0, out dpp, out len, out ok, out ok)).Returns(t_eInternal_Return_Codes.RETURN_OK);

        var config = new MasterConfiguration { Name = "Bench", MasterType = "TmgUsbV2", Connection = "COM3" };
        var session = new MasterSession(config, MasterCatalog.TmgUsbV2, hal.Object);
        var cloud = new FakeCloud();
        return (new MasterRuntime(session, cloud, "1.0.0", NullLogger.Instance, time), cloud, () => pd);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task UnchangedProcessData_ProducedBeforeCloudConnects_IsSentOnConnect()
    {
        var (runtime, cloud, pd) = Create();
        await using var _ = runtime;
        runtime.Start();
        await Eventually(() => runtime.Session.Ports.Any(p => p.State == PortState.SensorFound));
        runtime.StartStreaming(null);

        // Sensor value arrives while the cloud is still connecting and never changes afterwards.
        pd()!(this, new InternalDataHAL(0, [0x7F, 0xFC, 0xFD, 0x03], 0, 0, 0, 0, 0, 0, 0, 0));
        Assert.Empty(cloud.Messages);

        cloud.AllowConnect.SetResult();

        await Eventually(() => { lock (cloud.Messages) return cloud.Messages.Count == 1; });
        Assert.Equal("7FFCFD03", cloud.Messages[0].PdIn);
        Assert.Equal(3147528, cloud.Messages[0].DeviceId);
        await Eventually(() => { lock (cloud.Reports) return cloud.Reports.Count >= 1; });
    }

    [Fact]
    public async Task AfterReconnect_LatestStateAndProcessDataAreSentAgain()
    {
        var (runtime, cloud, pd) = Create();
        await using var _ = runtime;
        cloud.AllowConnect.SetResult();
        runtime.Start();
        await Eventually(() => cloud.State == CloudConnectionState.Connected &&
                               runtime.Session.Ports.Any(p => p.State == PortState.SensorFound));
        runtime.StartStreaming(null);
        pd()!(this, new InternalDataHAL(0, [0x00, 0x64, 0xFD, 0x03], 0, 0, 0, 0, 0, 0, 0, 0));
        await Eventually(() => { lock (cloud.Messages) return cloud.Messages.Count == 1; });
        int reportsBefore;
        lock (cloud.Reports) reportsBefore = cloud.Reports.Count;

        cloud.Drop(); // network gone, SDK retrying
        cloud.Restore(); // back

        await Eventually(() => { lock (cloud.Messages) return cloud.Messages.Count == 2; });
        Assert.Equal("0064FD03", cloud.Messages[1].PdIn);
        await Eventually(() => { lock (cloud.Reports) return cloud.Reports.Count > reportsBefore; });
    }

    private static async Task<(MasterRuntime runtime, FakeCloud cloud, Func<DataTunnel<InternalDataHAL>.DataEventHandler?> pd)>
        StartConnected(TimeProvider? time = null)
    {
        var (runtime, cloud, pd) = Create(time);
        cloud.AllowConnect.SetResult();
        runtime.Start();
        await Eventually(() => cloud.State == CloudConnectionState.Connected &&
                               runtime.Session.Ports.Any(p => p.State == PortState.SensorFound));
        return (runtime, cloud, pd);
    }

    private static int Count(FakeCloud cloud)
    {
        lock (cloud.Messages) return cloud.Messages.Count;
    }

    [Fact]
    public async Task NobodyWatching_NoProcessDataIsSent_StartSendsLatestValueAtOnce()
    {
        var (runtime, cloud, pd) = await StartConnected();
        await using var _ = runtime;

        pd()!(this, new InternalDataHAL(0, [0x00, 0x64, 0xFD, 0x03], 0, 0, 0, 0, 0, 0, 0, 0));
        await Task.Delay(200);
        Assert.Equal(0, Count(cloud));
        Assert.Equal("0064FD03", runtime.Session.LatestProcessData!.DataHex); // the local page still sees it

        var status = runtime.StartStreaming(TimeSpan.FromSeconds(60));

        Assert.True(status.Streaming);
        Assert.NotNull(status.ExpiresAt);
        await Eventually(() => Count(cloud) == 1);
        Assert.Equal("0064FD03", cloud.Messages[0].PdIn);
        Assert.Null(cloud.Messages[0].KeepAlive);
    }

    [Fact]
    public async Task UnchangedValue_IsResentAsKeepAlive_UntilLeaseRunsOut()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (runtime, cloud, pd) = await StartConnected(time);
        await using var _ = runtime;
        pd()!(this, new InternalDataHAL(0, [0x00, 0x64, 0xFD, 0x03], 0, 0, 0, 0, 0, 0, 0, 0));
        await Eventually(() => runtime.Session.LatestProcessData != null);
        runtime.StartStreaming(TimeSpan.FromSeconds(30));
        await Eventually(() => Count(cloud) == 1);

        time.Advance(TimeSpan.FromSeconds(10));
        await Eventually(() => Count(cloud) == 2);
        var keepAlive = cloud.Messages[1];
        Assert.True(keepAlive.KeepAlive);
        Assert.Equal("0064FD03", keepAlive.PdIn);
        Assert.Equal(time.GetUtcNow(), keepAlive.Timestamp);
        Assert.Contains("\"keepAlive\":true", JsonSerializer.Serialize(keepAlive, CloudJson.Options));
        Assert.DoesNotContain("keepAlive", JsonSerializer.Serialize(cloud.Messages[0], CloudJson.Options));

        time.Advance(TimeSpan.FromSeconds(20)); // lease over
        Assert.False(runtime.Telemetry.IsActive);
        var afterLease = Count(cloud);
        time.Advance(TimeSpan.FromMinutes(1));
        pd()!(this, new InternalDataHAL(0, [0x00, 0x70, 0xFD, 0x03], 0, 0, 0, 0, 0, 0, 0, 0));
        await Task.Delay(200);
        Assert.Equal(afterLease, Count(cloud));
    }

    [Fact]
    public async Task StartAndStopProcessData_DirectMethods()
    {
        var (runtime, _, _) = await StartConnected();
        await using var __ = runtime;
        var commands = new MasterCommandHandler(runtime.Session, "1.0.0", streaming: runtime);

        var start = await commands.HandleAsync("startProcessData", """{"leaseSeconds":5}""", default);
        Assert.Equal(200, start.Status);
        var started = JsonSerializer.Deserialize<StreamingStatus>(start.Json, CloudJson.Options)!;
        Assert.True(started.Streaming);
        Assert.Equal(1, started.IntervalSeconds);
        Assert.Equal(10, started.KeepAliveSeconds);
        Assert.InRange((started.ExpiresAt!.Value - DateTimeOffset.UtcNow).TotalSeconds, 8, 10.5); // clamped to 10 s

        var renewed = await commands.HandleAsync("startProcessData", "", default); // default lease 60 s
        Assert.InRange((JsonSerializer.Deserialize<StreamingStatus>(renewed.Json, CloudJson.Options)!.ExpiresAt!.Value -
                        DateTimeOffset.UtcNow).TotalSeconds, 55, 60.5);

        var stop = await commands.HandleAsync("stopProcessData", "{}", default);
        Assert.False(JsonSerializer.Deserialize<StreamingStatus>(stop.Json, CloudJson.Options)!.Streaming);
        Assert.False(runtime.Telemetry.IsActive);
    }

    [Theory]
    [InlineData("""{"leaseSeconds":0}""", 400)]
    [InlineData("not json", 400)]
    public async Task StartProcessData_InvalidRequest_IsRejected(string payload, int status)
    {
        var (runtime, _, _) = Create();
        await using var _ = runtime;
        var commands = new MasterCommandHandler(runtime.Session, "1.0.0", streaming: runtime);
        Assert.Equal(status, (await commands.HandleAsync("startProcessData", payload, default)).Status);
        Assert.False(runtime.Telemetry.IsActive);
    }

    [Fact]
    public async Task StartProcessData_WithoutStreaming_Returns501()
    {
        var (runtime, _, _) = Create();
        await using var _ = runtime;
        var response = await new MasterCommandHandler(runtime.Session, "1.0.0").HandleAsync("startProcessData", "{}", default);
        Assert.Equal(501, response.Status);
    }
}
