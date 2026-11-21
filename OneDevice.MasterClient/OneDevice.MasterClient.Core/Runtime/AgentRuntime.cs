using System.Reflection;
using Microsoft.Extensions.Logging;
using OneDevice.Master.IoLink.Products;
using OneDevice.MasterClient.Core.Cloud;
using OneDevice.MasterClient.Core.Configuration;
using OneDevice.MasterClient.Core.Masters;

namespace OneDevice.MasterClient.Core.Runtime;

/// <summary>
///     Runs all configured masters and applies changes made on the local setup page.
/// </summary>
public sealed class AgentRuntime(
    ConfigurationStore store,
    ILoggerFactory loggerFactory,
    Func<MasterTypeInfo, IMasterHAL>? halFactory = null,
    Func<CloudConfiguration, ICloudConnector>? cloudFactory = null) : IAsyncDisposable
{
    private readonly Func<CloudConfiguration, ICloudConnector> _cloudFactory = cloudFactory ?? (c =>
        c.Mode == CloudMode.Offline
            ? new OfflineCloudConnector()
            : new IotHubCloudConnector(c, loggerFactory.CreateLogger<IotHubCloudConnector>()));

    private readonly Func<MasterTypeInfo, IMasterHAL> _halFactory = halFactory ?? MasterCatalog.CreateHal;
    private readonly ILogger _logger = loggerFactory.CreateLogger<AgentRuntime>();
    private readonly Dictionary<Guid, MasterRuntime> _runtimes = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AgentConfiguration _config = new();

    public static string AgentVersion { get; } =
        typeof(AgentRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";

    /// <summary>Copy of the current configuration (secrets included: only for the local page).</summary>
    public AgentConfiguration Configuration => _config;

    public IReadOnlyList<MasterRuntime> Masters
    {
        get
        {
            lock (_runtimes) return _runtimes.Values.ToList();
        }
    }

    /// <summary>Something changed (master state, ports, cloud, configuration): the local page refreshes.</summary>
    public event EventHandler? Changed;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _config = store.Load();
            foreach (var master in _config.Masters.Where(m => m.Enabled)) StartMaster(master);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    /// <summary>Adds or replaces a master, saves the configuration and (re)starts it.</summary>
    public async Task SaveMasterAsync(MasterConfiguration master, CancellationToken ct = default)
    {
        var error = Validate(master);
        if (error != null) throw new ArgumentException(error);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopMasterAsync(master.Id).ConfigureAwait(false);
            _config.Masters.RemoveAll(m => m.Id == master.Id);
            _config.Masters.Add(master);
            store.Save(_config);
            if (master.Enabled) StartMaster(master);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    public async Task RemoveMasterAsync(Guid id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopMasterAsync(id).ConfigureAwait(false);
            _config.Masters.RemoveAll(m => m.Id == id);
            store.Save(_config);
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged();
    }

    public MasterRuntime? Find(Guid id)
    {
        lock (_runtimes) return _runtimes.GetValueOrDefault(id);
    }

    /// <summary>Returns an error message, or null when the master configuration is valid.</summary>
    public string? Validate(MasterConfiguration master)
    {
        if (string.IsNullOrWhiteSpace(master.Name)) return "Please give the master a name.";
        var type = MasterCatalog.Find(master.MasterType);
        if (type == null) return "Please select the master type.";
        if (string.IsNullOrWhiteSpace(master.Connection)) return "Please select the COM port.";
        if (master.SensorPorts.Count == 0) return "Please select at least one sensor port.";
        if (master.SensorPorts.Any(p => p < 0 || p >= type.PortCount))
            return $"{type.DisplayName} has port(s) 1..{type.PortCount}.";
        if (master.TelemetryIntervalSeconds is < 1 or > 3600) return "Telemetry interval must be 1..3600 s.";
        if (_config.Masters.Any(m => m.Id != master.Id &&
                                     string.Equals(m.Connection, master.Connection, StringComparison.OrdinalIgnoreCase)))
            return $"{master.Connection} is already used by another master.";
        return master.Cloud.Mode switch
        {
            CloudMode.ConnectionString when string.IsNullOrWhiteSpace(master.Cloud.DeviceConnectionString) =>
                "Please enter the IoT Hub device connection string.",
            CloudMode.Dps when string.IsNullOrWhiteSpace(master.Cloud.DpsIdScope) ||
                               string.IsNullOrWhiteSpace(master.Cloud.DpsRegistrationId) ||
                               string.IsNullOrWhiteSpace(master.Cloud.DpsSymmetricKey) =>
                "Please enter DPS id scope, registration id and key.",
            _ => null
        };
    }

    public async ValueTask DisposeAsync()
    {
        List<MasterRuntime> all;
        lock (_runtimes)
        {
            all = _runtimes.Values.ToList();
            _runtimes.Clear();
        }

        foreach (var runtime in all) await runtime.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private void StartMaster(MasterConfiguration master)
    {
        var type = MasterCatalog.Find(master.MasterType);
        if (type == null)
        {
            _logger.LogWarning("Master {Name}: unknown type {Type}", master.Name, master.MasterType);
            return;
        }

        IMasterHAL hal;
        try
        {
            hal = _halFactory(type);
        }
        catch (Exception ex)
        {
            // One broken master (e.g. missing driver DLL) must not stop the others.
            _logger.LogError(ex, "Master {Name}: cannot create driver for {Type}", master.Name, type.DisplayName);
            return;
        }

        var session = new MasterSession(master, type, hal, loggerFactory.CreateLogger<MasterSession>());
        var runtime = new MasterRuntime(session, _cloudFactory(master.Cloud), AgentVersion,
            loggerFactory.CreateLogger<MasterRuntime>());
        runtime.Changed += (_, _) => RaiseChanged();
        lock (_runtimes) _runtimes[master.Id] = runtime;
        runtime.Start();
        _logger.LogInformation("Started master {Name} ({Type} on {Connection})", master.Name, type.DisplayName,
            master.Connection);
    }

    private async Task StopMasterAsync(Guid id)
    {
        MasterRuntime? runtime;
        lock (_runtimes)
        {
            if (_runtimes.Remove(id, out runtime) == false) return;
        }

        await runtime!.DisposeAsync().ConfigureAwait(false);
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Changed handler threw");
        }
    }
}
