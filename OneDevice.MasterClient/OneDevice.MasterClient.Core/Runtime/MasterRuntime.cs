using Microsoft.Extensions.Logging;
using OneDevice.MasterClient.Core.Cloud;
using OneDevice.MasterClient.Core.Masters;

namespace OneDevice.MasterClient.Core.Runtime;

/// <summary>
///     Keeps one master running: connects it, probes its ports, connects it to the cloud, forwards process data and
///     retries after failures (master unplugged, network down).
/// </summary>
public sealed class MasterRuntime : IAsyncDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    private readonly string _agentVersion;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public MasterRuntime(MasterSession session, ICloudConnector cloud, string agentVersion, ILogger logger)
    {
        Session = session;
        Cloud = cloud;
        _agentVersion = agentVersion;
        _logger = logger;
        Session.ProcessDataChanged += OnProcessData;
        Session.StateChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        Cloud.StateChanged += OnCloudStateChanged;
    }

    private CloudConnectionState _lastCloudState = CloudConnectionState.Disconnected;

    // On every (re)connect: publish the current state and the latest process data. Process data is only sent on
    // change, so a sample produced while the cloud was not connected (or a value that never changes) would
    // otherwise never reach the cloud.
    private void OnCloudStateChanged(object? sender, EventArgs e)
    {
        var state = Cloud.State;
        var becameConnected = state == CloudConnectionState.Connected && _lastCloudState != CloudConnectionState.Connected;
        _lastCloudState = state;
        Changed?.Invoke(this, EventArgs.Empty);
        if (!becameConnected) return;

        _ = Task.Run(async () =>
        {
            await ReportAsync(_stop.Token).ConfigureAwait(false);
            if (Session.LatestProcessData is { } latest) await SendAsync(ToMessage(latest)).ConfigureAwait(false);
        });
    }

    public MasterSession Session { get; }
    public ICloudConnector Cloud { get; }

    /// <summary>Number of process-data messages handed to the cloud connector.</summary>
    public long TelemetrySent => Interlocked.Read(ref _telemetrySent);

    private long _telemetrySent;

    public event EventHandler? Changed;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    /// <summary>Probes the ports again (e.g. after the customer plugged in a sensor).</summary>
    public async Task RescanAsync(CancellationToken ct = default)
    {
        await Session.ProbePortsAsync(ct).ConfigureAwait(false);
        await ReportAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop != null)
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

        Session.ProcessDataChanged -= OnProcessData;
        await Session.DisposeAsync().ConfigureAwait(false);
        await Cloud.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var commands = new MasterCommandHandler(Session, _agentVersion, () => ReportAsync(ct));
        var cloudConnected = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!Session.IsConnected && await Session.ConnectAsync(ct).ConfigureAwait(false))
                {
                    await Session.ProbePortsAsync(ct).ConfigureAwait(false);
                    await ReportAsync(ct).ConfigureAwait(false);
                }

                if (!cloudConnected && Cloud is not OfflineCloudConnector)
                {
                    await Cloud.ConnectAsync(commands, ct).ConfigureAwait(false);
                    cloudConnected = true;
                    await ReportAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Master {Name}: {Message}; retrying in {Delay}", Session.Configuration.Name,
                    ex.Message, RetryDelay);
            }

            try
            {
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReportAsync(CancellationToken ct)
    {
        try
        {
            await Cloud.ReportStateAsync(MasterReport.From(Session, _agentVersion), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reporting state of {Name} failed", Session.Configuration.Name);
        }
    }

    private void OnProcessData(object? sender, ProcessDataSample sample) => _ = SendAsync(ToMessage(sample));

    private ProcessDataMessage ToMessage(ProcessDataSample sample)
    {
        var sensor = Session.Ports.FirstOrDefault(p => p.Port == sample.Port)?.Sensor;
        return new ProcessDataMessage(Session.Configuration.Id.ToString(), Session.Configuration.Name,
            sample.Port, sensor?.VendorId, sensor?.DeviceId, sample.Timestamp, sample.DataHex, sample.IsValid);
    }

    private async Task SendAsync(ProcessDataMessage message)
    {
        try
        {
            await Cloud.SendProcessDataAsync(message, _stop.Token).ConfigureAwait(false);
            if (Cloud.State == CloudConnectionState.Connected) Interlocked.Increment(ref _telemetrySent);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Sending process data of {Name} failed", Session.Configuration.Name);
        }
    }
}
