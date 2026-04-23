using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;

using EliteSoft.Erwin.AlterDdl.ComInterop;
using EliteSoft.Erwin.AlterDdl.Core.Abstractions;
using EliteSoft.Erwin.AlterDdl.Core.Models;
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
            getDefaultValue: () => "mock",
            description: "mock | in-process (Phase 2 defaults to mock, Phase 3 adds out-of-process)");
        var artifactsDir = new Option<DirectoryInfo?>(
            aliases: ["--artifacts-dir"],
            description: "Used by --session-mode mock: directory containing diff.xls and (optional) metadata");
        var verboseOption = new Option<bool>(aliases: ["--verbose"], description: "Verbose console logging");

        var root = new RootCommand("erwin-ddl-diff: produce a typed Change list for two .erwin models")
        {
            leftOption, rightOption, outOption, levelOption, optionSetOption, sessionMode, artifactsDir, verboseOption,
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

            ctx.ExitCode = await RunAsync(
                left, right, outFile, level, preset, mode, artifacts, verbose, ctx.GetCancellationToken());
        });

        return await root.InvokeAsync(args);
    }

    private static async Task<int> RunAsync(
        FileInfo left, FileInfo right, FileInfo outFile,
        string level, string preset, string sessionMode, DirectoryInfo? artifactsDir,
        bool verbose, CancellationToken ct)
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

        await using (session)
        {
            var orchestrator = new CompareOrchestrator(session, CreateOrchestratorLogger());
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
                    "in-process session requires a live SCAPI handle; use the add-in integration or wait for Phase 3 out-of-process");
            case "out-of-process":
                throw new NotImplementedException("Phase 3 feature; use --session-mode mock for now");
            default:
                throw new ArgumentException($"unknown --session-mode: {mode}");
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
