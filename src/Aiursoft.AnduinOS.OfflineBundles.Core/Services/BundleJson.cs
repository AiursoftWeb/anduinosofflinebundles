using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aiursoft.AnduinOS.OfflineBundles.Core.Services;

public static class BundleJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 32
    };

    public static T Read<T>(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicateKeys(document.RootElement);
        return document.Deserialize<T>(Options) ?? throw new InvalidDataException("The JSON document is empty.");
    }

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options) + "\n";

    public static async Task<string> HashAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidDataException("Explicit null values are not supported; omit optional fields.");
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name))
                {
                    throw new InvalidDataException($"Duplicate JSON property: {property.Name}");
                }
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                RejectDuplicateKeys(child);
            }
        }
    }
}
