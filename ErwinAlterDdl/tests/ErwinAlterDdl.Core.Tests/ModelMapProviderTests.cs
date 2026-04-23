using EliteSoft.Erwin.AlterDdl.Core.Abstractions;
using EliteSoft.Erwin.AlterDdl.Core.Models;
using EliteSoft.Erwin.AlterDdl.Core.Parsing;
using EliteSoft.Erwin.AlterDdl.Core.Pipeline;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AlterDdl.Core.Tests;

/// <summary>
/// Tests the <see cref="IModelMapProvider"/> abstraction that replaces the
/// previous hard-coded "xml sibling" lookup inside
/// <see cref="CompareOrchestrator"/>. The provider indirection lets the add-in
/// (which cannot safely export XML at runtime) plug its own map source while
/// keeping the CLI / test fixtures on the default XML-sibling path.
/// </summary>
public class ModelMapProviderTests
{
    [Fact]
    public async Task XmlFileProvider_loads_sibling_xml_next_to_erwin_path()
    {
        using var dir = new TempDir();
        var erwinPath = dir.Create("v1.erwin", "fake binary");
        dir.Create("v1.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <erwin xmlns="http://www.erwin.com/dm">
              <Entity id="{E1}+0" name="CUSTOMER"/>
            </erwin>
            """);

        var provider = new XmlFileModelMapProvider();
        var map = await provider.BuildMapAsync(erwinPath);
        map.TotalObjectCount.Should().Be(1);
        map.TryGetId("Entity", "CUSTOMER", out var id).Should().BeTrue();
        id.Should().Be("{E1}+0");
    }

    [Fact]
    public async Task XmlFileProvider_throws_with_clear_guidance_when_xml_missing()
    {
        using var dir = new TempDir();
        var erwinPath = dir.Create("v1.erwin", "fake binary");
        // no v1.xml

        var provider = new XmlFileModelMapProvider();
        var act = () => provider.BuildMapAsync(erwinPath);
        var ex = await act.Should().ThrowAsync<FileNotFoundException>();
        ex.Which.Message.Should().Contain("Actions > Export > XML");
    }

    [Fact]
    public async Task PrebuiltProvider_returns_preloaded_maps_by_path()
    {
        var left = ErwinXmlObjectIdMapper.ParseXml("""
            <erwin xmlns="http://www.erwin.com/dm">
              <Entity id="{E1}+0" name="CUSTOMER"/>
            </erwin>
            """);
        var right = ErwinXmlObjectIdMapper.ParseXml("""
            <erwin xmlns="http://www.erwin.com/dm">
              <Entity id="{E1}+0" name="CUSTOMER"/>
              <Entity id="{E2}+0" name="ORDER"/>
            </erwin>
            """);
        var provider = new PrebuiltModelMapProvider("v1.erwin", left, "v2.erwin", right);

        (await provider.BuildMapAsync("v1.erwin")).TotalObjectCount.Should().Be(1);
        (await provider.BuildMapAsync("v2.erwin")).TotalObjectCount.Should().Be(2);

        var act = () => provider.BuildMapAsync("unknown.erwin");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CompareOrchestrator_uses_injected_provider_over_xml_sibling()
    {
        // Goal: prove the orchestrator no longer demands an .xml sibling when
        // a custom provider is supplied. If the provider returns a valid map
        // the compare succeeds even when no .xml file exists on disk.
        using var dir = new TempDir();
        var v1 = dir.Create("v1.erwin", "bin");
        var v2 = dir.Create("v2.erwin", "bin");
        var xlsPath = dir.Create("diff.xls", "<html><body><table></table></body></html>");

        var leftMap = ErwinXmlObjectIdMapper.ParseXml("""
            <erwin xmlns="http://www.erwin.com/dm">
              <Entity id="{E1}+0" name="CUSTOMER"/>
            </erwin>
            """);
        var rightMap = ErwinXmlObjectIdMapper.ParseXml("""
            <erwin xmlns="http://www.erwin.com/dm">
              <Entity id="{E1}+0" name="CUSTOMER"/>
              <Entity id="{E2}+0" name="ORDER"/>
            </erwin>
            """);

        var session = new TestSession { XlsOutPath = xlsPath };
        var provider = new PrebuiltModelMapProvider(v1, leftMap, v2, rightMap);
        var orch = new CompareOrchestrator(session, provider);

        var result = await orch.CompareAsync(v1, v2, CompareOptions.Default);
        result.Changes.OfType<EntityAdded>().Should().ContainSingle(c => c.Target.Name == "ORDER");
    }

    [Fact]
    public async Task CompareOrchestrator_default_provider_still_requires_xml_sibling_for_backcompat()
    {
        using var dir = new TempDir();
        var v1 = dir.Create("v1.erwin", "bin");
        var v2 = dir.Create("v2.erwin", "bin");
        // deliberately no v1.xml / v2.xml and no custom provider
        var session = new TestSession { XlsOutPath = dir.Create("diff.xls", "<html><body><table></table></body></html>") };
        var orch = new CompareOrchestrator(session); // default = XmlFileModelMapProvider

        var act = () => orch.CompareAsync(v1, v2, CompareOptions.Default);
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    // ---------- helpers (duplicated tiny fixtures) ----------

    private sealed class TestSession : IScapiSession
    {
        public string? XlsOutPath { get; set; }

        public Task<CompareArtifact> RunCompleteCompareAsync(
            string leftErwinPath, string rightErwinPath, CompareOptions options, CancellationToken ct = default)
        {
            if (XlsOutPath is null) throw new InvalidOperationException("XlsOutPath not set");
            return Task.FromResult(new CompareArtifact(XlsOutPath, new FileInfo(XlsOutPath).Length, 0));
        }

        public Task<DdlArtifact> GenerateCreateDdlAsync(
            string erwinPath, DdlOptions options, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ModelMetadata> ReadModelMetadataAsync(string erwinPath, CancellationToken ct = default)
            => Task.FromResult(new ModelMetadata(
                PersistenceUnitId: "{PU}+0",
                Name: Path.GetFileNameWithoutExtension(erwinPath),
                ModelType: "Physical",
                TargetServer: "SQL Server",
                TargetServerVersion: 15,
                TargetServerMinorVersion: 0));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "model-map-provider-" + Guid.NewGuid());
            Directory.CreateDirectory(Path);
        }
        public string Create(string name, string content)
        {
            var full = System.IO.Path.Combine(Path, name);
            File.WriteAllText(full, content);
            return full;
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
