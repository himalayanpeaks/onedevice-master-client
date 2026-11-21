using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Moq;
using OneDevice.Framework.Libs.Announcer;
using OneDevice.Framework.Libs.Validator;
using OneDevice.Master.IoLink.Products;
using OneDevice.MasterClient.Core.Cloud;
using OneDevice.MasterClient.Core.Configuration;
using OneDevice.MasterClient.Core.Masters;
using static OneDevice.Master.IoLink.Products.Definition;

namespace OneDevice.MasterClient.Core.UnitTest;

/// <summary>MasterSession and the cloud command handler against a mocked TMG HAL with UB6000 data.</summary>
public class MasterSessionTests
{
    private static readonly byte[] Dpp = Convert.FromHexString("0022211B11C300000130070800000000");

    private sealed class Fixture
    {
        public readonly Mock<IMasterHAL> Hal = new();
        public readonly FakeTimeProvider Time = new(DateTimeOffset.Parse("2026-11-21T12:00:00Z"));
        public DataTunnel<InternalDataHAL>.DataEventHandler? PdHandler;
        public readonly MasterSession Session;
        public readonly List<ProcessDataSample> Sent = [];

        public Fixture(bool sensorPresent = true)
        {
            Hal.SetupProperty(h => h.SensorPortNumber);
            Hal.Setup(h => h.AttachToProcessDataEvent(It.IsAny<DataTunnel<InternalDataHAL>.DataEventHandler>()))
                .Callback<DataTunnel<InternalDataHAL>.DataEventHandler>(h => PdHandler = h);
            Hal.Setup(h => h.Open("COM3", It.IsAny<IValidator>()))
                .Returns(OneDevice.Module.Definition.ConnectionError.NoError);
            Hal.Setup(h => h.ConnectSensorWithMaster(It.IsAny<t_eTargetMode>())).Returns(t_eInternal_Return_Codes.RETURN_OK);

            if (sensorPresent)
            {
                SetupRead(0, Dpp);
                SetupRead(0x10, Encoding.ASCII.GetBytes("Pepperl+Fuchs"));
                SetupRead(0x12, Encoding.ASCII.GetBytes("UB6000-F42-2EP-IO-V15"));
                SetupRead(0x15, Encoding.ASCII.GetBytes("0000000000000000"));
                SetupRead(0x17, Encoding.ASCII.GetBytes("FW01.01"));
            }
            else
            {
                byte[] none = [];
                byte len = 0, ec = 0, ac = 0;
                Hal.Setup(h => h.ReadRecord(It.IsAny<ushort>(), It.IsAny<byte>(), out none, out len, out ec, out ac))
                    .Returns(t_eInternal_Return_Codes.No_details);
            }

            var config = new MasterConfiguration { Name = "Bench", MasterType = "TmgUsbV2", Connection = "COM3" };
            Session = new MasterSession(config, MasterCatalog.TmgUsbV2, Hal.Object, time: Time);
            Session.ProcessDataChanged += (_, s) => Sent.Add(s);
        }

        public void SetupRead(ushort index, byte[] data, byte subindex = 0)
        {
            byte len = (byte)data.Length, ok = 0;
            Hal.Setup(h => h.ReadRecord(index, subindex, out data, out len, out ok, out ok))
                .Returns(t_eInternal_Return_Codes.RETURN_OK);
        }

        public void PushProcessData(params byte[] pd) =>
            PdHandler!(this, new InternalDataHAL(0, pd, 0, 0, 0, 0, 0, 0, 0, 0) { ProcessDataChanged = true });
    }

    [Fact]
    public async Task Probe_ReadsIdentity_WithoutIodd_AndStartsProcessData()
    {
        var f = new Fixture();
        Assert.True(await f.Session.ConnectAsync());

        var ports = await f.Session.ProbePortsAsync();

        var port = Assert.Single(ports);
        Assert.Equal(PortState.SensorFound, port.State);
        Assert.Equal(1, port.Sensor!.VendorId);
        Assert.Equal(3147528, port.Sensor.DeviceId);
        Assert.Equal("Pepperl+Fuchs", port.Sensor.VendorName);
        Assert.Equal("UB6000-F42-2EP-IO-V15", port.Sensor.ProductName);
        Assert.Equal("FW01.01", port.Sensor.FirmwareRevision);
        Assert.Null(port.Sensor.HardwareRevision); // not readable -> null, probe still succeeds
        f.Hal.Verify(h => h.StartProcessDataAnnouncer(), Times.Once);
    }

    [Fact]
    public async Task Probe_EmptyPort_DoesNotStartProcessData()
    {
        var f = new Fixture(sensorPresent: false);
        await f.Session.ConnectAsync();

        var port = Assert.Single(await f.Session.ProbePortsAsync());

        Assert.Equal(PortState.Empty, port.State);
        f.Hal.Verify(h => h.StartProcessDataAnnouncer(), Times.Never);
    }

    [Fact]
    public async Task ProcessData_IsThrottledToOnePerSecond_KeepingTheLatest()
    {
        var f = new Fixture();
        await f.Session.ConnectAsync();
        await f.Session.ProbePortsAsync();

        f.PushProcessData(0x7F, 0xFC, 0xFD, 0x03);
        f.PushProcessData(0x00, 0x64, 0xFD, 0x03);
        f.PushProcessData(0x00, 0x65, 0xFD, 0x03);
        Assert.Single(f.Sent);
        Assert.Equal("7FFCFD03", f.Sent[0].DataHex);
        Assert.Equal("0065FD03", f.Session.LatestProcessData!.DataHex); // local page sees every change

        f.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, f.Sent.Count);
        Assert.Equal("0065FD03", f.Sent[1].DataHex);
    }

    [Fact]
    public async Task ReadParameter_ReturnsHexData_AndIsduErrors()
    {
        var f = new Fixture();
        await f.Session.ConnectAsync();
        f.SetupRead(60, [0x00, 0x00, 0x1B, 0x6F], subindex: 1);
        byte[] none = [];
        byte len = 0, ec = 0x80, ac = 0x11;
        f.Hal.Setup(h => h.ReadRecord(999, 0, out none, out len, out ec, out ac)).Returns(t_eInternal_Return_Codes.RETURN_OK);
        var commands = new MasterCommandHandler(f.Session, "1.0.0");

        var ok = await commands.HandleAsync("readParameter", """{"port":0,"index":60,"subindex":1}""", default);
        var bad = await commands.HandleAsync("readParameter", """{"port":0,"index":999}""", default);

        Assert.Equal(200, ok.Status);
        var okBody = JsonSerializer.Deserialize<ParameterResponse>(ok.Json, CloudJson.Options)!;
        Assert.True(okBody.Success);
        Assert.Equal("00001B6F", okBody.Data);
        var badBody = JsonSerializer.Deserialize<ParameterResponse>(bad.Json, CloudJson.Options)!;
        Assert.False(badBody.Success);
        Assert.Equal(0x8011, badBody.IsduError);
        Assert.Equal("Index not available", badBody.Error);
    }

    [Fact]
    public async Task WriteParameter_PassesBytes_AndReportsResult()
    {
        var f = new Fixture();
        await f.Session.ConnectAsync();
        byte ok = 0;
        f.Hal.Setup(h => h.WriteRecord(24, 0, It.Is<byte[]>(b => Encoding.ASCII.GetString(b) == "Tank 1"), out ok, out ok))
            .Returns(t_eInternal_Return_Codes.RETURN_OK);
        var commands = new MasterCommandHandler(f.Session, "1.0.0");

        var hex = Convert.ToHexString(Encoding.ASCII.GetBytes("Tank 1"));
        var response = await commands.HandleAsync("writeParameter",
            $$"""{"port":0,"index":24,"subindex":0,"data":"{{hex}}"}""", default);

        Assert.True(JsonSerializer.Deserialize<ParameterResponse>(response.Json, CloudJson.Options)!.Success);
    }

    [Theory]
    [InlineData("writeParameter", """{"port":0,"index":24,"data":"zz"}""", 400)]
    [InlineData("writeParameter", """{"port":0,"index":24}""", 400)]
    [InlineData("readParameter", """{"port":0,"index":70000}""", 400)]
    [InlineData("readParameter", "not json", 400)]
    [InlineData("reboot", "{}", 404)]
    public async Task InvalidCommands_AreRejected(string method, string payload, int status)
    {
        var f = new Fixture();
        await f.Session.ConnectAsync();
        var response = await new MasterCommandHandler(f.Session, "1.0.0").HandleAsync(method, payload, default);
        Assert.Equal(status, response.Status);
    }

    [Fact]
    public async Task UnconfiguredPort_IsRejected()
    {
        var f = new Fixture();
        await f.Session.ConnectAsync();
        var result = await f.Session.ReadAsync(3, 0x10, 0);
        Assert.False(result.Success);
        Assert.Contains("not configured", result.Status);
    }

    [Fact]
    public async Task Report_UsesTwinSafePortNames()
    {
        var f = new Fixture();
        await f.Session.ConnectAsync();
        await f.Session.ProbePortsAsync();

        var report = MasterReport.From(f.Session, "1.2.3");
        var json = JsonSerializer.Serialize(report, CloudJson.Options);

        Assert.Contains("\"port0\"", json);
        Assert.Contains("\"deviceId\":3147528", json);
        Assert.Contains("\"state\":\"sensorFound\"", json);
        Assert.DoesNotContain(".", report.Ports.Keys.Single());
    }
}
