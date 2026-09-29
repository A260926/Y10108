namespace CompatibilityLab.Tests;

public static class TestData
{
    public const string BaseSchema = """
    {
      "rootType": "record",
      "unknownPolicy": "preserve",
      "fields": [
        { "name": "id", "type": "string", "required": true },
        { "name": "kind", "type": "enum", "required": true, "enumValues": ["A", "B"] },
        {
          "name": "body", "type": "union", "required": true,
          "tagField": "type",
          "branches": [
            { "tag": "X", "record": { "type": "record", "fields": [ { "name": "x", "type": "int", "required": true } ] } },
            { "tag": "Y", "record": { "type": "record", "fields": [ { "name": "y", "type": "string", "required": true } ] } }
          ]
        },
        { "name": "token", "type": "string", "required": false, "secret": true }
      ]
    }
    """;

    public const string BaseSample = """
    {
      "id": "s1",
      "kind": "A",
      "body": { "__tag": "X", "__index": 0, "value": { "x": 10 } },
      "token": "plaintext-secret-value",
      "region": "north"
    }
    """;
}
