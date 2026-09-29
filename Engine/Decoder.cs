using System.Text.Json.Nodes;
using CompatibilityLab.Models;

namespace CompatibilityLab.Engine;

public sealed class DecodeContext
{
    public string ReaderVersion { get; init; } = "";
    public string WriterVersion { get; init; } = "";
}

// Produces a "reader view": a canonical node tree plus a sidecar of unknown fields.
public static class Decoder
{
    public static (JsonObject node, DecodeReport report) Decode(
        SchemaDefinition schema, string json, DecodeContext ctx)
    {
        var report = new DecodeReport();
        var output = new JsonObject();
        JsonNode parsed;
        try { parsed = JsonUtil.Parse(json); }
        catch (Exception)
        {
            report.ErrorCodes.Add("INVALID_JSON");
            return (output, report);
        }
        if (parsed is not JsonObject obj)
        {
            report.ErrorCodes.Add("ROOT_NOT_OBJECT");
            return (output, report);
        }
        var sidecar = new List<(string path, string name, JsonNode? value, bool secretLike)>();
        DecodeRecord(schema.Fields, obj.AsObject(), "", report, output, sidecar, ctx);
        foreach (var (p, n, v, sl) in sidecar)
            report.Unknown.Add(new UnknownFieldInfo
            {
                Path = p,
                Field = n,
                RawJson = v?.ToJsonString() ?? "null",
                SecretLike = sl
            });
        return (output, report);
    }

    private static FieldDef? Resolve(List<FieldDef> fields, JsonObject data, FieldDef target,
        out string? matchedName, out bool similar)
    {
        matchedName = null;
        similar = false;
        if (data.ContainsKey(target.Name)) { matchedName = target.Name; return target; }
        // ONLY a declared alias lets a renamed field inherit.
        foreach (var alias in target.Aliases)
            if (data.ContainsKey(alias)) { matchedName = alias; return target; }
        foreach (var key in data.Select(kv => kv.Key))
        {
            if (target.Aliases.Contains(key) || key == target.Name) continue;
            if (LooksSimilar(key, target.Name)) { similar = true; break; }
        }
        return null;
    }

    public static bool LooksSimilar(string a, string b)
    {
        static string Norm(string s) =>
            new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var na = Norm(a); var nb = Norm(b);
        if (na.Length < 4 || na == nb) return false;
        return Levenshtein(na, nb) <= 2;
    }

    private static int Levenshtein(string a, string b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) dp[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
            }
        return dp[a.Length, b.Length];
    }

    private static bool LooksSecret(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("secret") || n.Contains("password") || n.Contains("token");
    }

    private static void DecodeRecord(List<FieldDef> fields, JsonObject data, string path,
        DecodeReport report, JsonObject output,
        List<(string, string, JsonNode?, bool)> sidecar, DecodeContext ctx)
    {
        var consumed = new HashSet<string>();
        foreach (var f in fields)
        {
            var fp = path + "/" + f.Name;
            var resolved = Resolve(fields, data, f, out var matchedName, out var similar);
            if (resolved is null)
            {
                if (similar)
                    report.Sources.Add(new FieldSourceInfo
                    {
                        Path = fp, Field = f.Name, Origin = "missing",
                        Note = "存在名称相似但未声明 alias 的字段，不继承"
                    });
                if (f.HasDefault)
                {
                    output[f.Name] = f.Default?.DeepClone();
                    report.Fields.Add(new DecodedFieldInfo { Path = fp, Field = f.Name, Type = f.Type, Source = "default", DefaultUsed = true });
                    report.Sources.Add(new FieldSourceInfo { Path = fp, Field = f.Name, Origin = "default", DeclaredIn = ctx.ReaderVersion });
                }
                else if (f.Required)
                {
                    report.ErrorCodes.Add("REQUIRED_MISSING");
                    report.Fields.Add(new DecodedFieldInfo { Path = fp, Field = f.Name, Type = f.Type, Source = "missing" });
                    report.Sources.Add(new FieldSourceInfo
                    {
                        Path = fp, Field = f.Name, Origin = "missing",
                        Note = similar ? "缺少必填字段；相似名称不构成 alias" : "缺少必填字段"
                    });
                }
                else
                {
                    report.Fields.Add(new DecodedFieldInfo { Path = fp, Field = f.Name, Type = f.Type, Source = "missing" });
                }
                continue;
            }
            consumed.Add(matchedName!);
            DecodeValue(f, data[matchedName!], matchedName!, fp, report, output, sidecar, ctx);
        }

        foreach (var kv in data)
        {
            if (consumed.Contains(kv.Key)) continue;
            if (fields.Any(x => x.Aliases.Contains(kv.Key))) { consumed.Add(kv.Key); continue; }
            sidecar.Add((path + "/" + kv.Key, kv.Key, kv.Value?.DeepClone(), LooksSecret(kv.Key)));
            report.Sources.Add(new FieldSourceInfo { Path = path + "/" + kv.Key, Field = kv.Key, Origin = "unknown" });
        }
    }

    private static FieldSourceInfo Source(string path, string name, string origin, string matched, DecodeContext ctx, bool via) =>
        new()
        {
            Path = path,
            Field = name,
            Origin = origin,
            ViaName = via ? matched : null,
            DeclaredIn = ctx.ReaderVersion
        };

    private static void DecodeValue(FieldDef f, JsonNode? val, string matchedName, string path,
        DecodeReport report, JsonObject output,
        List<(string, string, JsonNode?, bool)> sidecar, DecodeContext ctx)
    {
        bool viaAlias = matchedName != f.Name;
        var source = viaAlias ? "alias" : "direct";
        switch (f.Type)
        {
            case "record":
                if (val is not JsonObject ro) { report.ErrorCodes.Add("TYPE_MISMATCH"); break; }
                var child = new JsonObject();
                output[f.Name] = child;
                DecodeRecord(f.Fields!, ro.AsObject(), path, report, child, sidecar, ctx);
                report.Fields.Add(new DecodedFieldInfo { Path = path, Field = f.Name, Type = "record", Source = source, ViaName = viaAlias ? matchedName : null });
                report.Sources.Add(Source(path, f.Name, source, matchedName, ctx, viaAlias));
                break;
            case "enum":
                string? sym = JsonUtil.AsString(val);
                if (sym is null)
                {
                    report.ErrorCodes.Add("TYPE_MISMATCH");
                    break;
                }
                if (!f.EnumValues!.Contains(sym))
                {
                    report.ErrorCodes.Add("ENUM_UNKNOWN_VALUE");
                    report.Sources.Add(new FieldSourceInfo
                    {
                        Path = path, Field = f.Name, Origin = source, ViaName = viaAlias ? matchedName : null,
                        Note = $"枚举值 '{sym}' 不在 reader 的 {f.EnumValues.Count} 个允许值中"
                    });
                }
                output[f.Name] = sym;
                report.Fields.Add(new DecodedFieldInfo { Path = path, Field = f.Name, Type = "enum", Source = source, ViaName = viaAlias ? matchedName : null });
                report.Sources.Add(Source(path, f.Name, source, matchedName, ctx, viaAlias));
                break;
            case "union":
                DecodeUnion(f, val, path, report, output, sidecar, ctx, source, matchedName, viaAlias);
                break;
            default:
                if (!PrimitiveReader.IsAssignable(val, f.Type))
                {
                    report.ErrorCodes.Add("TYPE_MISMATCH");
                    report.Sources.Add(new FieldSourceInfo { Path = path, Field = f.Name, Origin = source, ViaName = viaAlias ? matchedName : null, Note = $"类型不匹配：期望 {f.Type}" });
                    break;
                }
                if (f.Secret && !JsonUtil.IsSecretToken(val))
                    report.ErrorCodes.Add("SECRET_NOT_TOKENIZED");
                output[f.Name] = val?.DeepClone();
                report.Fields.Add(new DecodedFieldInfo { Path = path, Field = f.Name, Type = f.Type, Source = source, ViaName = viaAlias ? matchedName : null });
                report.Sources.Add(Source(path, f.Name, source, matchedName, ctx, viaAlias));
                break;
        }
    }

    private static void DecodeUnion(FieldDef f, JsonNode? val, string path,
        DecodeReport report, JsonObject output,
        List<(string, string, JsonNode?, bool)> sidecar, DecodeContext ctx,
        string source, string matchedName, bool viaAlias)
    {
        if (val is not JsonObject uo)
        {
            report.ErrorCodes.Add("TYPE_MISMATCH");
            return;
        }
        string? tag = JsonUtil.AsString(uo["__tag"]);
        int writerIndex = (uo["__index"] as JsonValue)?.TryGetValue(out int wi) == true ? wi : -1;
        var branch = f.Branches!.FirstOrDefault(b => b.Tag == tag);
        if (tag is null || branch is null)
        {
            report.ErrorCodes.Add("UNION_UNKNOWN_BRANCH");
            report.Sources.Add(new FieldSourceInfo { Path = path, Field = f.Name, Origin = source, Note = tag is null ? "union 缺少 __tag" : $"未知 union 分支 '{tag}'" });
            return;
        }
        int readerIndex = f.Branches!.FindIndex(b => b.Tag == tag);
        if (writerIndex >= 0 && writerIndex != readerIndex)
        {
            report.ErrorCodes.Add("UNION_BRANCH_REORDERED");
            report.Sources.Add(new FieldSourceInfo
            {
                Path = path,
                Field = f.Name,
                Origin = "union-order",
                ViaName = viaAlias ? matchedName : null,
                Note = $"分支 '{tag}' 在 writer 位于位置 {writerIndex}，reader 中位于 {readerIndex}；按 tag 解析成功，但分支顺序已变化"
            });
        }
        report.MatchedUnionTag = tag;
        report.MatchedUnionIndex = readerIndex;

        var canonical = new JsonObject
        {
            ["__tag"] = tag,
            ["__index"] = readerIndex
        };
        var valueOut = new JsonObject();
        canonical["value"] = valueOut;
        if (uo["value"] is JsonObject vo)
            DecodeRecord(branch.Record.Fields!, vo, path + "[" + tag + "]", report, valueOut, sidecar, ctx);
        // unknown envelope members of the union
        foreach (var key in new[] { "__tag", "__index", "value" })
            if (uo.ContainsKey(key)) { }
        output[f.Name] = canonical;
        report.Fields.Add(new DecodedFieldInfo { Path = path, Field = f.Name, Type = "union", Source = source, ViaName = viaAlias ? matchedName : null });
        report.Sources.Add(Source(path, f.Name, source, matchedName, ctx, viaAlias));
    }
}
