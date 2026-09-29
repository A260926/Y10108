using Microsoft.Data.Sqlite;

namespace CompatibilityLab.Store;

public sealed class Database
{
    private readonly string _connectionString;
    public string Path { get; }

    public Database(string dbPath)
    {
        Path = dbPath;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }
        return conn;
    }

    public void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS families (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            created_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS versions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            family_id TEXT NOT NULL REFERENCES families(id),
            version TEXT NOT NULL,
            revision INTEGER NOT NULL,
            schema_json TEXT NOT NULL,
            schema_sha TEXT NOT NULL,
            unknown_policy TEXT NOT NULL,
            published_at TEXT NOT NULL,
            UNIQUE(family_id, version)
        );

        -- working draft per family; optimistic concurrency via revision
        CREATE TABLE IF NOT EXISTS drafts (
            family_id TEXT PRIMARY KEY REFERENCES families(id),
            schema_json TEXT NOT NULL,
            revision INTEGER NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS samples (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            family_id TEXT NOT NULL REFERENCES families(id),
            version TEXT NOT NULL,
            sample_json TEXT NOT NULL,   -- secrets already tokenized
            sample_sha TEXT NOT NULL,
            byte_length INTEGER NOT NULL,
            secret_count INTEGER NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE(family_id, version)
        );

        CREATE TABLE IF NOT EXISTS runs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            family_id TEXT NOT NULL REFERENCES families(id),
            idempotency_key TEXT NOT NULL,
            unknown_policy TEXT NOT NULL,       -- effective policy, part of identity
            content_hash TEXT NOT NULL,         -- frozen schema+samples+policy hash
            snapshot_json TEXT NOT NULL,        -- frozen schema + sample summaries
            status TEXT NOT NULL,               -- pending|running|complete|failed
            cursor INTEGER NOT NULL DEFAULT 0,  -- cells acknowledged as processed
            total INTEGER NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE(family_id, idempotency_key)
        );

        CREATE TABLE IF NOT EXISTS cells (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id INTEGER NOT NULL REFERENCES runs(id),
            writer_version TEXT NOT NULL,
            reader_version TEXT NOT NULL,
            status TEXT NOT NULL,               -- pending|ok|error
            result_json TEXT,
            computed_at TEXT,
            UNIQUE(run_id, writer_version, reader_version)
        );

        CREATE INDEX IF NOT EXISTS idx_cells_run ON cells(run_id);
        CREATE INDEX IF NOT EXISTS idx_runs_status ON runs(status);
        """;
        cmd.ExecuteNonQuery();
    }
}
