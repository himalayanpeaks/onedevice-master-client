using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OneDevice.Framework.Libs.Announcer;
using OneDevice.Framework.Libs.Validator;
using OneDevice.Master.IoLink.Products;
using OneDevice.MasterClient.Core.Cloud;
using OneDevice.MasterClient.Core.Configuration;
using OneDevice.MasterClient.Core.Discovery;
using OneDevice.MasterClient.Core.Masters;
using OneDevice.MasterClient.Core.Runtime;
using static OneDevice.Master.IoLink.Products.Definition;

namespace OneDevice.MasterClient.Core.UnitTest;

/// <summary>A master is found by its UID (USB serial number), never just by its COM port.</summary>
public class UidBindingTests : IDisposable
{
    private const string SerialA = "1392765243", SerialB = "2373498377";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "odmc-uid-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static SerialPortInfo Tmg(string com, string serial) =>
        new(com, $"USB Serial Device ({com})", "2843", "0201", MasterCatalog.TmgUsbV2, serial);

    private static MasterConfiguration MasterA(string com = "COM3") => new()
    {
        Name = "Bench A", MasterType = "TmgUsbV2", Connection = com, SensorPorts = [0],
        UsbVendorId = "2843", UsbProductId = "0201", UsbSerialNumber = SerialA
    };

    private sealed class Scanner : ISerialPortScanner
    {
        public IReadOnlyList<SerialPortInfo> Ports = [];
        public IReadOnlyList<SerialPortInfo> Scan() => Ports;
    }

    // ---- resolver ----

    [Fact]
    public void MasterOnAnotherPort_IsFoundBySerialNumber()
    {
        var found = MasterPortResolver.Resolve(MasterA("COM3"), [Tmg("COM3", SerialB), Tmg("COM5", SerialA)]);
        Assert.Equal("COM5", found.Port);
    }

    [Fact]
    public void AnotherMasterOnTheOldPort_IsNotTakenForThisOne()
    {
        var found = MasterPortResolver.Resolve(MasterA("COM3"), [Tmg("COM3", SerialB)]);
        Assert.Null(found.Port);
        Assert.Equal($"Master {SerialA} is not plugged in.", found.Error);
    }

    [Fact]
    public void MasterWithoutUsbIdentity_KeepsItsComPort()
    {
        var master = MasterA("COM7");
        master.UsbSerialNumber = null;
        Assert.Equal("COM7", MasterPortResolver.Resolve(master, []).Port);
    }

    [Fact]
    public void MasterId_RoundTrips()
    {
        Assert.Equal("usb-2843-0201-1392765243", MasterPortResolver.ToMasterId("2843", "0201", SerialA));
        Assert.True(MasterPortResolver.TryParseMasterId("usb-2843-0201-1392765243", out var vid, out var pid, out var serial));
        Assert.Equal(("2843", "0201", SerialA), (vid, pid, serial));
        Assert.False(MasterPortResolver.TryParseMasterId("powersupply-001", out _, out _, out _));
    }

    // ---- runtime: connects the right master ----

    private static (MasterRuntime Runtime, List<string> Opened) Runtime(MasterConfiguration master, Scanner scanner)
    {
        var opened = new List<string>();
        var hal = new Mock<IMasterHAL>();
        hal.SetupProperty(h => h.SensorPortNumber);
        hal.Setup(h => h.AttachToProcessDataEvent(It.IsAny<DataTunnel<InternalDataHAL>.DataEventHandler>()));
        hal.Setup(h => h.Open(It.IsAny<string>(), It.IsAny<IValidator>()))
            .Callback<string, IValidator>((com, _) =>
            {
                lock (opened) opened.Add(com);
            })
            .Returns(OneDevice.Module.Definition.ConnectionError.NoError);
        hal.Setup(h => h.ConnectSensorWithMaster(It.IsAny<t_eTargetMode>())).Returns(t_eInternal_Return_Codes.RETURN_OK);
        byte[] none = [];
        byte len = 0, ec = 0, ac = 0;
        hal.Setup(h => h.ReadRecord(It.IsAny<ushort>(), It.IsAny<byte>(), out none, out len, out ec, out ac))
            .Returns(t_eInternal_Return_Codes.No_details);
        var session = new MasterSession(master, MasterCatalog.TmgUsbV2, hal.Object);
        return (new MasterRuntime(session, new OfflineCloudConnector(), "1.0.0", NullLogger.Instance, scanner: scanner), opened);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task Runtime_OpensThePortWhereTheMasterIsNow()
    {
        var master = MasterA("COM3");
        var scanner = new Scanner { Ports = [Tmg("COM3", SerialB), Tmg("COM5", SerialA)] };
        var (runtime, opened) = Runtime(master, scanner);
        await using var _ = runtime;

        runtime.Start();

        await Eventually(() => runtime.Session.IsConnected);
        Assert.Equal(["COM5"], opened);
        Assert.Equal("COM5", master.Connection);
    }

    [Fact]
    public async Task Runtime_MasterNotPluggedIn_OpensNothing_AndSaysSo()
    {
        var scanner = new Scanner { Ports = [Tmg("COM3", SerialB)] }; // another master got the old port
        var (runtime, opened) = Runtime(MasterA("COM3"), scanner);
        await using var _ = runtime;

        runtime.Start();

        await Eventually(() => runtime.Session.LastError != null);
        Assert.Equal($"Master {SerialA} is not plugged in.", runtime.Session.LastError);
        Assert.Empty(opened);
        Assert.False(runtime.Session.IsConnected);
    }

    // ---- registration stores the UID ----

    [Fact]
    public void ExistingRegisteredMaster_GetsItsUidFromTheRegistrationId()
    {
        var master = MasterA();
        master.UsbVendorId = master.UsbProductId = master.UsbSerialNumber = null;
        master.Cloud = new CloudConfiguration { Mode = CloudMode.Dps, DpsRegistrationId = "usb-2843-0201-1392765243" };

        Assert.True(MasterPortResolver.BackfillFromRegistration(master));
        Assert.Equal(("2843", "0201", SerialA), (master.UsbVendorId, master.UsbProductId, master.UsbSerialNumber));
        Assert.False(MasterPortResolver.BackfillFromRegistration(master)); // only once
    }

    [Fact]
    public async Task AgentStart_FillsAndSavesTheUidOfRegisteredMasters()
    {
        var store = new ConfigurationStore(Path.Combine(_dir, "agent.json"));
        var master = MasterA();
        master.UsbVendorId = master.UsbProductId = master.UsbSerialNumber = null;
        master.Enabled = false;
        master.Cloud = new CloudConfiguration
        {
            Mode = CloudMode.Dps, DpsIdScope = "0ne00TEST", DpsRegistrationId = "usb-2843-0201-1392765243", DpsSymmetricKey = "a2V5"
        };
        store.Save(new AgentConfiguration { Masters = [master] });

        await using var runtime = new AgentRuntime(store, NullLoggerFactory.Instance);
        await runtime.StartAsync();

        Assert.Equal(SerialA, store.Load().Masters[0].UsbSerialNumber);
    }

    // ---- validation ----

    [Fact]
    public void SameMasterTwice_IsRefused_EvenOnDifferentPorts()
    {
        var store = new ConfigurationStore(Path.Combine(_dir, "agent.json"));
        store.Save(new AgentConfiguration { Masters = [MasterA("COM3")] });
        var runtime = new AgentRuntime(store, NullLoggerFactory.Instance);
        runtime.StartAsync().GetAwaiter().GetResult();

        var again = MasterA("COM5");
        again.Name = "Copy";
        Assert.Contains($"Master {SerialA} is already configured", runtime.Validate(again));

        var other = MasterA("COM3"); // other master, the old port number of A: allowed
        other.UsbSerialNumber = SerialB;
        Assert.Null(runtime.Validate(other));
    }

    [Fact]
    public void RegisteredMaster_CannotBePointedAtAnotherMaster()
    {
        var runtime = new AgentRuntime(new ConfigurationStore(Path.Combine(_dir, "agent.json")), NullLoggerFactory.Instance);
        var master = MasterA();
        master.UsbSerialNumber = SerialB; // user picked the port of master B
        master.Cloud = new CloudConfiguration
        {
            Mode = CloudMode.Dps, DpsIdScope = "0ne00TEST", DpsRegistrationId = "usb-2843-0201-1392765243", DpsSymmetricKey = "a2V5"
        };

        Assert.Contains("registered as usb-2843-0201-1392765243", runtime.Validate(master));
    }
}
