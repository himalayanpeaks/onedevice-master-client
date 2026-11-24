using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OneDevice.MasterClient.Core.Cloud;
using OneDevice.MasterClient.Core.Configuration;
using OneDevice.MasterClient.Core.Discovery;

namespace OneDevice.MasterClient.Core.Runtime;

public enum RegistrationPhase
{
    Idle,
    Starting,

    /// <summary>The claim link is ready: the user confirms on the website.</summary>
    WaitingForUser,

    /// <summary>Confirmed; the master now connects through DPS.</summary>
    Completed,
    Failed
}

public sealed record RegistrationState(
    RegistrationPhase Phase,
    string? ClaimUrl = null,
    string? Code = null,
    DateTimeOffset? ExpiresAt = null,
    string? Message = null)
{
    public static readonly RegistrationState Idle = new(RegistrationPhase.Idle);
}

/// <summary>
///     "Register this master": reads the master's USB identity, starts a claim on the OneDevice website, waits until
///     the user confirmed it there and switches the master to DPS with the key it received (stored encrypted).
/// </summary>
public sealed class MasterRegistrar(
    IRegistrationClient client,
    ISerialPortScanner scanner,
    Func<Guid, MasterConfiguration?> findMaster,
    Func<MasterConfiguration, Task> saveMaster,
    ILogger<MasterRegistrar> logger,
    TimeProvider? time = null) : IDisposable
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<Guid, RegistrationState> _states = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public bool IsAvailable => client.PortalUrl != null;

    public event EventHandler? Changed;

    public RegistrationState State(Guid masterId) => _states.GetValueOrDefault(masterId, RegistrationState.Idle);

    /// <summary>Starts (or restarts) the registration and returns once the claim link is ready or it failed.</summary>
    public async Task<RegistrationState> StartAsync(Guid masterId, CancellationToken ct = default)
    {
        Cancel(masterId);
        var master = findMaster(masterId);
        if (master == null) return Set(masterId, new RegistrationState(RegistrationPhase.Failed, Message: "Master not found."));
        if (!IsAvailable)
            return Set(masterId, new RegistrationState(RegistrationPhase.Failed,
                Message: "The OneDevice website address is not configured (Cloud:PortalUrl)."));

        var port = scanner.Scan().FirstOrDefault(p => string.Equals(p.PortName, master.Connection, StringComparison.OrdinalIgnoreCase));
        if (port?.UsbVendorId == null || port.UsbProductId == null)
            return Set(masterId, new RegistrationState(RegistrationPhase.Failed,
                Message: $"No USB master found on {master.Connection}. Is it plugged in?"));
        if (port.UsbSerialNumber == null)
            return Set(masterId, new RegistrationState(RegistrationPhase.Failed,
                Message: $"The device on {master.Connection} reports no USB serial number, so it cannot be registered."));

        Set(masterId, new RegistrationState(RegistrationPhase.Starting));
        ClaimTicket ticket;
        try
        {
            ticket = await client.StartAsync(port.UsbVendorId, port.UsbProductId, port.UsbSerialNumber, master.MasterType, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Registration of {Name} could not be started", master.Name);
            return Set(masterId, new RegistrationState(RegistrationPhase.Failed,
                Message: $"Cannot reach the OneDevice website: {ex.Message}"));
        }

        logger.LogInformation("Registration of {Name} (serial {Serial}) started: code {Code}", master.Name,
            port.UsbSerialNumber, ticket.Code);
        var cts = new CancellationTokenSource();
        _running[masterId] = cts;
        var waiting = Set(masterId, new RegistrationState(RegistrationPhase.WaitingForUser, ticket.ClaimUrl, ticket.Code, ticket.ExpiresAt));
        _ = Task.Run(() => WaitForApprovalAsync(masterId, ticket, cts.Token));
        return waiting;
    }

    public void Cancel(Guid masterId)
    {
        if (_running.TryRemove(masterId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        if (State(masterId).Phase is RegistrationPhase.Starting or RegistrationPhase.WaitingForUser)
            Set(masterId, RegistrationState.Idle);
    }

    public void Dispose()
    {
        foreach (var id in _running.Keys) Cancel(id);
    }

    private async Task WaitForApprovalAsync(Guid masterId, ClaimTicket ticket, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(ticket.PollIntervalSeconds, 1, 30));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, _time, ct).ConfigureAwait(false);
                ClaimPollResult result;
                try
                {
                    result = await client.PollAsync(ticket, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Network hiccup: keep trying until the link expires.
                    logger.LogDebug(ex, "Polling registration {Code}", ticket.Code);
                    if (_time.GetUtcNow() < ticket.ExpiresAt) continue;
                    Finish(masterId, RegistrationPhase.Failed, "The registration link expired. Please start again.");
                    return;
                }

                switch (result.Status)
                {
                    case ClaimPollStatus.Pending:
                        continue;
                    case ClaimPollStatus.Approved when result.Dps != null:
                        await ApplyAsync(masterId, result.Dps).ConfigureAwait(false);
                        return;
                    case ClaimPollStatus.Rejected:
                        Finish(masterId, RegistrationPhase.Failed,
                            "This master is registered to another OneDevice account and cannot be used with yours.");
                        return;
                    case ClaimPollStatus.Expired:
                    case ClaimPollStatus.Unknown:
                        Finish(masterId, RegistrationPhase.Failed, "The registration link expired. Please start again.");
                        return;
                    default:
                        Finish(masterId, RegistrationPhase.Failed, "The registration was completed elsewhere. Please start again.");
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // cancelled by the user or the agent stopping
        }
    }

    private async Task ApplyAsync(Guid masterId, DpsCredentials dps)
    {
        var current = findMaster(masterId);
        if (current == null)
        {
            Finish(masterId, RegistrationPhase.Failed, "The master was removed while registering.");
            return;
        }

        // Edit a copy: the runtime restarts the master with the new cloud settings.
        var master = JsonSerializer.Deserialize<MasterConfiguration>(JsonSerializer.Serialize(current))!;
        master.Cloud = new CloudConfiguration
        {
            Mode = CloudMode.Dps,
            DpsGlobalEndpoint = dps.GlobalEndpoint,
            DpsIdScope = dps.IdScope,
            DpsRegistrationId = dps.RegistrationId,
            DpsSymmetricKey = dps.SymmetricKey
        };
        try
        {
            await saveMaster(master).ConfigureAwait(false);
            logger.LogInformation("Master {Name} registered as {RegistrationId}; connecting through DPS", master.Name,
                dps.RegistrationId);
            Finish(masterId, RegistrationPhase.Completed, "Registered. The master is connecting to the cloud.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Saving the registration of {Name} failed", master.Name);
            Finish(masterId, RegistrationPhase.Failed, $"Registered, but the key could not be saved: {ex.Message}");
        }
    }

    private void Finish(Guid masterId, RegistrationPhase phase, string message)
    {
        if (_running.TryRemove(masterId, out var cts)) cts.Dispose();
        Set(masterId, new RegistrationState(phase, Message: message));
    }

    private RegistrationState Set(Guid masterId, RegistrationState state)
    {
        _states[masterId] = state;
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Changed handler threw");
        }

        return state;
    }
}
