using System.Text.RegularExpressions;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.Demos.Maker;

public sealed class DeterministicMakerProvider : ILLMProvider, ILLMProviderFactory
{
    public string Name => "deterministic-maker";

    public Task<LLMResponse> ChatAsync(LLMRequest request, CancellationToken ct = default)
    {
        var userMessage = request.Messages.LastOrDefault(x => x.Role == "user")?.Content ?? "";
        var content = Resolve(userMessage);
        return Task.FromResult(new LLMResponse
        {
            Content = content,
            FinishReason = "stop",
            Usage = new TokenUsage(32, 24, 56),
        });
    }

    public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
        LLMRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var full = await ChatAsync(request, ct);
        foreach (var ch in full.Content ?? "")
            yield return new LLMStreamChunk { DeltaContent = ch.ToString() };
        yield return new LLMStreamChunk { IsLast = true, Usage = full.Usage };
    }

    public ILLMProvider GetProvider(string name) => this;
    public ILLMProvider GetDefault() => this;
    public IReadOnlyList<string> GetAvailableProviders() => [Name];

    private static string Resolve(string userMessage)
    {
        if (ContainsAny(userMessage,
                "ATOMIC or DECOMPOSE",
                "ATOMIC/DECOMPOSE",
                "atomicity judge"))
        {
            var depth = ParseDepth(userMessage);
            return depth == 0 ? "DECOMPOSE" : "ATOMIC";
        }

        if (ContainsAny(userMessage,
                "Break the task into",
                "Output only subtasks",
                "MAKER decomposer"))
        {
            // Use line-list format (instead of the vote delimiter) to avoid colliding
            // with parallel candidate aggregation.
            return """
                1. Identify the paper's core claim and target capability.
                2. Explain why decomposition plus voting enables scaling reliability.
                3. Analyze assumptions, failure modes, and mitigation experiments.
                """;
        }

        if (ContainsAny(userMessage,
                "Solve this atomic task",
                "MAKER worker"))
        {
            if (userMessage.Contains("core claim", StringComparison.OrdinalIgnoreCase))
            {
                return "The paper claims MAKER can execute over one million LLM steps with zero errors.";
            }

            if (userMessage.Contains("decomposition plus voting", StringComparison.OrdinalIgnoreCase) ||
                userMessage.Contains("enables scaling reliability", StringComparison.OrdinalIgnoreCase))
            {
                return "It decomposes work into micro-tasks and applies per-step multi-agent voting, so local errors are corrected before compounding.";
            }

            if (userMessage.Contains("failure modes", StringComparison.OrdinalIgnoreCase) ||
                userMessage.Contains("mitigation experiments", StringComparison.OrdinalIgnoreCase))
            {
                return "Risks include poor decomposition granularity and correlated voter bias; mitigation uses task-quality diagnostics and diversity-aware voter assignment.";
            }

            return "The task is solved with a concise, evidence-grounded answer.";
        }

        if (ContainsAny(userMessage,
                "Merge child solutions",
                "MAKER composer"))
        {
            return """
                MAKER demonstrates million-step reliable execution by splitting complex work into micro-tasks and enforcing vote-based correction at each step.
                This limits error accumulation, but success still depends on decomposition quality and voter diversity.
                Two practical follow-up experiments are (1) stress-testing decomposition quality under ambiguous tasks and (2) measuring robustness gains from heterogeneous voter pools.
                """;
        }

        return "ATOMIC";
    }

    private static int ParseDepth(string userMessage)
    {
        var match = Regex.Match(userMessage, @"DEPTH:\s*(\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var depth) ? depth : 0;
    }

    private static bool ContainsAny(string text, params string[] probes) =>
        probes.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
}
