using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ErpWeb.Core.StockLedger;

public static class StockPostingFingerprint
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static StockPostingEvidence Create(object semanticRequest, object sourceSnapshot)
    {
        var requestJson = Canonicalize(JsonSerializer.Serialize(semanticRequest, JsonOptions));
        var snapshotJson = Canonicalize(JsonSerializer.Serialize(sourceSnapshot, JsonOptions));
        return new(
            Hash(requestJson),
            snapshotJson,
            Hash(snapshotJson));
    }

    public static string Canonicalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonical(writer, document.RootElement);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string Hash(string canonicalJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

public sealed record StockPostingEvidence(
    string RequestFingerprint,
    string SourceSnapshotJson,
    string SourceSnapshotHash);
