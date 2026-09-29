using Microsoft.Extensions.Hosting;
using System.Net.Http.Json;
using CompatibilityLab.Engine;
using CompatibilityLab.Store;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CompatibilityLab.Tests;

public class LabApp : WebApplicationFactory<Program>
{
    public string DbPath { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "complab_" + Guid.NewGuid().ToString("N") + ".db");

    static LabApp() { }

    public LabApp()
    {
        // Read before host build so Program's top-level Database picks this path.
        Environment.SetEnvironmentVariable("COMPLAB_DB_PATH", DbPath);
        Environment.SetEnvironmentVariable("COMPLAB_SEED", "false");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseContentRoot(TestEnvironment.ProjectRoot);
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Lab:Seed"] = "false",
                ["Lab:DatabasePath"] = DbPath
            });
        });
    }

    public T Resolve<T>() where T : notnull
    {
        using var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<T>();
    }
}

[CollectionDefinition("lab")]
public class LabCollection : ICollectionFixture<LabApp> { }

public static class HttpExt
{
    public static async Task<JsonContent> ReadJson(this HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return new JsonContent(r.StatusCode, text);
    }
}

public sealed class JsonContent
{
    public System.Net.HttpStatusCode Status { get; }
    public string Text { get; }
    public System.Text.Json.Nodes.JsonNode? Node { get; }
    public JsonContent(System.Net.HttpStatusCode status, string text)
    {
        Status = status; Text = text;
        Node = string.IsNullOrWhiteSpace(text) ? null : System.Text.Json.Nodes.JsonNode.Parse(text);
    }
    public System.Text.Json.Nodes.JsonNode this[string key] => Node![key]!;
}
