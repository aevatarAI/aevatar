using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Sisyphus.Application.Services;

namespace Sisyphus.Application.Tests;

public class TarGzParserServiceTests
{
    private readonly TarGzParserService _sut = new(NullLogger<TarGzParserService>.Instance);

    // ─── Helpers ───

    private static MemoryStream BuildTarGz(params (string fileName, string content)[] files)
    {
        var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            foreach (var (fileName, content) in files)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, fileName)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
                };
                tar.WriteEntry(entry);
            }
        }
        ms.Position = 0;
        return ms;
    }

    private static string MakeMetaJson(
        string kgId,
        string label,
        string atomType = "tp-note",
        List<object>? parentEdges = null)
    {
        var meta = new Dictionary<string, object>
        {
            ["kg_id"] = kgId,
            ["label"] = label,
            ["atom_type"] = atomType,
            ["source_path"] = "/path/to/source.tex",
            ["source_tex_label"] = "sec:intro",
            ["canonical_label"] = "sec-intro",
            ["unit_env"] = "gap_note",
            ["unit_fingerprint"] = "fp-123",
            ["merged_sha256"] = "sha256-abc",
            ["extractor_version"] = "v1.0",
            ["proof_orphan"] = false,
        };
        if (parentEdges is not null)
            meta["parent_edges"] = parentEdges;
        return JsonSerializer.Serialize(meta);
    }

    private static (string texFile, string metaFile) MakeFilePair(
        string kgId, string label, string atomType = "tp-note", string hash = "h-abc123")
    {
        var texFile = $"{kgId}__{label}__{atomType}__{hash}.tex";
        var metaFile = texFile + ".meta.json";
        return (texFile, metaFile);
    }

    // ─── Tests ───

    [Fact]
    public void ParseAndValidate_ValidPair_ReturnsRedNode()
    {
        var (texFile, metaFile) = MakeFilePair("KG-20260305-00001", "lbl-intro-h123", "tp-note");
        var meta = MakeMetaJson("KG-20260305-00001", "lbl-intro-h123", "tp-note");

        using var stream = BuildTarGz(
            (texFile, "\\section{Introduction} Some TeX content"),
            (metaFile, meta));

        var result = _sut.ParseAndValidate(stream);

        result.Nodes.Should().HaveCount(1);
        result.Edges.Should().BeEmpty();
        result.UnresolvedLabels.Should().BeEmpty();

        var node = result.Nodes[0];
        node.KgId.Should().Be("KG-20260305-00001");
        node.Label.Should().Be("lbl-intro-h123");
        node.AtomType.Should().Be("tp-note");
        node.TexContent.Should().Contain("Introduction");
        node.SourcePath.Should().Be("/path/to/source.tex");
        node.UnitEnv.Should().Be("gap_note");
    }

    [Fact]
    public void ParseAndValidate_MultipleFiles_ReturnsAllNodes()
    {
        var files = new List<(string, string)>();
        for (var i = 1; i <= 3; i++)
        {
            var kgId = $"KG-20260305-{i:D5}";
            var label = $"lbl-node-{i}";
            var (texFile, metaFile) = MakeFilePair(kgId, label);
            files.Add((texFile, $"TeX content {i}"));
            files.Add((metaFile, MakeMetaJson(kgId, label)));
        }

        using var stream = BuildTarGz([.. files]);
        var result = _sut.ParseAndValidate(stream);

        result.Nodes.Should().HaveCount(3);
    }

    [Fact]
    public void ParseAndValidate_ExtractsKgIdFromFilename()
    {
        var (texFile, metaFile) = MakeFilePair("KG-20260305-00042", "lbl-test-xyz", "tp-lemma", "h-deadbeef");
        var meta = MakeMetaJson("KG-20260305-00042", "lbl-test-xyz", "tp-lemma");

        using var stream = BuildTarGz((texFile, "content"), (metaFile, meta));
        var result = _sut.ParseAndValidate(stream);

        result.Nodes.Should().HaveCount(1);
        result.Nodes[0].KgId.Should().Be("KG-20260305-00042");
    }

    [Fact]
    public void ParseAndValidate_ParsesParentEdges()
    {
        var kgId = "KG-20260305-00001";
        var label = "lbl-child";
        var (texFile, metaFile) = MakeFilePair(kgId, label);

        var parentEdges = new List<object>
        {
            new Dictionary<string, string>
            {
                ["parent"] = "lbl-parent",
                ["edge_type"] = "inference_ref",
                ["edge_source"] = "kg_ingest_atoms",
                ["edge_reason"] = "source_refs",
            }
        };
        var meta = MakeMetaJson(kgId, label, parentEdges: parentEdges);

        using var stream = BuildTarGz((texFile, "content"), (metaFile, meta));
        var result = _sut.ParseAndValidate(stream);

        result.Nodes[0].ParentEdges.Should().HaveCount(1);
        result.Nodes[0].ParentEdges[0].Parent.Should().Be("lbl-parent");
        result.Nodes[0].ParentEdges[0].EdgeType.Should().Be("inference_ref");
    }

    [Fact]
    public void ParseAndValidate_BuildsRedEdges()
    {
        // Node A references Node B via parent_edges
        var kgIdA = "KG-20260305-00001";
        var labelA = "lbl-node-a";
        var kgIdB = "KG-20260305-00002";
        var labelB = "lbl-node-b";

        var (texA, metaA) = MakeFilePair(kgIdA, labelA);
        var (texB, metaB) = MakeFilePair(kgIdB, labelB, hash: "h-bbb");

        var parentEdges = new List<object>
        {
            new Dictionary<string, string>
            {
                ["parent"] = labelB,
                ["edge_type"] = "inference_proof_anchor",
            }
        };

        using var stream = BuildTarGz(
            (texA, "content A"),
            (metaA, MakeMetaJson(kgIdA, labelA, parentEdges: parentEdges)),
            (texB, "content B"),
            (metaB, MakeMetaJson(kgIdB, labelB)));

        var result = _sut.ParseAndValidate(stream);

        result.Edges.Should().HaveCount(1);
        result.Edges[0].SourceKgId.Should().Be(kgIdA);
        result.Edges[0].TargetKgId.Should().Be(kgIdB);
        result.Edges[0].EdgeType.Should().Be("inference_proof_anchor");
    }

    [Fact]
    public void ParseAndValidate_ValidParentRefs_NoUnresolvedLabels()
    {
        var kgIdA = "KG-20260305-00001";
        var labelA = "lbl-a";
        var kgIdB = "KG-20260305-00002";
        var labelB = "lbl-b";

        var (texA, metaA) = MakeFilePair(kgIdA, labelA);
        var (texB, metaB) = MakeFilePair(kgIdB, labelB, hash: "h-bbb");

        var parentEdges = new List<object>
        {
            new Dictionary<string, string> { ["parent"] = labelB, ["edge_type"] = "inference_ref" }
        };

        using var stream = BuildTarGz(
            (texA, "content A"),
            (metaA, MakeMetaJson(kgIdA, labelA, parentEdges: parentEdges)),
            (texB, "content B"),
            (metaB, MakeMetaJson(kgIdB, labelB)));

        var result = _sut.ParseAndValidate(stream);

        result.UnresolvedLabels.Should().BeEmpty();
    }

    [Fact]
    public void ParseAndValidate_UnresolvedParentRef_ReturnsUnresolvedLabels()
    {
        var kgId = "KG-20260305-00001";
        var label = "lbl-child";
        var (texFile, metaFile) = MakeFilePair(kgId, label);

        var parentEdges = new List<object>
        {
            new Dictionary<string, string> { ["parent"] = "nonexistent-label", ["edge_type"] = "inference_ref" }
        };

        using var stream = BuildTarGz(
            (texFile, "content"),
            (metaFile, MakeMetaJson(kgId, label, parentEdges: parentEdges)));

        var result = _sut.ParseAndValidate(stream);

        result.UnresolvedLabels.Should().Contain("nonexistent-label");
        result.Edges.Should().BeEmpty();
    }

    [Fact]
    public void ParseAndValidate_EmptyTarGz_ReturnsEmptyLists()
    {
        using var stream = BuildTarGz();
        var result = _sut.ParseAndValidate(stream);

        result.Nodes.Should().BeEmpty();
        result.Edges.Should().BeEmpty();
    }

    [Fact]
    public void ParseAndValidate_MissingMetaJson_SkipsFile()
    {
        var (texFile, _) = MakeFilePair("KG-20260305-00001", "lbl-lonely");

        using var stream = BuildTarGz((texFile, "content without meta"));
        var result = _sut.ParseAndValidate(stream);

        result.Nodes.Should().BeEmpty();
    }

    [Fact]
    public void ParseAndValidate_MalformedMetaJson_SkipsFile()
    {
        var (texFile, metaFile) = MakeFilePair("KG-20260305-00001", "lbl-bad-meta");

        using var stream = BuildTarGz(
            (texFile, "content"),
            (metaFile, "{ invalid json !!!"));

        var result = _sut.ParseAndValidate(stream);

        result.Nodes.Should().BeEmpty();
    }
}
