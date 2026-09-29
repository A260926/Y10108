using CompatibilityLab.Tests;
using CompatibilityLab.Engine;
using CompatibilityLab.Store;using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace CompatibilityLab.Tests;

public abstract class ApiTestBase
{
    protected readonly LabApp App;
    protected readonly HttpClient Http;
    protected ApiTestBase(LabApp app)
    {
        App = app;
        Http = app.CreateClient();
    }

    protected async Task<string> NewFamily(string name = "测试家族")
    {
        var r = await Http.PostAsJsonAsync("/api/families", new { name });
        var n = await r.Content.ReadFromJsonAsync<JsonObject>();
        return n!["id"]!.GetValue<string>();
    }

    protected async Task<int> PutDraft(string fam, string schema, int? rev = null)
    {
        var r = await Http.PutAsJsonAsync($"/api/families/{fam}/draft", new { schemaJson = schema, expectedRevision = rev });
        r.EnsureSuccessStatusCode();
        var n = await r.Content.ReadFromJsonAsync<JsonObject>();
        return n!["revision"]!.GetValue<int>();
    }

    protected async Task Publish(string fam, string version)
    {
        var r = await Http.PostAsJsonAsync($"/api/families/{fam}/publish", new { version, expectedDraftRevision = (long?)null });
        r.EnsureSuccessStatusCode();
    }

    protected async Task UploadSample(string fam, string version, string sample)
    {
        var r = await Http.PutAsJsonAsync($"/api/families/{fam}/versions/{version}/sample", new { sampleJson = sample });
        r.EnsureSuccessStatusCode();
    }

    protected async Task<JsonObject> CreateFamilyWithVersions(
        List<(string version, string schema, string? sample)> versions)
    {
        var fam = await NewFamily();
        foreach (var (version, schema, sample) in versions)
        {
            await PutDraft(fam, schema);
            await Publish(fam, version);
            if (sample is not null) await UploadSample(fam, version, sample);
        }
        return new JsonObject { ["familyId"] = fam };
    }

    protected async Task<JsonObject> StartRun(string fam, string key, string policy)
    {
        var r = await Http.PostAsJsonAsync($"/api/families/{fam}/runs",
            new { idempotencyKey = key, unknownPolicy = policy });
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    protected async Task Drain(long runId)
    {
        var runner = App.Resolve<MatrixRunner>();
        runner.Drain(runId);
    }

    protected async Task<JsonObject> GetCell(string fam, long runId, string w, string r)
    {
        var resp = await Http.GetAsync($"/api/families/{fam}/runs/{runId}/cells/{w}/{r}");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    protected async Task<JsonObject> GetRun(string fam, long runId)
    {
        var resp = await Http.GetAsync($"/api/families/{fam}/runs/{runId}");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonObject>())!;
    }
}
