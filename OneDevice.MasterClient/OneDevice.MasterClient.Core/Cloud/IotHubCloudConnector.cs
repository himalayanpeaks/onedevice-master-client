using System.Text;
using System.Text.Json;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Provisioning.Client;
using Microsoft.Azure.Devices.Provisioning.Client.Transport;
using Microsoft.Azure.Devices.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OneDevice.MasterClient.Core.Configuration;

namespace OneDevice.MasterClient.Core.Cloud;

/// <summary>
///     Azure IoT Hub connection for one master (= one IoT Hub device), over MQTT.
///     Authentication: device connection string (Phase 1) or Device Provisioning Service with a symmetric key.
/// </summary>
/// <remarks>
///     Telemetry: <see cref="ProcessDataMessage" /> as JSON (message property messageType=processData).
///     Device twin reported properties: <see cref="MasterReport" />.
///     Direct methods: readParameter, writeParameter, rescan, getState (handled by <see cref="ICloudCommandHandler" />).
/// </remarks>
public sealed class IotHubCloudConnector(CloudConfiguration config, ILogger? logger = null) : ICloudConnector
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private DeviceClient? _client;
    private ICloudCommandHandler? _commands;

    public CloudConnectionState State { get; private set; } = CloudConnectionState.Disconnected;
    public string? LastError { get; private set; }
    public string? DeviceId { get; private set; }
    public event EventHandler? StateChanged;

    public async Task ConnectAsync(ICloudCommandHandler commands, CancellationToken ct)
    {
        _commands = commands;
        SetState(CloudConnectionState.Connecting, null);
        try
        {
            _client = config.Mode switch
            {
                CloudMode.ConnectionString => CreateFromConnectionString(),
                CloudMode.Dps => await CreateFromDpsAsync(ct).ConfigureAwait(false),
                _ => throw new InvalidOperationException("Cloud mode is Offline")
            };
            _client.SetConnectionStatusChangesHandler(OnConnectionStatusChanged);
            await _client.SetMethodDefaultHandlerAsync(OnMethodAsync, null, ct).ConfigureAwait(false);
            await _client.OpenAsync(ct).ConfigureAwait(false);
            SetState(CloudConnectionState.Connected, null);
            _logger.LogInformation("Connected to Azure IoT Hub as device {DeviceId}", DeviceId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Connecting to Azure IoT Hub failed");
            SetState(CloudConnectionState.Error, ex.Message);
            throw;
        }
    }

    public async Task SendProcessDataAsync(ProcessDataMessage message, CancellationToken ct)
    {
        if (_client == null || State != CloudConnectionState.Connected) return;
        using var msg = new Message(JsonSerializer.SerializeToUtf8Bytes(message, CloudJson.Options))
        {
            ContentType = "application/json",
            ContentEncoding = "utf-8"
        };
        msg.Properties["messageType"] = message.MessageType;
        msg.Properties["port"] = message.Port.ToString();
        await _client.SendEventAsync(msg, ct).ConfigureAwait(false);
    }

    public async Task ReportStateAsync(MasterReport report, CancellationToken ct)
    {
        if (_client == null || State != CloudConnectionState.Connected) return;
        var json = JsonSerializer.Serialize(new { oneDevice = report }, CloudJson.Options);
        await _client.UpdateReportedPropertiesAsync(new TwinCollection(json), ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client != null)
        {
            try
            {
                await _client.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Closing IoT Hub client failed");
            }

            _client.Dispose();
            _client = null;
        }

        SetState(CloudConnectionState.Disconnected, null);
    }

    private DeviceClient CreateFromConnectionString()
    {
        if (string.IsNullOrWhiteSpace(config.DeviceConnectionString))
            throw new InvalidOperationException("No IoT Hub device connection string configured");
        var client = DeviceClient.CreateFromConnectionString(config.DeviceConnectionString, TransportType.Mqtt);
        DeviceId = IotHubConnectionStringBuilder.Create(config.DeviceConnectionString).DeviceId;
        return client;
    }

    private async Task<DeviceClient> CreateFromDpsAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.DpsIdScope) || string.IsNullOrWhiteSpace(config.DpsRegistrationId) ||
            string.IsNullOrWhiteSpace(config.DpsSymmetricKey))
            throw new InvalidOperationException("DPS id scope, registration id and key are required");

        using var security = new SecurityProviderSymmetricKey(config.DpsRegistrationId, config.DpsSymmetricKey, null);
        using var transport = new ProvisioningTransportHandlerMqtt();
        var provisioning = ProvisioningDeviceClient.Create(config.DpsGlobalEndpoint, config.DpsIdScope, security,
            transport);
        var result = await provisioning.RegisterAsync(ct).ConfigureAwait(false);
        if (result.Status != ProvisioningRegistrationStatusType.Assigned)
            throw new InvalidOperationException($"DPS registration failed: {result.Status} {result.ErrorMessage}");

        DeviceId = result.DeviceId;
        _logger.LogInformation("DPS assigned device {DeviceId} on {Hub}", result.DeviceId, result.AssignedHub);
        var auth = new DeviceAuthenticationWithRegistrySymmetricKey(result.DeviceId, config.DpsSymmetricKey);
        return DeviceClient.Create(result.AssignedHub, auth, TransportType.Mqtt);
    }

    private async Task<MethodResponse> OnMethodAsync(MethodRequest request, object userContext)
    {
        if (_commands == null) return new MethodResponse(503);
        try
        {
            using var cts = new CancellationTokenSource(request.ResponseTimeout ?? TimeSpan.FromSeconds(30));
            var response = await _commands.HandleAsync(request.Name, request.DataAsJson ?? "{}", cts.Token)
                .ConfigureAwait(false);
            return new MethodResponse(Encoding.UTF8.GetBytes(response.Json), response.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Direct method {Method} failed", request.Name);
            var error = CommandResponse.Error(500, ex.Message);
            return new MethodResponse(Encoding.UTF8.GetBytes(error.Json), error.Status);
        }
    }

    private void OnConnectionStatusChanged(ConnectionStatus status, ConnectionStatusChangeReason reason)
    {
        _logger.LogInformation("IoT Hub connection: {Status} ({Reason})", status, reason);
        SetState(status switch
        {
            ConnectionStatus.Connected => CloudConnectionState.Connected,
            ConnectionStatus.Disconnected_Retrying => CloudConnectionState.Connecting,
            ConnectionStatus.Disabled => CloudConnectionState.Disconnected,
            _ => CloudConnectionState.Error
        }, status == ConnectionStatus.Connected ? null : $"{status}: {reason}");
    }

    private void SetState(CloudConnectionState state, string? error)
    {
        State = state;
        LastError = error;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
