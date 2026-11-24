using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneDevice.MasterClient.Core.Cloud;

/// <summary>A registration started on the OneDevice website.</summary>
/// <param name="Secret">Known only to this agent; needed to collect the result.</param>
public sealed record ClaimTicket(Guid ClaimId, string Code, string ClaimUrl, DateTimeOffset ExpiresAt, int PollIntervalSeconds,
    string Secret);

public enum ClaimPollStatus
{
    Pending,
    Approved,
    Collected,
    Expired,
    Rejected,

    /// <summary>The website does not know the claim (any more).</summary>
    Unknown
}

/// <summary>DPS credentials issued by the website after the user confirmed.</summary>
public sealed record DpsCredentials(string GlobalEndpoint, string IdScope, string RegistrationId, string SymmetricKey);

public sealed record ClaimPollResult(ClaimPollStatus Status, DpsCredentials? Dps = null);

/// <summary>The website's registration API (POST /api/claims, GET /api/claims/{id}).</summary>
public interface IRegistrationClient
{
    /// <summary>OneDevice website, null when not configured (Cloud:PortalUrl).</summary>
    Uri? PortalUrl { get; }

    Task<ClaimTicket> StartAsync(string vendorId, string productId, string serialNumber, string masterType, CancellationToken ct);
    Task<ClaimPollResult> PollAsync(ClaimTicket ticket, CancellationToken ct);
}

public sealed class RegistrationClient(HttpClient http, Uri? portalUrl) : IRegistrationClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public Uri? PortalUrl { get; } = portalUrl;

    /// <summary>Only https, or http to this PC (development).</summary>
    public static Uri? ParsePortalUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            throw new InvalidOperationException($"Cloud:PortalUrl must be an https address, got '{value}'.");
        return uri;
    }

    public async Task<ClaimTicket> StartAsync(string vendorId, string productId, string serialNumber, string masterType,
        CancellationToken ct)
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var response = await http.PostAsJsonAsync(Url("api/claims"),
            new { vendorId, productId, serialNumber, masterType, agentSecret = secret }, Json, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var problem = await response.Content.ReadFromJsonAsync<ErrorBody>(Json, ct).ConfigureAwait(false);
            throw new InvalidOperationException($"The website refused the registration: {problem?.Error ?? "invalid request"}");
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<StartBody>(Json, ct).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("Empty answer from the website");
        return new ClaimTicket(body.ClaimId, body.Code, body.ClaimUrl, body.ExpiresAt, body.PollIntervalSeconds, secret);
    }

    public async Task<ClaimPollResult> PollAsync(ClaimTicket ticket, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url($"api/claims/{ticket.ClaimId}"));
        request.Headers.Add("X-Claim-Secret", ticket.Secret);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return new ClaimPollResult(ClaimPollStatus.Unknown);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClaimPollResult>(Json, ct).ConfigureAwait(false)
               ?? new ClaimPollResult(ClaimPollStatus.Unknown);
    }

    private Uri Url(string path) =>
        new(PortalUrl ?? throw new InvalidOperationException("The OneDevice website address is not configured (Cloud:PortalUrl)."),
            path);

    private sealed record StartBody(Guid ClaimId, string Code, string ClaimUrl, DateTimeOffset ExpiresAt, int PollIntervalSeconds);

    private sealed record ErrorBody(string? Error);
}
