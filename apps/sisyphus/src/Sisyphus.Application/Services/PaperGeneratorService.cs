using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sisyphus.Application.Models.Graph;
using Sisyphus.Application.Models.Paper;

namespace Sisyphus.Application.Services;

public class PaperGeneratorService(
    IOptions<PaperOptions> paperOptions,
    ILogger<PaperGeneratorService> logger)
{
    // amsthm theorem-like environments (proof uses built-in \begin{proof})
    private static readonly Dictionary<string, string> TheoremEnvNames = new()
    {
        ["theorem"] = "theorem",
        ["lemma"] = "lemma",
        ["definition"] = "definition",
        ["corollary"] = "corollary",
        ["conjecture"] = "conjecture",
        ["proposition"] = "proposition",
        ["remark"] = "remark",
        ["conclusion"] = "conclusion",
        ["example"] = "example",
        ["notation"] = "notation",
        ["axiom"] = "axiom",
        ["observation"] = "observation",
        ["note"] = "note",
    };

    /// <summary>
    /// Generates a PDF from a blue graph snapshot using LaTeX + tectonic.
    /// </summary>
    public virtual async Task<byte[]> GeneratePdfAsync(BlueGraphSnapshot snapshot, CancellationToken ct = default)
    {
        var sortedNodes = TopologicalSort(snapshot);
        var latex = GenerateLatex(sortedNodes, snapshot.Edges);

        logger.LogInformation("Generated LaTeX document: {Length} chars, {NodeCount} nodes",
            latex.Length, sortedNodes.Count);

        return await CompilePdfAsync(latex, ct);
    }

    /// <summary>
    /// Topological sort via Kahn's algorithm. Falls back to type-based ordering on cycles.
    /// </summary>
    internal static List<GraphNode> TopologicalSort(BlueGraphSnapshot snapshot)
    {
        var nodeIds = new HashSet<string>(snapshot.Nodes.Select(n => n.Id));
        var adjacency = new Dictionary<string, List<string>>();
        var inDegree = new Dictionary<string, int>();

        foreach (var node in snapshot.Nodes)
        {
            adjacency[node.Id] = [];
            inDegree[node.Id] = 0;
        }

        foreach (var edge in snapshot.Edges)
        {
            if (!nodeIds.Contains(edge.Source) || !nodeIds.Contains(edge.Target))
                continue;

            // edge: source depends on target (source references/proves target)
            // so target should come before source
            adjacency[edge.Target].Add(edge.Source);
            inDegree[edge.Source] = inDegree.GetValueOrDefault(edge.Source) + 1;
        }

        var queue = new Queue<string>();
        foreach (var (id, deg) in inDegree)
        {
            if (deg == 0)
                queue.Enqueue(id);
        }

        var sorted = new List<string>();
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            sorted.Add(current);

            foreach (var neighbor in adjacency.GetValueOrDefault(current, []))
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                    queue.Enqueue(neighbor);
            }
        }

        var nodeMap = snapshot.Nodes.ToDictionary(n => n.Id);

        if (sorted.Count == snapshot.Nodes.Count)
            return sorted.Select(id => nodeMap[id]).ToList();

        // Cycle detected — fall back to type-based ordering
        var typeOrder = new Dictionary<string, int>
        {
            ["axiom"] = 0, ["notation"] = 1, ["definition"] = 2,
            ["lemma"] = 3, ["proposition"] = 4, ["theorem"] = 5,
            ["corollary"] = 6, ["conjecture"] = 7, ["proof"] = 8,
            ["example"] = 9, ["remark"] = 10, ["observation"] = 11,
            ["note"] = 12, ["conclusion"] = 13,
        };

        return snapshot.Nodes
            .OrderBy(n => typeOrder.GetValueOrDefault(n.Type, 99))
            .ToList();
    }

    internal static string GenerateLatex(List<GraphNode> sortedNodes, List<GraphEdge> edges)
    {
        var sb = new StringBuilder();

        // Preamble
        sb.AppendLine(@"\documentclass[11pt,a4paper]{article}");
        sb.AppendLine(@"\usepackage[utf8]{inputenc}");
        sb.AppendLine(@"\usepackage{amsmath,amssymb,amsthm}");
        sb.AppendLine(@"\usepackage{mathtools,mathrsfs,bm}");
        sb.AppendLine(@"\usepackage{hyperref}");
        sb.AppendLine(@"\usepackage{xcolor}");
        // Gracefully handle undefined commands from LLM-generated body content
        sb.AppendLine(@"\newcommand{\placeholder}[1]{\textbf{#1}}");
        sb.AppendLine();

        // Declare amsthm environments with shared counter and correct styles (proof is built-in)
        // Plain style (italic body): theorem, lemma, proposition, corollary, conjecture
        sb.AppendLine(@"\newtheorem{theorem}{Theorem}");
        sb.AppendLine(@"\newtheorem{lemma}[theorem]{Lemma}");
        sb.AppendLine(@"\newtheorem{proposition}[theorem]{Proposition}");
        sb.AppendLine(@"\newtheorem{corollary}[theorem]{Corollary}");
        sb.AppendLine(@"\newtheorem{conjecture}[theorem]{Conjecture}");

        // Definition style (upright body): definition, example, notation, axiom
        sb.AppendLine(@"\theoremstyle{definition}");
        sb.AppendLine(@"\newtheorem{definition}[theorem]{Definition}");
        sb.AppendLine(@"\newtheorem{example}[theorem]{Example}");
        sb.AppendLine(@"\newtheorem{notation}[theorem]{Notation}");
        sb.AppendLine(@"\newtheorem{axiom}[theorem]{Axiom}");

        // Remark style (upright body, smaller title): remark, observation, note, conclusion
        sb.AppendLine(@"\theoremstyle{remark}");
        sb.AppendLine(@"\newtheorem{remark}[theorem]{Remark}");
        sb.AppendLine(@"\newtheorem{observation}[theorem]{Observation}");
        sb.AppendLine(@"\newtheorem{note}[theorem]{Note}");
        sb.AppendLine(@"\newtheorem{conclusion}[theorem]{Conclusion}");

        sb.AppendLine();
        sb.AppendLine(@"\begin{document}");
        sb.AppendLine(@"\title{Knowledge Graph Paper}");
        sb.AppendLine(@"\maketitle");
        sb.AppendLine();

        // Build edge lookup for cross-references
        var nodeIdSet = new HashSet<string>(sortedNodes.Select(n => n.Id));
        var referencedBy = new Dictionary<string, List<string>>();
        foreach (var edge in edges)
        {
            if (!nodeIdSet.Contains(edge.Source) || !nodeIdSet.Contains(edge.Target))
                continue;
            if (!referencedBy.ContainsKey(edge.Source))
                referencedBy[edge.Source] = [];
            referencedBy[edge.Source].Add(edge.Target);
        }

        // Emit each node
        for (var i = 0; i < sortedNodes.Count; i++)
        {
            var node = sortedNodes[i];
            var label = $"node:{node.Id}";
            var nodeAbstract = GetStringProperty(node.Properties, "abstract");
            var body = SanitizeBody(GetStringProperty(node.Properties, "body"));

            if (node.Type == "proof")
            {
                // proof uses built-in LaTeX proof environment
                sb.AppendLine(@"\begin{proof}");
                sb.AppendLine($@"\label{{{label}}}");
                if (!string.IsNullOrWhiteSpace(nodeAbstract))
                    sb.AppendLine($@"\textit{{{EscapeLatex(nodeAbstract)}}}");
                sb.AppendLine();
                sb.AppendLine(body);
                EmitCrossRefs(sb, node.Id, referencedBy);
                sb.AppendLine(@"\end{proof}");
            }
            else if (TheoremEnvNames.TryGetValue(node.Type, out var envName))
            {
                sb.AppendLine($@"\begin{{{envName}}}");
                sb.AppendLine($@"\label{{{label}}}");
                if (!string.IsNullOrWhiteSpace(nodeAbstract))
                    sb.AppendLine($@"\textit{{{EscapeLatex(nodeAbstract)}}}");
                sb.AppendLine();
                sb.AppendLine(body);
                EmitCrossRefs(sb, node.Id, referencedBy);
                sb.AppendLine($@"\end{{{envName}}}");
            }
            else
            {
                // Unknown type — emit as paragraph
                sb.AppendLine($@"\paragraph{{{EscapeLatex(node.Type)}}}");
                sb.AppendLine($@"\label{{{label}}}");
                if (!string.IsNullOrWhiteSpace(nodeAbstract))
                    sb.AppendLine($@"\textit{{{EscapeLatex(nodeAbstract)}}}");
                sb.AppendLine();
                sb.AppendLine(body);
                EmitCrossRefs(sb, node.Id, referencedBy);
            }

            sb.AppendLine();
        }

        sb.AppendLine(@"\end{document}");
        return sb.ToString();
    }

    private static void EmitCrossRefs(
        StringBuilder sb, string nodeId, Dictionary<string, List<string>> referencedBy)
    {
        if (!referencedBy.TryGetValue(nodeId, out var targets) || targets.Count == 0)
            return;

        sb.Append(@"\noindent\textit{See also: ");
        sb.Append(string.Join(", ", targets.Select(t => $@"\ref{{node:{t}}}")));
        sb.AppendLine("}");
    }

    private static string GetStringProperty(Dictionary<string, JsonElement> properties, string key)
    {
        if (!properties.TryGetValue(key, out var element))
            return "";
        return element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText();
    }

    private static readonly string[] DangerousEnvs =
    [
        "theorem", "lemma", "definition", "corollary", "conjecture",
        "proposition", "remark", "conclusion", "example", "notation",
        "axiom", "observation", "note", "proof", "document",
    ];

    internal static string SanitizeBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return body;

        // LLM-generated body often has double-escaped LaTeX commands (\\frac, \\( etc.)
        // from JSON serialization. Normalize \\<command> to \<command> but preserve
        // standalone \\ (LaTeX line break).
        body = body.Replace(@"\textbackslash{}", @"\");
        // \\<letter> → \<letter> (double-escaped commands like \\frac, \\le, \\sum)
        body = Regex.Replace(body, @"\\\\([a-zA-Z])", @"\$1");
        // \\( → \(  and \\[ → \[  and \\) → \)  and \\] → \]  (math delimiters)
        body = Regex.Replace(body, @"\\\\([\(\)\[\]])", @"\$1");

        // Strip \begin{env} and \end{env} for theorem-like environments
        // that the LLM may have included in body content, to prevent
        // mismatched environments that cause fatal LaTeX errors.
        foreach (var env in DangerousEnvs)
        {
            body = body.Replace($@"\begin{{{env}}}", "", StringComparison.Ordinal);
            body = body.Replace($@"\end{{{env}}}", "", StringComparison.Ordinal);
        }

        return body;
    }

    private static string EscapeLatex(string text)
    {
        // Order matters: replace backslash first using a placeholder to avoid
        // subsequent replacements mangling the braces in \textbackslash{}.
        const string placeholder = "\x00BACKSLASH\x00";
        return text
            .Replace(@"\", placeholder)
            .Replace("{", @"\{")
            .Replace("}", @"\}")
            .Replace("&", @"\&")
            .Replace("%", @"\%")
            .Replace("$", @"\$")
            .Replace("#", @"\#")
            .Replace("_", @"\_")
            .Replace("~", @"\textasciitilde{}")
            .Replace("^", @"\textasciicircum{}")
            .Replace(placeholder, @"\textbackslash{}");
    }

    private async Task<byte[]> CompilePdfAsync(string latex, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sisyphus-paper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var texPath = Path.Combine(tempDir, "paper.tex");
            var pdfPath = Path.Combine(tempDir, "paper.pdf");

            await File.WriteAllTextAsync(texPath, latex, ct);

            var timeout = TimeSpan.FromSeconds(paperOptions.Value.CompileTimeoutSeconds);

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "tectonic",
                Arguments = $"--untrusted -Z continue-on-errors {texPath}",
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            logger.LogInformation("Compiling LaTeX with tectonic in {TempDir}", tempDir);
            process.Start();

            // Read stdout/stderr concurrently to avoid pipe buffer deadlock
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException(
                    $"Tectonic compilation timed out after {paperOptions.Value.CompileTimeoutSeconds}s");
            }

            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                logger.LogWarning("Tectonic exited with code {ExitCode}: {Stderr}",
                    process.ExitCode, stderr);

                // With -Z continue-on-errors, tectonic may still produce a PDF
                if (!File.Exists(pdfPath))
                {
                    logger.LogError("Tectonic did not produce a PDF file despite continue-on-errors");
                    throw new InvalidOperationException(
                        $"Tectonic compilation failed (exit code {process.ExitCode})");
                }

                logger.LogInformation("Tectonic produced a PDF despite errors — using it");
            }

            if (!File.Exists(pdfPath))
                throw new InvalidOperationException("Tectonic did not produce a PDF file");

            var pdfBytes = await File.ReadAllBytesAsync(pdfPath, ct);
            logger.LogInformation("PDF compiled successfully: {Size} bytes", pdfBytes.Length);
            return pdfBytes;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to clean up temp directory {TempDir}", tempDir);
            }
        }
    }
}
