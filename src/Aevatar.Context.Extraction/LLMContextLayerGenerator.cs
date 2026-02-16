using Aevatar.AI.Abstractions.LLMProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Context.Extraction;

/// <summary>
/// 基于 LLM 的 L0/L1 生成器实现。
/// 调用 ILLMProvider 生成摘要和概览。
/// </summary>
public sealed class LLMContextLayerGenerator : IContextLayerGenerator
{
    private readonly ILLMProviderFactory _llmFactory;
    private readonly ILogger _logger;

    public LLMContextLayerGenerator(
        ILLMProviderFactory llmFactory,
        ILogger<LLMContextLayerGenerator>? logger = null)
    {
        _llmFactory = llmFactory;
        _logger = logger ?? NullLogger<LLMContextLayerGenerator>.Instance;
    }

    public async Task<string> GenerateAbstractAsync(
        string content,
        string fileName,
        CancellationToken ct = default)
    {
        var provider = _llmFactory.GetDefault();
        var prompt = $"""
            Summarize the following content in ONE sentence (max 100 tokens).
            Focus on what this content IS and its primary purpose.
            Return ONLY the summary, no extra text.

            File: {fileName}
            Content:
            {Truncate(content, 8000)}
            """;

        var request = new LLMRequest
        {
            Messages = [ChatMessage.User(prompt)],
            MaxTokens = 150,
        };

        var response = await provider.ChatAsync(request, ct);
        var result = response.Content?.Trim() ?? "";
        _logger.LogDebug("Generated L0 for {File}: {Abstract}", fileName, result);
        return result;
    }

    public async Task<string> GenerateOverviewAsync(
        string content,
        string fileName,
        CancellationToken ct = default)
    {
        var provider = _llmFactory.GetDefault();
        var prompt = $"""
            Create a structured overview of the following content (max ~2000 tokens).
            Include:
            1. A brief description of what this is
            2. Key sections or components
            3. Important concepts or APIs
            4. How to access detailed content

            Use markdown format. Return ONLY the overview.

            File: {fileName}
            Content:
            {Truncate(content, 12000)}
            """;

        var request = new LLMRequest
        {
            Messages = [ChatMessage.User(prompt)],
            MaxTokens = 2500,
        };

        var response = await provider.ChatAsync(request, ct);
        var result = response.Content?.Trim() ?? "";
        _logger.LogDebug("Generated L1 for {File} ({Len} chars)", fileName, result.Length);
        return result;
    }

    public async Task<(string Abstract, string Overview)> GenerateDirectoryLayersAsync(
        string directoryName,
        IReadOnlyList<string> childAbstracts,
        CancellationToken ct = default)
    {
        var childSummary = string.Join("\n", childAbstracts.Select((a, i) => $"- {a}"));
        var provider = _llmFactory.GetDefault();

        var abstractPrompt = $"""
            Given a directory "{directoryName}" with the following child summaries:
            {childSummary}

            Write ONE sentence (max 100 tokens) describing what this directory contains.
            Return ONLY the summary.
            """;

        var abstractRequest = new LLMRequest
        {
            Messages = [ChatMessage.User(abstractPrompt)],
            MaxTokens = 150,
        };

        var abstractResponse = await provider.ChatAsync(abstractRequest, ct);
        var abstractText = abstractResponse.Content?.Trim() ?? "";

        var overviewPrompt = $"""
            Given a directory "{directoryName}" with the following child summaries:
            {childSummary}

            Create a structured overview (max ~2000 tokens) in markdown:
            1. What this directory contains
            2. List of contents with brief descriptions
            3. How to navigate to specific content

            Return ONLY the overview.
            """;

        var overviewRequest = new LLMRequest
        {
            Messages = [ChatMessage.User(overviewPrompt)],
            MaxTokens = 2500,
        };

        var overviewResponse = await provider.ChatAsync(overviewRequest, ct);
        var overviewText = overviewResponse.Content?.Trim() ?? "";

        _logger.LogDebug("Generated directory layers for {Dir}: L0={L0Len}, L1={L1Len}",
            directoryName, abstractText.Length, overviewText.Length);

        return (abstractText, overviewText);
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + "\n... (truncated)";
}
