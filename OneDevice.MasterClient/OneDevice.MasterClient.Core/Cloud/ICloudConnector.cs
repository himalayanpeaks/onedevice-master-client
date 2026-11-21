namespace OneDevice.MasterClient.Core.Cloud;

public enum CloudConnectionState
{
    Offline,
    Connecting,
    Connected,
    Disconnected,
    Error
}

/// <summary>Handles commands sent from the cloud (IoT Hub direct methods) to one master.</summary>
public interface ICloudCommandHandler
{
    Task<CommandResponse> HandleAsync(string method, string payloadJson, CancellationToken ct);
}

/// <summary>
///     Connection of one master to the cloud. The agent only depends on this interface, so another cloud
///     (Event Grid MQTT, AWS IoT Core, ...) only needs a new implementation.
/// </summary>
public interface ICloudConnector : IAsyncDisposable
{
    CloudConnectionState State { get; }
    string? LastError { get; }

    /// <summary>Device id in the cloud (known after connecting).</summary>
    string? DeviceId { get; }

    event EventHandler? StateChanged;

    Task ConnectAsync(ICloudCommandHandler commands, CancellationToken ct);
    Task SendProcessDataAsync(ProcessDataMessage message, CancellationToken ct);
    Task ReportStateAsync(MasterReport report, CancellationToken ct);
}

/// <summary>Used when the customer has not configured the cloud yet: everything stays local.</summary>
public sealed class OfflineCloudConnector : ICloudConnector
{
    public CloudConnectionState State => CloudConnectionState.Offline;
    public string? LastError => null;
    public string? DeviceId => null;

    public event EventHandler? StateChanged
    {
        add { }
        remove { }
    }

    public Task ConnectAsync(ICloudCommandHandler commands, CancellationToken ct) => Task.CompletedTask;
    public Task SendProcessDataAsync(ProcessDataMessage message, CancellationToken ct) => Task.CompletedTask;
    public Task ReportStateAsync(MasterReport report, CancellationToken ct) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
