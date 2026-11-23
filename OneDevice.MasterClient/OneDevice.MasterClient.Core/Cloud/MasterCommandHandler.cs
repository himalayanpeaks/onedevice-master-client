using System.Text.Json;
using OneDevice.MasterClient.Core.Masters;

namespace OneDevice.MasterClient.Core.Cloud;

/// <summary>
///     Executes cloud commands on a master:
///     <list type="bullet">
///         <item>readParameter {port, index, subindex} → {success, data (hex), isduError, error}</item>
///         <item>writeParameter {port, index, subindex, data (hex)} → {success, isduError, error}</item>
///         <item>rescan → <see cref="MasterReport" /> (probes the ports again)</item>
///         <item>getState → <see cref="MasterReport" /></item>
///         <item>getEvents {port, after} → <see cref="EventsResponse" /> (IO-Link Events, communication lost / started)</item>
///         <item>setDataStorage {port, enabled} → {success, error} (master Data Storage on / off for the port)</item>
///     </list>
/// </summary>
public sealed class MasterCommandHandler(MasterSession session, string agentVersion, Func<Task>? afterRescan = null)
    : ICloudCommandHandler
{
    public async Task<CommandResponse> HandleAsync(string method, string payloadJson, CancellationToken ct)
    {
        switch (method.ToLowerInvariant())
        {
            case "readparameter":
            {
                if (!TryParse(payloadJson, out var request, out var error)) return error!;
                if (!TryRange(request!, out error)) return error!;
                var result = await session.ReadAsync(request!.Port, (ushort)request.Index, (byte)request.Subindex, ct)
                    .ConfigureAwait(false);
                return CommandResponse.Ok(ParameterResponse.From(result));
            }
            case "writeparameter":
            {
                if (!TryParse(payloadJson, out var request, out var error)) return error!;
                if (!TryRange(request!, out error)) return error!;
                byte[] data;
                try
                {
                    data = Convert.FromHexString(request!.Data ?? "");
                }
                catch (FormatException)
                {
                    return CommandResponse.Error(400, "data must be a hex string, e.g. \"0A1B\"");
                }

                if (data.Length == 0) return CommandResponse.Error(400, "data is required");
                var result = await session.WriteAsync(request.Port, (ushort)request.Index, (byte)request.Subindex, data,
                    ct).ConfigureAwait(false);
                return CommandResponse.Ok(ParameterResponse.From(result));
            }
            case "rescan":
                await session.ProbePortsAsync(ct).ConfigureAwait(false);
                if (afterRescan != null) await afterRescan().ConfigureAwait(false);
                return CommandResponse.Ok(MasterReport.From(session, agentVersion));
            case "getstate":
                return CommandResponse.Ok(MasterReport.From(session, agentVersion));
            case "getevents":
            {
                if (!TryParse<EventsRequest>(payloadJson, out var request, out var error)) return error!;
                return CommandResponse.Ok(new EventsResponse(session.Events.LastSequence,
                    session.IsCommunicating(request!.Port), session.SensorStatus(request.Port),
                    session.IsDataStorageEnabled(request.Port), session.Events.After(request.After, request.Port)));
            }
            case "setdatastorage":
            {
                if (!TryParse<DataStorageRequest>(payloadJson, out var request, out var error)) return error!;
                var result = await session.SetDataStorageAsync(request!.Port, request.Enabled, ct).ConfigureAwait(false);
                return CommandResponse.Ok(ParameterResponse.From(result));
            }
            default:
                return CommandResponse.Error(404, $"Unknown method '{method}'");
        }
    }

    private static bool TryParse(string json, out ParameterRequest? request, out CommandResponse? error) =>
        TryParse<ParameterRequest>(json, out request, out error);

    private static bool TryParse<T>(string json, out T? request, out CommandResponse? error) where T : class
    {
        try
        {
            request = JsonSerializer.Deserialize<T>(json, CloudJson.Options);
            error = request == null ? CommandResponse.Error(400, "Request body is missing") : null;
            return request != null;
        }
        catch (JsonException ex)
        {
            request = null;
            error = CommandResponse.Error(400, $"Invalid request: {ex.Message}");
            return false;
        }
    }

    private static bool TryRange(ParameterRequest request, out CommandResponse? error)
    {
        error = request switch
        {
            { Index: < 0 or > 65535 } => CommandResponse.Error(400, "index must be 0..65535"),
            { Subindex: < 0 or > 255 } => CommandResponse.Error(400, "subindex must be 0..255"),
            _ => null
        };
        return error == null;
    }
}
