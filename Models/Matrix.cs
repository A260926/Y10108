namespace CompatibilityLab.Models;

public sealed class CellResult
{
    public string WriterVersion { get; set; } = "";
    public string ReaderVersion { get; set; } = "";
    public string Status { get; set; } = "pending"; // ok|error

    // Encoding summary (writer side)
    public string EncodeSha256 { get; set; } = "";
    public long EncodeByteLength { get; set; }

    // Decoded fields + unknown sidecar
    public List<DecodedFieldInfo> DecodedFields { get; set; } = new();
    public List<UnknownFieldInfo> UnknownSidecar { get; set; } = new();

    // Defaults used
    public List<string> DefaultsUsed { get; set; } = new();

    // Errors
    public List<string> ErrorCodes { get; set; } = new();

    // Field provenance
    public List<FieldSourceInfo> FieldSources { get; set; } = new();

    // Explanations of schema evolution, not merely a boolean
    public List<FieldChange> Changes { get; set; } = new();
    public bool Compatible { get; set; }

    // Re-encode through the intermediate reader
    public string EffectiveUnknownPolicy { get; set; } = "";
    public string? ReEncodedSha256 { get; set; }
    public long? ReEncodedByteLength { get; set; }
    public bool Reversible { get; set; }
    public string? ReversibilityNote { get; set; }

    // Byte drilldown
    public List<EncodedFieldInfo> EncodeFields { get; set; } = new();
    public string EncodedJson { get; set; } = "";     // redacted (secrets hidden)
    public string? ReEncodedJson { get; set; }

    // Secret existence + digest only
    public List<SecretSummary> Secrets { get; set; } = new();

    public DateTimeOffset ComputedAt { get; set; }
}

public sealed class SecretSummary
{
    public string Path { get; set; } = "";
    public bool Present { get; set; }
    public string? Digest { get; set; }
}

public sealed class SampleSummary
{
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long ByteLength { get; set; }
    public int SecretCount { get; set; }
    public string RedactedJson { get; set; } = "";
}

public sealed class RunSnapshotVersion
{
    public string Version { get; set; } = "";
    public string SchemaJson { get; set; } = "";
    public string UnknownPolicy { get; set; } = "";
    public string SchemaSha256 { get; set; } = "";
}

public sealed class RunSnapshot
{
    public List<RunSnapshotVersion> Versions { get; set; } = new();
    public List<SampleSummary> Samples { get; set; } = new();
}
