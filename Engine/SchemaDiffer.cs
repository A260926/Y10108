using System.Text.Json.Nodes;
using CompatibilityLab.Models;

namespace CompatibilityLab.Engine;

// Explains schema evolution between writer and reader versions. Produces per-field
// changes (kind + human explanation + machine code), never only a boolean.
public static class SchemaDiffer
{
    public static List<FieldChange> Diff(SchemaDefinition writer, SchemaDefinition reader)
    {
        var changes = new List<FieldChange>();
        DiffRecords(writer.Fields, reader.Fields, "", changes);
        return changes;
    }

    private static FieldDef? FindPath(List<FieldDef> fields, string name) =>
        fields.FirstOrDefault(f => f.Name == name);

    private static void DiffRecords(List<FieldDef> writerFields, List<FieldDef> readerFields,
        string path, List<FieldChange> changes)
    {
        foreach (var rf in readerFields)
        {
            var rp = path + "/" + rf.Name;
            var wf = writerFields.FirstOrDefault(w => w.Name == rf.Name);
            if (wf is null)
            {
                // renamed? only a declared alias inherits
                var aliasMatch = writerFields.FirstOrDefault(w => rf.Aliases.Contains(w.Name));
                if (aliasMatch is not null)
                {
                    changes.Add(new FieldChange
                    {
                        Path = rp,
                        Kind = "alias",
                        Code = "ALIAS_RENAME_INHERITED",
                        From = aliasMatch.Name,
                        To = rf.Name,
                        Explanation = $"字段由 '{aliasMatch.Name}' 重命名为 '{rf.Name}'，reader 声明了 alias，值可以继承。"
                    });
                }
                else
                {
                    var similar = writerFields.FirstOrDefault(w => Decoder.LooksSimilar(w.Name, rf.Name));
                    if (similar is not null)
                        changes.Add(new FieldChange
                        {
                            Path = rp,
                            Kind = "rename",
                            Code = "SIMILAR_NAME_NOT_ALIAS",
                            From = similar.Name,
                            To = rf.Name,
                            Explanation = $"'{similar.Name}' 与 '{rf.Name}' 名称相似，但 reader 未声明 alias，视为两个无关字段，值不继承。"
                        });
                    if (rf.Required)
                        changes.Add(new FieldChange
                        {
                            Path = rp,
                            Kind = "required",
                            Code = "REQUIRED_ADDED",
                            To = rf.Name,
                            Explanation = $"reader 新增必填字段 '{rf.Name}'，旧 writer 的数据中缺失，解码报错 REQUIRED_MISSING。"
                        });
                    else if (rf.HasDefault)
                        changes.Add(new FieldChange
                        {
                            Path = rp,
                            Kind = "required",
                            Code = "OPTIONAL_ADDED_WITH_DEFAULT",
                            To = rf.Name,
                            Explanation = $"reader 新增带默认值字段 '{rf.Name}'，旧数据缺失时使用默认值。"
                        });
                    else
                        changes.Add(new FieldChange
                        {
                            Path = rp,
                            Kind = "required",
                            Code = "OPTIONAL_ADDED",
                            To = rf.Name,
                            Explanation = $"reader 新增可选字段 '{rf.Name}'，旧数据中保持缺省。"
                        });
                }
                continue;
            }

            if (wf.Type != rf.Type)
            {
                changes.Add(new FieldChange
                {
                    Path = rp, Kind = "type", Code = "TYPE_CHANGED", From = wf.Type, To = rf.Type,
                    Explanation = $"字段 '{rf.Name}' 类型由 {wf.Type} 变为 {rf.Type}，解码时可能 TYPE_MISMATCH。"
                });
                continue;
            }

            if (!wf.Required && rf.Required)
                changes.Add(new FieldChange
                {
                    Path = rp, Kind = "required", Code = "REQUIRED_TIGHTENED", From = "optional", To = "required",
                    Explanation = $"字段 '{rf.Name}' 由可选收紧为必填，缺失即报错。"
                });
            if (wf.Required && !rf.Required)
                changes.Add(new FieldChange
                {
                    Path = rp, Kind = "required", Code = "REQUIRED_RELAXED", From = "required", To = "optional",
                    Explanation = $"字段 '{rf.Name}' 由必填放宽为可选，旧数据仍然可读。"
                });

            // default presence / type change
            var wdType = wf.HasDefault ? PrimitiveReader.KindOf(wf.Default) : null;
            var rdType = rf.HasDefault ? PrimitiveReader.KindOf(rf.Default) : null;
            if (wf.HasDefault != rf.HasDefault)
                changes.Add(new FieldChange
                {
                    Path = rp, Kind = "default",
                    Code = rf.HasDefault ? "DEFAULT_ADDED" : "DEFAULT_REMOVED",
                    From = wf.HasDefault ? "有默认值" : "无默认值",
                    To = rf.HasDefault ? "有默认值" : "无默认值",
                    Explanation = rf.HasDefault
                        ? $"字段 '{rf.Name}' 新增默认值，缺失时填入默认。"
                        : $"字段 '{rf.Name}' 移除了默认值。"
                });
            else if (wf.HasDefault && rf.HasDefault && wdType != rdType)
                changes.Add(new FieldChange
                {
                    Path = rp, Kind = "default", Code = "DEFAULT_TYPE_CHANGED", From = wdType, To = rdType,
                    Explanation = $"字段 '{rf.Name}' 默认值类型由 {wdType} 变为 {rdType}，默认值不会被当作同一种数据。"
                });
            else if (wf.HasDefault && rf.HasDefault &&
                     wf.Default is not null && rf.Default is not null &&
                     !JsonUtil.DeepEqual(wf.Default, rf.Default))
                changes.Add(new FieldChange
                {
                    Path = rp, Kind = "default", Code = "DEFAULT_VALUE_CHANGED",
                    Explanation = $"字段 '{rf.Name}' 默认值发生变化（类型保持 {rdType}）。"
                });

            switch (rf.Type)
            {
                case "enum" when wf.Type == "enum":
                    DiffEnum(wf, rf, rp, changes);
                    break;
                case "union" when wf.Type == "union":
                    DiffUnion(wf, rf, rp, changes);
                    break;
                case "record" when wf.Type == "record":
                    DiffRecords(wf.Fields!, rf.Fields!, rp, changes);
                    break;
            }
        }

        foreach (var wf in writerFields)
        {
            var wp = path + "/" + wf.Name;
            if (readerFields.Any(r => r.Name == wf.Name)) continue;
            var viaAlias = readerFields.FirstOrDefault(r => r.Aliases.Contains(wf.Name));
            if (viaAlias is not null) continue; // already reported on reader side
            changes.Add(new FieldChange
            {
                Path = wp, Kind = "required", Code = "FIELD_REMOVED_TO_UNKNOWN", From = wf.Name,
                Explanation = $"reader 移除了字段 '{wf.Name}'，旧数据中的该字段进入未知字段 sidecar；是否保留取决于 unknownPolicy。"
            });
        }
    }

    private static void DiffEnum(FieldDef wf, FieldDef rf, string path, List<FieldChange> changes)
    {
        var added = rf.EnumValues!.Except(wf.EnumValues!).ToList();
        var removed = wf.EnumValues!.Except(rf.EnumValues!).ToList();
        if (added.Count > 0)
            changes.Add(new FieldChange
            {
                Path = path, Kind = "enum", Code = "ENUM_VALUES_ADDED",
                Explanation = $"reader 枚举新增: {string.Join(", ", added)}；旧数据不含这些值，读取旧数据不受影响，反向（旧 reader 读新数据）遇新值报 ENUM_UNKNOWN_VALUE。"
            });
        if (removed.Count > 0)
            changes.Add(new FieldChange
            {
                Path = path, Kind = "enum", Code = "ENUM_VALUES_REMOVED",
                Explanation = $"reader 枚举移除: {string.Join(", ", removed)}；数据中若仍出现这些值报 ENUM_UNKNOWN_VALUE。"
            });
    }

    private static void DiffUnion(FieldDef wf, FieldDef rf, string path, List<FieldChange> changes)
    {
        var wTags = wf.Branches!.Select((b, i) => (b.Tag, i)).ToList();
        var rTags = rf.Branches!.Select((b, i) => (b.Tag, i)).ToList();
        var added = wTags.Select(x => x.Tag).Except(rTags.Select(x => x.Tag)).ToList();
        var removed = rTags.Select(x => x.Tag).Except(wTags.Select(x => x.Tag)).ToList();

        bool reordered = false;
        foreach (var (tag, wi) in wTags)
        {
            var ri = rTags.FindIndex(x => x.Tag == tag);
            if (ri >= 0 && wi != ri) { reordered = true; break; }
        }
        if (reordered)
            changes.Add(new FieldChange
            {
                Path = path, Kind = "union", Code = "UNION_BRANCH_ORDER_CHANGED",
                Explanation = "union 分支顺序变化：编码时位置 (__index) 改变；本系统按 __tag 选分支，因此顺序变化不改变解码结果，但旧实现若按位置解析会错配。矩阵记录 UNION_BRANCH_REORDERED。"
            });
        if (added.Count > 0)
            changes.Add(new FieldChange
            {
                Path = path, Kind = "union", Code = "UNION_BRANCH_ADDED",
                Explanation = $"reader 新增 union 分支: {string.Join(", ", added)}（反向旧 reader 遇新分支报 UNION_UNKNOWN_BRANCH）。"
            });
        if (removed.Count > 0)
            changes.Add(new FieldChange
            {
                Path = path, Kind = "union", Code = "UNION_BRANCH_REMOVED",
                Explanation = $"reader 移除 union 分支: {string.Join(", ", removed)}；数据命中即报 UNION_UNKNOWN_BRANCH。"
            });

        // nested branch field evolution
        foreach (var wb in wf.Branches!)
        {
            var rb = rf.Branches!.FirstOrDefault(b => b.Tag == wb.Tag);
            if (rb is not null)
                DiffRecords(wb.Record.Fields!, rb.Record.Fields!, path + "[" + wb.Tag + "]", changes);
        }
    }
}
