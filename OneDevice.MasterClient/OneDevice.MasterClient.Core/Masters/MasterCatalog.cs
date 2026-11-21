using OneDevice.Master.IoLink.Products;

namespace OneDevice.MasterClient.Core.Masters;

/// <summary>A supported IO-Link master model.</summary>
/// <param name="Key">Stable identifier stored in the configuration.</param>
/// <param name="DisplayName">Name shown to the customer.</param>
/// <param name="PortCount">Number of IO-Link ports.</param>
/// <param name="UsbVendorId">USB vendor id used to recognise the master on a COM port (hex, upper case).</param>
/// <param name="UsbProductId">USB product id (hex, upper case).</param>
public sealed record MasterTypeInfo(
    string Key,
    string DisplayName,
    int PortCount,
    string? UsbVendorId,
    string? UsbProductId);

/// <summary>Masters the agent can drive, and how to create their HAL.</summary>
public static class MasterCatalog
{
    public static readonly MasterTypeInfo TmgUsbV2 =
        new("TmgUsbV2", "TMG IO-Link USB Master V2", 1, "2843", "0201");

    public static IReadOnlyList<MasterTypeInfo> All { get; } = [TmgUsbV2];

    public static MasterTypeInfo? Find(string? key) =>
        All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Master type whose USB ids match, or null.</summary>
    public static MasterTypeInfo? FindByUsbId(string? vendorId, string? productId) =>
        All.FirstOrDefault(t =>
            t.UsbVendorId != null &&
            string.Equals(t.UsbVendorId, vendorId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.UsbProductId, productId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Creates the HAL for a master type.</summary>
    public static IMasterHAL CreateHal(MasterTypeInfo type) => type.Key switch
    {
        "TmgUsbV2" => new TmgMaster2 { ProcessDataReadDelayMs = 50 },
        _ => throw new NotSupportedException($"Master type '{type.Key}' is not supported.")
    };
}
