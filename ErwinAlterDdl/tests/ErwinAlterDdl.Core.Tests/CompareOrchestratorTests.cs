using EliteSoft.Erwin.AlterDdl.Core.Abstractions;
using EliteSoft.Erwin.AlterDdl.Core.Models;
using EliteSoft.Erwin.AlterDdl.Core.Parsing;
using EliteSoft.Erwin.AlterDdl.Core.Pipeline;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AlterDdl.Core.Tests;

public class CompareOrchestratorTests
{
    [Fact]
    public async Task CompareAsync_throws_when_erwin_file_missing()
    {
        using var workDir = new TempDir();
        var session = new TestSession { XlsOutPath = workDir.CreateEmpty("diff.xls") };
        var provider = new PrebuiltModelMapProvider("no/such/v1.erwin", EmptyMap(), "also/no.erwin", EmptyMap());
        var orch = new CompareOrchestrator(session, provider);

        var act = () => orch.CompareAsync("no/such/v1.erwin", "also/no.erwin", CompareOptions.Default);
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Ctor_rejects_null_mapProvider()
    {
        Action act = () => _ = new CompareOrchestrator(new TestSession(), mapProvider: null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task CompareAsync_returns_result_with_changes_and_metadata()
    {
        using var workDir = new TempDir();
        var v1Erwin = workDir.Create("v1.erwin", "fake binary");
        var v2Erwin = workDir.Create("v2.erwin", "fake binary");

        var v1Map = ErwinXmlObjectIdMapper.ParseXml("""
            <erwin xmlns="http://www.erwin.com/dm">
              <Entity id="{E1}+0" name="CUSTOMER"/>
              <Entity id="{E2}+0" name="OBSOLETE"/>
            </erwin>
            """);
        var v2Map = ErwinXmlObjectIdMapper.ParseXml("""
            <erwin xmlns="http://www.erwin.com/dm">
              <Entity id="{E1}+0" name="CUSTOMER"/>
              <Entity id="{E3}+0" name="NEWLY_ADDED"/>
            </erwin>
            """);

        // CC reports both the drop (OBSOLETE on the left only) and the add
        // (NEWLY_ADDED on the right only). The correlator's CC-allowlist
        // filter requires that every emitted change be backed by a CC row,
        // so both Entity/Table rows must appear in the XLS even though the
        // structural diff alone could find them.
        var xlsPath = workDir.Create("diff.xls",
            "<html><body><table>"
            + "<tr><td>Entity/Table</td><td>OBSOLETE</td><td>Not Equal</td><td></td></tr>"
            + "<tr><td>Entity/Table</td><td></td><td>Not Equal</td><td>NEWLY_ADDED</td></tr>"
            + "</table></body></html>");

        var session = new TestSession { XlsOutPath = xlsPath };
        var provider = new PrebuiltModelMapProvider(v1Erwin, v1Map, v2Erwin, v2Map);
        var orch = new CompareOrchestrator(session, provider);
        var result = await orch.CompareAsync(v1Erwin, v2Erwin, CompareOptions.Default);

        result.Changes.OfType<EntityAdded>().Should().ContainSingle(c => c.Target.Name == "NEWLY_ADDED");
        result.Changes.OfType<EntityDropped>().Should().ContainSingle(c => c.Target.Name == "OBSOLETE");
        result.XlsArtifact.XlsPath.Should().Be(xlsPath);
        result.LeftMetadata.Name.Should().Be("left");
        result.RightMetadata.Name.Should().Be("right");
    }

    // ---------- helpers ----------

    private static ErwinModelMap EmptyMap() => ErwinXmlObjectIdMapper.ParseXml(
        """<erwin xmlns="http://www.erwin.com/dm"/>""");

    private sealed class TestSession : IScapiSession
    {
        public string? XlsOutPath { get; set; }
        public string XlsPayload { get; set; } = string.Empty;

        public Task<CompareArtifact> RunCompleteCompareAsync(
            string leftErwinPath, string rightErwinPath, CompareOptions options, CancellationToken ct = default)
        {
            if (XlsOutPath is null) throw new InvalidOperationException("XlsOutPath not set");
            if (XlsPayload.Length > 0) File.WriteAllText(XlsOutPath, XlsPayload);
            return Task.FromResult(new CompareArtifact(XlsOutPath, new FileInfo(XlsOutPath).Length, 0));
        }

        public Task<DdlArtifact> GenerateCreateDdlAsync(
            string erwinPath, DdlOptions options, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ModelMetadata> ReadModelMetadataAsync(string erwinPath, CancellationToken ct = default)
        {
            var label = erwinPath.Contains("v2") ? "right" : "left";
            return Task.FromResult(new ModelMetadata(
                PersistenceUnitId: $"{{PU-{label}}}+0",
                Name: label,
                ModelType: "Physical",
                TargetServer: "SQL Server",
                TargetServerVersion: 15,
                TargetServerMinorVersion: 0));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "erwin-alter-ddl-test-" + Guid.NewGuid());
            Directory.CreateDirectory(Path);
        }

        public string Create(string fileName, string content)
        {
            var full = System.IO.Path.Combine(Path, fileName);
            File.WriteAllText(full, content);
            return full;
        }

        public string CreateEmpty(string fileName)
        {
            var full = System.IO.Path.Combine(Path, fileName);
            File.WriteAllText(full, "");
            return full;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
