using System.Text.Json.Nodes;
using CompatibilityLab.Models;

namespace CompatibilityLab.Engine;

// Replaces secret string literals with opaque tokens BEFORE persistence, so the
// plaintext secret never touches the database or encoded bytes.
public static class SecretIngestor
{
    public static string Ingest(SchemaDefinition schema, string rawJson, out int secretCount)
    {
        var node = JsonUtil.Parse(rawJson);
        if (node is not JsonObject obj)
            throw new SchemaValidationException("样例根节点必须是对象");
        secretCount = 0;
        var processed = ProcessRecord(schema.Fields, obj, ref secretCount);
        return processed.ToJsonString(JsonUtil.Compact);
    }

    private static JsonObject ProcessRecord(List<FieldDef> fields, JsonObject data, ref int count)
    {
        var result = new JsonObject();
        foreach (var kv in data)
        {
            var f = fields.FirstOrDefault(x => x.Name == kv.Key);
            if (f is null)
            {
                result[kv.Key] = kv.Value?.DeepClone();
                continue;
            }
            result[kv.Key] = ProcessField(f, kv.Value, ref count);
        }
        return result;
    }

    private static JsonNode? ProcessField(FieldDef f, JsonNode? value, ref int count)
    {
        switch (f.Type)
        {
            case "record":
                return value is JsonObject ro ? ProcessRecord(f.Fields!, ro, ref count) : value?.DeepClone();
            case "union":
                return ProcessUnion(f, value, ref count);
            default:
                if (f.Secret && JsonUtil.AsString(value) is string s)
                {
                    count++;
                    return JsonUtil.MakeSecretToken(s);
                }
                return value?.DeepClone();
        }
    }

    private static JsonNode? ProcessUnion(FieldDef union, JsonNode? value, ref int count)
    {
        if (value is not JsonObject uo) return value?.DeepClone();
        var tag = JsonUtil.AsString(uo["__tag"]);
        var clone = new JsonObject
        {
            ["__tag"] = uo["__tag"]?.DeepClone(),
            ["__index"] = uo["__index"]?.DeepClone()
        };
        var branch = union.Branches!.FirstOrDefault(b => b.Tag == tag);
        clone["value"] = uo["value"] is JsonObject vo && branch is not null
            ? ProcessRecord(branch.Record.Fields!, vo, ref count)
            : uo["value"]?.DeepClone();
        return clone;
    }
}
