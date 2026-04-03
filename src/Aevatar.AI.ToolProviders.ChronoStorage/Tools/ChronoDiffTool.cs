using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.ChronoStorage.Tools;

/// <summary>
/// Compares two files in chrono-storage and returns a unified diff.
/// </summary>
public sealed class ChronoDiffTool : IAgentTool
{
    private readonly ChronoStorageApiClient _client;

    public ChronoDiffTool(ChronoStorageApiClient client) => _client = client;

    public string Name => "chrono_diff";

    public string Description =>
        "Compare two files in chrono-storage and show differences in unified diff format. " +
        "Use this to understand what changed between two files.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "key_a": {
              "type": "string",
              "description": "File path key of the first (original) file"
            },
            "key_b": {
              "type": "string",
              "description": "File path key of the second (modified) file"
            },
            "context_lines": {
              "type": "integer",
              "description": "Number of context lines around changes (default: 3)"
            }
          },
          "required": ["key_a", "key_b"]
        }
        """;

    public bool IsReadOnly => true;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return """{"error":"No NyxID access token available. User must be authenticated."}""";

        string keyA = "", keyB = "";
        int contextLines = 3;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("key_a", out var a))
                keyA = a.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("key_b", out var b))
                keyB = b.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("context_lines", out var c) && c.TryGetInt32(out var cv))
                contextLines = Math.Clamp(cv, 0, 10);
        }
        catch { /* use defaults */ }

        if (string.IsNullOrWhiteSpace(keyA))
            return """{"error":"'key_a' is required"}""";
        if (string.IsNullOrWhiteSpace(keyB))
            return """{"error":"'key_b' is required"}""";

        var contentA = await _client.GetFileAsync(token, keyA, ct);
        if (IsError(contentA))
            return JsonSerializer.Serialize(new { error = $"Failed to read '{keyA}'" });

        var contentB = await _client.GetFileAsync(token, keyB, ct);
        if (IsError(contentB))
            return JsonSerializer.Serialize(new { error = $"Failed to read '{keyB}'" });

        if (contentA == contentB)
            return JsonSerializer.Serialize(new { key_a = keyA, key_b = keyB, identical = true, message = "Files are identical." });

        var linesA = contentA.Split('\n');
        var linesB = contentB.Split('\n');
        var diff = ComputeUnifiedDiff(linesA, linesB, keyA, keyB, contextLines);

        return JsonSerializer.Serialize(new { key_a = keyA, key_b = keyB, identical = false, diff });
    }

    private static bool IsError(string content) =>
        content.TrimStart().StartsWith("{\"error\"", StringComparison.Ordinal);

    private static string ComputeUnifiedDiff(
        string[] linesA, string[] linesB, string nameA, string nameB, int context)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"--- {nameA}");
        sb.AppendLine($"+++ {nameB}");

        var edits = ComputeEditScript(linesA, linesB);
        var hunks = GroupIntoHunks(edits, context);

        foreach (var hunk in hunks)
        {
            sb.AppendLine($"@@ -{hunk.StartA + 1},{hunk.CountA} +{hunk.StartB + 1},{hunk.CountB} @@");
            foreach (var line in hunk.Lines)
                sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd();
    }

    private enum EditKind { Keep, Delete, Insert }
    private sealed record Edit(EditKind Kind, string Line, int IndexA, int IndexB);
    private sealed class Hunk
    {
        public int StartA, CountA, StartB, CountB;
        public List<string> Lines { get; } = [];
    }

    private static List<Edit> ComputeEditScript(string[] a, string[] b)
    {
        var n = a.Length;
        var m = b.Length;

        if ((long)n * m > 10_000_000)
        {
            var edits = new List<Edit>(n + m);
            for (var i = 0; i < n; i++) edits.Add(new Edit(EditKind.Delete, a[i], i, 0));
            for (var j = 0; j < m; j++) edits.Add(new Edit(EditKind.Insert, b[j], n, j));
            return edits;
        }

        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var result = new List<Edit>();
        int ai = 0, bi = 0;
        while (ai < n && bi < m)
        {
            if (a[ai] == b[bi]) { result.Add(new Edit(EditKind.Keep, a[ai], ai, bi)); ai++; bi++; }
            else if (dp[ai + 1, bi] >= dp[ai, bi + 1]) { result.Add(new Edit(EditKind.Delete, a[ai], ai, bi)); ai++; }
            else { result.Add(new Edit(EditKind.Insert, b[bi], ai, bi)); bi++; }
        }
        while (ai < n) { result.Add(new Edit(EditKind.Delete, a[ai], ai, m)); ai++; }
        while (bi < m) { result.Add(new Edit(EditKind.Insert, b[bi], n, bi)); bi++; }
        return result;
    }

    private static List<Hunk> GroupIntoHunks(List<Edit> edits, int context)
    {
        var hunks = new List<Hunk>();
        var changes = edits.Select((e, i) => (e, i)).Where(x => x.e.Kind != EditKind.Keep).Select(x => x.i).ToList();
        if (changes.Count == 0) return hunks;

        var groups = new List<(int start, int end)>();
        var gs = changes[0]; var ge = changes[0];
        for (var i = 1; i < changes.Count; i++)
        {
            if (changes[i] - ge <= context * 2 + 1) ge = changes[i];
            else { groups.Add((gs, ge)); gs = changes[i]; ge = changes[i]; }
        }
        groups.Add((gs, ge));

        foreach (var (start, end) in groups)
        {
            var hs = Math.Max(0, start - context);
            var he = Math.Min(edits.Count - 1, end + context);
            var hunk = new Hunk();
            int? firstA = null, firstB = null;
            for (var i = hs; i <= he; i++)
            {
                var e = edits[i];
                firstA ??= e.IndexA; firstB ??= e.IndexB;
                switch (e.Kind)
                {
                    case EditKind.Keep: hunk.Lines.Add($" {e.Line}"); hunk.CountA++; hunk.CountB++; break;
                    case EditKind.Delete: hunk.Lines.Add($"-{e.Line}"); hunk.CountA++; break;
                    case EditKind.Insert: hunk.Lines.Add($"+{e.Line}"); hunk.CountB++; break;
                }
            }
            hunk.StartA = firstA ?? 0; hunk.StartB = firstB ?? 0;
            hunks.Add(hunk);
        }
        return hunks;
    }
}
