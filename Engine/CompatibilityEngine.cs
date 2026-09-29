using System.Text.Json.Nodes;
using CompatibilityLab.Models;

namespace CompatibilityLab.Engine;

public sealed class CellRequest
{
    public string WriterVersion { get; init; } = "";
    public string ReaderVersion { get; init; } = "";
    public SchemaDefinition Writer { get; init; } = new();
    public SchemaDefinition Reader { get; init; } = new();
    public string SampleJson { get; init; } = "";
    public string? RunUnknownPolicyOverride { get; init; }
}

public static class CompatibilityEngine
{
    public static CellResult Compute(CellRequest req)
    {
        var cell = new CellResult
        {
            WriterVersion = req.WriterVersion,
            ReaderVersion = req.ReaderVersion,
            ComputedAt = DateTimeOffset.UtcNow
        };
        try
        {
            // 1) writer encodes its version sample. Two renderings:
            //    - transport keeps opaque secret TOKENS so round-trip is real;
            //    - the redacted view is the only byte string ever stored/shown.
            var sampleNode = JsonUtil.Parse(req.SampleJson);
            var transport = Encoder.Encode(req.Writer, sampleNode, redactSecrets: false);
            var encoded = Encoder.Encode(req.Writer, sampleNode, redactSecrets: true);
            // Hash/bytes reported are the redacted representation (existence-bearing, secret-free).
            cell.EncodeSha256 = encoded.Report.Sha256;
            cell.EncodeByteLength = encoded.Report.ByteLength;
            cell.EncodeFields = encoded.Report.Fields;
            cell.EncodedJson = encoded.Json;
            cell.Secrets = ExtractSecrets(req.Writer.Fields, sampleNode as JsonObject, "");

            // 2) reader decodes the actual writer bytes (tokens intact on the wire).
            var (readerView, report) = Decoder.Decode(req.Reader, transport.Json,
                new DecodeContext { WriterVersion = req.WriterVersion, ReaderVersion = req.ReaderVersion });
            cell.DecodedFields = report.Fields;
            cell.UnknownSidecar = report.Unknown;
            cell.FieldSources = report.Sources;
            cell.ErrorCodes.AddRange(report.ErrorCodes);
            cell.DefaultsUsed = report.Fields.Where(f => f.DefaultUsed).Select(f => f.Path).ToList();

            // 3) explanations of evolution
            cell.Changes = SchemaDiffer.Diff(req.Writer, req.Reader);

            // 4) effective unknown policy: run override > reader version option
            var policy = req.RunUnknownPolicyOverride is "preserve" or "drop"
                ? req.RunUnknownPolicyOverride!
                : req.Reader.UnknownPolicy;
            cell.EffectiveUnknownPolicy = policy;

            // 5) re-encode through the intermediate reader
            var reInput = readerView.DeepClone()!.AsObject();
            if (policy == "preserve")
                InjectSidecar(reInput, report.Unknown);
            else
                cell.Changes.Add(new FieldChange
                {
                    Kind = "unknown", Code = "UNKNOWN_DROPPED",
                    Explanation = report.Unknown.Count == 0
                        ? "unknownPolicy=drop，但本次没有未知字段需要处理。"
                        : $"unknownPolicy=drop：中间 reader 再写出时丢弃 {report.Unknown.Count} 个未知字段（{string.Join(", ", report.Unknown.Select(u => u.Path))}）。"
                });

            var reTransport = Encoder.Encode(req.Reader, (JsonObject)reInput, redactSecrets: false);
            var reEncoded = Encoder.Encode(req.Reader, (JsonObject)reInput, redactSecrets: true);
            cell.ReEncodedSha256 = reEncoded.Report.Sha256;
            cell.ReEncodedByteLength = reEncoded.Report.ByteLength;
            cell.ReEncodedJson = reEncoded.Json;

            // 6) writer re-decodes the intermediate bytes; reversibility vs original
            var (backView, backReport) = Decoder.Decode(req.Writer, reTransport.Json,
                new DecodeContext { WriterVersion = req.ReaderVersion, ReaderVersion = req.WriterVersion });
            if (policy == "preserve")
                InjectSidecar(backView, backReport.Unknown);

            var origNorm = NormalizeRecord(req.Writer.Fields, sampleNode as JsonObject);
            // Fields the reader synthesized from its own defaults (not present in the
            // writer message) are allowed extras; they must not make the trip lossy.
            var synthesized = report.Fields
                .Where(f => f.DefaultUsed)
                .Select(f => f.Path.TrimStart('/'))
                .ToHashSet();
            var backNorm = NormalizeRecord(req.Writer.Fields, backView, extraOmit: synthesized);
            bool structurallyEqual = JsonUtil.Canonical(origNorm) == JsonUtil.Canonical(backNorm);
            bool noLossyError = !cell.ErrorCodes.Contains("ENUM_UNKNOWN_VALUE")
                                && !cell.ErrorCodes.Contains("REQUIRED_MISSING")
                                && !cell.ErrorCodes.Contains("TYPE_MISMATCH")
                                && !cell.ErrorCodes.Contains("UNION_UNKNOWN_BRANCH");
            cell.Reversible = structurallyEqual && noLossyError;
            cell.ReversibilityNote = cell.Reversible
                ? $"在“{PolicyLabel(policy)}”模式下，reader 视角再写出后被原 writer 解码，与原消息逐字段一致（可逆）。"
                : ExplainIrreversibility(cell, policy, structurallyEqual, report.Unknown.Count);

            cell.Compatible = !cell.ErrorCodes.Any(IsHardError);
            cell.Status = "ok";
            return cell;
        }
        catch (Exception ex)
        {
            cell.Status = "error";
            cell.Compatible = false;
            cell.Reversible = false;
            cell.ErrorCodes.Add("CELL_COMPUTE_FAULT");
            cell.ReversibilityNote = "计算格子时发生异常: " + ex.Message;
            return cell;
        }
    }

    private static string PolicyLabel(string p) => p == "preserve" ? "保留未知字段" : "丢弃未知字段";

    private static bool IsHardError(string code) => code is
        "INVALID_JSON" or "ROOT_NOT_OBJECT" or "REQUIRED_MISSING" or "TYPE_MISMATCH"
        or "ENUM_UNKNOWN_VALUE" or "UNION_UNKNOWN_BRANCH" or "SECRET_NOT_TOKENIZED"
        or "CELL_COMPUTE_FAULT";

    private static string ExplainIrreversibility(CellResult cell, string policy, bool structurallyEqual, int unknownCount)
    {
        if (policy == "drop" && unknownCount > 0)
            return "在“丢弃未知字段”模式下，中间 reader 再写出时丢弃了未知字段，回到原 writer 后这些字段永久丢失，因此不可逆；同一组合切换到“保留未知字段”可能变为可逆。";
        if (cell.ErrorCodes.Contains("ENUM_UNKNOWN_VALUE"))
            return "reader 遇到未定义枚举值，无法安全往返，不可逆。";
        if (cell.ErrorCodes.Contains("REQUIRED_MISSING"))
            return "缺少 reader 必填字段，再写出后无法恢复原值，不可逆。";
        if (cell.ErrorCodes.Contains("TYPE_MISMATCH"))
            return "存在类型不匹配，再写出后值语义改变，不可逆。";
        if (!structurallyEqual)
            return "reader 视角再写出后与原消息存在字段差异，不可逆。";
        return "该组合无法完整往返。";
    }

    private static List<SecretSummary> ExtractSecrets(List<FieldDef> fields, JsonObject? data, string path)
    {
        var result = new List<SecretSummary>();
        if (data is null) return result;
        foreach (var f in fields)
        {
            var fp = path + "/" + f.Name;
            if (f.Type == "record" && data[f.Name] is JsonObject ro)
                result.AddRange(ExtractSecrets(f.Fields!, ro, fp));
            else if (f.Type == "union" && data[f.Name] is JsonObject uo && uo["value"] is JsonObject vo)
            {
                var tag = JsonUtil.AsString(uo["__tag"]);
                var branch = f.Branches!.FirstOrDefault(b => b.Tag == tag);
                if (branch is not null) result.AddRange(ExtractSecrets(branch.Record.Fields!, vo, fp + "[" + tag + "]"));
            }
            else if (f.Secret)
            {
                var token = JsonUtil.AsString(data[f.Name]);
                result.Add(new SecretSummary { Path = fp, Present = data.ContainsKey(f.Name), Digest = JsonUtil.TokenDigest(token) });
            }
        }
        return result;
    }

    // Unknown sidecar entries have absolute paths like /a/b/field; splice them back
    // into the reader view object tree before re-encoding.
    public static void InjectSidecar(JsonObject root, List<UnknownFieldInfo> unknown)
    {
        foreach (var u in unknown.OrderBy(x => x.Path))
        {
            var segments = u.Path.Trim('/').Split('/');
            JsonObject cur = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                var seg = segments[i];
                if (cur[seg] is not JsonObject child)
                {
                    child = new JsonObject();
                    cur[seg] = child;
                }
                cur = child;
            }
            var leaf = segments[^1];
            if (!cur.ContainsKey(leaf))
                cur[leaf] = JsonUtil.Parse(u.RawJson);
        }
    }

    private static JsonObject NormalizeRecord(List<FieldDef> fields, JsonObject? data,
        HashSet<string>? extraOmit = null, string pathPrefix = "")
    {
        var result = new JsonObject();
        if (data is null) return result;
        foreach (var f in fields)
        {
            if (!data.ContainsKey(f.Name)) continue;
            var fp = pathPrefix + f.Name;
            if (extraOmit is not null && extraOmit.Contains(fp)) continue;
            var val = data[f.Name];
            result[f.Name] = f.Type switch
            {
                "record" => NormalizeRecord(f.Fields!, val as JsonObject, extraOmit, fp + "/"),
                "union" => NormalizeUnion(f, val as JsonObject, extraOmit, fp + "/"),
                _ when f.Secret => "●",
                _ => val?.DeepClone()
            };
        }
        var known = fields.Select(f => f.Name).ToHashSet();
        foreach (var kv in data)
            if (!known.Contains(kv.Key) && !kv.Key.StartsWith("__")
                && (extraOmit is null || !extraOmit.Contains(pathPrefix + kv.Key)))
                result[kv.Key] = kv.Value?.DeepClone();
        return result;
    }

    private static JsonNode? NormalizeUnion(FieldDef union, JsonObject? data,
        HashSet<string>? extraOmit = null, string pathPrefix = "")
    {
        if (data is null) return null;
        var result = new JsonObject { ["__tag"] = data["__tag"]?.DeepClone() };
        var tag = JsonUtil.AsString(data["__tag"]);
        var branch = union.Branches!.FirstOrDefault(b => b.Tag == tag);
        result["value"] = branch is null
            ? data["value"]?.DeepClone()
            : NormalizeRecord(branch.Record.Fields!, data["value"] as JsonObject, extraOmit, pathPrefix);
        return result;
    }

}
