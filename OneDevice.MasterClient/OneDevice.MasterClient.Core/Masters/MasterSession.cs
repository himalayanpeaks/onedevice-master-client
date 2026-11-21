using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OneDevice.Framework.Libs.Validator;
using OneDevice.Master.IoLink.Products;
using OneDevice.MasterClient.Core.Configuration;
using static OneDevice.Master.IoLink.Products.Definition;

namespace OneDevice.MasterClient.Core.Masters;

public enum PortState
{
    NotProbed,
    Empty,
    SensorFound,
    Error
}

/// <summary>State of one master port.</summary>
public sealed record PortStatus(int Port, PortState State, SensorIdentity? Sensor = null, string? Error = null);

/// <summary>One process-data sample as sent to the cloud.</summary>
public sealed record ProcessDataSample(int Port, byte[] Data, bool? IsValid, DateTimeOffset Timestamp)
{
    public string DataHex => Convert.ToHexString(Data);
}

/// <summary>
///     Drives one IO-Link master on the HAL level, without an IODD: connect, probe ports, read sensor identity,
///     stream process data (on change, throttled), and raw parameter (ISDU) access by index.
/// </summary>
/// <remarks>
///     All master operations are serialized. Process data is polled by the HAL; this class forwards it at most once
///     per <see cref="MasterConfiguration.TelemetryIntervalSeconds" />. Masters with one shared PD channel (TMG USB V2)
///     stream the first port that has a sensor.
/// </remarks>
public sealed class MasterSession : IAsyncDisposable
{
    private readonly IMasterHAL _hal;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LatestValueThrottle<ProcessDataSample> _throttle;
    private readonly object _stateLock = new();
    private Dictionary<int, PortStatus> _ports;
    private int? _streamingPort;
    private ProcessDataSample? _latest;

    public MasterSession(MasterConfiguration configuration, MasterTypeInfo type, IMasterHAL hal,
        ILogger? logger = null, TimeProvider? time = null)
    {
        Configuration = configuration;
        Type = type;
        _hal = hal;
        _logger = logger ?? NullLogger.Instance;
        _ports = ConfiguredPorts().ToDictionary(p => p, p => new PortStatus(p, PortState.NotProbed));
        _throttle = new LatestValueThrottle<ProcessDataSample>(
            TimeSpan.FromSeconds(Math.Max(1, configuration.TelemetryIntervalSeconds)),
            sample => ProcessDataChanged?.Invoke(this, sample), time);
        _hal.AttachToProcessDataEvent(OnHalProcessData);
    }

    public MasterConfiguration Configuration { get; }
    public MasterTypeInfo Type { get; }
    public bool IsConnected { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Latest process data (not throttled), for the local page.</summary>
    public ProcessDataSample? LatestProcessData => Volatile.Read(ref _latest);

    public IReadOnlyList<PortStatus> Ports
    {
        get
        {
            lock (_stateLock) return _ports.Values.OrderBy(p => p.Port).ToList();
        }
    }

    /// <summary>Process data changed (at most once per telemetry interval).</summary>
    public event EventHandler<ProcessDataSample>? ProcessDataChanged;

    /// <summary>Connection, ports or identities changed.</summary>
    public event EventHandler? StateChanged;

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsConnected) return true;
            var result = await Task.Run(() => _hal.Open(Configuration.Connection, new ComportValidator()), ct)
                .ConfigureAwait(false);
            IsConnected = result == OneDevice.Module.Definition.ConnectionError.NoError;
            LastError = IsConnected ? null : $"Cannot open {Type.DisplayName} on {Configuration.Connection}: {result}";
            if (!IsConnected) _logger.LogWarning("{Error}", LastError);
            else _logger.LogInformation("Connected to {Type} on {Connection}", Type.DisplayName, Configuration.Connection);
        }
        finally
        {
            _gate.Release();
        }

        RaiseStateChanged();
        return IsConnected;
    }

    /// <summary>
    ///     Switches every configured port to IO-Link operate mode and reads the sensor identity. Starts process-data
    ///     streaming for the first port with a sensor.
    /// </summary>
    public async Task<IReadOnlyList<PortStatus>> ProbePortsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            _hal.StopProcessDataAnnouncer();
            var results = new Dictionary<int, PortStatus>();
            foreach (var port in ConfiguredPorts())
            {
                ct.ThrowIfCancellationRequested();
                results[port] = await Task.Run(() => ProbePort(port), ct).ConfigureAwait(false);
            }

            lock (_stateLock) _ports = results;
            _streamingPort = results.Values.Where(p => p.State == PortState.SensorFound)
                .Select(p => (int?)p.Port).FirstOrDefault();
            if (_streamingPort is { } streamPort)
            {
                _hal.SensorPortNumber = streamPort;
                _hal.StartProcessDataAnnouncer();
            }
        }
        finally
        {
            _gate.Release();
        }

        RaiseStateChanged();
        return Ports;
    }

    public async Task<IsduResult> ReadAsync(int port, ushort index, byte subindex, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsConnected) return IsduResult.Failed("Master not connected");
            if (!IsConfiguredPort(port)) return IsduResult.Failed($"Port {port} is not configured");
            return await Task.Run(() =>
            {
                SelectPort(port);
                return Read(index, subindex);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IsduResult> WriteAsync(int port, ushort index, byte subindex, byte[] data,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsConnected) return IsduResult.Failed("Master not connected");
            if (!IsConfiguredPort(port)) return IsduResult.Failed($"Port {port} is not configured");
            if (data.Length is 0 or > 232) return IsduResult.Failed("Write data must be 1..232 bytes");
            return await Task.Run(() =>
            {
                SelectPort(port);
                var status = _hal.WriteRecord(index, subindex, data, out var errorCode, out var additionalCode);
                var ok = status == t_eInternal_Return_Codes.RETURN_OK && errorCode == 0;
                _logger.LogInformation("Write port {Port} index {Index}.{Subindex} ({Length} bytes): {Status}",
                    port, index, subindex, data.Length, ok ? "OK" : $"{status} 0x{errorCode:X2}{additionalCode:X2}");
                return new IsduResult(ok, [], errorCode, additionalCode, status.ToString());
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsConnected) return;
            await Task.Run(() =>
            {
                _hal.StopProcessDataAnnouncer();
                try
                {
                    _hal.DisconnectSensorFromMaster();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Switching port back failed");
                }

                _hal.Close();
            }).ConfigureAwait(false);
            IsConnected = false;
            lock (_stateLock)
                _ports = ConfiguredPorts().ToDictionary(p => p, p => new PortStatus(p, PortState.NotProbed));
        }
        finally
        {
            _gate.Release();
        }

        RaiseStateChanged();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _throttle.Dispose();
        _gate.Dispose();
    }

    // ---- internals (called with _gate held) ----

    private PortStatus ProbePort(int port)
    {
        try
        {
            _hal.SensorPortNumber = port;
            var mode = _hal.ConnectSensorWithMaster();
            if (mode != t_eInternal_Return_Codes.RETURN_OK)
                return new PortStatus(port, PortState.Error, Error: $"Port could not be switched to IO-Link: {mode}");

            var page = Read(StandardIndex.DirectParameterPage1, 0);
            if (!page.Success)
                return new PortStatus(port, PortState.Empty, Error: page.ErrorText);

            var identity = SensorIdentity.FromDirectParameterPage(page.Data);
            if (identity == null)
                return new PortStatus(port, PortState.Error, Error: "Invalid direct parameter page");

            identity = identity with
            {
                VendorName = ReadText(StandardIndex.VendorName),
                VendorText = ReadText(StandardIndex.VendorText),
                ProductName = ReadText(StandardIndex.ProductName),
                ProductId = ReadText(StandardIndex.ProductId),
                ProductText = ReadText(StandardIndex.ProductText),
                SerialNumber = ReadText(StandardIndex.SerialNumber),
                HardwareRevision = ReadText(StandardIndex.HardwareRevision),
                FirmwareRevision = ReadText(StandardIndex.FirmwareRevision),
                ApplicationSpecificTag = ReadText(StandardIndex.ApplicationSpecificTag)
            };
            _logger.LogInformation("Port {Port}: {Vendor} {Product} (vendor {VendorId}, device {DeviceId})",
                port, identity.VendorName, identity.ProductName, identity.VendorId, identity.DeviceId);
            return new PortStatus(port, PortState.SensorFound, identity);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Probing port {Port} failed", port);
            return new PortStatus(port, PortState.Error, Error: ex.Message);
        }
    }

    private IsduResult Read(ushort index, byte subindex)
    {
        var status = _hal.ReadRecord(index, subindex, out var data, out _, out var errorCode, out var additionalCode);
        data ??= [];
        var ok = status == t_eInternal_Return_Codes.RETURN_OK && errorCode == 0 && data.Length > 0;
        return new IsduResult(ok, ok ? data : [], errorCode, additionalCode, status.ToString());
    }

    private string? ReadText(ushort index)
    {
        var result = Read(index, 0);
        return result.Success ? Encoding.UTF8.GetString(result.Data).TrimEnd('\0', ' ') : null;
    }

    private void SelectPort(int port)
    {
        if (_hal.SensorPortNumber != port) _hal.SensorPortNumber = port;
    }

    private void EnsureConnected()
    {
        if (!IsConnected) throw new InvalidOperationException("Master not connected");
    }

    private IEnumerable<int> ConfiguredPorts() =>
        Configuration.SensorPorts.Where(p => p >= 0 && p < Type.PortCount).Distinct().OrderBy(p => p);

    private bool IsConfiguredPort(int port) => ConfiguredPorts().Contains(port);

    private void OnHalProcessData(object sender, InternalDataHAL e)
    {
        if (!e.ProcessDataChanged || e.ProcessData is not { Length: > 0 } || _streamingPort is not { } port) return;
        var sample = new ProcessDataSample(port, (byte[])e.ProcessData.Clone(), e.IsProcessDataValid,
            new DateTimeOffset(e.TimeStamp.ToUniversalTime(), TimeSpan.Zero));
        Volatile.Write(ref _latest, sample);
        _throttle.Post(sample);
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StateChanged handler threw");
        }
    }
}
