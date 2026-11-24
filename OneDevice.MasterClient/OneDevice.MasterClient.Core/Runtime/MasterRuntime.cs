using Microsoft.Extensions.Logging;
using OneDevice.MasterClient.Core.Cloud;
using OneDevice.MasterClient.Core.Discovery;
using OneDevice.MasterClient.Core.Masters;

namespace OneDevice.MasterClient.Core.Runtime;

/// <summary>
///     Keeps one master running: connects it, probes its ports, connects it to the cloud, forwards process data while
///     the cloud asks for it (<see cref="Telemetry" />) and retries after failures (master unplugged, network down).
/// </summary>
public sealed class MasterRuntime : IAsyncDisposable, IProcessDataStreaming
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    private readonly string _agentVersion;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    private readonly ISerialPortScanner? _scanner;

    /// <param name="scanner">
    ///     Finds a master with a USB identity on whatever COM port it has now. Without a scanner the configured port
    ///     is used as is.
    /// </param>
    public MasterRuntime(MasterSession session, ICloudConnector cloud, string agentVersion, ILogger logger,
        TimeProvider? time = null, ISerialPortScanner? scanner = null)
    {
        _scanner = scanner;
        Session = session;
        Cloud = cloud;
        _agentVersion = agentVersion;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        Telemetry = new TelemetryGate(OnKeepAlive, _time);
        Telemetry.Changed += OnTelemetryChanged;
        Session.ProcessDataChanged += OnProcessData;
        Session.StateChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        Cloud.StateChanged += OnCloudStateChanged;
    }

    private CloudConnectionState _lastCloudState = CloudConnectionState.Disconnected;

    // On every (re)connect: publish the current state and, while streaming, the latest process data. Process data is
    // only sent on change, so a sample produced while the cloud was not connected would otherwise reach the cloud
    // only with the next keep-alive.
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
            if (Telemetry.IsActive && Session.LatestProcessData is { } latest)
                await SendAsync(ToMessage(latest)).ConfigureAwait(false);
        });
    }

    public MasterSession Session { get; }
    public ICloudConnector Cloud { get; }

    /// <summary>Whether process data is streamed to the cloud right now (started and renewed by the website).</summary>
    public TelemetryGate Telemetry { get; }

    private readonly TimeProvider _time;
    private bool _wasStreaming;

    public StreamingStatus StartStreaming(TimeSpan? lease)
    {
        if (Telemetry.Start(lease) && Session.LatestProcessData is { } latest)
            _ = SendAsync(ToMessage(latest)); // the viewer sees the current value at once, not with the next change
        return StreamingStatus();
    }

    public StreamingStatus StopStreaming()
    {
        Telemetry.Stop();
        return StreamingStatus();
    }

    private StreamingStatus StreamingStatus()
    {
        var active = Telemetry.IsActive;
        return new StreamingStatus(active, active ? Telemetry.ExpiresAt : null,
            Math.Max(1, Session.Configuration.TelemetryIntervalSeconds), (int)TelemetryGate.KeepAliveInterval.TotalSeconds);
    }

    private void OnTelemetryChanged(object? sender, EventArgs e)
    {
        var streaming = Telemetry.IsActive;
        if (streaming != _wasStreaming)
        {
            _wasStreaming = streaming;
            _logger.LogInformation("Master {Name}: process data streaming to the cloud {State}",
                Session.Configuration.Name, streaming ? "started" : "stopped");
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Nothing sent for the keep-alive interval: the value has not changed. Send it again so the viewer sees it is live.
    private void OnKeepAlive()
    {
        if (Session.LatestProcessData is { } latest)
            _ = SendAsync(ToMessage(latest) with { Timestamp = _time.GetUtcNow(), KeepAlive = true });
    }

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
        Telemetry.Dispose();
        await Session.DisposeAsync().ConfigureAwait(false);
        await Cloud.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var commands = new MasterCommandHandler(Session, _agentVersion, () => ReportAsync(ct), this);
        var cloudConnected = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!Session.IsConnected && FindPort() && await Session.ConnectAsync(ct).ConfigureAwait(false))
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

    /// <summary>
    ///     Points the session at the COM port where this master (by USB serial number) is plugged in now. False when it
    ///     is not plugged in: then nothing is opened, so another master that got this master's old COM port is never
    ///     connected under this master's identity.
    /// </summary>
    private bool FindPort()
    {
        var master = Session.Configuration;
        if (_scanner == null || !MasterPortResolver.HasUsbIdentity(master)) return true;
        IReadOnlyList<SerialPortInfo> ports;
        try
        {
            ports = _scanner.Scan();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Master {Name}: COM ports could not be listed", master.Name);
            Session.ReportUnavailable($"COM ports could not be listed: {ex.Message}");
            return false;
        }

        var found = MasterPortResolver.Resolve(master, ports);
        if (found.Port == null)
        {
            Session.ReportUnavailable(found.Error!);
            return false;
        }

        if (!string.Equals(found.Port, master.Connection, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Master {Name} ({Serial}) is on {Port} now (was {Previous})", master.Name,
                master.UsbSerialNumber, found.Port, master.Connection);
            master.Connection = found.Port;
        }

        return true;
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

    private void OnProcessData(object? sender, ProcessDataSample sample)
    {
        if (Telemetry.IsActive) _ = SendAsync(ToMessage(sample));
    }

    private ProcessDataMessage ToMessage(ProcessDataSample sample)
    {
        var sensor = Session.Ports.FirstOrDefault(p => p.Port == sample.Port)?.Sensor;
        return new ProcessDataMessage(Session.Configuration.Id.ToString(), Session.Configuration.Name,
            sample.Port, sensor?.VendorId, sensor?.DeviceId, sample.Timestamp, sample.DataHex, sample.IsValid);
    }

    private async Task SendAsync(ProcessDataMessage message)
    {
        Telemetry.NoteSent();
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
