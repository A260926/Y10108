using System.Text.Json.Nodes;

namespace CompatibilityLab.Engine;

// Coerces JSON DOM values to schema primitive types and reports mismatches.
public static class PrimitiveReader
{
    public static string? KindOf(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        // Prefer the backing JsonElement kind; JsonValue.TryGetValue(out string)
        // is unreliable for values produced via JsonNode.Parse.
        if (v.TryGetValue(out System.Text.Json.JsonElement el))
        {
            return el.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => "string",
                System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => "bool",
                System.Text.Json.JsonValueKind.Number => NumberKind(el),
                _ => null
            };
        }
        if (v.TryGetValue(out string? _)) return "string";
        if (v.TryGetValue(out bool _)) return "bool";
        if (v.TryGetValue(out long _) || v.TryGetValue(out int _)) return "long";
        if (v.TryGetValue(out double _) || v.TryGetValue(out decimal _)) return "double";
        return null;
    }

    private static string NumberKind(System.Text.Json.JsonElement el)
    {
        if (el.TryGetInt64(out _)) return el.ValueKind == System.Text.Json.JsonValueKind.Number && IsIntegralText(el) ? "int" : "long";
        return "double";
    }

    private static bool IsIntegralText(System.Text.Json.JsonElement el)
    {
        var raw = el.GetRawText();
        return !raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E');
    }

    public static bool IsAssignable(JsonNode? node, string type)
    {
        var kind = KindOf(node);
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
