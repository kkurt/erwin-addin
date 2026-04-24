using System.Text.Json;
using System.Text.Json.Serialization;

using EliteSoft.Erwin.AlterDdl.ComInterop;
using EliteSoft.Erwin.AlterDdl.Core.Abstractions;
using EliteSoft.Erwin.AlterDdl.Core.Models;
using EliteSoft.Erwin.AlterDdl.Core.Parsing;
using EliteSoft.Erwin.AlterDdl.Core.Pipeline;

using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

// Config: X-Api-Key header is read from `Api:ApiKey` in appsettings.json or env `API__APIKEY`.
var apiKey = builder.Configuration["Api:ApiKey"] ?? "dev-change-me";

var app = builder.Build();

// --- X-Api-Key middleware (internal network auth) ---
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/health"))
    {
        await next();
        return;
    }
    if (!ctx.Request.Headers.TryGetValue("X-Api-Key", out var provided)
        || !string.Equals(provided, apiKey, StringComparison.Ordinal))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync("missing or invalid X-Api-Key");
        return;
    }
    await next();
});

var jsonOpts = new JsonSerializerOptions
{
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

app.MapGet("/health", () => Results.Ok(new { status = "ok", phase = "2" }));

app.MapGet("/", () => Results.Ok(new { service = "erwin-alter-ddl", version = "0.1.0-alpha" }));

// Mock-mode compare endpoint for smoke / local harnesses. Clients upload:
//   * leftErwin, rightErwin          - binary .erwin files (not parsed, stored
//                                      for the orchestrator to hand to SCAPI)
//   * leftModelMap, rightModelMap    - pre-dumped ErwinModelMapDto JSONs
//                                      (produced by the Worker's dump-model)
//   * diffXls                        - CC XLS (MockScapiSession reads this)
// Out-of-process live mode (kicks off Worker per upload) is pending.
app.MapPost("/compare", async (HttpRequest request, CancellationToken ct) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "expected multipart/form-data" });

    var form = await request.ReadFormAsync(ct);
    var leftErwin = form.Files.GetFile("leftErwin");
    var rightErwin = form.Files.GetFile("rightErwin");
    var leftMap = form.Files.GetFile("leftModelMap");
    var rightMap = form.Files.GetFile("rightModelMap");
    var diffXls = form.Files.GetFile("diffXls");

    if (leftErwin is null || rightErwin is null || leftMap is null || rightMap is null || diffXls is null)
        return Results.BadRequest(new { error = "leftErwin, rightErwin, leftModelMap, rightModelMap, diffXls form files are required" });

    var workDir = Path.Combine(Path.GetTempPath(), "erwin-alter-ddl-api-" + Guid.NewGuid());
    Directory.CreateDirectory(workDir);
    try
    {
        var leftErwinPath = await SaveUploadAsync(leftErwin, workDir, "v1.erwin", ct);
        var rightErwinPath = await SaveUploadAsync(rightErwin, workDir, "v2.erwin", ct);
        var leftMapPath = await SaveUploadAsync(leftMap, workDir, "v1.model-map.json", ct);
        var rightMapPath = await SaveUploadAsync(rightMap, workDir, "v2.model-map.json", ct);
        await SaveUploadAsync(diffXls, workDir, "diff.xls", ct);

        var leftMapObj = ModelMapJsonSerializer.DeserializeFile(leftMapPath);
        var rightMapObj = ModelMapJsonSerializer.DeserializeFile(rightMapPath);
        var mapProvider = new PrebuiltModelMapProvider(leftErwinPath, leftMapObj, rightErwinPath, rightMapObj);

        await using var session = new MockScapiSession(workDir);
        var orchestrator = new CompareOrchestrator(session, mapProvider);
        var result = await orchestrator.CompareAsync(leftErwinPath, rightErwinPath, CompareOptions.Default, ct);

        var json = JsonSerializer.Serialize(result, jsonOpts);
        return Results.Text(json, "application/json");
    }
    finally
    {
        try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
    }
});

app.Run();

static async Task<string> SaveUploadAsync(IFormFile file, string dir, string name, CancellationToken ct)
{
    var full = Path.Combine(dir, name);
    await using var fs = File.Create(full);
    await file.CopyToAsync(fs, ct);
    return full;
}
