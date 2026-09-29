using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CompatibilityLab.Engine;

public static class JsonUtil
{
    public static readonly JsonNodeOptions NodeOpts = new() { PropertyNameCaseInsensitive = false };
    public static readonly JsonDocumentOptions DocOpts = new() { AllowTrailingCommas = false };

    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static JsonNode Parse(string text) =>
        JsonNode.Parse(text, NodeOpts, new JsonDocumentOptions())
        ?? throw new InvalidOperationException("null document");

    // Deterministic key-sorted canonicalization for hashing / deep equality.
    public static string Canonical(JsonNode? node)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
            WriteCanonical(w, node);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter w, JsonNode? node)
    {
        switch (node)
        {
            case null:
                w.WriteNullValue();
                break;
            case JsonObject obj:
                w.WriteStartObject();
                foreach (var kv in obj.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    w.WritePropertyName(kv.Key);
                    WriteCanonical(w, kv.Value);
                }
                w.WriteEndObject();
                break;
            case JsonArray arr:
                w.WriteStartArray();
                foreach (var item in arr) WriteCanonical(w, item);
                w.WriteEndArray();
                break;
            case JsonValue v:
                WriteValue(w, v);
                break;
        }
    }

    private static void WriteValue(Utf8JsonWriter w, JsonValue v)
    {
        if (v.TryGetValue(out JsonElement el))
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String: w.WriteStringValue(el.GetString()); break;
                case JsonValueKind.Number: w.WriteRawValue(el.GetRawText()); break;
                case JsonValueKind.True: w.WriteBooleanValue(true); break;
                case JsonValueKind.False: w.WriteBooleanValue(false); break;
                case JsonValueKind.Null: w.WriteNullValue(); break;
                default: w.WriteRawValue(el.GetRawText()); break;
            }
        }
        else if (v.TryGetValue(out string? s)) w.WriteStringValue(s);
        else if (v.TryGetValue(out bool b)) w.WriteBooleanValue(b);
        else if (v.TryGetValue(out int i)) w.WriteNumberValue(i);
        else if (v.TryGetValue(out long l)) w.WriteNumberValue(l);
        else if (v.TryGetValue(out double d)) w.WriteNumberValue(d);
        else if (v.TryGetValue(out decimal dec)) w.WriteNumberValue(dec);
        else w.WriteStringValue(v.ToJsonString());
    }

    public static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static bool DeepEqual(JsonNode? a, JsonNode? b) => Canonical(a) == Canonical(b);

    // Secrets are ingested to an opaque token so the literal never lands in storage.
    public const string SecretTokenPrefix = "__SECRET__:";
    public static string MakeSecretToken(string raw) => SecretTokenPrefix + Sha256Hex(raw ?? "");
    public static string? AsString(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue(out string? s) && s is not null) return s;
        if (v.TryGetValue(out JsonElement el) && el.ValueKind == JsonValueKind.String) return el.GetString();
        return null;
    }

    public static bool IsSecretToken(JsonNode? node)
    {
        var s = AsString(node);
        return s is not null && s.StartsWith(SecretTokenPrefix);
    }
    public static string? TokenDigest(string? token) =>
        token is not null && token.StartsWith(SecretTokenPrefix) ? token[SecretTokenPrefix.Length..] : null;
}
