using EliteSoft.Erwin.AlterDdl.Core.Abstractions;
using EliteSoft.Erwin.AlterDdl.Core.Correlation;
using EliteSoft.Erwin.AlterDdl.Core.Models;
using EliteSoft.Erwin.AlterDdl.Core.Parsing;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EliteSoft.Erwin.AlterDdl.Core.Pipeline;

/// <summary>
/// End-to-end orchestrator for the Phase 2 compare flow:
///   [SCAPI] CompleteCompare v1+v2 -> xls path
///   [SCAPI] PropertyBag read x2   -> metadata
///   [Core]  XLS parse             -> xlsRows
///   [Core]  XML parse (v1, v2)    -> left/right ModelMap
///   [Core]  Correlator             -> List&lt;Change&gt;
/// </summary>
public sealed class CompareOrchestrator
{
    private readonly IScapiSession _session;
    private readonly ILogger<CompareOrchestrator> _logger;

    public CompareOrchestrator(IScapiSession session, ILogger<CompareOrchestrator>? logger = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
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

        // 3. Parse XLS + both XMLs. The XMLs are co-located next to the .erwin
        //    files by erwin's "Export as XML" convention.
        var leftXmlPath = Path.ChangeExtension(leftErwinPath, ".xml");
        var rightXmlPath = Path.ChangeExtension(rightErwinPath, ".xml");
        if (!File.Exists(leftXmlPath)) throw new FileNotFoundException("xml export missing", leftXmlPath);
        if (!File.Exists(rightXmlPath)) throw new FileNotFoundException("xml export missing", rightXmlPath);

        var xlsRows = XlsDiffParser.Parse(xlsArtifact.XlsPath);
        var leftMap = ErwinXmlObjectIdMapper.ParseFile(leftXmlPath);
        var rightMap = ErwinXmlObjectIdMapper.ParseFile(rightXmlPath);

        _logger.LogInformation(
            "Parsed: {Rows} xls rows, {LeftObjects} left objects, {RightObjects} right objects",
            xlsRows.Count, leftMap.TotalObjectCount, rightMap.TotalObjectCount);

        // 4. Correlate.
        var changes = ChangeCorrelator.Correlate(leftMap, rightMap, xlsRows);
        _logger.LogInformation("Correlator produced {Count} changes", changes.Count);

        return new CompareResult(leftMeta, rightMeta, changes, xlsArtifact);
    }
}
