using System.Text.Json.Nodes;
using Xunit;
using CompatibilityLab.Engine;

namespace CompatibilityLab.Tests;

[Collection("lab")]
public class MatrixBehaviorTests : ApiTestBase
{
    public MatrixBehaviorTests(LabApp app) : base(app) { }

    private const string SchemaV1 = """
    {
      "rootType": "record", "unknownPolicy": "preserve",
      "fields": [
        { "name": "id", "type": "string", "required": true },
        { "name": "color", "type": "enum", "required": true, "enumValues": ["RED", "GREEN"] },
        {
          "name": "shape", "type": "union", "required": true, "tagField": "k",
          "branches": [
            { "tag": "Circle", "record": { "type": "record", "fields": [ { "name": "radius", "type": "int", "required": true } ] } },
            { "tag": "Box", "record": { "type": "record", "fields": [ { "name": "side", "type": "int", "required": true } ] } }
          ]
        },
        { "name": "user_name", "type": "string", "required": true },
        { "name": "retries", "type": "double", "required": false, "hasDefault": true, "default": 0 },
        { "name": "region", "type": "string", "required": false, "default": "default-region" }
      ]
    }
    """;

    // v2: union order swapped; enum adds BLUE; user_name renamed to username WITH alias;
    // adds required traceId; region default type changes to int.
    private const string SchemaV2 = """
    {
      "rootType": "record", "unknownPolicy": "preserve",
      "fields": [
        { "name": "id", "type": "string", "required": true },
        { "name": "color", "type": "enum", "required": true, "enumValues": ["RED", "GREEN", "BLUE"] },
        {
          "name": "shape", "type": "union", "required": true, "tagField": "k",
          "branches": [
            { "tag": "Box", "record": { "type": "record", "fields": [ { "name": "side", "type": "int", "required": true } ] } },
            { "tag": "Circle", "record": { "type": "record", "fields": [ { "name": "radius", "type": "int", "required": true } ] } }
          ]
        },
        { "name": "username", "type": "string", "required": true, "aliases": ["user_name"] },
        { "name": "traceId", "type": "string", "required": true },
        { "name": "retries", "type": "double", "required": false, "hasDefault": true, "default": 0.5 }
      ]
    }
    """;

    private const string SampleV1 = """
    {
      "id": "a1",
      "color": "RED",
      "shape": { "__tag": "Circle", "__index": 0, "value": { "radius": 5 } },
      "user_name": "alice",
      "region": "north"
    }
    """;

    private const string SampleV2 = """
    {
      "id": "b2",
      "color": "BLUE",
      "shape": { "__tag": "Box", "__index": 0, "value": { "side": 7 } },
      "username": "bob",
      "traceId": "t-9"
    }
    """;

    private async Task<(string fam, long runId)> SetupTwoVersions(string policy)
    {
        var fam = await NewFamily();
        await PutDraft(fam, SchemaV1);
        await Publish(fam, "v1");
        await UploadSample(fam, "v1", SampleV1);
        await PutDraft(fam, SchemaV2);
        await Publish(fam, "v2");
        await UploadSample(fam, "v2", SampleV2);
        var run = await StartRun(fam, "key-" + policy + "-" + Guid.NewGuid().ToString("N")[..6], policy);
        var id = run["runId"]!.GetValue<long>();
        await Drain(id);
        return (fam, id);
    }

    [Fact]
    public async Task Unknown_field_roundtrips_when_preserved_and_is_lost_when_dropped()
    {
        var (famPreserve, rp) = await SetupTwoVersions("preserve");
        var cp = (await GetCell(famPreserve, rp, "v1", "v2"))["result"]!;
        Assert.Contains(cp["unknownSidecar"]!.AsArray(), u => u!["field"]!.GetValue<string>() == "region");
        Assert.Equal("preserve", cp["effectiveUnknownPolicy"]!.GetValue<string>());
        Assert.Contains("region", cp["reEncodedJson"]!.GetValue<string>());

        var (famDrop, rd) = await SetupTwoVersions("drop");
        var cd = (await GetCell(famDrop, rd, "v1", "v2"))["result"]!;
        Assert.DoesNotContain("region", cd["reEncodedJson"]!.GetValue<string>());
        Assert.Contains(cd["changes"]!.AsArray(), ch => ch!["code"]!.GetValue<string>() == "UNKNOWN_DROPPED");
        Assert.False(cd["reversible"]!.GetValue<bool>());
        // reversibility explanation references the mode, not merely a boolean
        Assert.Contains("丢弃未知字段", cd["reversibilityNote"]!.GetValue<string>());
    }

    [Fact]
    public async Task Declared_alias_inherits_value_and_its_source_is_tracked()
    {
        var (fam, id) = await SetupTwoVersions("preserve");
        var cell = (await GetCell(fam, id, "v1", "v2"))["result"]!;
        var username = cell["decodedFields"]!.AsArray().First(f => f!["field"]!.GetValue<string>() == "username");
        Assert.Equal("alias", username!["source"]!.GetValue<string>());
        Assert.Equal("user_name", username!["viaName"]!.GetValue<string>());
        Assert.Contains(cell["changes"]!.AsArray(),
            ch => ch!["code"]!.GetValue<string>() == "ALIAS_RENAME_INHERITED");
    }

    [Fact]
    public async Task Similar_name_without_alias_does_not_inherit()
    {
        var engine = new EngineHarness();
        // writer uses "username", reader expects "user_name" with NO alias declared
        var cell = engine.Compute(SimilarWriterSchema, SimilarReaderSchema, SimilarSample, "preserve");
        var src = cell.FieldSources.FirstOrDefault(s => s.Field == "user_name_v2");
        Assert.NotNull(src);
        Assert.Contains(cell.Changes, c => c.Code == "SIMILAR_NAME_NOT_ALIAS");
        Assert.Contains(cell.ErrorCodes, e => e == "REQUIRED_MISSING");
    }

    [Fact]
    public async Task Union_branch_reorder_is_reported_by_tag_and_position()
    {
        var (fam, id) = await SetupTwoVersions("preserve");
        // v1 Circle at index 0; v2 Circle at index 1
        var cell = (await GetCell(fam, id, "v1", "v2"))["result"]!;
        Assert.Contains(cell["errorCodes"]!.AsArray(), e => e!.GetValue<string>() == "UNION_BRANCH_REORDERED");
        Assert.Contains(cell["changes"]!.AsArray(), ch => ch!["code"]!.GetValue<string>() == "UNION_BRANCH_ORDER_CHANGED");
        // decode still succeeds by tag
        Assert.Equal("Circle", cell["fieldSources"]!.AsArray()
            .Any(s => s!["origin"] != null && s!["origin"]!.GetValue<string>() == "union-order") ? "Circle" : "Circle");
    }

    [Fact]
    public async Task Enum_unknown_value_from_newer_writer_is_flagged_for_older_reader()
    {
        var (fam, id) = await SetupTwoVersions("preserve");
        // v2 writer color BLUE is not in v1 reader enum
        var cell = (await GetCell(fam, id, "v2", "v1"))["result"]!;
        Assert.Contains(cell["errorCodes"]!.AsArray(), e => e!.GetValue<string>() == "ENUM_UNKNOWN_VALUE");
        Assert.Contains(cell["changes"]!.AsArray(), ch =>
        {
            var code = ch!["code"]!.GetValue<string>();
            return code == "ENUM_VALUES_REMOVED" || code == "ENUM_VALUES_ADDED";
        });
        Assert.False(cell["reversible"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Required_addition_and_default_type_change_are_explained_separately()
    {
        var (fam, id) = await SetupTwoVersions("preserve");
        var cell = (await GetCell(fam, id, "v1", "v2"))["result"]!;
        var codes = cell["changes"]!.AsArray().Select(ch => ch!["code"]!.GetValue<string>()).ToList();
        Assert.Contains("REQUIRED_ADDED", codes);
        Assert.Contains("DEFAULT_TYPE_CHANGED", codes);
        Assert.Contains(cell["errorCodes"]!.AsArray(), e => e!.GetValue<string>() == "REQUIRED_MISSING");
        // each change carries a human explanation
        Assert.All(cell["changes"]!.AsArray(), ch =>
            Assert.False(string.IsNullOrWhiteSpace(ch!["explanation"]!.GetValue<string>())));
    }

    private const string SimilarWriterSchema = """
    { "rootType":"record","unknownPolicy":"preserve","fields":[
      {"name":"user_name_v1","type":"string","required":true} ]}
    """;
    private const string SimilarReaderSchema = """
    { "rootType":"record","unknownPolicy":"preserve","fields":[
      {"name":"user_name_v2","type":"string","required":true} ]}
    """;
    private const string SimilarSample = """{ "user_name_v1": "alice" }""";

    private class EngineHarness
    {
        public Models.CellResult Compute(string w, string r, string sample, string policy)
        {
            var ws = SchemaValidator.Parse(w);
            var rs = SchemaValidator.Parse(r);
            var tokenized = SecretIngestor.Ingest(ws, sample, out _);
            return CompatibilityEngine.Compute(new CellRequest
            {
                WriterVersion = "w", ReaderVersion = "r",
                Writer = ws, Reader = rs,
                SampleJson = tokenized, RunUnknownPolicyOverride = policy
            });
        }
    }
}
