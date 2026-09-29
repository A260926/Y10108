using System.Text.Json.Nodes;

namespace CompatibilityLab.Models;

// User-defined schema. Stored verbatim as JSON text in SQLite and re-parsed per use,
// so published versions are an immutable byte snapshot.
// Supported shapes: record (root + nested), enum, tagged union.
public sealed class FieldDef
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";            // string|int|long|double|bool|record|enum|union
    public bool Required { get; set; } = true;
    public JsonNode? Default { get; set; }            // default value; null + HasDefault==false => none
    public bool HasDefault { get; set; }
    public bool Secret { get; set; }
    public List<string> Aliases { get; set; } = new(); // ONLY declared aliases inherit a renamed field
    public List<FieldDef>? Fields { get; set; }        // Type == record
    public List<string>? EnumValues { get; set; }      // Type == enum
    public string? TagField { get; set; }              // Type == union
    public List<UnionBranchDef>? Branches { get; set; }// Type == union
}

public sealed class UnionBranchDef
{
    public string Tag { get; set; } = "";
    public FieldDef Record { get; set; } = new() { Type = "record" };
}

public sealed class SchemaDefinition
{
    public string RootType { get; set; } = "record";
    public List<FieldDef> Fields { get; set; } = new();
    // Version-level option for unknown fields passing through an intermediate reader.
    // A run may override; the effective choice is part of matrix identity.
    public string UnknownPolicy { get; set; } = "preserve"; // preserve | drop
}
