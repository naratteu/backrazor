using System.Text.Json;
using System.Text.Json.Serialization;

namespace BackRazor.EdgeNode;

public sealed record BrokerOptions(string Host = "127.0.0.1", int Port = 1883, string GroupId = "BackRazor", string NodeId = "edge-1", bool UseTls = false, string? UserName = null, string? Password = null);
public sealed record DeviceSpec(string Id, string? Name = null, int IntervalMs = 1000, int StopDelayMs = 0);
public sealed record Configuration(BrokerOptions Broker, DeviceSpec[] Devices)
{
    public static Configuration Parse(string text)
    {
        var value = JsonSerializer.Deserialize(text, EdgeJsonContext.Default.Configuration)
            ?? throw new JsonException("Configuration must be an object.");
        if (value.Broker is null || string.IsNullOrWhiteSpace(value.Broker.Host) || value.Broker.Port is < 1 or > 65535)
            throw new JsonException("A valid broker host and port are required.");
        ValidateId(value.Broker.GroupId);
        ValidateId(value.Broker.NodeId);
        if (value.Devices is null) throw new JsonException("devices must be an array.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in value.Devices)
        {
            if (device is null) throw new JsonException("Device entries cannot be null.");
            ValidateId(device.Id);
            if (!ids.Add(device.Id)) throw new JsonException($"Duplicate device id: {device.Id}");
            if (device.IntervalMs is < 50 or > 60000 || device.StopDelayMs is < 0 or > 5000)
                throw new JsonException($"Device {device.Id}: intervalMs must be 50..60000 and stopDelayMs 0..5000.");
        }
        return value;
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id != id.Trim() || id.IndexOfAny(['/', '+', '#', '\0']) >= 0)
            throw new JsonException("Sparkplug identities must be nonempty and cannot contain /, +, #, or NUL.");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(Configuration))]
[JsonSerializable(typeof(IList<string>))]
public partial class EdgeJsonContext : JsonSerializerContext;
