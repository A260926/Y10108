using CompatibilityLab.Store;

namespace CompatibilityLab.Engine;

public sealed class DemoSeeder
{
    public const string FamilyName = "事件消息 (EventMessage)";

    private readonly LabStore _lab;
    private readonly RunService _runs;
    public DemoSeeder(LabStore lab, RunService runs) { _lab = lab; _runs = runs; }

    public const string V1 = """
    {
      "rootType": "record",
      "unknownPolicy": "preserve",
      "fields": [
        { "name": "id", "type": "string", "required": true },
        { "name": "status", "type": "enum", "required": true, "enumValues": ["DRAFT", "ACTIVE", "ARCHIVED"] },
        { "name": "user_name", "type": "string", "required": true, "aliases": ["username"] },
        {
          "name": "payload", "type": "record", "required": true,
          "fields": [
            { "name": "actor", "type": "string", "required": false, "secret": true },
            {
              "name": "detail", "type": "union", "required": true,
              "tagField": "kind",
              "branches": [
                { "tag": "Text", "record": { "type": "record", "fields": [ { "name": "text", "type": "string", "required": true } ] } },
                { "tag": "Image", "record": { "type": "record", "fields": [ { "name": "url", "type": "string", "required": true } ] } }
              ]
            }
          ]
        },
        { "name": "region", "type": "string", "required": false, "default": "cn-east-1" }
      ]
    }
    """;

    public const string V2 = """
    {
      "rootType": "record",
      "unknownPolicy": "drop",
      "fields": [
        { "name": "id", "type": "string", "required": true },
        { "name": "status", "type": "enum", "required": true, "enumValues": ["ACTIVE", "ARCHIVED", "BLOCKED"] },
        { "name": "user_name", "type": "string", "required": true },
        { "name": "trace_id", "type": "string", "required": true },
        {
          "name": "payload", "type": "record", "required": true,
          "fields": [
            { "name": "actor", "type": "string", "required": false, "secret": true },
            {
              "name": "detail", "type": "union", "required": true,
              "tagField": "kind",
              "branches": [
                { "tag": "Image", "record": { "type": "record", "fields": [ { "name": "url", "type": "string", "required": true } ] } },
                { "tag": "Text", "record": { "type": "record", "fields": [ { "name": "text", "type": "string", "required": true } ] } }
              ]
            }
          ]
        },
        { "name": "retries", "type": "int", "required": false, "hasDefault": true, "default": 3 },
        { "name": "region", "type": "string", "required": false, "default": "us-west-0" }
      ]
    }
    """;

    public const string V3 = """
    {
      "rootType": "record",
      "unknownPolicy": "preserve",
      "fields": [
        { "name": "id", "type": "string", "required": true },
        { "name": "status", "type": "enum", "required": true, "enumValues": ["DRAFT", "ACTIVE", "ARCHIVED", "BLOCKED"] },
        { "name": "username", "type": "string", "required": false, "aliases": ["user_name"] },
        { "name": "retries", "type": "string", "required": false, "hasDefault": true, "default": "3" },
        {
          "name": "payload", "type": "record", "required": true,
          "fields": [
            { "name": "actor", "type": "string", "required": false, "secret": true },
            {
              "name": "detail", "type": "union", "required": true,
              "tagField": "kind",
              "branches": [
                { "tag": "Image", "record": { "type": "record", "fields": [ { "name": "url", "type": "string", "required": true } ] } },
                { "tag": "Text", "record": { "type": "record", "fields": [ { "name": "text", "type": "string", "required": true } ] } }
              ]
            }
          ]
        }
      ]
    }
    """;

    public const string SampleV1 = """
    {
      "id": "evt-1001",
      "status": "ACTIVE",
      "user_name": "alice",
      "payload": {
        "actor": "super-secret-token",
        "detail": { "__tag": "Text", "__index": 0, "value": { "text": "hello world" } }
      },
      "region": "cn-north-2"
    }
    """;

    public const string SampleV2 = """
    {
      "id": "evt-2002",
      "status": "BLOCKED",
      "user_name": "bob",
      "trace_id": "tr-9",
      "payload": {
        "actor": "another-secret",
        "detail": { "__tag": "Image", "__index": 0, "value": { "url": "https://example.com/a.png" } }
      },
      "retries": 7,
      "region": "eu-west-1"
    }
    """;

    public const string SampleV3 = """
    {
      "id": "evt-3003",
      "status": "DRAFT",
      "username": "carol",
      "payload": {
        "actor": "third-secret",
        "detail": { "__tag": "Image", "__index": 0, "value": { "url": "https://example.com/b.png" } }
      }
    }
    """;

    public sealed record SeededIds(string FamilyId, long PreserveRunId, long DropRunId);

    public SeededIds? Seed()
    {
        if (_lab.ListFamilies().Count > 0) return null; // idempotent
        var familyId = _lab.CreateFamily(FamilyName);

        PublishVersion(familyId, "v1", V1, SampleV1);
        PublishVersion(familyId, "v2", V2, SampleV2);
        PublishVersion(familyId, "v3", V3, SampleV3);

        var preserve = _runs.CreateRun(new RunService.CreateRunSpec
        {
            FamilyId = familyId,
            IdempotencyKey = "seed-preserve",
            UnknownPolicy = "preserve"
        });
        var drop = _runs.CreateRun(new RunService.CreateRunSpec
        {
            FamilyId = familyId,
            IdempotencyKey = "seed-drop",
            UnknownPolicy = "drop"
        });
        return new SeededIds(familyId, preserve.result.RunId, drop.result.RunId);
    }

    private void PublishVersion(string familyId, string version, string schema, string sample)
    {
        _lab.PutDraft(familyId, schema, null);
        _lab.Publish(familyId, version, null);
        _lab.SaveSample(familyId, version, sample);
    }
}
