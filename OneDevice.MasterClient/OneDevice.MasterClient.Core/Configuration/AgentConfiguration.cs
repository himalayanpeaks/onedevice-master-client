namespace OneDevice.MasterClient.Core.Configuration;

/// <summary>Everything the agent persists: the masters the customer set up on the local page.</summary>
public sealed class AgentConfiguration
{
    public List<MasterConfiguration> Masters { get; set; } = [];
}

/// <summary>One IO-Link master connected to this PC. Each master is one IoT Hub device.</summary>
public sealed class MasterConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Name chosen by the customer, e.g. "Line 3 – filling station".</summary>
    public string Name { get; set; } = "";

    /// <summary>Key into <see cref="Masters.MasterCatalog" />, e.g. "TmgUsbV2".</summary>
    public string MasterType { get; set; } = "";

    /// <summary>
    ///     Connection of the master to this PC, e.g. "COM3". For a master with a USB identity this is only the port it
    ///     was last seen on: the agent looks the master up by its serial number before every connection attempt.
    /// </summary>
    public string Connection { get; set; } = "";

    /// <summary>USB vendor id of the master (hex), e.g. "2843". Null when unknown.</summary>
    public string? UsbVendorId { get; set; }

    /// <summary>USB product id of the master (hex), e.g. "0201".</summary>
    public string? UsbProductId { get; set; }

    /// <summary>
    ///     USB serial number stored in the master itself, e.g. "1392765243": the master's UID. Same on every PC and USB
    ///     socket. Null for masters without one (then the master is found by <see cref="Connection" /> only).
    /// </summary>
    public string? UsbSerialNumber { get; set; }

    /// <summary>Master ports (0-based) the customer uses for sensors.</summary>
    public List<int> SensorPorts { get; set; } = [0];

    /// <summary>Process data is sent to the cloud when it changed, at most once per this interval.</summary>
    public int TelemetryIntervalSeconds { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    public CloudConfiguration Cloud { get; set; } = new();
}

public enum CloudMode
{
    /// <summary>Local only: nothing is sent to the cloud.</summary>
    Offline,

    /// <summary>IoT Hub device connection string (manually registered device, Phase 1).</summary>
    ConnectionString,

    /// <summary>Azure Device Provisioning Service with a symmetric key (enrollment, Phase 2).</summary>
    Dps
}

public sealed class CloudConfiguration
{
    public CloudMode Mode { get; set; } = CloudMode.Offline;

    /// <summary>Secret. Stored encrypted (DPAPI) in the configuration file.</summary>
    public string? DeviceConnectionString { get; set; }

    public string DpsGlobalEndpoint { get; set; } = "global.azure-devices-provisioning.net";
    public string? DpsIdScope { get; set; }
    public string? DpsRegistrationId { get; set; }

    /// <summary>Secret. Stored encrypted (DPAPI) in the configuration file.</summary>
    public string? DpsSymmetricKey { get; set; }
}
