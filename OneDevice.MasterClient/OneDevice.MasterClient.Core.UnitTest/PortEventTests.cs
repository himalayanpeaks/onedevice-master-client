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

/// <summary>IO-Link Events, communication monitor and master Data Storage control.</summary>
public class PortEventTests
{
    private static readonly byte[] Dpp = Convert.FromHexString("0022211B11C300000130070800000000");

    private delegate t_eInternal_Return_Codes GetModeCallback(out string com, out byte[] deviceId, out byte[] vendorId,
        out byte[] functionId, out byte actualMode, out byte sensorState, out byte masterCycle, out byte baudRate);

    private sealed class Fixture
    {
        public readonly Mock<IMasterHAL> Hal = new();
        public readonly FakeTimeProvider Time = new(DateTimeOffset.Parse("2026-11-23T12:00:00Z"));
        public DataTunnel<InternalDataHAL>.DataEventHandler? PdHandler;
        public readonly MasterSession Session;
        /// <summary>IOL_GetMode sensor state: 0xFF OPERATE, 0x80 PREOPERATE, 0x00 disconnected.</summary>
        public byte State = 0xFF;

        public t_eInternal_Return_Codes ModeResult = t_eInternal_Return_Codes.RETURN_OK;
        public bool SensorAnswers = true;

        public Fixture()
        {
            Hal.SetupProperty(h => h.SensorPortNumber);
            Hal.Setup(h => h.AttachToProcessDataEvent(It.IsAny<DataTunnel<InternalDataHAL>.DataEventHandler>()))
                .Callback<DataTunnel<InternalDataHAL>.DataEventHandler>(h => PdHandler = h);
            Hal.Setup(h => h.Open("COM3", It.IsAny<IValidator>())).Returns(OneDevice.Module.Definition.ConnectionError.NoError);
            Hal.Setup(h => h.ConnectSensorWithMaster(It.IsAny<t_eTargetMode>())).Returns(t_eInternal_Return_Codes.RETURN_OK);
            Hal.Setup(h => h.SetMode(It.IsAny<t_eTargetMode>(), It.IsAny<t_ePortModeDetails>(),
                It.IsAny<t_eDsConfigureCommands>(), It.IsAny<t_eIoLinkVersion>())).Returns(t_eInternal_Return_Codes.RETURN_OK);
            string com;
            byte[] did, vid, fid;
            byte actual, state, cycle, baud;
            Hal.Setup(h => h.GetMode(out com, out did, out vid, out fid, out actual, out state, out cycle, out baud))
                .Returns(new GetModeCallback((out string c, out byte[] d, out byte[] v, out byte[] fn, out byte a, out byte st,
                    out byte cy, out byte b) =>
                {
                    c = "COM3";
                    d = [0x30, 0x07, 0x08];
                    v = [0x00, 0x01];
                    fn = [0, 0];
                    a = 1;
                    st = State;
                    cy = 0x22;
                    b = 2;
                    return ModeResult;
                }));
            byte[] data = Dpp;
            byte len = 16, ec = 0, ac = 0;
            Hal.Setup(h => h.ReadRecord(It.IsAny<ushort>(), It.IsAny<byte>(), out data, out len, out ec, out ac))
                .Returns(() => SensorAnswers ? t_eInternal_Return_Codes.RETURN_OK : t_eInternal_Return_Codes.No_details);

            var config = new MasterConfiguration { Name = "Bench", MasterType = "TmgUsbV2", Connection = "COM3" };
            Session = new MasterSession(config, MasterCatalog.TmgUsbV2, Hal.Object, time: Time);
        }

        public void PushEvent(ushort code, byte mode, byte type) =>
            PdHandler!(this, new InternalDataHAL(0, [0x7F], 1, code, 0, mode, type, 0, 0, 0) { ProcessDataChanged = false });
    }

    [Fact]
    public async Task Io_link_events_are_recorded_and_returned_by_getEvents()
    {
        var f = new Fixture();
        await f.Session.ConnectAsync();
        await f.Session.ProbePortsAsync();

        f.PushEvent(0xFF91, 0x40, 0x10); // DS_UPLOAD_REQ: single shot notification (TMG raw qualifier bits)
        f.PushEvent(0x4000, 0xC0, 0x30); // temperature fault: appears, error

        var handler = new MasterCommandHandler(f.Session, "1.0.0");
        var response = await handler.HandleAsync("getEvents", """{"port":0,"after":0}""", default);
        Assert.Equal(200, response.Status);
        var result = JsonSerializer.Deserialize<EventsResponse>(response.Json, CloudJson.Options)!;
        Assert.Equal(2, result.Events.Count);
        Assert.Equal(0xFF91, result.Events[0].Code);
        Assert.Equal(1, result.Events[0].Mode);
        Assert.Equal(1, result.Events[0].Type);
        Assert.Equal(3, result.Events[1].Mode);
        Assert.Equal(3, result.Events[1].Type);
        Assert.Equal(PortEventKind.Event, result.Events[1].Kind);

        var later = await handler.HandleAsync("getEvents", $$"""{"port":0,"after":{{result.Events[0].Sequence}}}""", default);
        Assert.Single(JsonSerializer.Deserialize<EventsResponse>(later.Json, CloudJson.Options)!.Events);
    }

    [Fact]
    public async Task Communication_lost_and_started_are_detected_from_the_sensor_status()
    {
        var f = new Fixture();
        await f.Session.ConnectAsync();
        await f.Session.ProbePortsAsync();

        await f.Session.PollCommunicationAsync(); // first reading: state only
        f.State = 0x00; // reset: no communication
        await f.Session.PollCommunicationAsync();
        f.State = 0x80; // startup: PREOPERATE
        await f.Session.PollCommunicationAsync();
        f.State = 0xFF; // OPERATE: still communicating
        await f.Session.PollCommunicationAsync();

        Assert.Equal([PortEventKind.CommunicationLost, PortEventKind.CommunicationStarted],
            f.Session.Events.After(0).Select(e => e.Kind));
        Assert.True(f.Session.IsCommunicating(0));
    }

    [Fact]
    public async Task Unknown_sensor_state_records_nothing()
    {
        var f = new Fixture { ModeResult = t_eInternal_Return_Codes.RESULT_SERVICE_PENDING };
        await f.Session.ConnectAsync();
        await f.Session.PollCommunicationAsync();
        await f.Session.PollCommunicationAsync();

        Assert.Empty(f.Session.Events.After(0));
        Assert.Null(f.Session.IsCommunicating(0));
    }

    [Fact]
    public async Task Communication_start_on_an_empty_port_probes_it_again()
    {
        var f = new Fixture { SensorAnswers = false, State = 0x00 };
        await f.Session.ConnectAsync();
        await f.Session.ProbePortsAsync();
        Assert.Equal(PortState.Empty, f.Session.Ports[0].State);
        await f.Session.PollCommunicationAsync();

        f.SensorAnswers = true;
        f.State = 0xFF;
        await f.Session.PollCommunicationAsync();

        Assert.Equal(PortState.SensorFound, f.Session.Ports[0].State);
    }

    [Fact]
    public async Task Data_storage_can_be_switched_off_and_stays_off_for_later_probes()
    {
        var f = new Fixture();
        await f.Session.ConnectAsync();
        var handler = new MasterCommandHandler(f.Session, "1.0.0");

        var response = await handler.HandleAsync("setDataStorage", """{"port":0,"enabled":false}""", default);
        Assert.Equal(200, response.Status);
        Assert.False(f.Session.IsDataStorageEnabled(0));
        await f.Session.ProbePortsAsync();

        f.Hal.Verify(h => h.SetMode(t_eTargetMode.SM_MODE_IOLINK_OPERATE, t_ePortModeDetails.SM_MODE_FREE_RUNNING,
            t_eDsConfigureCommands.DS_CFG_DISABLED, t_eIoLinkVersion.VERSION_1_1), Times.Exactly(2));
        f.Hal.Verify(h => h.ConnectSensorWithMaster(It.IsAny<t_eTargetMode>()), Times.Never);

        await handler.HandleAsync("setDataStorage", """{"port":0,"enabled":true}""", default);
        Assert.True(f.Session.IsDataStorageEnabled(0));
        f.Hal.Verify(h => h.ConnectSensorWithMaster(It.IsAny<t_eTargetMode>()), Times.Once);
    }
}
