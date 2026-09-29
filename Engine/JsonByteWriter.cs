using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CompatibilityLab.Engine;

// Minimal deterministic JSON writer over a MemoryStream. Supports recording exact
// [offset,length) byte spans of property values for cell drilldown. Indentation is
// fixed two spaces; object members are emitted in schema/insertion order.
public sealed class JsonByteWriter
{
    private readonly MemoryStream _ms = new();
    private readonly bool _indent;
    private int _depth;

    public JsonByteWriter(bool indent = true) => _indent = indent;

    public int Position => (int)_ms.Length;
    public byte[] GetBytes() => _ms.ToArray();

    private void Raw(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        _ms.Write(b, 0, b.Length);
    }
    private void Pad()
    {
        if (!_indent) return;
        Raw("\n");
        for (int i = 0; i < _depth; i++) Raw("  ");
    }

    public void WriteNode(JsonNode? node)
    {
        switch (node)
        {
            case null: Raw("null"); break;
            case JsonValue v: WriteScalar(v); break;
            case JsonObject o:
                WriteStartObject();
                bool first = true;
                foreach (var kv in o)
                {
                    if (!first) Raw(",");
                    first = false;
                    Pad();
                    WritePropertyName(kv.Key);
                    WriteNode(kv.Value);
                }
                WriteEndObject();
                break;
            case JsonArray a:
                WriteStartArray();
                bool fa = true;
                foreach (var item in a)
                {
                    if (!fa) Raw(",");
                    fa = false;
                    Pad();
                    WriteNode(item);
                }
                WriteEndArray();
                break;
        }
    }

    public void WriteStartObject() { Raw("{"); _depth++; }
    public void WriteEndObject() { _depth--; Pad(); Raw("}"); }
    public void WriteStartArray() { Raw("["); _depth++; }
    public void WriteEndArray() { _depth--; Pad(); Raw("]"); }
    public void MemberSeparator(bool first) { if (!first) Raw(","); Pad(); }
    public void ElementSeparator(bool first) { if (!first) Raw(","); Pad(); }
    public void WritePropertyName(string name) { Raw("\"" + Escape(name) + "\":"); if (_indent) Raw(" "); }

    // Writes a property name and returns the offset where the value begins.
    public int BeginPropertyValue(string name)
    {
        WritePropertyName(name);
        return Position;
    }

    public void WriteString(string s) => Raw("\"" + Escape(s) + "\"");
    public void WriteRaw(string raw) => Raw(raw);
    public void WriteBool(bool b) => Raw(b ? "true" : "false");
    public void WriteNull() => Raw("null");

    private void WriteScalar(JsonValue v)
    {
        if (v.TryGetValue(out string? s)) WriteString(s ?? "");
        else if (v.TryGetValue(out bool b)) WriteBool(b);
        else if (v.TryGetValue(out JsonElement el))
        {
            if (el.ValueKind == JsonValueKind.String) WriteString(el.GetString() ?? "");
            else if (el.ValueKind == JsonValueKind.Null) WriteNull();
            else Raw(el.GetRawText());
        }
        else Raw(v.ToJsonString());
    }

    public static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        foreach (var c in s)
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                    else sb.Append(c);
                    break;
            }
        return sb.ToString();
    }
}
