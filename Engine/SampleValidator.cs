using System.Text.Json.Nodes;
using CompatibilityLab.Models;

namespace CompatibilityLab.Engine;

// Validates that an uploaded sample conforms to its version schema well enough to
// be encoded. Uses the decoder's rules on the same-version pair.
public static class SampleValidator
{
    public static void Validate(SchemaDefinition schema, string tokenizedJson)
    {
        // Same-version round trip: any hard error means the sample is invalid.
        var encoded = Encoder.Encode(schema, JsonUtil.Parse(tokenizedJson), redactSecrets: false);
        var (_, report) = Decoder.Decode(schema, encoded.Json,
            new DecodeContext { WriterVersion = "sample", ReaderVersion = "sample" });
        var hard = report.ErrorCodes.Where(IsHard).ToList();
        if (hard.Count > 0)
            throw new SchemaValidationException("样例不符合 schema: " + string.Join(", ", hard));
    }

    private static bool IsHard(string code) => code is
        "INVALID_JSON" or "ROOT_NOT_OBJECT" or "REQUIRED_MISSING" or "TYPE_MISMATCH"
        or "ENUM_UNKNOWN_VALUE" or "UNION_UNKNOWN_BRANCH" or "SECRET_NOT_TOKENIZED";
}
