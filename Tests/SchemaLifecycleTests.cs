using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace CompatibilityLab.Tests;

[Collection("lab")]
public class SchemaLifecycleTests : ApiTestBase
{
    public SchemaLifecycleTests(LabApp app) : base(app) { }

    [Fact]
    public async Task Published_schema_is_immutable_and_changes_must_form_new_version()
    {
        var fam = await NewFamily();
        await PutDraft(fam, TestData.BaseSchema);
        await Publish(fam, "v1");

        var put = await Http.PutAsJsonAsync($"/api/families/{fam}/versions/v1", new { });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, put.StatusCode);
        var body = await put.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("VERSION_IMMUTABLE", body!["code"]!.GetValue<string>());

        var del = await Http.DeleteAsync($"/api/families/{fam}/versions/v1");
        Assert.Equal(System.Net.HttpStatusCode.Conflict, del.StatusCode);

        // republishing same version is rejected
        await PutDraft(fam, TestData.BaseSchema);
        var republish = await Http.PostAsJsonAsync($"/api/families/{fam}/publish",
            new { version = "v1", expectedDraftRevision = (long?)null });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, republish.StatusCode);
        var rb = await republish.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("VERSION_IMMUTABLE", rb!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Two_concurrent_draft_saves_conflict_on_stale_revision()
    {
        var fam = await NewFamily();
        var rev = await PutDraft(fam, TestData.BaseSchema);

        // page A saves successfully
        var a = await Http.PutAsJsonAsync($"/api/families/{fam}/draft",
            new { schemaJson = TestData.BaseSchema, expectedRevision = rev });
        a.EnsureSuccessStatusCode();

        // page B still holds old revision -> 409
        var b = await Http.PutAsJsonAsync($"/api/families/{fam}/draft",
            new { schemaJson = TestData.BaseSchema, expectedRevision = rev });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, b.StatusCode);
        var bb = await b.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("DRAFT_REVISION_CONFLICT", bb!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Secrets_are_never_stored_or_returned_as_literal()
    {
        var fam = await NewFamily();
        await PutDraft(fam, TestData.BaseSchema);
        await Publish(fam, "v1");
        await UploadSample(fam, "v1", TestData.BaseSample);

        var r = await Http.GetAsync($"/api/families/{fam}/versions/v1/sample");
        var n = await r.Content.ReadFromJsonAsync<JsonObject>();
        var text = n!.ToJsonString();
        Assert.DoesNotContain("plaintext-secret-value", text);
        Assert.Equal(1, n!["secretCount"]!.GetValue<int>());
        var token = n!["redactedSample"]!["token"]!.GetValue<string>();
        Assert.StartsWith("__SECRET__:", token);
        Assert.DoesNotContain("plaintext-secret-value", System.IO.File.ReadAllText(App.DbPath));
    }

    [Fact]
    public async Task Invalid_schema_is_rejected_with_reason()
    {
        var fam = await NewFamily();
        var bad = TestData.BaseSchema.Replace("\"string\", \"required\": true", "\"bogus\", \"required\": true");
        var r = await Http.PutAsJsonAsync($"/api/families/{fam}/draft",
            new { schemaJson = bad, expectedRevision = (int?)null });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, r.StatusCode);
        var n = await r.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("SCHEMA_INVALID", n!["code"]!.GetValue<string>());
    }
}
