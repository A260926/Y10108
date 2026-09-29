using CompatibilityLab.Api;
using CompatibilityLab.Engine;
using CompatibilityLab.Store;

var builder = WebApplication.CreateBuilder(args);

var dbPath = Environment.GetEnvironmentVariable("COMPLAB_DB_PATH")
    ?? builder.Configuration["Lab:DatabasePath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "compatibility-lab.db");
var db = new Database(dbPath);
db.Initialize();

builder.Services.AddSingleton(db);
builder.Services.AddSingleton<LabStore>();
builder.Services.AddSingleton<RunService>();
builder.Services.AddSingleton<ExportService>();
builder.Services.AddSingleton<DemoSeeder>();
builder.Services.AddSingleton<MatrixRunner>();
builder.Services.AddHostedService<MatrixBackgroundService>();

var app = builder.Build();
app.UseStaticFiles();

app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (ConflictException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status409Conflict;
        await Results.Json(new { code = ex.Code, error = ex.Message }).ExecuteAsync(ctx);
    }
    catch (NotFoundException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await Results.Json(new { code = "NOT_FOUND", error = ex.Message }).ExecuteAsync(ctx);
    }
    catch (SchemaValidationException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await Results.Json(new { code = "SCHEMA_INVALID", error = ex.Message }).ExecuteAsync(ctx);
    }
});

Endpoints.Map(app);
app.MapFallbackToFile("index.html");

// Seed the demo everywhere except the Testing environment. (The test host injects
// seed-related env vars, so gating on those would wrongly suppress the live demo;
// environment name is the reliable signal.)
var seedDisabled = string.Equals(builder.Environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase)
    || string.Equals(builder.Configuration["Lab:Seed"], "false", StringComparison.OrdinalIgnoreCase);

if (!seedDisabled)
{
    using var scope = app.Services.CreateScope();
    var seeder = scope.ServiceProvider.GetRequiredService<DemoSeeder>();
    var seeded = seeder.Seed();
    if (seeded is not null)
    {
        var runner = scope.ServiceProvider.GetRequiredService<MatrixRunner>();
        runner.Drain(seeded.PreserveRunId);
        runner.Drain(seeded.DropRunId);
        app.Logger.LogInformation("已预置演示家族 {Family}，preserve 运行 {P}，drop 运行 {D}",
            seeded.FamilyId, seeded.PreserveRunId, seeded.DropRunId);
    }
}

app.Run();

public partial class Program { }
