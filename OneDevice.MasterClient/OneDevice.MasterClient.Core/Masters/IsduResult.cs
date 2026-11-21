namespace OneDevice.MasterClient.Core.Masters;

/// <summary>Outcome of one ISDU (acyclic parameter) read or write.</summary>
/// <param name="Success">True when the device accepted the request.</param>
/// <param name="Data">Read data (empty for writes and failures).</param>
/// <param name="ErrorCode">ISDU error code, 0 = none (e.g. 0x80).</param>
/// <param name="AdditionalCode">ISDU additional code (e.g. 0x11).</param>
/// <param name="Status">Master/driver status text.</param>
public sealed record IsduResult(bool Success, byte[] Data, byte ErrorCode, byte AdditionalCode, string Status)
{
    /// <summary>Combined ISDU error, e.g. 0x8011; 0 when none.</summary>
    public int IsduError => ErrorCode << 8 | AdditionalCode;

    /// <summary>Human readable error (IO-Link spec, ISDU error types), or null on success.</summary>
    public string? ErrorText => Success ? null : DescribeIsduError(IsduError) ?? Status;

    public string DataHex => Convert.ToHexString(Data);

    public static IsduResult Failed(string status) => new(false, [], 0, 0, status);

    public static string? DescribeIsduError(int error) => error switch
    {
        0 => null,
        0x8000 => "Device application error, no details",
        0x8011 => "Index not available",
        0x8012 => "Subindex not available",
        0x8020 => "Service temporarily not available",
        0x8021 => "Service temporarily not available (local control)",
        0x8022 => "Service temporarily not available (device control)",
        0x8023 => "Access denied",
        0x8030 => "Parameter value out of range",
        0x8031 => "Parameter value above limit",
        0x8032 => "Parameter value below limit",
        0x8033 => "Parameter length overrun",
        0x8034 => "Parameter length underrun",
        0x8035 => "Function not available",
        0x8036 => "Function temporarily unavailable",
        0x8040 => "Invalid parameter set",
        0x8041 => "Inconsistent parameter set",
        0x8082 => "Application not ready",
        _ => $"ISDU error 0x{error:X4}"
    };
}
