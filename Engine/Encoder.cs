using System.Text.Json.Nodes;
using CompatibilityLab.Models;

namespace CompatibilityLab.Engine;

public sealed class EncodedDocument
{
    public string Json { get; set; } = "";
    public EncodeReport Report { get; set; } = new();
}

public static class Encoder
{
    // Encode a canonical value tree (JsonNode) against the writer schema.
    public static EncodedDocument Encode(SchemaDefinition schema, JsonNode root, bool redactSecrets = true)
    {
        var w = new JsonByteWriter(indent: true);
        var info = new List<EncodedFieldInfo>();
        var secrets = new List<SecretSummary>();
        if (root is not JsonObject ro)
            throw new InvalidOperationException("根节点必须是对象");
        WriteRecord(w, schema.Fields, ro.AsObject(), "", info, secrets, redactSecrets);
        var bytes = w.GetBytes();
        var json = System.Text.Encoding.UTF8.GetString(bytes);
        return new EncodedDocument
        {
            Json = json,
            Report = new EncodeReport
            {
                ByteLength = bytes.Length,
                Sha256 = JsonUtil.Sha256Hex(bytes),
                Fields = info
            }
        };
    }

    private static void WriteRecord(JsonByteWriter w, List<FieldDef> fields, JsonObject data,
        string path, List<EncodedFieldInfo> info, List<SecretSummary> secrets, bool redactSecrets)
    {
        w.WriteStartObject();
        bool first = true;
        // Unknown extras that survive through an intermediate reader come after known fields.
        var knownNames = fields.Select(f => f.Name).ToHashSet();
        foreach (var f in fields)
        {
            var fp = path + "/" + f.Name;
            bool present = data.ContainsKey(f.Name);
            JsonNode? val = present ? data[f.Name] : null;

            if (!present && f.HasDefault)
                val = f.Default?.DeepClone();
            if (!present && !f.HasDefault && !f.Required)
                continue; // optional, absent
            if (!present && f.Required)
                continue; // would have failed decoding; emit nothing defensively

            w.MemberSeparator(first);
            first = false;
            int off = w.BeginPropertyValue(f.Name);
            WriteFieldValue(w, f, val, fp, info, secrets, off, redactSecrets);
        }

        // Emit unknown keys that were injected from a preserved sidecar (in schema
        // order, after known fields). These carry no field metadata by design.
        foreach (var kv in data)
        {
            if (knownNames.Contains(kv.Key)) continue;
            w.MemberSeparator(first);
            first = false;
            int uoff = w.BeginPropertyValue(kv.Key);
            w.WriteNode(kv.Value?.DeepClone());
            info.Add(new EncodedFieldInfo
            {
                Path = path + "/" + kv.Key,
                Field = kv.Key,
                Type = "unknown",
                ByteOffset = uoff,
                ByteLength = w.Position - uoff,
                Source = "unknown"
            });
        }
        w.WriteEndObject();
    }

    private static void WriteFieldValue(JsonByteWriter w, FieldDef f, JsonNode? val, string path,
        List<EncodedFieldInfo> info, List<SecretSummary> secrets, int off, bool redactSecrets)
    {
        switch (f.Type)
        {
            case "record":
                if (val is not JsonObject ro) { w.WriteNull(); break; }
                WriteRecord(w, f.Fields!, ro.AsObject(), path, info, secrets, redactSecrets);
                break;
            case "enum":
                // store the symbolic string
                string? sym = JsonUtil.AsString(val);
                w.WriteString(sym ?? "");
                RecordInfo(info, f, path, off, w.Position, "direct", sym);
                break;
            case "union":
                WriteUnion(w, f, val, path, info, secrets, off, redactSecrets);
                break;
            default:
                WritePrimitive(w, f, val, path, info, secrets, off, redactSecrets);
                break;
        }
    }

    private static void WritePrimitive(JsonByteWriter w, FieldDef f, JsonNode? val, string path,
        List<EncodedFieldInfo> info, List<SecretSummary> secrets, int off, bool redactSecrets)
    {
        string source = "direct";
        if (f.Secret)
        {
            string? token = JsonUtil.AsString(val);
            string? digest = JsonUtil.TokenDigest(token);
            secrets.Add(new SecretSummary { Path = path, Present = true, Digest = digest });
            if (redactSecrets)
            {
                // Byte view exposes existence only, never the value/digest.
                w.WriteString("●●●●●●");
                RecordInfo(info, f, path, off, w.Position, "secret-redacted", null, secret: true, digest: digest);
            }
            else
            {
                // Keep the opaque token on the wire (it is not the plaintext secret).
                w.WriteString(token ?? "");
                RecordInfo(info, f, path, off, w.Position, "direct", null, secret: true, digest: digest);
            }
            return;
        }
        w.WriteNode(val);
        RecordInfo(info, f, path, off, w.Position, source, null);
    }

    private static void WriteUnion(JsonByteWriter w, FieldDef f, JsonNode? val, string path,
        List<EncodedFieldInfo> info, List<SecretSummary> secrets, int off, bool redactSecrets)
    {
        // Canonical union node: { "__tag": string, "__index": int, "value": {record} }
        if (val is not JsonObject uo) { w.WriteNull(); return; }
        string tag = JsonUtil.AsString(uo["__tag"]) ?? "";
        int index = uo["__index"]?.AsValue().TryGetValue(out int ix) == true ? ix : -1;
        var branch = f.Branches!.FirstOrDefault(b => b.Tag == tag)
                     ?? (index >= 0 && index < f.Branches!.Count ? f.Branches![index] : null);

        w.WriteStartObject();
        bool fu = true;
        w.MemberSeparator(fu); fu = false;
        int toff = w.BeginPropertyValue("__tag");
        w.WriteString(tag);
        info.Add(new EncodedFieldInfo { Path = path, Field = "__tag", Type = "union-tag", ByteOffset = toff, ByteLength = w.Position - toff, Source = "union-tag" });

        w.MemberSeparator(fu); fu = false;
        int ioff = w.BeginPropertyValue("__index");
        w.WriteRaw((branch is null ? index : f.Branches!.IndexOf(branch)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.Add(new EncodedFieldInfo { Path = path, Field = "__index", Type = "union-index", ByteOffset = ioff, ByteLength = w.Position - ioff, Source = "union-tag" });

        w.MemberSeparator(fu); fu = false;
        int voff = w.BeginPropertyValue("value");
        var valueObj = uo["value"] as JsonObject;
        if (branch is not null && valueObj is not null)
            WriteRecord(w, branch.Record.Fields!, valueObj, path + "[" + tag + "]", info, secrets, redactSecrets);
        else
            w.WriteNode(uo["value"]);
        w.WriteEndObject();

        info.Add(new EncodedFieldInfo { Path = path, Field = f.Name, Type = "union", ByteOffset = off, ByteLength = w.Position - off, Source = "direct" });
    }

    private static void RecordInfo(List<EncodedFieldInfo> info, FieldDef f, string path,
        int off, int end, string source, string? via, bool secret = false, string? digest = null)
    {
        info.Add(new EncodedFieldInfo
        {
            Path = path,
            Field = f.Name,
            Type = f.Type,
            ByteOffset = off,
            ByteLength = end - off,
            Source = source,
            Secret = secret,
            SecretDigest = digest
        });
    }
}
