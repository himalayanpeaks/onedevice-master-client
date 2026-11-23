using System.Text.Json;
using System.Text.Json.Serialization;
using OneDevice.MasterClient.Core.Masters;

namespace OneDevice.MasterClient.Core.Cloud;

/// <summary>JSON contracts between agent and cloud (camelCase). Independent of the cloud provider.</summary>
public static class CloudJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

/// <summary>Telemetry: one process-data sample. Raw bytes; the cloud decodes them with the IODD.</summary>
public sealed record ProcessDataMessage(
    string MasterId,
    string MasterName,
    int Port,
    int? VendorId,
    int? DeviceId,
    DateTimeOffset Timestamp,
    string PdIn,
    bool? PdValid)
{
    /// <summary>
    ///     True when the value did not change and is sent again only to show the stream is alive (timestamp = time of
    ///     sending). Omitted for regular samples.
    /// </summary>
    public bool? KeepAlive { get; init; }

    public string MessageType => "processData";
    public int SchemaVersion => 1;
}

/// <summary>Reported state of a master (device twin "reported" properties).</summary>
public sealed record MasterReport(
    string MasterId,
    string Name,
    string MasterType,
    string MasterTypeName,
    int PortCount,
    bool Connected,
    string AgentVersion,
    IReadOnlyDictionary<string, PortReport> Ports)
{
    public int SchemaVersion => 1;

    public static MasterReport From(MasterSession session, string agentVersion) => new(
        session.Configuration.Id.ToString(),
        session.Configuration.Name,
        session.Type.Key,
        session.Type.DisplayName,
        session.Type.PortCount,
        session.IsConnected,
        agentVersion,
        // Twin property names may not contain '.', '$' or spaces: "port0", "port1", ...
        session.Ports.ToDictionary(p => $"port{p.Port}", PortReport.From));
}

public sealed record PortReport(
    int Port,
    PortState State,
    string? Error,
    int? VendorId,
    int? DeviceId,
    string? IoLinkRevision,
    int? PdInBits,
    string? VendorName,
    string? ProductName,
    string? ProductId,
    string? ProductText,
    string? SerialNumber,
    string? HardwareRevision,
    string? FirmwareRevision,
    string? ApplicationSpecificTag)
{
    public static PortReport From(PortStatus p) => new(
        p.Port, p.State, p.Error,
        p.Sensor?.VendorId, p.Sensor?.DeviceId, p.Sensor?.IoLinkRevision, p.Sensor?.ProcessDataInBits,
        p.Sensor?.VendorName, p.Sensor?.ProductName, p.Sensor?.ProductId, p.Sensor?.ProductText,
        p.Sensor?.SerialNumber, p.Sensor?.HardwareRevision, p.Sensor?.FirmwareRevision,
        p.Sensor?.ApplicationSpecificTag);
}

/// <summary>Request of the direct methods readParameter / writeParameter.</summary>
public sealed record ParameterRequest(int Port, int Index, int Subindex = 0, string? Data = null);

/// <summary>Response of readParameter / writeParameter.</summary>
public sealed record ParameterResponse(bool Success, string? Data, int IsduError, string? Error)
{
    public static ParameterResponse From(IsduResult r) =>
        new(r.Success, r.Success && r.Data.Length > 0 ? r.DataHex : null, r.IsduError, r.ErrorText);
}

/// <summary>Request of the direct method getEvents: events of a port after a sequence number.</summary>
public sealed record EventsRequest(int Port, long After = 0);

/// <summary>Response of getEvents: events plus the current communication state of the port.</summary>
public sealed record EventsResponse(
    long LastSequence,
    bool? Communicating,
    uint? SensorStatus,
    bool DataStorageEnabled,
    IReadOnlyList<PortEvent> Events);

/// <summary>Request of the direct method setDataStorage.</summary>
public sealed record DataStorageRequest(int Port, bool Enabled);

/// <summary>Result of a cloud command: HTTP-like status and a JSON payload.</summary>
public sealed record CommandResponse(int Status, string Json)
{
    public static CommandResponse Ok(object payload) => new(200, JsonSerializer.Serialize(payload, CloudJson.Options));

    public static CommandResponse Error(int status, string message) =>
        new(status, JsonSerializer.Serialize(new { error = message }, CloudJson.Options));
}

/// <summary>startProcessData request: lease in seconds (10..600, default 60). Renew before it runs out.</summary>
public sealed record StartProcessDataRequest(int? LeaseSeconds);

/// <summary>Answer of startProcessData / stopProcessData.</summary>
/// <param name="Streaming">Process data is sent to the cloud.</param>
/// <param name="ExpiresAt">Streaming stops at this time unless renewed.</param>
/// <param name="IntervalSeconds">At most one message per this interval (on change).</param>
/// <param name="KeepAliveSeconds">An unchanged value is sent again after this time.</param>
public sealed record StreamingStatus(bool Streaming, DateTimeOffset? ExpiresAt, int IntervalSeconds, int KeepAliveSeconds);

/// <summary>Switches process-data streaming to the cloud on (with a lease) and off.</summary>
public interface IProcessDataStreaming
{
    StreamingStatus StartStreaming(TimeSpan? lease);
    StreamingStatus StopStreaming();
}
