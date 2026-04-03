using System.Text.Json;
using System.Text.RegularExpressions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.ChronoStorage.Tools;

/// <summary>
/// Returns a tree-structured view of the directory hierarchy in chrono-storage.
/// </summary>
public sealed class ChronoTreeTool : IAgentTool
{
    private readonly ChronoStorageApiClient _client;

    public ChronoTreeTool(ChronoStorageApiClient client) => _client = client;

    public string Name => "chrono_tree";

    public string Description =>
        "Show the directory tree structure of files in chrono-storage. " +
        "Returns an indented tree view. Use this to get an overview of a project's structure. " +
        "Optionally filter to a subdirectory or by file pattern.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Root path to start from (default: root). Example: 'src/components'"
            },
            "max_depth": {
              "type": "integer",
              "description": "Maximum directory depth (default: 5, max: 10)"
            },
            "pattern": {
              "type": "string",
              "description": "Glob pattern to filter files (e.g. '*.ts', '*.yaml')"
            }
          }
        }
        """;

    public bool IsReadOnly => true;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return "Error: No NyxID access token available. User must be authenticated.";

        string rootPath = "";
        int maxDepth = 5;
        Regex? filterRegex = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty("path", out var p))
                rootPath = (p.GetString() ?? "").Trim('/');
            if (doc.RootElement.TryGetProperty("max_depth", out var d) && d.TryGetInt32(out var dv))
                maxDepth = Math.Clamp(dv, 1, 10);
            if (doc.RootElement.TryGetProperty("pattern", out var pat))
            {
                var glob = pat.GetString();
                if (!string.IsNullOrWhiteSpace(glob))
                    filterRegex = GlobToRegex(glob);
            }
        }
        catch { /* use defaults */ }

        var manifestJson = await _client.GetManifestAsync(token, ct);

        try
        {
            using var manifest = JsonDocument.Parse(manifestJson);
            if (!manifest.RootElement.TryGetProperty("files", out var files))
                return "(empty)";

            var keys = new List<string>();
            foreach (var file in files.EnumerateArray())
            {
                var key = file.GetProperty("key").GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(rootPath) &&
                    !key.StartsWith(rootPath + "/", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(key, rootPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (filterRegex != null && !filterRegex.IsMatch(key))
                    continue;
                keys.Add(key);
            }

            if (keys.Count == 0)
                return $"(no files found{(string.IsNullOrWhiteSpace(rootPath) ? "" : $" under '{rootPath}'")})";

            keys.Sort(StringComparer.OrdinalIgnoreCase);
            return BuildTree(keys, rootPath, maxDepth);
        }
        catch
        {
            return manifestJson;
        }
    }

    private static string BuildTree(List<string> keys, string rootPath, int maxDepth)
    {
        var root = new TreeNode("");
        var prefixLen = string.IsNullOrWhiteSpace(rootPath) ? 0 : rootPath.Length + 1;

        foreach (var key in keys)
        {
            var relative = prefixLen > 0 && key.Length > prefixLen ? key[prefixLen..] : key;
            var parts = relative.Split('/');
            var node = root;
            for (var i = 0; i < parts.Length; i++)
            {
                if (i >= maxDepth && i < parts.Length - 1)
                {
                    node.TruncatedCount++;
                    break;
                }

                if (!node.Children.TryGetValue(parts[i], out var child))
                {
                    child = new TreeNode(parts[i]);
                    node.Children[parts[i]] = child;
                }
                node = child;
            }
        }

        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(rootPath))
            sb.AppendLine(rootPath + "/");
        RenderTree(root, sb, "", true);
        return sb.ToString().TrimEnd();
    }

    private static void RenderTree(TreeNode node, System.Text.StringBuilder sb, string indent, bool isRoot)
    {
        var entries = node.Children
            .OrderBy(kv => kv.Value.Children.Count == 0 ? 1 : 0)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = 0; i < entries.Count; i++)
        {
            var (name, child) = entries[i];
            var isLast = i == entries.Count - 1 && node.TruncatedCount == 0;
            var prefix = isRoot ? "" : (isLast ? "\u2514\u2500\u2500 " : "\u251c\u2500\u2500 ");
            var childIndent = isRoot ? "" : indent + (isLast ? "    " : "\u2502   ");

            if (child.Children.Count > 0)
            {
                sb.AppendLine($"{indent}{prefix}{name}/");
                RenderTree(child, sb, childIndent, false);
            }
            else
            {
                sb.AppendLine($"{indent}{prefix}{name}");
            }
        }

        if (node.TruncatedCount > 0)
            sb.AppendLine($"{indent}\u2514\u2500\u2500 ... ({node.TruncatedCount} more beyond max depth)");
    }

    private sealed class TreeNode(string name)
    {
        public string Name { get; } = name;
        public Dictionary<string, TreeNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int TruncatedCount { get; set; }
    }

    private static Regex GlobToRegex(string glob)
    {
        var pattern = "^" + Regex.Escape(glob)
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]") + "$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
