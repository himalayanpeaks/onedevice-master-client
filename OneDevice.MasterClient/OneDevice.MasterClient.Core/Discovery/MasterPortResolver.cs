using System.Text.RegularExpressions;
using OneDevice.MasterClient.Core.Configuration;

namespace OneDevice.MasterClient.Core.Discovery;

/// <summary>Where a master is plugged in right now: <paramref name="Port" />, or an <paramref name="Error" /> text.</summary>
public sealed record PortResolution(string? Port, string? Error);

/// <summary>
///     Finds a master by its USB identity (UID = vendor id + product id + USB serial number) instead of its COM port.
///     Windows hands out COM numbers per plug-in; two masters can swap ports, but never their serial numbers.
/// </summary>
public static partial class MasterPortResolver
{
    /// <summary>The master's id in the cloud (DPS registration id, IoT Hub device id): "usb-2843-0201-1392765243".</summary>
    public static string ToMasterId(string vendorId, string productId, string serialNumber) =>
        $"usb-{vendorId.ToLowerInvariant()}-{productId.ToLowerInvariant()}-{serialNumber.ToLowerInvariant()}";

    /// <summary>Reads vendor id, product id and serial number back from a master id (serial in lower case).</summary>
    public static bool TryParseMasterId(string? masterId, out string vendorId, out string productId, out string serialNumber)
    {
        var match = MasterId().Match(masterId ?? "");
        vendorId = match.Success ? match.Groups["vid"].Value.ToUpperInvariant() : "";
        productId = match.Success ? match.Groups["pid"].Value.ToUpperInvariant() : "";
        serialNumber = match.Success ? match.Groups["serial"].Value : "";
        return match.Success;
    }

    public static bool HasUsbIdentity(MasterConfiguration master) =>
        !string.IsNullOrEmpty(master.UsbSerialNumber) && !string.IsNullOrEmpty(master.UsbVendorId) &&
        !string.IsNullOrEmpty(master.UsbProductId);

    /// <summary>Whether the port has the master with this USB identity plugged in.</summary>
    public static bool Matches(SerialPortInfo port, string vendorId, string productId, string serialNumber) =>
        string.Equals(port.UsbVendorId, vendorId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(port.UsbProductId, productId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(port.UsbSerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     The COM port of the master now. Masters without a USB identity keep their configured port; a master with
    ///     one is only found on the port where exactly that master is plugged in.
    /// </summary>
    public static PortResolution Resolve(MasterConfiguration master, IReadOnlyList<SerialPortInfo> ports)
    {
        if (!HasUsbIdentity(master)) return new PortResolution(master.Connection, null);
        var port = ports.FirstOrDefault(p => Matches(p, master.UsbVendorId!, master.UsbProductId!, master.UsbSerialNumber!));
        return port != null
            ? new PortResolution(port.PortName, null)
            : new PortResolution(null, $"Master {master.UsbSerialNumber} is not plugged in.");
    }

    /// <summary>
    ///     Fills the USB identity of a registered master from its DPS registration id (configurations saved before the
    ///     identity was stored). Returns true when something was filled in.
    /// </summary>
    public static bool BackfillFromRegistration(MasterConfiguration master)
    {
        if (HasUsbIdentity(master) || master.Cloud.Mode != CloudMode.Dps ||
            !TryParseMasterId(master.Cloud.DpsRegistrationId, out var vid, out var pid, out var serial))
            return false;
        master.UsbVendorId = vid;
        master.UsbProductId = pid;
        master.UsbSerialNumber = serial;
        return true;
    }

    /// <summary>
    ///     Error when a master registered in the cloud would be connected to a different physical master, otherwise
    ///     null.
    /// </summary>
    public static string? CheckRegistrationMatches(MasterConfiguration master)
    {
        if (master.Cloud.Mode != CloudMode.Dps || !HasUsbIdentity(master) ||
            !TryParseMasterId(master.Cloud.DpsRegistrationId, out var vid, out var pid, out var serial))
            return null;
        return string.Equals(vid, master.UsbVendorId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(pid, master.UsbProductId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(serial, master.UsbSerialNumber, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"This master is registered as {master.Cloud.DpsRegistrationId}, but the selected port has master " +
              $"{master.UsbSerialNumber}. Add and register that master separately.";
    }

    [GeneratedRegex("^usb-(?<vid>[0-9a-f]{4})-(?<pid>[0-9a-f]{4})-(?<serial>[a-z0-9._-]{1,64})$", RegexOptions.IgnoreCase)]
    private static partial Regex MasterId();
}
