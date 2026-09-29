using System.Text.Json.Nodes;
using CompatibilityLab.Models;
using CompatibilityLab.Store;

namespace CompatibilityLab.Engine;

public sealed class ExportService
{
    private readonly LabStore _lab;
    private readonly RunService _runs;
    public ExportService(LabStore lab, RunService runs) { _lab = lab; _runs = runs; }

    public JsonObject ExportFamily(string familyId, long? runId)
    {
        var versions = _lab.ListVersions(familyId);
        var root = new JsonObject
        {
            ["familyId"] = familyId,
            ["exportedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["versions"] = new JsonArray(versions.Select(v =>
            {
                var vo = new JsonObject
                {
                    ["version"] = v.Version,
                    ["revision"] = v.Revision,
                    ["schemaSha256"] = v.SchemaSha,
                    ["unknownPolicy"] = v.UnknownPolicy,
                    ["publishedAt"] = v.PublishedAt,
                    ["schema"] = JsonUtil.Parse(v.SchemaJson)
                };
                var sample = _lab.GetSample(familyId, v.Version);
                if (sample is not null)
                    vo["sampleSummary"] = new JsonObject
                    {
                        ["version"] = sample.Version,
                        ["sha256"] = sample.Sha,
                        ["byteLength"] = sample.Bytes,
                        ["secretCount"] = sample.SecretCount,
                        ["secrets"] = SecretExistenceOnly(sample.SampleJson)
                        // raw sample intentionally excluded
                    };
                return (JsonNode)vo;
            }).ToArray())
        };

        var runs = runId is null ? _runs.ListRuns(familyId) : new List<RunRow> { _runs.GetRun(familyId, runId.Value.ToString()) };
        var runArr = new JsonArray();
        foreach (var run in runs)
        {
            var cells = _runs.GetCells(run.Id);
            var cellArr = new JsonArray();
            foreach (var c in cells)
            {
                var co = new JsonObject
                {
                    ["writerVersion"] = c.WriterVersion,
                    ["readerVersion"] = c.ReaderVersion,
                    ["status"] = c.Status,
                };
                if (c.Result is { } res)
                {
                    co["compatible"] = res.Compatible;
                    co["reversible"] = res.Reversible;
                    co["effectiveUnknownPolicy"] = res.EffectiveUnknownPolicy;
                    co["reversibilityNote"] = res.ReversibilityNote;
                    co["encodeSha256"] = res.EncodeSha256;
                    co["encodeByteLength"] = res.EncodeByteLength;
                    co["reEncodedSha256"] = res.ReEncodedSha256;
                    co["errorCodes"] = new JsonArray(res.ErrorCodes.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray());
                    co["defaultsUsed"] = new JsonArray(res.DefaultsUsed.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray());
                    co["changes"] = new JsonArray(res.Changes.Select(ch => (JsonNode)new JsonObject
                    {
                        ["path"] = ch.Path,
                        ["kind"] = ch.Kind,
                        ["code"] = ch.Code,
                        ["explanation"] = ch.Explanation,
                        ["from"] = ch.From,
                        ["to"] = ch.To
                    }).ToArray());
                    // secret fields: existence + digest only
                    co["secrets"] = new JsonArray(res.Secrets.Select(sc => (JsonNode)new JsonObject
                    {
                        ["path"] = sc.Path,
                        ["present"] = sc.Present,
                        ["digest"] = sc.Digest
                    }).ToArray());
                    co["unknownSidecar"] = new JsonArray(res.UnknownSidecar.Select(u => (JsonNode)new JsonObject
                    {
                        ["path"] = u.Path,
                        ["field"] = u.Field,
                        ["secretLike"] = u.SecretLike
                        // rawJson intentionally excluded
                    }).ToArray());
                }
                cellArr.Add(co);
            }
            runArr.Add(new JsonObject
            {
                ["runId"] = run.Id,
                ["idempotencyKey"] = run.IdempotencyKey,
                ["unknownPolicy"] = run.UnknownPolicy,
                ["contentHash"] = run.ContentHash,
                ["status"] = run.Status,
                ["cursor"] = run.Cursor,
                ["total"] = run.Total,
                ["frozenSnapshot"] = JsonUtil.Parse(run.SnapshotJson),
                ["matrix"] = cellArr
            });
        }
        root["runs"] = runArr;
        root["secretPolicy"] = "secret 字段导出时只保留存在性与 SHA-256 摘要，不含原始值与完整字节";
        return root;
    }

    // Scans a stored (tokenized) sample for secret tokens and returns existence+digest.
    private static JsonArray SecretExistenceOnly(string tokenizedSample)
    {
        var arr = new JsonArray();
        var node = JsonUtil.Parse(tokenizedSample);
        Walk(node, "", arr);
        return arr;
    }

    private static void Walk(JsonNode? node, string path, JsonArray arr)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var kv in o)
                {
                    var p = path + "/" + kv.Key;
                    if (JsonUtil.AsString(kv.Value) is string s && s.StartsWith(JsonUtil.SecretTokenPrefix))
                        arr.Add(new JsonObject
                        {
                            ["path"] = p,
                            ["present"] = true,
                            ["digest"] = JsonUtil.TokenDigest(s)
                        });
                    else
                        Walk(kv.Value, p, arr);
                }
                break;
            case JsonArray a:
                foreach (var item in a) Walk(item, path, arr);
                break;
        }
    }
}
