using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;

using EliteSoft.Erwin.AlterDdl.ComInterop;
using EliteSoft.Erwin.AlterDdl.Core.Abstractions;
using EliteSoft.Erwin.AlterDdl.Core.Emitting;
using EliteSoft.Erwin.AlterDdl.Core.Emitting.Dialect;
using EliteSoft.Erwin.AlterDdl.Core.Models;
using EliteSoft.Erwin.AlterDdl.Core.Parsing;
using EliteSoft.Erwin.AlterDdl.Core.Pipeline;

using Microsoft.Extensions.Logging;

using Serilog;

namespace EliteSoft.Erwin.AlterDdl.Cli;

internal static class ExitCodes
{
    public const int Success = 0;
    public const int ComActivation = 1;
    public const int License = 2;
    public const int InputValidation = 3;
    public const int CompleteCompareFailed = 4;
    public const int DiffParsingFailed = 5;
    public const int ModelMutationFailed = 6;  // unused in Phase 2
    public const int AlterDdlFailed = 7;       // unused in Phase 2
    public const int Unhandled = 99;
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var leftOption = new Option<FileInfo>("--left") { Description = "Baseline .erwin file", IsRequired = true };
        var rightOption = new Option<FileInfo>("--right") { Description = "Target .erwin file", IsRequired = true };
        var outOption = new Option<FileInfo>("--out") { Description = "Output JSON file", IsRequired = true };
        var levelOption = new Option<string>(
            aliases: ["--compare-level"],
            getDefaultValue: () => "LP",
            description: "LP | L | P | DB");
        var optionSetOption = new Option<string?>(
            aliases: ["--cc-option-set"],
            getDefaultValue: () => "Standard",
            description: "\"Standard\" | \"Advance\" | \"Speed\" OR path to custom CC option XML");
        var sessionMode = new Option<string>(
            aliases: ["--session-mode"],
            getDefaultValue: () => "out-of-process",
            description: "out-of-process (default, spawns Worker per op) | mock (needs --artifacts-dir + sibling .model-map.json)");
        var artifactsDir = new Option<DirectoryInfo?>(
            aliases: ["--artifacts-dir"],
            description: "Used by --session-mode mock: directory containing diff.xls and (optional) metadata");
        var verboseOption = new Option<bool>(aliases: ["--verbose"], description: "Verbose console logging");
        var includeDdlOption = new Option<bool>(
            aliases: ["--include-create-ddl"],
            description: "Also run FEModel_DDL on each side and attach DdlArtifact to the result");
        var emitSqlOption = new Option<FileInfo?>(
            aliases: ["--emit-sql"],
            description: "Emit alter SQL for the target server into this file (MSSQL in Phase 3.C; Oracle + Db2 in 3.E)");

        var root = new RootCommand("erwin-ddl-diff: produce a typed Change list for two .erwin models")
        {
            leftOption, rightOption, outOption, levelOption, optionSetOption, sessionMode, artifactsDir, verboseOption, includeDdlOption, emitSqlOption,
        };

        root.SetHandler(async ctx =>
        {
            var left = ctx.ParseResult.GetValueForOption(leftOption)!;
            var right = ctx.ParseResult.GetValueForOption(rightOption)!;
            var outFile = ctx.ParseResult.GetValueForOption(outOption)!;
            var level = ctx.ParseResult.GetValueForOption(levelOption)!;
            var preset = ctx.ParseResult.GetValueForOption(optionSetOption)!;
            var mode = ctx.ParseResult.GetValueForOption(sessionMode)!;
            var artifacts = ctx.ParseResult.GetValueForOption(artifactsDir);
            var verbose = ctx.ParseResult.GetValueForOption(verboseOption);
            var includeDdl = ctx.ParseResult.GetValueForOption(includeDdlOption);
            var emitSql = ctx.ParseResult.GetValueForOption(emitSqlOption);

            ctx.ExitCode = await RunAsync(
                left, right, outFile, level, preset, mode, artifacts, verbose, includeDdl, emitSql, ctx.GetCancellationToken());
        });

        return await root.InvokeAsync(args);
    }

    private static async Task<int> RunAsync(
        FileInfo left, FileInfo right, FileInfo outFile,
        string level, string preset, string sessionMode, DirectoryInfo? artifactsDir,
        bool verbose, bool includeDdl, FileInfo? emitSql, CancellationToken ct)
    {
        ConfigureLogging(verbose);
        var logger = CreateLogger();

        if (!left.Exists || !right.Exists)
        {
            logger.LogError("Input file missing: left exists={L} right exists={R}", left.Exists, right.Exists);
            return ExitCodes.InputValidation;
        }

        CompareLevel parsedLevel;
        try { parsedLevel = ParseLevel(level); }
        catch (ArgumentException ex) { logger.LogError(ex, "bad --compare-level"); return ExitCodes.InputValidation; }

        var options = CompareOptions.Default with
        {
            Level = parsedLevel,
            PresetOrOptionXmlPath = preset,
            IncludeCreateDdl = includeDdl,
        };

        IScapiSession session;
        try
        {
            session = BuildSession(sessionMode, artifactsDir, logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "cannot initialize SCAPI session (mode={Mode})", sessionMode);
            return ExitCodes.ComActivation;
        }

        IModelMapProvider mapProvider;
        try
        {
            mapProvider = BuildMapProvider(sessionMode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "cannot initialize model-map provider");
            return ExitCodes.ComActivation;
        }

        await using (session)
        {
            var orchestrator = new CompareOrchestrator(session, mapProvider, CreateOrchestratorLogger());
            CompareResult result;
            try
            {
                result = await orchestrator.CompareAsync(left.FullName, right.FullName, options, ct);
            }
            catch (FileNotFoundException ex)
            {
                logger.LogError(ex, "required file missing");
                return ExitCodes.InputValidation;
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError(ex, "CompleteCompare returned failure");
                return ExitCodes.CompleteCompareFailed;
            }

            try
            {
                await WriteJsonAsync(outFile.FullName, result, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "failed to write JSON result");
                return ExitCodes.DiffParsingFailed;
            }

            if (emitSql is not null)
            {
                try
                {
                    var registry = new SqlEmitterRegistry()
                        .Register(new MssqlEmitter(), "SQL Server", "MSSQL", "SQLServer")
                        .Register(new OracleEmitter(), "Oracle", "Oracle Database")
                        .Register(new Db2Emitter(), "Db2", "IBM Db2", "DB2", "Db2 z/OS", "DB2 for z/OS");
                    if (!registry.TryResolve(result.RightMetadata.TargetServer, out var emitter))
                    {
                        logger.LogWarning(
                            "no SQL emitter for Target_Server '{Target}'; skipping --emit-sql (Phase 3.E adds Oracle + Db2)",
                            result.RightMetadata.TargetServer);
                    }
                    else
                    {
                        var script = emitter.Emit(result);
                        await File.WriteAllTextAsync(emitSql.FullName, script.ToScript(),
                            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
                        logger.LogInformation(
                            "OK: emitted {N} {Dialect} statements to {Path}",
                            script.Statements.Count, script.Dialect, emitSql.FullName);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "failed to emit alter SQL");
                    return ExitCodes.AlterDdlFailed;
                }
            }

            logger.LogInformation(
                "OK: wrote {Count} changes to {Path}",
                result.Changes.Count, outFile.FullName);
            return ExitCodes.Success;
        }
    }

    private static CompareLevel ParseLevel(string s) => s.ToUpperInvariant() switch
    {
        "LP" => CompareLevel.LogicalAndPhysical,
        "L" => CompareLevel.LogicalOnly,
        "P" => CompareLevel.PhysicalOnly,
        "DB" => CompareLevel.DatabaseOnly,
        _ => throw new ArgumentException($"invalid compare level: {s}")
    };

    private static IScapiSession BuildSession(string mode, DirectoryInfo? artifactsDir, Microsoft.Extensions.Logging.ILogger logger)
    {
        switch (mode.ToLowerInvariant())
        {
            case "mock":
                if (artifactsDir is null || !artifactsDir.Exists)
                    throw new ArgumentException("--session-mode mock requires --artifacts-dir pointing to a directory with diff.xls");
                logger.LogInformation("Using MockScapiSession over {Dir}", artifactsDir.FullName);
                return new MockScapiSession(artifactsDir.FullName);
            case "in-process":
                throw new NotSupportedException(
                    "in-process session requires a live SCAPI handle; only the add-in integration uses this mode");
            case "out-of-process":
                logger.LogInformation("Using OutOfProcessScapiSession (Worker)");
                return new OutOfProcessScapiSession(
                    logger: LoggerFactory.Create(b => b.AddSerilog(Log.Logger))
                        .CreateLogger<OutOfProcessScapiSession>());
            default:
                throw new ArgumentException($"unknown --session-mode: {mode}");
        }
    }

    /// <summary>
    /// Pick the <see cref="IModelMapProvider"/> to pair with the chosen
    /// session mode. Real-SCAPI modes (out-of-process) drive the Worker to
    /// walk <c>session.ModelObjects</c> directly from the .erwin file (no
    /// sibling .xml required). Mock mode expects the artifacts dir to
    /// contain pre-built JSON dumps named after the .erwin files.
    /// </summary>
    private static IModelMapProvider BuildMapProvider(string mode)
    {
        switch (mode.ToLowerInvariant())
        {
            case "out-of-process":
                return new WorkerJsonModelMapProvider(
                    logger: LoggerFactory.Create(b => b.AddSerilog(Log.Logger))
                        .CreateLogger<WorkerJsonModelMapProvider>());
            case "mock":
                // Mock mode expects a pre-dumped JSON next to each .erwin
                // file: v1.erwin -> v1.model-map.json. Helpful for unit /
                // fixture test runs that do not have a Worker available.
                return new SiblingJsonModelMapProvider();
            case "in-process":
                throw new NotSupportedException(
                    "in-process mode is owned by the add-in; CLI uses out-of-process or mock");
            default:
                throw new ArgumentException($"unknown --session-mode: {mode}");
        }
    }

    /// <summary>
    /// Fallback provider for <c>--session-mode mock</c>: reads
    /// <c>&lt;erwinPath&gt;.model-map.json</c> next to the .erwin file.
    /// Produced by hand or by a prior out-of-process run.
    /// </summary>
    private sealed class SiblingJsonModelMapProvider : IModelMapProvider
    {
        public Task<Core.Parsing.ErwinModelMap> BuildMapAsync(string erwinPath, CancellationToken ct = default)
        {
            var jsonPath = Path.ChangeExtension(erwinPath, ".model-map.json");
            if (!File.Exists(jsonPath))
                throw new FileNotFoundException(
                    "sibling model-map JSON missing. Mock session mode expects a .model-map.json next to the .erwin file.",
                    jsonPath);
            return Task.FromResult(Core.Parsing.ModelMapJsonSerializer.DeserializeFile(jsonPath));
        }
    }

    private static void ConfigureLogging(bool verbose)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? Serilog.Events.LogEventLevel.Verbose : Serilog.Events.LogEventLevel.Information)
            .WriteTo.Console(
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private static Microsoft.Extensions.Logging.ILogger CreateLogger() =>
        LoggerFactory.Create(b => b.AddSerilog(Log.Logger)).CreateLogger("ErwinAlterDdl.Cli");

    private static ILogger<CompareOrchestrator> CreateOrchestratorLogger() =>
        LoggerFactory.Create(b => b.AddSerilog(Log.Logger)).CreateLogger<CompareOrchestrator>();

    private static async Task WriteJsonAsync(string path, CompareResult result, CancellationToken ct)
    {
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        using var fs = File.Create(path);
        await JsonSerializer.SerializeAsync(fs, result, opts, ct);
    }
}
