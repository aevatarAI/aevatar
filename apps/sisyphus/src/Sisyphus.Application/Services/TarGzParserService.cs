using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Sisyphus.Application.Models.Upload;

namespace Sisyphus.Application.Services;

public partial class TarGzParserService(ILogger<TarGzParserService> logger)
{
    /// <summary>Filename regex: {kg_id}__{label}__{atom_type}__{hash}.tex</summary>
    [GeneratedRegex(@"^(KG-\d{8}-\d{5})__(.+)__(.+)__(.+)\.tex$")]
    private static partial Regex TexFilenameRegex();

    public record ParseResult(List<RedNode> Nodes, List<RedEdge> Edges, List<string> UnresolvedLabels);

    public virtual ParseResult ParseAndValidate(Stream tarGzStream)
    {
        logger.LogInformation("Starting tar.gz parse");

        // Read all entries into memory: filename -> bytes
        var entries = ExtractEntries(tarGzStream);
        logger.LogInformation("Extracted {Count} entries from tar.gz", entries.Count);

        // Separate .tex and .meta.json files
        var texFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var metaFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, data) in entries)
        {
            // Strip directory prefixes — use just the filename
            var fileName = Path.GetFileName(name);
            if (string.IsNullOrEmpty(fileName)) continue;

            if (fileName.EndsWith(".tex.meta.json", StringComparison.OrdinalIgnoreCase))
                metaFiles[fileName] = data;
            else if (fileName.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                texFiles[fileName] = data;
        }

        logger.LogInformation("Found {TexCount} .tex files and {MetaCount} .meta.json files",
            texFiles.Count, metaFiles.Count);

        // Parse file pairs into red nodes
        var nodes = new List<RedNode>();
        var labelToKgId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (texFileName, texData) in texFiles)
        {
            var match = TexFilenameRegex().Match(texFileName);
            if (!match.Success)
            {
                logger.LogWarning("Skipping file with non-matching filename: {FileName}", texFileName);
                continue;
            }

            var kgId = match.Groups[1].Value;
            var label = match.Groups[2].Value;
            var atomType = match.Groups[3].Value;

            var metaFileName = texFileName + ".meta.json";
            if (!metaFiles.TryGetValue(metaFileName, out var metaData))
            {
                logger.LogWarning("Skipping .tex file without matching .meta.json: {FileName}", texFileName);
                continue;
            }

            MetaJson? meta;
            try
            {
                meta = JsonSerializer.Deserialize<MetaJson>(metaData, MetaJsonOptions);
                if (meta is null)
                {
                    logger.LogError("Null meta.json deserialization for {FileName}", metaFileName);
                    continue;
                }
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Malformed meta.json: {FileName}", metaFileName);
                continue;
            }

            var texContent = System.Text.Encoding.UTF8.GetString(texData);

            var node = new RedNode
            {
                KgId = meta.KgId ?? kgId,
                Label = meta.Label ?? label,
                AtomType = meta.AtomType ?? atomType,
                TexContent = texContent,
                SourcePath = meta.SourcePath,
                SourceTexLabel = meta.SourceTexLabel,
                CanonicalLabel = meta.CanonicalLabel,
                UnitEnv = meta.UnitEnv,
                UnitFingerprint = meta.UnitFingerprint,
                MergedSha256 = meta.MergedSha256,
                ExtractorVersion = meta.ExtractorVersion,
                ProofOrphan = meta.ProofOrphan ?? false,
                ParentEdges = (meta.ParentEdges ?? []).Select(pe => new RawParentEdge
                {
                    Parent = pe.Parent ?? "",
                    EdgeType = pe.EdgeType ?? "inference_ref",
                    EdgeSource = pe.EdgeSource,
                    EdgeReason = pe.EdgeReason,
                }).ToList(),
            };

            nodes.Add(node);
            labelToKgId[node.Label] = node.KgId;

            logger.LogDebug("Parsed red node: KgId={KgId}, Label={Label}, AtomType={AtomType}",
                node.KgId, node.Label, node.AtomType);
        }

        logger.LogInformation("Parsed {NodeCount} red nodes", nodes.Count);

        // Pre-validation: check all parent labels resolve
        var unresolvedLabels = ValidateParentReferences(nodes, labelToKgId);
        if (unresolvedLabels.Count > 0)
        {
            logger.LogWarning("Found {Count} unresolved parent references: {Labels}",
                unresolvedLabels.Count, string.Join(", ", unresolvedLabels));
            return new ParseResult(nodes, [], unresolvedLabels);
        }

        // Build red edges from parent_edges
        var edges = BuildEdges(nodes, labelToKgId);
        logger.LogInformation("Built {EdgeCount} red edges", edges.Count);

        return new ParseResult(nodes, edges, []);
    }

    private static Dictionary<string, byte[]> ExtractEntries(Stream tarGzStream)
    {
        var entries = new Dictionary<string, byte[]>();

        if (tarGzStream.Length == 0 || (tarGzStream.CanSeek && tarGzStream.Position >= tarGzStream.Length))
            return entries;

        using var gzipStream = new GZipStream(tarGzStream, CompressionMode.Decompress);
        using var tarReader = new TarReader(gzipStream);

        while (tarReader.GetNextEntry() is { } entry)
        {
            if (entry.DataStream is null) continue;

            using var ms = new MemoryStream();
            entry.DataStream.CopyTo(ms);
            entries[entry.Name] = ms.ToArray();
        }

        return entries;
    }

    private static List<RedEdge> BuildEdges(List<RedNode> nodes, Dictionary<string, string> labelToKgId)
    {
        var edges = new List<RedEdge>();

        foreach (var node in nodes)
        {
            foreach (var pe in node.ParentEdges)
            {
                if (!labelToKgId.TryGetValue(pe.Parent, out var targetKgId))
                    continue; // Already validated — should not happen

                edges.Add(new RedEdge
                {
                    SourceKgId = node.KgId,
                    TargetKgId = targetKgId,
                    EdgeType = pe.EdgeType,
                    EdgeSource = pe.EdgeSource,
                    EdgeReason = pe.EdgeReason,
                });
            }
        }

        return edges;
    }

    private static List<string> ValidateParentReferences(
        List<RedNode> nodes, Dictionary<string, string> labelToKgId)
    {
        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in nodes)
        {
            foreach (var pe in node.ParentEdges)
            {
                if (!labelToKgId.ContainsKey(pe.Parent))
                    unresolved.Add(pe.Parent);
            }
        }

        return [.. unresolved];
    }

    // JSON options for meta.json deserialization (snake_case field names)
    private static readonly JsonSerializerOptions MetaJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    // Internal model matching meta.json structure
    private sealed class MetaJson
    {
        public string? KgId { get; set; }
        public string? Label { get; set; }
        public string? AtomType { get; set; }
        public string? SourcePath { get; set; }
        public string? SourceTexLabel { get; set; }
        public string? CanonicalLabel { get; set; }
        public string? UnitEnv { get; set; }
        public string? UnitFingerprint { get; set; }
        public string? MergedSha256 { get; set; }
        public string? ExtractorVersion { get; set; }
        public bool? ProofOrphan { get; set; }
        public List<MetaParentEdge>? ParentEdges { get; set; }
    }

    private sealed class MetaParentEdge
    {
        public string? Parent { get; set; }
        public string? EdgeType { get; set; }
        public string? EdgeSource { get; set; }
        public string? EdgeReason { get; set; }
    }
}
