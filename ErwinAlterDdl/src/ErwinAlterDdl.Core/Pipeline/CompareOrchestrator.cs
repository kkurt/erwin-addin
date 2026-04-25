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
/// The <see cref="IModelMapProvider"/> is required - Core no longer assumes a
/// sibling <c>.xml</c> export. Callers supply the provider that matches their
/// runtime: CLI uses <c>WorkerJsonModelMapProvider</c> (walks the .erwin via a
/// short-lived worker), tests use <c>PrebuiltModelMapProvider</c>, and the
/// add-in plugs in a live-SCAPI provider once it lands.
/// </summary>
public sealed class CompareOrchestrator
{
    private readonly IScapiSession _session;
    private readonly IModelMapProvider _mapProvider;
    private readonly ILogger<CompareOrchestrator> _logger;

    public CompareOrchestrator(
        IScapiSession session,
        IModelMapProvider mapProvider,
        ILogger<CompareOrchestrator>? logger = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _mapProvider = mapProvider ?? throw new ArgumentNullException(nameof(mapProvider));
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
        // Path existence checks only apply when we actually have a disk path.
        // Live-SCAPI providers route through a virtual pathKey (e.g.
        // "active-pu://<name>") that is not a file.
        if (IsFilePath(leftErwinPath) && !File.Exists(leftErwinPath)) throw new FileNotFoundException(leftErwinPath);
        if (IsFilePath(rightErwinPath) && !File.Exists(rightErwinPath)) throw new FileNotFoundException(rightErwinPath);

        // 1. Read metadata for both PUs. Sequential (not parallel) because SCAPI
        //    is a process-level singleton on r10.10 - two concurrent worker
        //    processes race on the same erwin.exe LocalServer and corrupt its
        //    state, breaking the later CC call with RPC_E_SERVERFAULT.
        _logger.LogInformation("Reading metadata for {Left} and {Right}", leftErwinPath, rightErwinPath);
        var leftMeta = await _session.ReadModelMetadataAsync(leftErwinPath, ct).ConfigureAwait(false);
        var rightMeta = await _session.ReadModelMetadataAsync(rightErwinPath, ct).ConfigureAwait(false);

        // 2. Run CompleteCompare to produce the XLS (unless the caller told
        //    us to skip it - e.g. the in-process add-in which cannot save the
        //    active Mart PU without corrupting it).
        CompareArtifact xlsArtifact;
        IReadOnlyList<XlsDiffRow> xlsRows;
        if (options.SkipCompleteCompare)
        {
            _logger.LogInformation("Skipping CompleteCompare (structural diff only)");
            xlsArtifact = new CompareArtifact(string.Empty, 0, 0);
            xlsRows = Array.Empty<XlsDiffRow>();
        }
        else
        {
            _logger.LogInformation("Running CompleteCompare");
            xlsArtifact = await _session
                .RunCompleteCompareAsync(leftErwinPath, rightErwinPath, options, ct)
                .ConfigureAwait(false);
            _logger.LogInformation("CC produced {Path} ({Size} bytes)", xlsArtifact.XlsPath, xlsArtifact.SizeBytes);
            xlsRows = XlsDiffParser.Parse(xlsArtifact.XlsPath);
        }

        // 3. Fetch both model maps through the configured provider.
        var leftMap = await _mapProvider.BuildMapAsync(leftErwinPath, ct).ConfigureAwait(false);
        var rightMap = await _mapProvider.BuildMapAsync(rightErwinPath, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Parsed: {Rows} xls rows, {LeftObjects} left objects, {RightObjects} right objects",
            xlsRows.Count, leftMap.TotalObjectCount, rightMap.TotalObjectCount);

        // 4. Correlate.
        var changes = ChangeCorrelator.Correlate(leftMap, rightMap, xlsRows);
        _logger.LogInformation("Correlator produced {Count} changes", changes.Count);

        // 4b. Pull schema-by-entity-name out of the XLS Entity/Table rows.
        //     erwin's CC consistently emits these as <schema>.<table>; we
        //     keep them aside so the SQL emitter can produce
        //     [schema].[table] even when FEModel_DDL output happens to be
        //     schema-less (some target-server defaults).
        var schemaByEntity = ExtractSchemaByEntity(xlsRows);
        if (schemaByEntity.Count > 0)
            _logger.LogInformation(
                "XLS yielded {Count} entity-schema mappings (e.g. first: {Sample})",
                schemaByEntity.Count,
                schemaByEntity.First().Key + "->" + schemaByEntity.First().Value);

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
            SchemaByEntityName = schemaByEntity.Count > 0 ? schemaByEntity : null,
        };
    }

    private static IReadOnlyDictionary<string, string> ExtractSchemaByEntity(IReadOnlyList<XlsDiffRow> xlsRows)
    {
        // erwin's CC XLS uses "Entity/Table" or just "Table" depending on the
        // metamodel version / DBMS adapter. Both are entity rows.
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? defaultSchema = null;

        foreach (var row in xlsRows)
        {
            bool isEntityRow =
                string.Equals(row.Type, "Entity/Table", StringComparison.Ordinal)
                || string.Equals(row.Type, "Table", StringComparison.Ordinal)
                || string.Equals(row.Type, "Entity", StringComparison.Ordinal);
            if (!isEntityRow) continue;

            var raw = row.LeftValue.Length > 0 ? row.LeftValue : row.RightValue;
            if (string.IsNullOrEmpty(raw)) continue;

            int dot = raw.IndexOf('.');
            if (dot <= 0 || dot >= raw.Length - 1) continue;
            var schema = raw[..dot];
            var bare = raw[(dot + 1)..];
            if (!map.ContainsKey(bare)) map[bare] = schema;
            // Track the most common schema in this XLS so we can fall back
            // for newly-added (yet-unowned) tables that come bare.
            defaultSchema ??= schema;
        }

        // Second pass: bare entity rows inherit the default schema. erwin
        // sometimes emits a fresh table without a schema prefix even though
        // the model itself is firmly under one (e.g. "dbo").
        if (!string.IsNullOrEmpty(defaultSchema))
        {
            foreach (var row in xlsRows)
            {
                bool isEntityRow =
                    string.Equals(row.Type, "Entity/Table", StringComparison.Ordinal)
                    || string.Equals(row.Type, "Table", StringComparison.Ordinal)
                    || string.Equals(row.Type, "Entity", StringComparison.Ordinal);
                if (!isEntityRow) continue;

                var raw = row.LeftValue.Length > 0 ? row.LeftValue : row.RightValue;
                if (string.IsNullOrEmpty(raw)) continue;
                if (raw.Contains('.')) continue;
                if (!map.ContainsKey(raw)) map[raw] = defaultSchema;
            }
        }
        return map;
    }

    /// <summary>
    /// Returns true when the given path looks like a real filesystem path
    /// (not a virtual locator like <c>mart://</c> or <c>active-pu://</c>).
    /// </summary>
    private static bool IsFilePath(string path) =>
        !(path.StartsWith("mart://", StringComparison.OrdinalIgnoreCase)
          || path.StartsWith("erwin://", StringComparison.OrdinalIgnoreCase)
          || path.StartsWith("active-pu://", StringComparison.OrdinalIgnoreCase));
}
