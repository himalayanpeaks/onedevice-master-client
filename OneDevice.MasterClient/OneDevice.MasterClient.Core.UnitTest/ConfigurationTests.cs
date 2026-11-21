using Microsoft.Extensions.Logging.Abstractions;
using OneDevice.MasterClient.Core.Configuration;
using OneDevice.MasterClient.Core.Runtime;

namespace OneDevice.MasterClient.Core.UnitTest;

public class ConfigurationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "odmc-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private ConfigurationStore Store() => new(Path.Combine(_dir, "agent.json"));

    private static MasterConfiguration Master(string name = "Bench", string com = "COM3") => new()
    {
        Name = name, MasterType = "TmgUsbV2", Connection = com, SensorPorts = [0],
        Cloud = new CloudConfiguration
        {
            Mode = CloudMode.ConnectionString,
            DeviceConnectionString = "HostName=h.azure-devices.net;DeviceId=d1;SharedAccessKey=c2VjcmV0"
        }
    };

    [Fact]
    public void Save_EncryptsSecrets_AndLoadDecrypts()
    {
        var store = Store();
        var config = new AgentConfiguration { Masters = [Master()] };

        store.Save(config);

        var file = File.ReadAllText(store.Path);
        Assert.DoesNotContain("SharedAccessKey", file);
        Assert.Contains(ConfigurationStore.EncryptedPrefix, file);
        Assert.Equal("HostName=h.azure-devices.net;DeviceId=d1;SharedAccessKey=c2VjcmV0",
            store.Load().Masters[0].Cloud.DeviceConnectionString);
        // the caller's object is not modified
        Assert.StartsWith("HostName=", config.Masters[0].Cloud.DeviceConnectionString);
    }

    [Fact]
    public void Load_MissingFile_GivesEmptyConfiguration()
    {
        Assert.Empty(Store().Load().Masters);
    }

    [Theory]
    [InlineData("", "TmgUsbV2", "COM3", "name")]
    [InlineData("Bench", "Unknown", "COM3", "type")]
    [InlineData("Bench", "TmgUsbV2", "", "COM port")]
    public void Validation_ReportsMissingFields(string name, string type, string com, string expected)
    {
        var runtime = new AgentRuntime(Store(), NullLoggerFactory.Instance);
        var master = Master();
        master.Name = name;
        master.MasterType = type;
        master.Connection = com;
        Assert.Contains(expected, runtime.Validate(master));
    }

    [Fact]
    public void Validation_RejectsPortsTheMasterDoesNotHave()
    {
        var runtime = new AgentRuntime(Store(), NullLoggerFactory.Instance);
        var master = Master();
        master.SensorPorts = [0, 3];
        Assert.Contains("port", runtime.Validate(master));
    }

    [Fact]
    public async Task Validation_RejectsTheSameComPortTwice()
    {
        var store = Store();
        store.Save(new AgentConfiguration { Masters = [Master("A")] });
        var runtime = new AgentRuntime(store, NullLoggerFactory.Instance,
            halFactory: _ => throw new InvalidOperationException("no hardware in tests"));
        await runtime.StartAsync(); // driver creation fails, but the configuration is loaded

        Assert.Contains("already used", runtime.Validate(Master("B")));
    }

    [Fact]
    public void Validation_RequiresCloudCredentials()
    {
        var runtime = new AgentRuntime(Store(), NullLoggerFactory.Instance);
        var master = Master();
        master.Cloud = new CloudConfiguration { Mode = CloudMode.Dps, DpsIdScope = "0ne001" };
        Assert.Contains("DPS", runtime.Validate(master));
        master.Cloud = new CloudConfiguration { Mode = CloudMode.Offline };
        Assert.Null(runtime.Validate(master));
    }
}
