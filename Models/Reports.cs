using System.Text.Json.Nodes;

namespace CompatibilityLab.Models;

// A single source fact for a decoded/re-encoded field.
public sealed class FieldSourceInfo
{
    public string Path { get; set; } = "";
    public string Field { get; set; } = "";
    public string Origin { get; set; } = "unknown"; // direct|alias|default|unknown|missing|union-tag|union-order
    public string? ViaName { get; set; }
    public string? DeclaredIn { get; set; }
    public string? Note { get; set; }
}

public sealed class FieldChange
{
    public string Path { get; set; } = "";
    public string Code { get; set; } = "";          // machine code
    public string Kind { get; set; } = "";          // required|default|type|alias|enum|union|rename
    public string Explanation { get; set; } = "";  // human-readable, never just a boolean
    public string? From { get; set; }
    public string? To { get; set; }
}

public sealed class EncodedFieldInfo
{
    public string Path { get; set; } = "";
    public string Field { get; set; } = "";
    public string Type { get; set; } = "";
    public int ByteOffset { get; set; }
    public int ByteLength { get; set; }
    public string Source { get; set; } = "";       // direct|alias|default|secret-redacted
    public bool Secret { get; set; }
    public string? SecretDigest { get; set; }
}

public sealed class EncodeReport
{
    public long ByteLength { get; set; }
    public string Sha256 { get; set; } = "";
    public List<EncodedFieldInfo> Fields { get; set; } = new();
}

public sealed class DecodedFieldInfo
{
    public string Path { get; set; } = "";
    public string Field { get; set; } = "";
    public string Type { get; set; } = "";
    public string Source { get; set; } = "";       // direct|alias|default|missing|union-tag
    public string? ViaName { get; set; }
    public bool DefaultUsed { get; set; }
}

public sealed class UnknownFieldInfo
{
    public string Path { get; set; } = "";
    public string Field { get; set; } = "";
    public string RawJson { get; set; } = "";
    public bool SecretLike { get; set; }
}

public sealed class DecodeReport
{
    public List<DecodedFieldInfo> Fields { get; set; } = new();
    public List<UnknownFieldInfo> Unknown { get; set; } = new();
    public List<string> ErrorCodes { get; set; } = new();
    public List<FieldSourceInfo> Sources { get; set; } = new();
    public string? MatchedUnionTag { get; set; }
    public int? MatchedUnionIndex { get; set; }
}
