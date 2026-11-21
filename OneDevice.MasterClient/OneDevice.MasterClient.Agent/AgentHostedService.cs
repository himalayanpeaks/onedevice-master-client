using OneDevice.MasterClient.Core.Runtime;

namespace OneDevice.MasterClient.Agent;

/// <summary>Starts the configured masters with the service and stops them cleanly (ports back, cloud closed).</summary>
public sealed class AgentHostedService(AgentRuntime runtime) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => runtime.StartAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken) => await runtime.DisposeAsync();
}
