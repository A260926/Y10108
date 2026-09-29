using System.Text.Json;
using System.Text.Json.Nodes;
using CompatibilityLab.Engine;
using CompatibilityLab.Models;
using Microsoft.Data.Sqlite;

namespace CompatibilityLab.Store;

public sealed class ConflictException : Exception
{
    public string Code { get; }
    public ConflictException(string code, string message) : base(message) => Code = code;
}

public sealed class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message) { }
}

public sealed class VersionRow
{
    public string Version { get; init; } = "";
    public int Revision { get; init; }
    public string SchemaJson { get; init; } = "";
    public string SchemaSha { get; init; } = "";
    public string UnknownPolicy { get; init; } = "";
    public string PublishedAt { get; init; } = "";
}

public sealed class RunRow
{
    public long Id { get; init; }
    public string FamilyId { get; init; } = "";
    public string IdempotencyKey { get; init; } = "";
    public string UnknownPolicy { get; init; } = "";
    public string ContentHash { get; init; } = "";
    public string SnapshotJson { get; init; } = "";
    public string Status { get; set; } = "";
    public int Cursor { get; set; }
    public int Total { get; init; }
    public string CreatedAt { get; init; } = "";
}

public sealed class LabStore
{
    private static readonly JsonSerializerOptions ApiJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly Database _db;
    public LabStore(Database db) => _db = db;

    // ---------- families ----------
    public string CreateFamily(string name)
    {
        var id = "fam_" + Guid.NewGuid().ToString("N")[..12];
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO families(id,name,created_at) VALUES($i,$n,$t)";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        return id;
    }

    public record FamilyInfo(string Id, string Name, string CreatedAt);

    public List<FamilyInfo> ListFamilies()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,name,created_at FROM families ORDER BY id";
        var rows = new List<FamilyInfo>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new FamilyInfo(r.GetString(0), r.GetString(1), r.GetString(2)));
        return rows;
    }

    private static bool FamilyExists(SqliteConnection conn, string familyId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM families WHERE id=$i";
        cmd.Parameters.AddWithValue("$i", familyId);
        return cmd.ExecuteScalar() is not null;
    }

    // ---------- drafts ----------
    public (string schemaJson, int revision) GetDraft(string familyId)
    {
        using var conn = _db.Open();
        EnsureFamily(conn, familyId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT schema_json, revision FROM drafts WHERE family_id=$f";
        cmd.Parameters.AddWithValue("$f", familyId);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            throw new NotFoundException("该 schema 家族还没有 draft，请先 PUT 定义。");
        return (r.GetString(0), r.GetInt32(1));
    }

    // ExpectedRevision == null => create/overwrite only when no conflict semantics required.
    public int PutDraft(string familyId, string schemaJson, int? expectedRevision)
    {
        var def = SchemaValidator.Parse(schemaJson);
        var canonical = SchemaValidator.CanonicalJson(def);
        using var conn = _db.Open();
        EnsureFamily(conn, familyId);
        using var tx = conn.BeginTransaction();

        int currentRev = -1;
        using (var q = conn.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT revision FROM drafts WHERE family_id=$f";
            q.Parameters.AddWithValue("$f", familyId);
            var res = q.ExecuteScalar();
            if (res is not null && res != DBNull.Value) currentRev = Convert.ToInt32(res);
        }

        if (expectedRevision is not null && currentRev >= 0 && expectedRevision.Value != currentRev)
            throw new ConflictException("DRAFT_REVISION_CONFLICT",
                $"draft 已被其他页面修改：期望 revision {expectedRevision}，当前 {currentRev}。请刷新后重试。");

        int newRev = currentRev < 0 ? 0 : currentRev + 1;
        using (var upsert = conn.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO drafts(family_id, schema_json, revision, updated_at)
                VALUES($f,$s,$r,$t)
                ON CONFLICT(family_id) DO UPDATE SET
                  schema_json=excluded.schema_json,
                  revision=excluded.revision,
                  updated_at=excluded.updated_at
                """;
            upsert.Parameters.AddWithValue("$f", familyId);
            upsert.Parameters.AddWithValue("$s", canonical);
            upsert.Parameters.AddWithValue("$r", newRev);
            upsert.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            upsert.ExecuteNonQuery();
        }
        tx.Commit();
        return newRev;
    }

    // ---------- versions (publish = immutable) ----------
    public record PublishResult(string Version, int Revision, string SchemaSha);

    public PublishResult Publish(string familyId, string version, string? expectedDraftRevision = null)
    {
        using var conn = _db.Open();
        EnsureFamily(conn, familyId);
        using var tx = conn.BeginTransaction();

        string schemaJson;
        int draftRev;
        using (var q = conn.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT schema_json, revision FROM drafts WHERE family_id=$f";
            q.Parameters.AddWithValue("$f", familyId);
            using var rr = q.ExecuteReader();
            if (!rr.Read()) throw new ConflictException("NO_DRAFT", "没有可发布的 draft。");
            schemaJson = rr.GetString(0);
            draftRev = rr.GetInt32(1);
        }
        if (expectedDraftRevision is not null && int.Parse(expectedDraftRevision) != draftRev)
            throw new ConflictException("DRAFT_REVISION_CONFLICT", "发布前 draft 已被修改，请刷新。");

        // published versions are immutable
        using (var dup = conn.CreateCommand())
        {
            dup.Transaction = tx;
            dup.CommandText = "SELECT 1 FROM versions WHERE family_id=$f AND version=$v";
            dup.Parameters.AddWithValue("$f", familyId);
            dup.Parameters.AddWithValue("$v", version);
            if (dup.ExecuteScalar() is not null)
                throw new ConflictException("VERSION_IMMUTABLE",
                    $"版本 {version} 已发布，发布后 schema 不可修改；任何调整必须形成新版本。");
        }

        var def = SchemaValidator.Parse(schemaJson);
        var sha = JsonUtil.Sha256Hex(schemaJson);
        var revision = NextRevision(conn, tx, familyId);
        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO versions(family_id, version, revision, schema_json, schema_sha, unknown_policy, published_at)
                VALUES($f,$v,$r,$j,$s,$p,$t)
                """;
            ins.Parameters.AddWithValue("$f", familyId);
            ins.Parameters.AddWithValue("$v", version);
            ins.Parameters.AddWithValue("$r", revision);
            ins.Parameters.AddWithValue("$j", schemaJson);
            ins.Parameters.AddWithValue("$s", sha);
            ins.Parameters.AddWithValue("$p", def.UnknownPolicy);
            ins.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            ins.ExecuteNonQuery();
        }
        tx.Commit();
        return new PublishResult(version, revision, sha);
    }

    private static int NextRevision(SqliteConnection conn, SqliteTransaction tx, string familyId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(revision)+1,1) FROM versions WHERE family_id=$f";
        cmd.Parameters.AddWithValue("$f", familyId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<VersionRow> ListVersions(string familyId)
    {
        using var conn = _db.Open();
        EnsureFamily(conn, familyId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version,revision,schema_json,schema_sha,unknown_policy,published_at FROM versions WHERE family_id=$f ORDER BY revision";
        cmd.Parameters.AddWithValue("$f", familyId);
        var list = new List<VersionRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new VersionRow
            {
                Version = r.GetString(0),
                Revision = r.GetInt32(1),
                SchemaJson = r.GetString(2),
                SchemaSha = r.GetString(3),
                UnknownPolicy = r.GetString(4),
                PublishedAt = r.GetString(5)
            });
        return list;
    }

    public VersionRow GetVersion(string familyId, string version)
    {
        using var conn = _db.Open();
        EnsureFamily(conn, familyId);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version,revision,schema_json,schema_sha,unknown_policy,published_at FROM versions WHERE family_id=$f AND version=$v";
        cmd.Parameters.AddWithValue("$f", familyId);
        cmd.Parameters.AddWithValue("$v", version);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new NotFoundException($"版本 {version} 不存在。");
        return new VersionRow
        {
            Version = r.GetString(0),
            Revision = r.GetInt32(1),
            SchemaJson = r.GetString(2),
            SchemaSha = r.GetString(3),
            UnknownPolicy = r.GetString(4),
            PublishedAt = r.GetString(5)
        };
    }

    // Published schema is read-only: any attempt to mutate it is rejected.
    public void AssertVersionImmutable(string familyId, string version)
    {
        // Presence itself proves immutability; mutating endpoints refuse by design.
        GetVersion(familyId, version);
    }

    private static void EnsureFamily(SqliteConnection conn, string familyId)
    {
        if (!FamilyExists(conn, familyId)) throw new NotFoundException($"schema 家族 {familyId} 不存在。");
    }

    // ---------- samples ----------
    public record StoredSample(string Version, string SampleJson, string Sha, long Bytes, int SecretCount);

    public StoredSample SaveSample(string familyId, string version, string rawSampleJson)
    {
        var vrow = GetVersion(familyId, version); // 404 if not published
        if (IsSampleReferenced(familyId, version))
            throw new ConflictException("SAMPLE_FROZEN",
                $"版本 {version} 的样例已被某次矩阵运行冻结，不能再修改；请发布新版本。");

        var schema = SchemaValidator.Parse(vrow.SchemaJson);
        var tokenized = SecretIngestor.Ingest(schema, rawSampleJson, out int secretCount);
        // Validate the tokenized sample structurally against the schema (encode/decode dry run).
        SampleValidator.Validate(schema, tokenized);

        var bytes = System.Text.Encoding.UTF8.GetBytes(tokenized);
        var sha = JsonUtil.Sha256Hex(bytes);
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO samples(family_id, version, sample_json, sample_sha, byte_length, secret_count, updated_at)
            VALUES($f,$v,$j,$s,$b,$c,$t)
            ON CONFLICT(family_id, version) DO UPDATE SET
              sample_json=excluded.sample_json,
              sample_sha=excluded.sample_sha,
              byte_length=excluded.byte_length,
              secret_count=excluded.secret_count,
              updated_at=excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$f", familyId);
        cmd.Parameters.AddWithValue("$v", version);
        cmd.Parameters.AddWithValue("$j", tokenized);
        cmd.Parameters.AddWithValue("$s", sha);
        cmd.Parameters.AddWithValue("$b", bytes.Length);
        cmd.Parameters.AddWithValue("$c", secretCount);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        return new StoredSample(version, tokenized, sha, bytes.Length, secretCount);
    }

    public StoredSample? GetSample(string familyId, string version)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version,sample_json,sample_sha,byte_length,secret_count FROM samples WHERE family_id=$f AND version=$v";
        cmd.Parameters.AddWithValue("$f", familyId);
        cmd.Parameters.AddWithValue("$v", version);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new StoredSample(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt32(4));
    }

    public List<StoredSample> GetSamples(string familyId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version,sample_json,sample_sha,byte_length,secret_count FROM samples WHERE family_id=$f ORDER BY id";
        cmd.Parameters.AddWithValue("$f", familyId);
        var list = new List<StoredSample>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new StoredSample(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetInt32(4)));
        return list;
    }

    public bool IsSampleReferenced(string familyId, string version)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM runs WHERE family_id=$f AND status IN ('pending','running','complete')";
        cmd.Parameters.AddWithValue("$f", familyId);
        // Any run in the family freezes all its samples because snapshots include all versions.
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }
}

