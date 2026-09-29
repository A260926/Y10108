using Microsoft.Extensions.Hosting;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CompatibilityLab.Tests;

// A separate factory that keeps seeding enabled to test the out-of-the-box demo.
public class SeededApp : WebApplicationFactory<Program>
{
    public string DbPath { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "complab_seed_" + Guid.NewGuid().ToString("N") + ".db");

    public SeededApp()
    {
        // Read before host build so the top-level seeder runs against an isolated, seeded DB.
        Environment.SetEnvironmentVariable("COMPLAB_DB_PATH", DbPath);
        Environment.SetEnvironmentVariable("COMPLAB_SEED", "true");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseContentRoot(TestEnvironment.ProjectRoot);
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Lab:DatabasePath"] = DbPath,
                ["Lab:Seed"] = "true"
            });
        });
    }
}

public class SeededDemoTests : IClassFixture<SeededApp>
{
    private readonly SeededApp _app;
    private readonly HttpClient _http;
    public SeededDemoTests(SeededApp app)
    {
        _app = app;
        _http = app.CreateClient();
    }

    [Fact]
    public async Task Homepage_shows_lab_title()
    {
        var html = await _http.GetStringAsync("/");
        Assert.Contains("消息结构兼容性实验室", html);
    }

    [Fact]
    public async Task Seeds_three_versions_and_a_cell_reversible_only_under_preserve()
    {
        // Wait briefly for the background runner to drain pre-created runs.
        var fams = await Poll(async () =>
        {
            var r = await _http.GetAsync("/api/families");
            var n = await r.Content.ReadFromJsonAsync<JsonArray>();
            return n!.Count > 0 ? n : null;
        }, ms: 5000);
        var famId = fams![0]!["id"]!.GetValue<string>();

        var versions = await _http.GetFromJsonAsync<JsonArray>($"/api/families/{famId}/versions");
        Assert.NotNull(versions);
        Assert.Equal(3, versions.Count);

        // locate preserve + drop seeded runs
        JsonArray? runs = null;
        await Poll(async () =>
        {
            runs = await _http.GetFromJsonAsync<JsonArray>($"/api/families/{famId}/runs");
            var complete = runs!.All(r => r!["status"] != null && r["status"]!.GetValue<string>() == "complete");
            return complete && runs!.Count >= 2 ? runs : null;
        }, ms: 8000);
        Assert.NotNull(runs);

        var preserveRun = runs!.First(r => r!["unknownPolicy"]!.GetValue<string>() == "preserve");
        var dropRun = runs.First(r => r!["unknownPolicy"]!.GetValue<string>() == "drop");

        // The v1->v3 cell keeps the legacy-only region field in the sidecar.
        var preserveCell = await GetCellResult(famId, preserveRun!["runId"]!.GetValue<long>(), "v1", "v3");
        var dropCell = await GetCellResult(famId, dropRun!["runId"]!.GetValue<long>(), "v1", "v3");

        Assert.True(preserveCell["reversible"]!.GetValue<bool>(),
            "preserve 模式下 v1→v3 应可逆（region 通过 sidecar 保留）");
        Assert.False(dropCell["reversible"]!.GetValue<bool>(),
            "drop 模式下 v1→v3 必须不可逆（region 被丢弃）");
        Assert.Contains("丢弃未知字段", dropCell["reversibilityNote"]!.GetValue<string>());
    }

    private async Task<JsonObject> GetCellResult(string famId, long runId, string w, string r)
    {
        var n = await _http.GetFromJsonAsync<JsonObject>(
            $"/api/families/{famId}/runs/{runId}/cells/{w}/{r}");
        return n!["result"]!.AsObject();
    }

    private static async Task<T?> Poll<T>(Func<Task<T?>> probe, int ms = 5000) where T : class
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            var v = await probe();
            if (v is not null) return v;
            await Task.Delay(150);
        }
        return null;
    }
}
