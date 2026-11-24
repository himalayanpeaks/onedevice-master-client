using OneDevice.MasterClient.Core.Discovery;
using OneDevice.MasterClient.Core.Masters;

namespace OneDevice.MasterClient.Core.UnitTest;

public class IdentityAndDiscoveryTests
{
    // Direct parameter page 1 read from a Pepperl+Fuchs UB6000-F42-2EP-IO-V15 on a TMG USB master.
    // 00-22-21-1B-11-C3-00-00-01-30-07-08-00-00-00-00
    private static readonly byte[] Ub6000Dpp = Convert.FromHexString("0022211B11C300000130070800000000");

    [Fact]
    public void DirectParameterPage_IsDecoded()
    {
        var id = SensorIdentity.FromDirectParameterPage(Ub6000Dpp)!;
        Assert.Equal(1, id.VendorId); // Pepperl+Fuchs
        Assert.Equal(3147528, id.DeviceId);
        Assert.Equal("1.1", id.IoLinkRevision);
        Assert.Equal(32, id.ProcessDataInBits); // 0xC3: 4 octets
    }

    [Theory]
    [InlineData(0xC3, 32)] // octets: (3+1)*8
    [InlineData(0x40, 8)] // octets: (0+1)*8
    [InlineData(0x10, 16)] // bits
    [InlineData(0x81, 1)] // SIO bit set, 1 bit
    public void ProcessDataLength_IsDecoded(int value, int bits)
    {
        Assert.Equal(bits, SensorIdentity.DecodeProcessDataLength((byte)value));
    }

    [Fact]
    public void TooShortPage_GivesNull()
    {
        Assert.Null(SensorIdentity.FromDirectParameterPage([0x00, 0x01]));
    }

    [Theory]
    [InlineData("USB Serial Device (COM3)", @"USB\VID_2843&PID_0201\1392765243", "COM3", "2843", "0201", true)]
    [InlineData("USB Serial Device (COM5)", @"USB\VID_0416&PID_5011\001F0FC20248", "COM5", "0416", "5011", false)]
    [InlineData("USB Serial Port (COM4)", @"FTDIBUS\VID_0403+PID_6001+A601IXGGA\0000", "COM4", "0403", "6001", false)]
    [InlineData("Kommunikationsanschluss (COM1)", @"ACPI\PNP0501\0", "COM1", null, null, false)]
    public void SerialPorts_AreParsed_AndTmgIsRecognised(string name, string pnp, string port, string? vid, string? pid,
        bool isTmg)
    {
        var info = SerialPortScanner.Parse(name, pnp)!;
        Assert.Equal(port, info.PortName);
        Assert.Equal(vid, info.UsbVendorId);
        Assert.Equal(pid, info.UsbProductId);
        Assert.Equal(isTmg ? MasterCatalog.TmgUsbV2 : null, info.MasterType);
    }

    [Theory]
    [InlineData(@"USB\VID_2843&PID_0201\1392765243", "1392765243")] // TMG master: real USB serial number
    [InlineData(@"USB\VID_2843&PID_0201\6&3365fbaf&0&5", null)] // no USB serial: Windows-generated id
    [InlineData(@"FTDIBUS\VID_0403+PID_6001+A601IXGGA\0000", null)] // not a plain USB device id
    [InlineData(@"ACPI\PNP0501\0", null)]
    public void UsbSerialNumber_IsTakenFromTheInstanceId(string pnp, string? serial)
    {
        Assert.Equal(serial, SerialPortScanner.Parse("USB Serial Device (COM3)", pnp)!.UsbSerialNumber);
    }

    [Fact]
    public void NonComDevice_IsIgnored()
    {
        Assert.Null(SerialPortScanner.Parse("Some USB device", @"USB\VID_1234&PID_5678\0"));
    }

    [Theory]
    [InlineData(0x80, 0x11, "Index not available")]
    [InlineData(0x80, 0x30, "Parameter value out of range")]
    [InlineData(0x80, 0x99, "ISDU error 0x8099")]
    public void IsduErrors_AreDescribed(byte code, byte additional, string text)
    {
        var result = new IsduResult(false, [], code, additional, "RETURN_OK");
        Assert.Equal(text, result.ErrorText);
    }
}
