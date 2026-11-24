using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OneDevice.MasterClient.Core.Cloud;
using OneDevice.MasterClient.Core.Configuration;
using OneDevice.MasterClient.Core.Discovery;
using OneDevice.MasterClient.Core.Masters;
using OneDevice.MasterClient.Core.Runtime;

namespace OneDevice.MasterClient.Core.UnitTest;

public class RegistrationTests
{
    // ---- HTTP client against the website's API ----

    private sealed class FakeWebsite(Func<HttpRequestMessage, string?, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Request, string? Body)> Requests = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request, body));
            return answer(request, body);
        }
    }

    private static HttpResponseMessage JsonAnswer(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Client_StartsClaim_AndCollectsCredentials()
    {
        var site = new FakeWebsite((request, _) => request.Method == HttpMethod.Post
            ? JsonAnswer(HttpStatusCode.Created,
                """{"claimId":"6f1c9c2e-41f5-4b8f-9b2a-0d3c6a1e2f10","code":"K7F2-9QXA","claimUrl":"https://portal/claim?code=K7F2-9QXA","expiresAt":"2026-11-23T12:15:00Z","pollIntervalSeconds":3}""")
            : JsonAnswer(HttpStatusCode.OK,
                """{"status":"approved","dps":{"globalEndpoint":"global.azure-devices-provisioning.net","idScope":"0ne00TEST","registrationId":"usb-2843-0201-1392765243","symmetricKey":"a2V5"}}"""));
        var client = new RegistrationClient(new HttpClient(site), RegistrationClient.ParsePortalUrl("https://portal.example.com"));

        var ticket = await client.StartAsync("2843", "0201", "1392765243", "TmgUsbV2", default);
        var poll = await client.PollAsync(ticket, default);

        Assert.Equal("K7F2-9QXA", ticket.Code);
        Assert.Equal(3, ticket.PollIntervalSeconds);
        Assert.True(ticket.Secret.Length >= 32);
        var sent = JsonDocument.Parse(site.Requests[0].Body!).RootElement;
        Assert.Equal("https://portal.example.com/api/claims", site.Requests[0].Request.RequestUri!.ToString());
        Assert.Equal("1392765243", sent.GetProperty("serialNumber").GetString());
        Assert.Equal(ticket.Secret, sent.GetProperty("agentSecret").GetString());

        Assert.Equal(ticket.Secret, site.Requests[1].Request.Headers.GetValues("X-Claim-Secret").Single());
        Assert.Equal(ClaimPollStatus.Approved, poll.Status);
        Assert.Equal("a2V5", poll.Dps!.SymmetricKey);
        Assert.Equal("0ne00TEST", poll.Dps.IdScope);
    }

    [Fact]
    public async Task Client_UnknownClaim_IsReportedAsUnknown()
    {
        var client = new RegistrationClient(new HttpClient(new FakeWebsite((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound))),
            new Uri("https://portal/"));
        var ticket = new ClaimTicket(Guid.NewGuid(), "X", "u", DateTimeOffset.UtcNow, 3, "secret");
        Assert.Equal(ClaimPollStatus.Unknown, (await client.PollAsync(ticket, default)).Status);
    }

    [Fact]
    public async Task Client_BadRequest_ShowsTheWebsitesReason()
    {
        var client = new RegistrationClient(new HttpClient(new FakeWebsite((_, _) =>
            JsonAnswer(HttpStatusCode.BadRequest, """{"error":"serialNumber must be 1..64 characters"}"""))), new Uri("https://portal/"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartAsync("2843", "0201", "x", "TmgUsbV2", default));
        Assert.Contains("serialNumber must be", ex.Message);
    }

    [Theory]
    [InlineData("https://onedevice.example.com", "https://onedevice.example.com/")]
    [InlineData("http://localhost:5090", "http://localhost:5090/")]
    [InlineData("", null)]
    public void PortalUrl_HttpsOrLocalhostOnly(string value, string? expected)
    {
        Assert.Equal(expected, RegistrationClient.ParsePortalUrl(value)?.ToString());
    }

    [Fact]
    public void PortalUrl_PlainHttpToAnotherHost_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => RegistrationClient.ParsePortalUrl("http://onedevice.example.com"));
    }

    // ---- registrar ----

    private sealed class FakeClient : IRegistrationClient
    {
        public readonly Queue<ClaimPollResult> Answers = new();
        public Exception? StartError;
        public (string Vid, string Pid, string Serial)? Started;

        public Uri? PortalUrl { get; set; } = new("https://portal/");

        public Task<ClaimTicket> StartAsync(string vendorId, string productId, string serialNumber, string masterType, CancellationToken ct)
        {
            if (StartError != null) throw StartError;
            Started = (vendorId, productId, serialNumber);
            return Task.FromResult(new ClaimTicket(Guid.NewGuid(), "K7F2-9QXA", "https://portal/claim?code=K7F2-9QXA",
                DateTimeOffset.Parse("2026-11-23T12:15:00Z"), 3, "secret"));
        }

        public Task<ClaimPollResult> PollAsync(ClaimTicket ticket, CancellationToken ct) =>
            Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : new ClaimPollResult(ClaimPollStatus.Pending));
    }

    private sealed class FakeScanner(params SerialPortInfo[] ports) : ISerialPortScanner
    {
        public IReadOnlyList<SerialPortInfo> Scan() => ports;
    }

    private sealed class Fixture
    {
        public readonly FakeClient Client = new();
        public readonly FakeTimeProvider Time = new(DateTimeOffset.Parse("2026-11-23T12:00:00Z"));
        public readonly MasterConfiguration Master = new() { Name = "Bench", MasterType = "TmgUsbV2", Connection = "COM3" };
        public readonly List<MasterConfiguration> Saved = [];
        public readonly MasterRegistrar Registrar;

        public Fixture(params SerialPortInfo[] ports)
        {
            if (ports.Length == 0)
                ports = [new SerialPortInfo("COM3", "USB Serial Device (COM3)", "2843", "0201", MasterCatalog.TmgUsbV2, "1392765243")];
            Registrar = new MasterRegistrar(Client, new FakeScanner(ports), id => id == Master.Id ? Master : null,
                m =>
                {
                    lock (Saved) Saved.Add(m);
                    return Task.CompletedTask;
                }, NullLogger<MasterRegistrar>.Instance, Time);
        }

        /// <summary>Moves the clock in poll steps until the condition holds.</summary>
        public async Task PollUntil(Func<bool> condition)
        {
            for (var i = 0; i < 200 && !condition(); i++)
            {
                Time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(5);
            }

            Assert.True(condition());
        }
    }

    [Fact]
    public async Task Register_WaitsForConfirmation_ThenSwitchesMasterToDps()
    {
        var f = new Fixture();
        var state = await f.Registrar.StartAsync(f.Master.Id);

        Assert.Equal(RegistrationPhase.WaitingForUser, state.Phase);
        Assert.Equal("https://portal/claim?code=K7F2-9QXA", state.ClaimUrl);
        Assert.Equal(("2843", "0201", "1392765243"), f.Client.Started);

        f.Client.Answers.Enqueue(new ClaimPollResult(ClaimPollStatus.Pending));
        f.Client.Answers.Enqueue(new ClaimPollResult(ClaimPollStatus.Approved,
            new DpsCredentials("global.azure-devices-provisioning.net", "0ne00TEST", "usb-2843-0201-1392765243", "a2V5")));
        await f.PollUntil(() => f.Registrar.State(f.Master.Id).Phase == RegistrationPhase.Completed);

        var saved = Assert.Single(f.Saved);
        Assert.Equal(f.Master.Id, saved.Id);
        Assert.Equal("Bench", saved.Name);
        Assert.Equal(CloudMode.Dps, saved.Cloud.Mode);
        Assert.Equal("0ne00TEST", saved.Cloud.DpsIdScope);
        Assert.Equal("usb-2843-0201-1392765243", saved.Cloud.DpsRegistrationId);
        Assert.Equal("a2V5", saved.Cloud.DpsSymmetricKey);
        Assert.Equal(("2843", "0201", "1392765243"), (saved.UsbVendorId, saved.UsbProductId, saved.UsbSerialNumber));
        Assert.Equal(CloudMode.Offline, f.Master.Cloud.Mode); // the live configuration is replaced by the runtime, not edited
    }

    [Theory]
    [InlineData(ClaimPollStatus.Rejected, "another OneDevice account")]
    [InlineData(ClaimPollStatus.Expired, "expired")]
    [InlineData(ClaimPollStatus.Unknown, "expired")]
    public async Task Register_StopsWithAMessage_WhenNotConfirmed(ClaimPollStatus status, string message)
    {
        var f = new Fixture();
        await f.Registrar.StartAsync(f.Master.Id);
        f.Client.Answers.Enqueue(new ClaimPollResult(status));

        await f.PollUntil(() => f.Registrar.State(f.Master.Id).Phase == RegistrationPhase.Failed);

        Assert.Contains(message, f.Registrar.State(f.Master.Id).Message);
        Assert.Empty(f.Saved);
    }

    [Fact]
    public async Task Register_MasterNotPluggedIn_Fails()
    {
        var f = new Fixture(new SerialPortInfo("COM1", "Kommunikationsanschluss (COM1)", null, null, null));
        var state = await f.Registrar.StartAsync(f.Master.Id);
        Assert.Equal(RegistrationPhase.Failed, state.Phase);
        Assert.Contains("No USB master found on COM3", state.Message);
        Assert.Null(f.Client.Started);
    }

    [Fact]
    public async Task Register_DeviceWithoutUsbSerial_Fails()
    {
        var f = new Fixture(new SerialPortInfo("COM3", "USB Serial Device (COM3)", "2843", "0201", MasterCatalog.TmgUsbV2));
        var state = await f.Registrar.StartAsync(f.Master.Id);
        Assert.Equal(RegistrationPhase.Failed, state.Phase);
        Assert.Contains("no USB serial number", state.Message);
    }

    [Fact]
    public async Task Register_WebsiteUnreachable_Fails()
    {
        var f = new Fixture();
        f.Client.StartError = new HttpRequestException("No such host is known.");
        var state = await f.Registrar.StartAsync(f.Master.Id);
        Assert.Equal(RegistrationPhase.Failed, state.Phase);
        Assert.Contains("Cannot reach the OneDevice website", state.Message);
    }

    [Fact]
    public async Task Cancel_StopsWaiting()
    {
        var f = new Fixture();
        await f.Registrar.StartAsync(f.Master.Id);
        f.Registrar.Cancel(f.Master.Id);
        f.Client.Answers.Enqueue(new ClaimPollResult(ClaimPollStatus.Approved,
            new DpsCredentials("g", "s", "r", "k")));

        f.Time.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(50);

        Assert.Equal(RegistrationPhase.Idle, f.Registrar.State(f.Master.Id).Phase);
        Assert.Empty(f.Saved);
    }

    [Fact]
    public async Task NotConfigured_IsUnavailable()
    {
        var f = new Fixture();
        f.Client.PortalUrl = null;
        Assert.False(f.Registrar.IsAvailable);
        Assert.Contains("Cloud:PortalUrl", (await f.Registrar.StartAsync(f.Master.Id)).Message);
    }
}
