using EliteSoft.Erwin.AlterDdl.Core.Abstractions;
using EliteSoft.Erwin.AlterDdl.Core.Correlation;
using EliteSoft.Erwin.AlterDdl.Core.Models;
using EliteSoft.Erwin.AlterDdl.Core.Parsing;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EliteSoft.Erwin.AlterDdl.Core.Pipeline;

/// <summary>
/// End-to-end orchestrator for the compare flow:
///   [SCAPI]    CompleteCompare v1+v2    -> xls path
///   [SCAPI]    PropertyBag read x2      -> metadata
///   [Core]     XLS parse                -> xlsRows
///   [Provider] left + right model maps  -> ErwinModelMap x2
///   [Core]     Correlator               -> List&lt;Change&gt;
///
/// The model-map source is pluggable via <see cref="IModelMapProvider"/>. The
/// default is <see cref="XmlFileModelMapProvider"/>, preserving the original
/// "sibling .xml export" contract; add-in / worker callers can inject their
/// own provider that reads from SCAPI or an out-of-process dump.
/// </summary>
public sealed class CompareOrchestrator
{
    private readonly IScapiSession _session;
    private readonly IModelMapProvider _mapProvider;
    private readonly ILogger<CompareOrchestrator> _logger;

    public CompareOrchestrator(IScapiSession session, ILogger<CompareOrchestrator>? logger = null)
        : this(session, mapProvider: null, logger)
    {
    }

    public CompareOrchestrator(
        IScapiSession session,
        IModelMapProvider? mapProvider,
        ILogger<CompareOrchestrator>? logger = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _mapProvider = mapProvider ?? new XmlFileModelMapProvider();
        _logger = logger ?? NullLogger<CompareOrchestrator>.Instance;
    }

    public async Task<CompareResult> CompareAsync(
        string leftErwinPath,
        string rightErwinPath,
        CompareOptions options,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leftErwinPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rightErwinPath);
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(leftErwinPath)) throw new FileNotFoundException(leftErwinPath);
        if (!File.Exists(rightErwinPath)) throw new FileNotFoundException(rightErwinPath);

        // 1. Read metadata for both PUs. Sequential (not parallel) because SCAPI
        //    is a process-level singleton on r10.10 - two concurrent worker
        //    processes race on the same erwin.exe LocalServer and corrupt its
        //    state, breaking the later CC call with RPC_E_SERVERFAULT.
        _logger.LogInformation("Reading metadata for {Left} and {Right}", leftErwinPath, rightErwinPath);
        var leftMeta = await _session.ReadModelMetadataAsync(leftErwinPath, ct).ConfigureAwait(false);
        var rightMeta = await _session.ReadModelMetadataAsync(rightErwinPath, ct).ConfigureAwait(false);

        // 2. Run CompleteCompare to produce the XLS.
        _logger.LogInformation("Running CompleteCompare");
        var xlsArtifact = await _session
            .RunCompleteCompareAsync(leftErwinPath, rightErwinPath, options, ct)
            .ConfigureAwait(false);
        _logger.LogInformation("CC produced {Path} ({Size} bytes)", xlsArtifact.XlsPath, xlsArtifact.SizeBytes);

        // 3. Parse XLS + fetch both model maps through the configured provider.
        //    The provider decides the source (xml sibling / live SCAPI / etc).
        var xlsRows = XlsDiffParser.Parse(xlsArtifact.XlsPath);
        var leftMap = await _mapProvider.BuildMapAsync(leftErwinPath, ct).ConfigureAwait(false);
        var rightMap = await _mapProvider.BuildMapAsync(rightErwinPath, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Parsed: {Rows} xls rows, {LeftObjects} left objects, {RightObjects} right objects",
            xlsRows.Count, leftMap.TotalObjectCount, rightMap.TotalObjectCount);

        // 4. Correlate.
        var changes = ChangeCorrelator.Correlate(leftMap, rightMap, xlsRows);
        _logger.LogInformation("Correlator produced {Count} changes", changes.Count);

        // 5. Optional: generate CREATE DDL for each side (Phase 3 emitter fuel).
        DdlArtifact? leftDdl = null, rightDdl = null;
        if (options.IncludeCreateDdl)
        {
            _logger.LogInformation("Generating CREATE DDL for both sides");
            leftDdl = await _session.GenerateCreateDdlAsync(leftErwinPath, DdlOptions.Default, ct).ConfigureAwait(false);
            rightDdl = await _session.GenerateCreateDdlAsync(rightErwinPath, DdlOptions.Default, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "DDL: left {LeftPath} ({LeftSize}), right {RightPath} ({RightSize})",
                leftDdl.SqlPath, leftDdl.SizeBytes, rightDdl.SqlPath, rightDdl.SizeBytes);
        }

        return new CompareResult(leftMeta, rightMeta, changes, xlsArtifact)
        {
            LeftDdl = leftDdl,
            RightDdl = rightDdl,
        };
    }
}
