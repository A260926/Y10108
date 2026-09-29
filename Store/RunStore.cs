using System.Text.Json;
using System.Text.Json.Nodes;
using CompatibilityLab.Engine;
using CompatibilityLab.Models;
using Microsoft.Data.Sqlite;

namespace CompatibilityLab.Store;

public sealed class CreateRunResult
{
    public long RunId { get; set; }
    public string Outcome { get; set; } = "created"; // created|reused
}

public sealed class RunService
{
    private static readonly JsonSerializerOptions ApiJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly Database _db;
    private readonly LabStore _lab;
    public RunService(Database db, LabStore lab) { _db = db; _lab = lab; }

    public sealed class CreateRunSpec
    {
        public string FamilyId { get; init; } = "";
        public string IdempotencyKey { get; init; } = "";
        public string? UnknownPolicy { get; init; } // override; part of identity
    }

    public (CreateRunResult result, RunRow? row) CreateRun(CreateRunSpec spec)
    {
        var versions = _lab.ListVersions(spec.FamilyId);
        if (versions.Count == 0)
            throw new ConflictException("NO_VERSIONS", "至少需要发布一个版本才能生成矩阵。");

        var samples = new Dictionary<string, LabStore.StoredSample>();
        foreach (var v in versions)
        {
            var sample = _lab.GetSample(spec.FamilyId, v.Version)
                ?? throw new ConflictException("SAMPLE_MISSING", $"版本 {v.Version} 还没有上传样例。");
            samples[v.Version] = sample;
        }

        var effectivePolicy = spec.UnknownPolicy is "preserve" or "drop"
            ? spec.UnknownPolicy!
            : "preserve"; // deterministic default for identity

        // Freeze schemas + samples and compute identity hash.
        var idPayload = new JsonObject
        {
            ["familyId"] = spec.FamilyId,
            ["policy"] = effectivePolicy,
            ["versions"] = new JsonArray(versions.OrderBy(v => v.Revision).Select(v =>
                (JsonNode)new JsonObject
                {
                    ["version"] = v.Version,
                    ["revision"] = v.Revision,
                    ["schemaSha"] = v.SchemaSha,
                    ["schemaJson"] = v.SchemaJson
                }).ToArray()),
            ["samples"] = new JsonArray(versions.OrderBy(v => v.Revision).Select(v =>
            {
                var sm = samples[v.Version];
                return (JsonNode)new JsonObject
                {
                    ["version"] = v.Version,
                    ["sha"] = sm.Sha,
                    ["bytes"] = sm.Bytes,
                    ["secretCount"] = sm.SecretCount
                };
            }).ToArray())
        };
        var contentHash = JsonUtil.Sha256Hex(JsonUtil.Canonical(idPayload));

        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        // Idempotency: same key + same content => original run; same key + diff => 409.
        using (var find = conn.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = "SELECT id, content_hash FROM runs WHERE family_id=$f AND idempotency_key=$k";
            find.Parameters.AddWithValue("$f", spec.FamilyId);
            find.Parameters.AddWithValue("$k", spec.IdempotencyKey);
            using var rr = find.ExecuteReader();
            if (rr.Read())
            {
                var existingId = rr.GetInt64(0);
                var existingHash = rr.GetString(1);
                if (existingHash != contentHash)
                    throw new ConflictException("IDEMPOTENCY_CONTENT_CONFLICT",
                        "幂等键相同但冻结内容（schema/样例/unknownPolicy）不同，拒绝创建新运行。");
                tx.Commit();
                return (new CreateRunResult { RunId = existingId, Outcome = "reused" }, GetRun(spec.FamilyId, existingId.ToString()));
            }
        }

        var snapshot = BuildSnapshot(versions, samples, effectivePolicy);
        var n = versions.Count;
        long runId;
        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO runs(family_id, idempotency_key, unknown_policy, content_hash, snapshot_json,
                                 status, cursor, total, created_at, updated_at)
                VALUES($f,$k,$p,$h,$s,'pending',0,$tot,$t,$t)
                RETURNING id;
                """;
            ins.Parameters.AddWithValue("$f", spec.FamilyId);
            ins.Parameters.AddWithValue("$k", spec.IdempotencyKey);
            ins.Parameters.AddWithValue("$p", effectivePolicy);
            ins.Parameters.AddWithValue("$h", contentHash);
            ins.Parameters.AddWithValue("$s", JsonSerializer.Serialize(snapshot, ApiJson));
            ins.Parameters.AddWithValue("$tot", n * n);
            ins.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            runId = Convert.ToInt64(ins.ExecuteScalar());
        }

        // Pre-create every cell as pending so missing cells are visible and recoverable.
        var ordered = versions.OrderBy(v => v.Revision).ToList();
        foreach (var w in ordered)
            foreach (var r in ordered)
                using (var cell = conn.CreateCommand())
                {
                    cell.Transaction = tx;
                    cell.CommandText = """
                        INSERT OR IGNORE INTO cells(run_id, writer_version, reader_version, status)
                        VALUES($id,$w,$r,'pending');
                        """;
                    cell.Parameters.AddWithValue("$id", runId);
                    cell.Parameters.AddWithValue("$w", w.Version);
                    cell.Parameters.AddWithValue("$r", r.Version);
                    cell.ExecuteNonQuery();
                }
        tx.Commit();
        return (new CreateRunResult { RunId = runId, Outcome = "created" }, GetRun(spec.FamilyId, runId.ToString()));
    }

    private static RunSnapshot BuildSnapshot(List<VersionRow> versions,
        Dictionary<string, LabStore.StoredSample> samples, string policy)
    {
        var snap = new RunSnapshot();
        foreach (var v in versions.OrderBy(v => v.Revision))
            snap.Versions.Add(new RunSnapshotVersion
            {
                Version = v.Version,
                SchemaJson = v.SchemaJson,
                UnknownPolicy = policy,
                SchemaSha256 = v.SchemaSha
            });
        foreach (var v in versions.OrderBy(v => v.Revision))
        {
            var sm = samples[v.Version];
            snap.Samples.Add(new SampleSummary
            {
                Version = v.Version,
                Sha256 = sm.Sha,
                ByteLength = sm.Bytes,
                SecretCount = sm.SecretCount,
                RedactedJson = sm.SampleJson
            });
        }
        return snap;
    }

    public RunRow GetRun(string familyId, string runId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, family_id, idempotency_key, unknown_policy, content_hash, snapshot_json,
                   status, cursor, total, created_at
            FROM runs WHERE id=$id AND family_id=$f
            """;
        cmd.Parameters.AddWithValue("$id", long.Parse(runId));
        cmd.Parameters.AddWithValue("$f", familyId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new NotFoundException($"运行 {runId} 不存在。");
        return new RunRow
        {
            Id = r.GetInt64(0),
            FamilyId = r.GetString(1),
            IdempotencyKey = r.GetString(2),
            UnknownPolicy = r.GetString(3),
            ContentHash = r.GetString(4),
            SnapshotJson = r.GetString(5),
            Status = r.GetString(6),
            Cursor = r.GetInt32(7),
            Total = r.GetInt32(8),
            CreatedAt = r.GetString(9)
        };
    }

    public List<RunRow> ListRuns(string familyId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, family_id, idempotency_key, unknown_policy, content_hash, snapshot_json,
                   status, cursor, total, created_at
            FROM runs WHERE family_id=$f ORDER BY id
            """;
        cmd.Parameters.AddWithValue("$f", familyId);
        var list = new List<RunRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new RunRow
            {
                Id = r.GetInt64(0),
                FamilyId = r.GetString(1),
                IdempotencyKey = r.GetString(2),
                UnknownPolicy = r.GetString(3),
                ContentHash = r.GetString(4),
                SnapshotJson = r.GetString(5),
                Status = r.GetString(6),
                Cursor = r.GetInt32(7),
                Total = r.GetInt32(8),
                CreatedAt = r.GetString(9)
            });
        return list;
    }

    public record PendingCell(long CellId, string WriterVersion, string ReaderVersion);

    // Claim one pending cell. Cells beyond the cursor are still claimable; the cursor
    // is advanced separately after acknowledgement, which makes the write-then-advance
    // crash window recoverable without recomputation.
    public PendingCell? ClaimPendingCell(long runId)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using (var mark = conn.CreateCommand())
        {
            mark.Transaction = tx;
            mark.CommandText = "UPDATE runs SET status='running', updated_at=$t WHERE id=$id AND status!='complete'";
            mark.Parameters.AddWithValue("$id", runId);
            mark.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            mark.ExecuteNonQuery();
        }
        using (var q = conn.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = """
                SELECT id, writer_version, reader_version FROM cells
                WHERE run_id=$id AND status='pending'
                ORDER BY id LIMIT 1
                """;
            q.Parameters.AddWithValue("$id", runId);
            using var r = q.ExecuteReader();
            if (!r.Read()) { tx.Commit(); return null; }
            var claimed = new PendingCell(r.GetInt64(0), r.GetString(1), r.GetString(2));
            tx.Commit();
            return claimed;
        }
    }

    public sealed record WriteCellOutcome(bool Written, string Status);

    // Idempotent cell write: if the cell already holds a result, do not overwrite it.
    public WriteCellOutcome WriteCellResult(long cellId, CellResult result)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        string existing;
        using (var q = conn.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT status FROM cells WHERE id=$id";
            q.Parameters.AddWithValue("$id", cellId);
            existing = (string)q.ExecuteScalar()!;
        }
        if (existing != "pending")
        {
            tx.Commit();
            return new WriteCellOutcome(false, existing);
        }
        using (var u = conn.CreateCommand())
        {
            u.Transaction = tx;
            u.CommandText = """
                UPDATE cells SET status=$st, result_json=$j, computed_at=$t WHERE id=$id AND status='pending'
                """;
            u.Parameters.AddWithValue("$st", result.Status);
            u.Parameters.AddWithValue("$j", JsonSerializer.Serialize(result, ApiJson));
            u.Parameters.AddWithValue("$t", result.ComputedAt.ToString("O"));
            u.Parameters.AddWithValue("$id", cellId);
            u.ExecuteNonQuery();
        }
        tx.Commit();
        return new WriteCellOutcome(true, result.Status);
    }

    // Reconcile cursor against actually-completed prefix and complete run when all done.
    public void Reconcile(long runId)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        var statuses = new List<string>();
        using (var q = conn.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT status FROM cells WHERE run_id=$id ORDER BY id";
            q.Parameters.AddWithValue("$id", runId);
            using var r = q.ExecuteReader();
            while (r.Read()) statuses.Add(r.GetString(0));
        }

        int prefix = 0;
        foreach (var st in statuses)
        {
            if (st == "pending") break;
            prefix++;
        }
        bool allDone = statuses.Count > 0 && statuses.All(s => s != "pending");
        using (var u = conn.CreateCommand())
        {
            u.Transaction = tx;
            u.CommandText = allDone
                ? "UPDATE runs SET cursor=$c, status='complete', updated_at=$t WHERE id=$id"
                : "UPDATE runs SET cursor=$c, status='running', updated_at=$t WHERE id=$id";
            u.Parameters.AddWithValue("$c", prefix);
            u.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            u.Parameters.AddWithValue("$id", runId);
            u.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public sealed class CellRow
    {
        public long Id { get; init; }
        public string WriterVersion { get; init; } = "";
        public string ReaderVersion { get; init; } = "";
        public string Status { get; init; } = "";
        public CellResult? Result { get; init; }
    }

    public List<CellRow> GetCells(long runId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, writer_version, reader_version, status, result_json FROM cells WHERE run_id=$id ORDER BY id";
        cmd.Parameters.AddWithValue("$id", runId);
        var list = new List<CellRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var status = r.GetString(3);
            CellResult? result = null;
            if (!r.IsDBNull(4))
                result = JsonSerializer.Deserialize<CellResult>(r.GetString(4), ApiJson);
            list.Add(new CellRow
            {
                Id = r.GetInt64(0),
                WriterVersion = r.GetString(1),
                ReaderVersion = r.GetString(2),
                Status = status,
                Result = result
            });
        }
        return list;
    }

    public CellRow GetCell(long runId, string writer, string reader)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, writer_version, reader_version, status, result_json FROM cells WHERE run_id=$id AND writer_version=$w AND reader_version=$r";
        cmd.Parameters.AddWithValue("$id", runId);
        cmd.Parameters.AddWithValue("$w", writer);
        cmd.Parameters.AddWithValue("$r", reader);
        using var rr = cmd.ExecuteReader();
        if (!rr.Read()) throw new NotFoundException("格子不存在。");
        CellResult? result = null;
        if (!rr.IsDBNull(4))
            result = JsonSerializer.Deserialize<CellResult>(rr.GetString(4), ApiJson);
        return new CellRow
        {
            Id = rr.GetInt64(0),
            WriterVersion = rr.GetString(1),
            ReaderVersion = rr.GetString(2),
            Status = rr.GetString(3),
            Result = result
        };
    }
}
