using EliteSoft.Erwin.AlterDdl.Core.Abstractions;
using EliteSoft.Erwin.AlterDdl.Core.Models;
using EliteSoft.Erwin.AlterDdl.Core.Parsing;
using EliteSoft.Erwin.AlterDdl.Core.Pipeline;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AlterDdl.Core.Tests;

/// <summary>
/// Covers the <see cref="IModelMapProvider"/> abstraction and the JSON DTO
/// round-trip used by the Worker-based provider. The runtime no longer has a
/// built-in "sibling .xml" path: callers must supply a provider.
/// </summary>
public class ModelMapProviderTests
{
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
    public async Task CompareOrchestrator_uses_injected_provider()
    {
        // Proves no .xml-on-disk requirement: only the injected provider
        // is consulted for model maps. If the provider returns a valid map
        // the compare succeeds regardless of any file-system siblings.
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

    // -------- JSON round-trip --------

    [Fact]
    public void Serialize_and_Deserialize_round_trip_preserves_ObjectIds_and_parent_chain()
    {
        const string xml = """
            <erwin xmlns="http://www.erwin.com/dm">
              <Entity id="{E1}+0" name="CUSTOMER">
                <Attribute id="{A1}+0" name="id"/>
                <Attribute id="{A2}+0" name="email"/>
              </Entity>
              <Entity id="{E2}+0" name="ORDER">
                <Attribute id="{A3}+0" name="id"/>
              </Entity>
            </erwin>
            """;
        var original = ErwinXmlObjectIdMapper.ParseXml(xml);
        var dto = ModelMapJsonSerializer.ToDto(original, "v1.erwin");
        var json = ModelMapJsonSerializer.Serialize(dto);
        var rebuilt = ModelMapJsonSerializer.Deserialize(json);

        rebuilt.TotalObjectCount.Should().Be(5);
        rebuilt.TryGetId("Entity", "CUSTOMER", out var custId).Should().BeTrue();
        custId.Should().Be("{E1}+0");
        rebuilt.TryGetAttributeId("CUSTOMER", "email", out var emailId).Should().BeTrue();
        emailId.Should().Be("{A2}+0");
        rebuilt.TryGetAttributeId("ORDER", "id", out var orderIdAttr).Should().BeTrue();
        orderIdAttr.Should().Be("{A3}+0");
    }

    [Fact]
    public void Deserialize_rejects_unknown_schema_version()
    {
        var badJson = """{ "schemaVersion": "999", "sourceErwinPath": "x", "objects": [] }""";
        var act = () => ModelMapJsonSerializer.Deserialize(badJson);
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Deserialize_rejects_missing_objects_array()
    {
        // A well-formed JSON with the right schema version but no objects
        // array is a malformed dump. Parser must reject it loudly rather
        // than silently returning an empty map.
        var badJson = """{ "schemaVersion": "1", "sourceErwinPath": "x" }""";
        var act = () => ModelMapJsonSerializer.Deserialize(badJson);
        act.Should().Throw<InvalidDataException>();
    }

    // -------- helpers (duplicated mini-fixtures) --------

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
