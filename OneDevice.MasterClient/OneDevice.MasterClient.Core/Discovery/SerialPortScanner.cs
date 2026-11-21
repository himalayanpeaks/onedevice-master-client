using System.Management;
using System.Text.RegularExpressions;
using OneDevice.MasterClient.Core.Masters;

namespace OneDevice.MasterClient.Core.Discovery;

/// <summary>A COM port present on this PC.</summary>
/// <param name="PortName">e.g. "COM3".</param>
/// <param name="Description">Windows device name, e.g. "USB Serial Device (COM3)".</param>
/// <param name="UsbVendorId">USB VID (hex) if the port is a USB device.</param>
/// <param name="UsbProductId">USB PID (hex) if the port is a USB device.</param>
/// <param name="MasterType">Recognised IO-Link master model, if any.</param>
public sealed record SerialPortInfo(
    string PortName,
    string Description,
    string? UsbVendorId,
    string? UsbProductId,
    MasterTypeInfo? MasterType);

public interface ISerialPortScanner
{
    IReadOnlyList<SerialPortInfo> Scan();
}

/// <summary>Lists COM ports with their USB ids (WMI Win32_PnPEntity) and recognises known masters.</summary>
public sealed partial class SerialPortScanner : ISerialPortScanner
{
    public IReadOnlyList<SerialPortInfo> Scan()
    {
        var result = new List<SerialPortInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%' AND Present = TRUE");
        foreach (var device in searcher.Get().Cast<ManagementObject>())
        {
            var info = Parse(device["Name"] as string, device["PNPDeviceID"] as string);
            if (info != null) result.Add(info);
        }

        return result.OrderBy(p => PortNumber(p.PortName)).ToList();
    }

    /// <summary>Builds a <see cref="SerialPortInfo" /> from a WMI name and PnP device id.</summary>
    internal static SerialPortInfo? Parse(string? name, string? pnpDeviceId)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var port = ComName().Match(name);
        if (!port.Success) return null;

        string? vid = null, pid = null;
        if (!string.IsNullOrEmpty(pnpDeviceId))
        {
            var ids = UsbIds().Match(pnpDeviceId);
            if (ids.Success)
            {
                vid = ids.Groups["vid"].Value.ToUpperInvariant();
                pid = ids.Groups["pid"].Value.ToUpperInvariant();
            }
        }

        return new SerialPortInfo(port.Groups[1].Value, name, vid, pid, MasterCatalog.FindByUsbId(vid, pid));
    }

    private static int PortNumber(string portName) =>
        int.TryParse(portName.AsSpan(3), out var n) ? n : int.MaxValue;

    [GeneratedRegex(@"\((COM\d+)\)")]
    private static partial Regex ComName();

    // USB\VID_2843&PID_0201\...  or  FTDIBUS\VID_0403+PID_6001+...
    [GeneratedRegex(@"VID_(?<vid>[0-9A-Fa-f]{4})[&+]PID_(?<pid>[0-9A-Fa-f]{4})")]
    private static partial Regex UsbIds();
}
