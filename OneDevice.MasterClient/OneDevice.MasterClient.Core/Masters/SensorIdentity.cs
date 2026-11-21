namespace OneDevice.MasterClient.Core.Masters;

/// <summary>
///     Identity of an IO-Link device, read without an IODD: direct parameter page 1 (index 0) plus the standard
///     identification parameters (indices 0x10..0x18).
/// </summary>
public sealed record SensorIdentity
{
    public required int VendorId { get; init; }
    public required int DeviceId { get; init; }

    /// <summary>IO-Link revision, e.g. "1.1".</summary>
    public required string IoLinkRevision { get; init; }

    /// <summary>Length of the input process data in bits.</summary>
    public required int ProcessDataInBits { get; init; }

    public string? VendorName { get; init; }
    public string? VendorText { get; init; }
    public string? ProductName { get; init; }
    public string? ProductId { get; init; }
    public string? ProductText { get; init; }
    public string? SerialNumber { get; init; }
    public string? HardwareRevision { get; init; }
    public string? FirmwareRevision { get; init; }
    public string? ApplicationSpecificTag { get; init; }

    /// <summary>
    ///     Parses direct parameter page 1 (16 bytes, ISDU index 0): RevisionID at 4, ProcessDataIn at 5,
    ///     VendorID at 7..8, DeviceID at 9..11 (IO-Link spec, big-endian).
    /// </summary>
    public static SensorIdentity? FromDirectParameterPage(byte[] page)
    {
        if (page is not { Length: >= 12 }) return null;
        var revision = page[4];
        return new SensorIdentity
        {
            VendorId = page[7] << 8 | page[8],
            DeviceId = page[9] << 16 | page[10] << 8 | page[11],
            IoLinkRevision = $"{revision >> 4}.{revision & 0x0F}",
            ProcessDataInBits = DecodeProcessDataLength(page[5])
        };
    }

    /// <summary>
    ///     ProcessDataIn byte: bit 6 = length in octets (then Length+1 octets), otherwise Length is in bits.
    ///     Example: 0xC3 = 4 octets = 32 bits.
    /// </summary>
    internal static int DecodeProcessDataLength(byte value)
    {
        var length = value & 0x1F;
        var inOctets = (value & 0x40) != 0;
        return inOctets ? (length + 1) * 8 : length;
    }
}

/// <summary>Standard identification parameters (IO-Link spec, ISDU indices).</summary>
internal static class StandardIndex
{
    public const ushort DirectParameterPage1 = 0x00;
    public const ushort VendorName = 0x10;
    public const ushort VendorText = 0x11;
    public const ushort ProductName = 0x12;
    public const ushort ProductId = 0x13;
    public const ushort ProductText = 0x14;
    public const ushort SerialNumber = 0x15;
    public const ushort HardwareRevision = 0x16;
    public const ushort FirmwareRevision = 0x17;
    public const ushort ApplicationSpecificTag = 0x18;
}
