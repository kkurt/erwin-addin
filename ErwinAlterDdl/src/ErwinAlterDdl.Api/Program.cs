using System.Text.Json;
using System.Text.Json.Serialization;

using EliteSoft.Erwin.AlterDdl.ComInterop;
using EliteSoft.Erwin.AlterDdl.Core.Abstractions;
using EliteSoft.Erwin.AlterDdl.Core.Models;
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

// Phase 2 synchronous compare endpoint. Clients upload two .erwin files and the
// two matching .xml exports (erwin's Export-As-XML siblings). Async job API is a
// Phase 3 improvement once OutOfProcessScapiSession exists.
app.MapPost("/compare", async (HttpRequest request, CancellationToken ct) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "expected multipart/form-data" });

    var form = await request.ReadFormAsync(ct);
    var leftErwin = form.Files.GetFile("leftErwin");
    var leftXml = form.Files.GetFile("leftXml");
    var rightErwin = form.Files.GetFile("rightErwin");
    var rightXml = form.Files.GetFile("rightXml");
    var diffXls = form.Files.GetFile("diffXls"); // optional for Phase 2 mock

    if (leftErwin is null || rightErwin is null || leftXml is null || rightXml is null)
        return Results.BadRequest(new { error = "leftErwin/leftXml/rightErwin/rightXml form files are required" });

    var workDir = Path.Combine(Path.GetTempPath(), "erwin-alter-ddl-api-" + Guid.NewGuid());
    Directory.CreateDirectory(workDir);
    try
    {
        var leftErwinPath = await SaveUploadAsync(leftErwin, workDir, "v1.erwin", ct);
        _ = await SaveUploadAsync(leftXml, workDir, "v1.xml", ct);
        var rightErwinPath = await SaveUploadAsync(rightErwin, workDir, "v2.erwin", ct);
        _ = await SaveUploadAsync(rightXml, workDir, "v2.xml", ct);
        if (diffXls is not null)
            await SaveUploadAsync(diffXls, workDir, "diff.xls", ct);

        // Phase 2 currently serves only mock session. Phase 3 will branch based
        // on a request header / query option to use out-of-process SCAPI.
        if (diffXls is null)
            return Results.BadRequest(new { error = "Phase 2 REST requires diffXls form file (mock mode). Phase 3 will run SCAPI out-of-process." });

        await using var session = new MockScapiSession(workDir);
        var orchestrator = new CompareOrchestrator(session);
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
