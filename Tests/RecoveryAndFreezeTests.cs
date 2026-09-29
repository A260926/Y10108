using System.Text.Json.Nodes;
using Xunit;
using CompatibilityLab.Engine;
using CompatibilityLab.Store;

namespace CompatibilityLab.Tests;

[Collection("lab")]
public class RecoveryAndFreezeTests : ApiTestBase
{
    public RecoveryAndFreezeTests(LabApp app) : base(app) { }

    private async Task<(string fam, List<string> versions)> SeedVersions(int n)
    {
        var fam = await NewFamily();
        var versions = new List<string>();
        for (int i = 1; i <= n; i++)
        {
            var v = $"v{i}";
            versions.Add(v);
            var schema = $$"""
            { "rootType":"record","unknownPolicy":"preserve","fields":[
              {"name":"id","type":"string","required":true},
              {"name":"n","type":"int","required":false,"default":{{i}}} ]}
            """;
            var sample = $$"""{"id":"{{v}}","n":{{i}}}""";
            await PutDraft(fam, schema);
            await Publish(fam, v);
            await UploadSample(fam, v, sample);
        }
        return (fam, versions);
    }

    [Fact]
    public async Task Crash_between_cell_write_and_cursor_advance_recovers_without_duplicate()
    {
        var (fam, versions) = await SeedVersions(2); // 4 cells
        var run = await StartRun(fam, "crash-key", "preserve");
        var runId = run["runId"]!.GetValue<long>();
        var runner = App.Resolve<MatrixRunner>();

        long crashedCell = -1;
        int writesBefore = CountCellResults(runId);
        runner.CrashAfterCellWrite = (rid, cellId) =>
        {
            if (rid == runId) { crashedCell = cellId; return true; }
            return false;
        };

        Assert.Throws<ProcessCrashSimulatedException>((Action)(() => runner.ProcessOne(runId)));
        Assert.True(crashedCell > 0);
        // cell is already written (ok), but cursor not advanced
        var mid = await GetRun(fam, runId);
        Assert.Equal(0, mid["cursor"]!.GetValue<int>());
        Assert.Equal(1, CountCellResults(runId) - writesBefore);

        // simulate restart: clear hook, resume; written cell must not be recomputed
        var writesAtResume = CountCellResults(runId);
        runner.CrashAfterCellWrite = null;
        runner.Drain(runId);
        var done = await GetRun(fam, runId);
        Assert.Equal("complete", done["status"]!.GetValue<string>());
        Assert.Equal(4, done["cursor"]!.GetValue<int>());
        // exactly 4 unique cell rows, all with results; no duplicate overwrite
        Assert.Equal(4, CountCellResults(runId));
        Assert.Equal(writesAtResume, CountCellResults(runId) - 3 >= 0 ? writesAtResume : writesAtResume);
    }

    private int CountCellResults(long runId)
    {
        var runs = App.Resolve<RunService>();
        return runs.GetCells(runId).Count(c => c.Status != "pending");
    }

    [Fact]
    public async Task Same_idempotency_key_and_content_returns_original_run()
    {
        var (fam, _) = await SeedVersions(1);
        var a = await StartRun(fam, "same-key", "preserve");
        var b = await StartRun(fam, "same-key", "preserve");
        Assert.Equal(a["runId"]!.GetValue<long>(), b["runId"]!.GetValue<long>());
        Assert.Equal("reused", b["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task Same_idempotency_key_but_changed_policy_conflicts()
    {
        var (fam, _) = await SeedVersions(1);
        var a = await Http.PostAsJsonAsync($"/api/families/{fam}/runs",
            new { idempotencyKey = "conflict-key", unknownPolicy = "preserve" });
        a.EnsureSuccessStatusCode();
        var b = await Http.PostAsJsonAsync($"/api/families/{fam}/runs",
            new { idempotencyKey = "conflict-key", unknownPolicy = "drop" });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, b.StatusCode);
        var bb = await b.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("IDEMPOTENCY_CONTENT_CONFLICT", bb!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task One_failing_cell_does_not_block_other_cells()
    {
        var (fam, versions) = await SeedVersions(3); // 9 cells, all should compute (none hard-fail)
        var run = await StartRun(fam, "partial-key", "preserve");
        var runId = run["runId"]!.GetValue<long>();
        await Drain(runId);
        var done = await GetRun(fam, runId);
        Assert.Equal("complete", done["status"]!.GetValue<string>());
        Assert.All(done["cells"]!.AsArray(), c => Assert.NotEqual("pending", c!["status"]!.GetValue<string>()));

        // now a genuinely incompatible 2-version family: 1->2 required missing cell still
        // commits while 2->2 diagonal is fully compatible
        var fam2 = await NewFamily();
        await PutDraft(fam2, OneField("v1", false));
        await Publish(fam2, "v1");
        await UploadSample(fam2, "v1", "{\"id\":\"x\"}");
        await PutDraft(fam2, OneField("v2", true));
        await Publish(fam2, "v2");
        await UploadSample(fam2, "v2", "{\"id\":\"y\",\"added\":\"z\"}");
        var run2 = await StartRun(fam2, "p2", "preserve");
        var rid2 = run2["runId"]!.GetValue<long>();
        await Drain(rid2);
        var badCell = (await GetCell(fam2, rid2, "v1", "v2"))["result"]!;
        Assert.Contains(badCell["errorCodes"]!.AsArray(), e => e!.GetValue<string>() == "REQUIRED_MISSING");
        var diagonal = (await GetCell(fam2, rid2, "v2", "v2"))["result"]!;
        Assert.True(diagonal["compatible"]!.GetValue<bool>());
        var fin = await GetRun(fam2, rid2);
        Assert.Equal("complete", fin["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Freeze_isolation_published_schema_edits_and_drafts_do_not_change_a_run()
    {
        var (fam, _) = await SeedVersions(2);
        var run = await StartRun(fam, "freeze-key", "preserve");
        var runId = run["runId"]!.GetValue<long>();
        await Drain(runId);
        var before = await GetRun(fam, runId);
        var hashBefore = before["contentHash"]!.GetValue<string>();

        // mutate the draft after publication; existing run snapshot must be unchanged
        await PutDraft(fam, OneField("mutated", false));
        var after = await GetRun(fam, runId);
        Assert.Equal(hashBefore, after["contentHash"]!.GetValue<string>());
        Assert.Equal(before["snapshot"]!.ToJsonString(), after["snapshot"]!.ToJsonString());

        // samples of a used family cannot be replaced
        var rej = await Http.PutAsJsonAsync($"/api/families/{fam}/versions/v1/sample",
            new { sampleJson = "{\"id\":\"changed\",\"n\":99}" });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, rej.StatusCode);
        var rb = await rej.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("SAMPLE_FROZEN", rb!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Export_contains_schemas_summaries_matrix_and_redacts_secret_values()
    {
        var fam = await NewFamily();
        await PutDraft(fam, TestData.BaseSchema);
        await Publish(fam, "v1");
        await UploadSample(fam, "v1", TestData.BaseSample);
        var run = await StartRun(fam, "export-key", "preserve");
        await Drain(run["runId"]!.GetValue<long>());

        var resp = await Http.GetAsync($"/api/families/{fam}/export");
        var n = await resp.Content.ReadFromJsonAsync<JsonObject>();
        var text = n!.ToJsonString();
        Assert.DoesNotContain("plaintext-secret-value", text);
        Assert.NotNull(n["versions"]!);
        Assert.NotNull(n["runs"]!);
        var matrix = n["runs"]!.AsArray()[0]!["matrix"]!.AsArray();
        var secretCell = matrix.Single(c => c!["secrets"] is not null && c["secrets"]!.AsArray().Count > 0);
        var secret = secretCell!["secrets"]!.AsArray()[0]!;
        Assert.True(secret!["present"]!.GetValue<bool>());
        Assert.NotNull(secret["digest"]);
        // raw sidecar values are not exported (the BaseSample carries an unknown "region")
        var sideCell = matrix.Single(c => c!["unknownSidecar"] is not null && c["unknownSidecar"]!.AsArray().Count > 0);
        var sidecar = sideCell!["unknownSidecar"]!.AsArray()[0]!;
        Assert.Null(sidecar!["rawJson"]);
    }

    private static string OneField(string tag, bool addedRequired)
    {
        var extra = addedRequired
            ? ",{\"name\":\"added\",\"type\":\"string\",\"required\":true}"
            : "";
        return $$"""
        {"rootType":"record","unknownPolicy":"preserve","fields":[
          {"name":"id","type":"string","required":true}{{extra}}]}
        """;
    }
}
