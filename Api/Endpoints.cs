using System.Text.Json.Nodes;
using CompatibilityLab.Engine;
using CompatibilityLab.Store;
using Microsoft.AspNetCore.Mvc;

namespace CompatibilityLab.Api;

public static class Endpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/health", () => Results.Ok(new { ok = true }));

        app.MapGet("/api/families", (LabStore lab) =>
            Results.Ok(lab.ListFamilies().Select(f => new { f.Id, f.Name, f.CreatedAt })));

        app.MapPost("/api/families", ([FromBody] JsonObject body, LabStore lab) =>
        {
            var name = body["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { error = "name 必填", code = "BAD_REQUEST" });
            var id = lab.CreateFamily(name!);
            return Results.Ok(new { id, name });
        });

        // ---- draft ----
        app.MapGet("/api/families/{familyId}/draft", (string familyId, LabStore lab) =>
        {
            var (json, rev) = lab.GetDraft(familyId);
            return Results.Ok(new { schema = JsonUtil.Parse(json), revision = rev });
        });

        app.MapPut("/api/families/{familyId}/draft", (string familyId, [FromBody] DraftReq req, LabStore lab) =>
        {
            var rev = lab.PutDraft(familyId, req.SchemaJson, req.ExpectedRevision);
            return Results.Ok(new { revision = rev });
        });

        app.MapPost("/api/families/{familyId}/publish", (string familyId, [FromBody] PublishReq req, LabStore lab) =>
        {
            var res = lab.Publish(familyId, req.Version, req.ExpectedDraftRevision?.ToString());
            return Results.Ok(new { res.Version, res.Revision, res.SchemaSha });
        });

        app.MapGet("/api/families/{familyId}/versions", (string familyId, LabStore lab) =>
            Results.Ok(lab.ListVersions(familyId).Select(v => new
            {
                v.Version, v.Revision, v.SchemaSha, v.UnknownPolicy, v.PublishedAt,
                schema = JsonUtil.Parse(v.SchemaJson)
            })));

        // Published schema immutability: PUT/DELETE to a version is refused.
        app.MapPut("/api/families/{familyId}/versions/{version}", (string familyId, string version) =>
            Results.Conflict(new
            {
                code = "VERSION_IMMUTABLE",
                error = $"版本 {version} 已发布，schema 不可修改；任何调整必须创建新版本。"
            }));
        app.MapDelete("/api/families/{familyId}/versions/{version}", (string familyId, string version) =>
            Results.Conflict(new
            {
                code = "VERSION_IMMUTABLE",
                error = $"版本 {version} 已发布且不可删除。"
            }));

        // ---- samples ----
        app.MapPut("/api/families/{familyId}/versions/{version}/sample",
            (string familyId, string version, [FromBody] SampleReq req, LabStore lab) =>
        {
            var s = lab.SaveSample(familyId, version, req.SampleJson);
            return Results.Ok(new
            {
                s.Version, s.Sha, byteLength = s.Bytes, secretCount = s.SecretCount,
                note = "secret 字面量已在入库前替换为不可逆 token；返回中不含明文。",
                redactedSample = JsonUtil.Parse(s.SampleJson)
            });
        });

        app.MapGet("/api/families/{familyId}/versions/{version}/sample", (string familyId, string version, LabStore lab) =>
        {
            var s = lab.GetSample(familyId, version);
            return s is null
                ? Results.NotFound(new { code = "NOT_FOUND", error = "尚无样例" })
                : Results.Ok(new
                {
                    s.Version, s.Sha, byteLength = s.Bytes, secretCount = s.SecretCount,
                    redactedSample = JsonUtil.Parse(s.SampleJson)
                });
        });

        // ---- runs ----
        app.MapPost("/api/families/{familyId}/runs", (string familyId, [FromBody] CreateRunReq req, RunService runs) =>
        {
            var (res, row) = runs.CreateRun(new RunService.CreateRunSpec
            {
                FamilyId = familyId,
                IdempotencyKey = req.IdempotencyKey,
                UnknownPolicy = req.UnknownPolicy
            });
            return Results.Ok(new
            {
                runId = row!.Id,
                res.Outcome,
                row.Status,
                row.Cursor,
                row.Total,
                row.UnknownPolicy,
                row.ContentHash
            });
        });

        app.MapGet("/api/families/{familyId}/runs", (string familyId, RunService runs) =>
            Results.Ok(runs.ListRuns(familyId).Select(r => new
            {
                runId = r.Id, r.IdempotencyKey, r.Status, r.Cursor, r.Total,
                r.UnknownPolicy, r.ContentHash, r.CreatedAt
            })));

        app.MapGet("/api/families/{familyId}/runs/{runId}", (string familyId, long runId, RunService runs) =>
        {
            var row = runs.GetRun(familyId, runId.ToString());
            var cells = runs.GetCells(runId).Select(c => new
            {
                c.WriterVersion, c.ReaderVersion, c.Status,
                compatible = c.Result?.Compatible,
                reversible = c.Result?.Reversible,
                effectiveUnknownPolicy = c.Result?.EffectiveUnknownPolicy,
                errorCodes = c.Result?.ErrorCodes
            });
            return Results.Ok(new
            {
                runId = row.Id, idempotencyKey = row.IdempotencyKey, row.Status, row.Cursor, row.Total,
                row.UnknownPolicy, row.ContentHash, snapshot = JsonUtil.Parse(row.SnapshotJson), cells
            });
        });

        app.MapGet("/api/families/{familyId}/runs/{runId}/cells/{writer}/{reader}",
            (string familyId, long runId, string writer, string reader, RunService runs) =>
            {
                runs.GetRun(familyId, runId.ToString());
                var c = runs.GetCell(runId, writer, reader);
                return Results.Ok(new
                {
                    c.WriterVersion, c.ReaderVersion, c.Status, result = c.Result
                });
            });

        // ---- export ----
        app.MapGet("/api/families/{familyId}/export", (string familyId, long? runId, ExportService export) =>
            Results.Ok(export.ExportFamily(familyId, runId)));
    }

    public record DraftReq([property: System.Text.Json.Serialization.JsonPropertyName("schemaJson")] string SchemaJson,
        [property: System.Text.Json.Serialization.JsonPropertyName("expectedRevision")] int? ExpectedRevision);
    public record PublishReq(string Version, long? ExpectedDraftRevision);
    public record SampleReq(string SampleJson);
    public record CreateRunReq(string IdempotencyKey, string? UnknownPolicy);
}
