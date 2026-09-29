using System.Text.Json;
using CompatibilityLab.Models;
using CompatibilityLab.Store;

namespace CompatibilityLab.Engine;

// Drives one tick of cell processing. The hosted service calls Tick repeatedly.
public sealed class MatrixRunner
{
    private static readonly JsonSerializerOptions ApiJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Database _db;
    private readonly RunService _runs;
    private readonly LabStore _lab;

    // Test hook: when set, the runner exits AFTER writing the given run's cell but
    // BEFORE reconciling/advancing the cursor, simulating a process crash.
    public Func<long, long, bool>? CrashAfterCellWrite { get; set; }

    public MatrixRunner(Database db, RunService runs, LabStore lab)
    {
        _db = db;
        _runs = runs;
        _lab = lab;
    }

    // Process a single cell of a run. Returns false when nothing was pending.
    public bool ProcessOne(long runId)
    {
        var run = _runs.GetRun(FamilyOf(runId), runId.ToString());
        var snapshot = JsonSerializer.Deserialize<RunSnapshot>(run.SnapshotJson, ApiJson)!;

        var claimed = _runs.ClaimPendingCell(runId);
        if (claimed is null)
        {
            _runs.Reconcile(runId);
            return false;
        }

        var writerSnap = snapshot.Versions.First(v => v.Version == claimed.WriterVersion);
        var readerSnap = snapshot.Versions.First(v => v.Version == claimed.ReaderVersion);
        var sampleSnap = snapshot.Samples.First(v => v.Version == claimed.WriterVersion);

        var writer = SchemaValidator.Parse(writerSnap.SchemaJson);
        var reader = SchemaValidator.Parse(readerSnap.SchemaJson);

        var cell = CompatibilityEngine.Compute(new CellRequest
        {
            WriterVersion = claimed.WriterVersion,
            ReaderVersion = claimed.ReaderVersion,
            Writer = writer,
            Reader = reader,
            SampleJson = sampleSnap.RedactedJson,
            RunUnknownPolicyOverride = run.UnknownPolicy
        });

        var outcome = _runs.WriteCellResult(claimed.CellId, cell);

        // Simulated crash point: between writing the cell and advancing the cursor.
        if (CrashAfterCellWrite is not null && CrashAfterCellWrite(runId, claimed.CellId))
            throw new ProcessCrashSimulatedException($"已写入格子 {claimed.CellId}，但在推进游标前模拟退出。");

        if (outcome.Written || !outcome.Written)
            _runs.Reconcile(runId);
        return true;
    }

    private string FamilyOf(long runId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT family_id FROM runs WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", runId);
        return (string)cmd.ExecuteScalar()!;
    }

    public List<long> PendingRunIds()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM runs WHERE status IN ('pending','running') ORDER BY id";
        var ids = new List<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    // Drains all currently available work (bounded). Used by the hosted loop and tests.
    public int Drain(long runId, int maxCells = 10_000)
    {
        int processed = 0;
        while (processed < maxCells && ProcessOne(runId)) processed++;
        return processed;
    }

    public int DrainAll(int maxCells = 100_000)
    {
        int processed = 0;
        foreach (var id in PendingRunIds())
            processed += Drain(id, maxCells - processed);
        return processed;
    }
}

public sealed class ProcessCrashSimulatedException : Exception
{
    public ProcessCrashSimulatedException(string message) : base(message) { }
}
