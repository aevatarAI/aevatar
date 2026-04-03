// ─────────────────────────────────────────────────────────────
// TextToolCallParser — Fallback parser for text-based tool calls
//
// Some LLM responses include tool invocations as DSML-formatted text
// instead of structured FunctionCallContent. This parser extracts
// those calls so the agent loop can execute them.
//
// This parser is defensive: it never throws. Malformed input is
// silently skipped and the original content is preserved.
// ─────────────────────────────────────────────────────────────

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.AI.Core.Tools;

/// <summary>
/// Parses DSML-formatted function call blocks from LLM text output.
/// Returns extracted <see cref="ToolCall"/> objects and the content
/// with DSML blocks stripped. Never throws — returns empty results on failure.
/// </summary>
public static class TextToolCallParser
{
    /// <summary>Result of parsing text-based function calls.</summary>
    public sealed class ParseResult
    {
        public required IReadOnlyList<ToolCall> ToolCalls { get; init; }
        public required string CleanedContent { get; init; }
    }

    private static readonly ParseResult EmptyResult = new() { ToolCalls = [], CleanedContent = string.Empty };

    // ─── DSML patterns ───
    // The pipe character may be ASCII | (U+007C) or full-width ｜ (U+FF5C).
    // Some LLMs (e.g. DeepSeek) emit full-width pipes in CJK context.
    private const string Pipe = @"[\|\uff5c]";

    // Matches: < | DSML | function_calls>...</ | DSML | function_calls>
    private static readonly Regex DsmlBlockRegex = new(
        $@"<\s*{Pipe}\s*DSML\s*{Pipe}\s*function_calls\s*>(?<body>[\s\S]*?)<\/\s*{Pipe}\s*DSML\s*{Pipe}\s*function_calls\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    private static readonly Regex DsmlInvokeRegex = new(
        $@"<\s*{Pipe}\s*DSML\s*{Pipe}\s*invoke\s+name\s*=\s*[""'](?<name>[^""']+)[""']\s*>(?<body>[\s\S]*?)<\/\s*{Pipe}\s*DSML\s*{Pipe}\s*invoke\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    private static readonly Regex DsmlParameterRegex = new(
        $@"<\s*{Pipe}\s*DSML\s*{Pipe}\s*parameter\s+name\s*=\s*[""'](?<name>[^""']+)[""'][^>]*>(?<value>[\s\S]*?)<\/\s*{Pipe}\s*DSML\s*{Pipe}\s*parameter\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    // ─── XML patterns (fallback) ───
    private static readonly Regex XmlBlockRegex = new(
        @"<function_calls\s*>(?<body>[\s\S]*?)<\/function_calls\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    private static readonly Regex XmlInvokeRegex = new(
        @"<invoke\s+name\s*=\s*[""'](?<name>[^""']+)[""']\s*>(?<body>[\s\S]*?)<\/invoke\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    private static readonly Regex XmlParameterRegex = new(
        @"<parameter\s+name\s*=\s*[""'](?<name>[^""']+)[""'][^>]*>(?<value>[\s\S]*?)<\/parameter\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    private static int _counter;

    /// <summary>
    /// Parse text-based function call blocks from LLM output.
    /// Returns extracted tool calls and content with blocks stripped.
    /// Never throws — returns empty results on any failure.
    /// </summary>
    public static ParseResult Parse(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return new ParseResult { ToolCalls = [], CleanedContent = content ?? string.Empty };

        try
        {
            var toolCalls = new List<ToolCall>();
            var cleaned = content;

            cleaned = ExtractFromBlocks(cleaned, DsmlBlockRegex, DsmlInvokeRegex, DsmlParameterRegex, toolCalls);
            cleaned = ExtractFromBlocks(cleaned, XmlBlockRegex, XmlInvokeRegex, XmlParameterRegex, toolCalls);

            cleaned = Regex.Replace(cleaned, @"\n[ \t]+\n", "\n\n");
            cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n");
            cleaned = cleaned.Trim();

            return new ParseResult { ToolCalls = toolCalls, CleanedContent = cleaned };
        }
        catch
        {
            // Parser must never crash the agent loop. Return content as-is.
            return new ParseResult { ToolCalls = [], CleanedContent = content };
        }
    }

    private static string ExtractFromBlocks(
        string content,
        Regex blockRegex,
        Regex invokeRegex,
        Regex parameterRegex,
        List<ToolCall> toolCalls)
    {
        MatchCollection matches;
        try
        {
            matches = blockRegex.Matches(content);
            if (matches.Count == 0)
                return content;
        }
        catch (RegexMatchTimeoutException)
        {
            return content;
        }

        foreach (Match blockMatch in matches)
        {
            var body = blockMatch.Groups["body"].Value;

            MatchCollection invokeMatches;
            try { invokeMatches = invokeRegex.Matches(body); }
            catch (RegexMatchTimeoutException) { continue; }

            foreach (Match invokeMatch in invokeMatches)
            {
                try
                {
                    var toolName = invokeMatch.Groups["name"].Value.Trim();
                    if (string.IsNullOrWhiteSpace(toolName))
                        continue; // skip empty tool names

                    var invokeBody = invokeMatch.Groups["body"].Value;

                    var parameters = new Dictionary<string, string>();
                    MatchCollection paramMatches;
                    try { paramMatches = parameterRegex.Matches(invokeBody); }
                    catch (RegexMatchTimeoutException) { paramMatches = null!; }

                    if (paramMatches != null)
                    {
                        foreach (Match paramMatch in paramMatches)
                        {
                            var paramName = paramMatch.Groups["name"].Value.Trim();
                            if (string.IsNullOrWhiteSpace(paramName))
                                continue;
                            var paramValue = paramMatch.Groups["value"].Value.Trim();
                            parameters[paramName] = paramValue;
                        }
                    }

                    var callId = $"text-tc-{Interlocked.Increment(ref _counter)}";
                    var argumentsJson = BuildArgumentsJson(parameters);

                    toolCalls.Add(new ToolCall
                    {
                        Id = callId,
                        Name = toolName,
                        ArgumentsJson = argumentsJson,
                    });
                }
                catch
                {
                    // Skip malformed individual invoke blocks
                }
            }
        }

        try
        {
            return blockRegex.Replace(content, "\n");
        }
        catch (RegexMatchTimeoutException)
        {
            return content;
        }
    }

    /// <summary>
    /// Build the arguments JSON string from parsed parameters.
    /// Always emits a JSON object. Never throws.
    /// </summary>
    private static string BuildArgumentsJson(Dictionary<string, string> parameters)
    {
        if (parameters.Count == 0)
            return "{}";

        try
        {
            using var stream = new System.IO.MemoryStream();
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            foreach (var (key, value) in parameters)
                writer.WriteString(key, value);
            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            // Fallback: emit a simple JSON object with escaped values
            var sb = new StringBuilder("{");
            var first = true;
            foreach (var (key, value) in parameters)
            {
                if (!first) sb.Append(',');
                sb.Append('"').Append(JsonEncodedText.Encode(key)).Append("\":\"")
                  .Append(JsonEncodedText.Encode(value)).Append('"');
                first = false;
            }
            sb.Append('}');
            return sb.ToString();
        }
    }
}
