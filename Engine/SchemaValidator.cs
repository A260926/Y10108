using System.Text.Json;
using CompatibilityLab.Models;

namespace CompatibilityLab.Engine;

public sealed class SchemaValidationException : Exception
{
    public SchemaValidationException(string message) : base(message) { }
}

public static class SchemaValidator
{
    private static readonly HashSet<string> Primitives =
        new() { "string", "int", "long", "double", "bool" };

    public static SchemaDefinition Parse(string json)
    {
        SchemaDefinition? def;
        try
        {
            def = JsonSerializer.Deserialize<SchemaDefinition>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch (JsonException ex)
        {
            throw new SchemaValidationException("schema JSON 无法解析: " + ex.Message);
        }
        if (def is null) throw new SchemaValidationException("schema 为空");
        Validate(def);
        return def;
    }

    public static string CanonicalJson(SchemaDefinition def) =>
        JsonUtil.Canonical(JsonSerializer.SerializeToNode(def, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));

    public static void Validate(SchemaDefinition def)
    {
        if (def.RootType != "record")
            throw new SchemaValidationException("顶层 rootType 必须为 record");
        if (def.UnknownPolicy is not ("preserve" or "drop"))
            throw new SchemaValidationException("unknownPolicy 必须为 preserve 或 drop");
        if (def.Fields is null || def.Fields.Count == 0)
            throw new SchemaValidationException("record 至少需要一个字段");
        var seen = new HashSet<string>();
        foreach (var f in def.Fields) ValidateField(f, "", seen);
    }

    private static void ValidateField(FieldDef f, string path, HashSet<string> seen)
    {
        // A declared default implies HasDefault even if the JSON omits hasDefault.
        if (f.Default is not null) f.HasDefault = true;

        var p = path + "/" + f.Name;
        if (string.IsNullOrWhiteSpace(f.Name))
            throw new SchemaValidationException($"字段名不能为空: {path}");
        if (!seen.Add(f.Name))
            throw new SchemaValidationException($"字段重复: {p}");
        if (f.HasDefault && f.Required)
            throw new SchemaValidationException($"{p} 不能同时 required=true 且声明 default");
        if (f.Aliases is null) f.Aliases = new();
        foreach (var a in f.Aliases)
            if (string.IsNullOrWhiteSpace(a))
                throw new SchemaValidationException($"{p} 存在空 alias");
        switch (f.Type)
        {
            case "record":
                if (f.Fields is null || f.Fields.Count == 0)
                    throw new SchemaValidationException($"record 字段 {p} 需要 fields");
                var childSeen = new HashSet<string>();
                foreach (var c in f.Fields) ValidateField(c, p, childSeen);
                break;
            case "enum":
                if (f.EnumValues is null || f.EnumValues.Count == 0)
                    throw new SchemaValidationException($"enum 字段 {p} 需要 enumValues");
                if (f.EnumValues.Distinct().Count() != f.EnumValues.Count)
                    throw new SchemaValidationException($"enum {p} 存在重复枚举值");
                break;
            case "union":
                if (string.IsNullOrWhiteSpace(f.TagField))
                    throw new SchemaValidationException($"union 字段 {p} 需要 tagField");
                if (f.Branches is null || f.Branches.Count == 0)
                    throw new SchemaValidationException($"union 字段 {p} 至少需要一个 branch");
                var tags = new HashSet<string>();
                var bi = 0;
                foreach (var b in f.Branches)
                {
                    if (string.IsNullOrWhiteSpace(b.Tag))
                        throw new SchemaValidationException($"union {p} branch[{bi}] 缺少 tag");
                    if (!tags.Add(b.Tag))
                        throw new SchemaValidationException($"union {p} tag 重复: {b.Tag}");
                    b.Record.Type = "record";
                    if (string.IsNullOrWhiteSpace(b.Record.Name)) b.Record.Name = "$" + b.Tag;
                    var bs = new HashSet<string>();
                    ValidateField(b.Record, p, bs);
                    bi++;
                }
                break;
            default:
                if (!Primitives.Contains(f.Type))
                    throw new SchemaValidationException($"{p} 的类型 {f.Type} 不受支持");
                if (f.Secret && f.Type != "string")
                    throw new SchemaValidationException($"{p} secret 仅支持 string 字段");
                if (f.HasDefault && !ValueMatchesType(f.Default, f.Type))
                    throw new SchemaValidationException($"{p} 的 default 与类型 {f.Type} 不符");
                break;
        }
    }

    public static bool ValueMatchesType(System.Text.Json.Nodes.JsonNode? node, string type)
    {
        var kind = PrimitiveReader.KindOf(node);
        if (kind is null) return false;
        return type switch
        {
            "string" => kind == "string",
            "bool" => kind == "bool",
            "double" => kind is "double" or "int" or "long",
            "int" or "long" => kind is "int" or "long",
            _ => false
        };
    }
}
