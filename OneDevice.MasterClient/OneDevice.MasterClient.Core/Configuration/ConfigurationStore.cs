using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneDevice.MasterClient.Core.Configuration;

/// <summary>
///     Loads and saves <see cref="AgentConfiguration" /> as JSON. Secrets (IoT Hub connection string, DPS key) are
///     encrypted with Windows DPAPI (machine scope), so the file cannot be copied to another PC and used there.
/// </summary>
public sealed class ConfigurationStore(string path)
{
    internal const string EncryptedPrefix = "dpapi:";
    private static readonly byte[] Entropy = "OneDevice.MasterClient.v1"u8.ToArray();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _sync = new();

    /// <summary>Default location: %ProgramData%\OneDevice\MasterClient\agent.json.</summary>
    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OneDevice", "MasterClient", "agent.json");

    public string Path { get; } = path;

    public AgentConfiguration Load()
    {
        lock (_sync)
        {
            if (!File.Exists(Path)) return new AgentConfiguration();
            var config = JsonSerializer.Deserialize<AgentConfiguration>(File.ReadAllText(Path), Json)
                         ?? new AgentConfiguration();
            foreach (var cloud in config.Masters.Select(m => m.Cloud))
            {
                cloud.DeviceConnectionString = Unprotect(cloud.DeviceConnectionString);
                cloud.DpsSymmetricKey = Unprotect(cloud.DpsSymmetricKey);
            }

            return config;
        }
    }

    public void Save(AgentConfiguration config)
    {
        lock (_sync)
        {
            // Serialize a copy with encrypted secrets; the caller's object keeps plain values.
            var copy = JsonSerializer.Deserialize<AgentConfiguration>(JsonSerializer.Serialize(config, Json), Json)!;
            foreach (var cloud in copy.Masters.Select(m => m.Cloud))
            {
                cloud.DeviceConnectionString = Protect(cloud.DeviceConnectionString);
                cloud.DpsSymmetricKey = Protect(cloud.DpsSymmetricKey);
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(copy, Json));
            File.Move(temp, Path, overwrite: true);
        }
    }

    internal static string? Protect(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.StartsWith(EncryptedPrefix, StringComparison.Ordinal)) return secret;
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.LocalMachine);
        return EncryptedPrefix + Convert.ToBase64String(cipher);
    }

    internal static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal)) return stored;
        var cipher = Convert.FromBase64String(stored[EncryptedPrefix.Length..]);
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.LocalMachine));
    }
}
