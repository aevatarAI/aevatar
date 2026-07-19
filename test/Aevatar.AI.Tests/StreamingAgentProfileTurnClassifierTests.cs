using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class StreamingAgentProfileTurnClassifierTests
{
    [Fact]
    public async Task ClassifyAsync_ShouldUseSingleToolFreeBoundedStreamingRequest()
    {
        var provider = new StubProvider([
            new LLMStreamChunk { DeltaContent = "{\"status\":\"matched\"," },
            new LLMStreamChunk { DeltaContent = "\"intent_id\":\"intent-a\"}" },
        ]);
        var classifier = new StreamingAgentProfileTurnClassifier(new StubProviderFactory(provider));

        var result = await classifier.ClassifyAsync(NewRequest());

        result.Should().Be(AgentProfileTurnClassificationResult.Matched("intent-a"));
        provider.CallCount.Should().Be(1);
        var request = provider.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().BeNull();
        request.ResponseFormat.Should().NotBeNull();
        request.MaxTokens.Should().Be(128);
        request.Temperature.Should().Be(0);
    }

    [Fact]
    public async Task ClassifyAsync_ShouldAcceptOnlyUnambiguousNoMatchOutput()
    {
        var noMatch = new StubProvider([
            new LLMStreamChunk { DeltaContent = "{\"status\":\"no_match\",\"intent_id\":null}" },
        ]);
        var ambiguous = new StubProvider([
            new LLMStreamChunk { DeltaContent = "{\"status\":\"no_match\",\"intent_id\":\"intent-a\"}" },
        ]);

        var accepted = await new StreamingAgentProfileTurnClassifier(new StubProviderFactory(noMatch))
            .ClassifyAsync(NewRequest());
        var rejected = await new StreamingAgentProfileTurnClassifier(new StubProviderFactory(ambiguous))
            .ClassifyAsync(NewRequest());

        accepted.Status.Should().Be(AgentProfileTurnClassificationStatus.NoMatch);
        rejected.Should().Be(AgentProfileTurnClassificationResult.Failed("unexpected_intent"));
    }

    [Fact]
    public async Task ClassifyAsync_ShouldRejectOutOfBoundsInputBeforeCallingProvider()
    {
        var provider = new StubProvider([]);
        var classifier = new StreamingAgentProfileTurnClassifier(new StubProviderFactory(provider));
        var tooManyCandidates = Enumerable.Range(0, StreamingAgentProfileTurnClassifier.MaximumCandidates + 1)
            .Select(index => new AgentProfileTurnClassificationCandidate($"intent-{index}", "route"))
            .ToArray();

        var countResult = await classifier.ClassifyAsync(new AgentProfileTurnClassificationRequest(
            "message",
            tooManyCandidates,
            TimeSpan.FromSeconds(1)));
        var sizeResult = await classifier.ClassifyAsync(new AgentProfileTurnClassificationRequest(
            new string('x', StreamingAgentProfileTurnClassifier.MaximumInputUtf8Bytes + 1),
            [new AgentProfileTurnClassificationCandidate("intent-a", "route")],
            TimeSpan.FromSeconds(1)));

        countResult.FailureCode.Should().Be("candidate_count_out_of_bounds");
        sizeResult.FailureCode.Should().Be("input_too_large");
        provider.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ClassifyAsync_ShouldFailClosedForToolMalformedUnknownAndOversizedOutput()
    {
        var cases = new[]
        {
            (
                Chunks: (IReadOnlyList<LLMStreamChunk>)[new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall { Id = "call", Name = "tool", ArgumentsJson = "{}" },
                }],
                Failure: "tool_call_not_allowed"),
            (
                Chunks: (IReadOnlyList<LLMStreamChunk>)[new LLMStreamChunk { DeltaContent = "not-json" }],
                Failure: "malformed_output"),
            (
                Chunks: (IReadOnlyList<LLMStreamChunk>)[new LLMStreamChunk
                {
                    DeltaContent = "{\"status\":\"matched\",\"intent_id\":\"unknown\"}",
                }],
                Failure: "unknown_intent"),
            (
                Chunks: (IReadOnlyList<LLMStreamChunk>)[new LLMStreamChunk
                {
                    DeltaContent = new string('x', StreamingAgentProfileTurnClassifier.MaximumOutputUtf8Bytes + 1),
                }],
                Failure: "output_too_large"),
        };

        foreach (var testCase in cases)
        {
            var classifier = new StreamingAgentProfileTurnClassifier(
                new StubProviderFactory(new StubProvider(testCase.Chunks)));

            var result = await classifier.ClassifyAsync(NewRequest());

            result.Status.Should().Be(AgentProfileTurnClassificationStatus.Failed);
            result.FailureCode.Should().Be(testCase.Failure);
        }
    }

    private static AgentProfileTurnClassificationRequest NewRequest() =>
        new(
            "please route this",
            [new AgentProfileTurnClassificationCandidate("intent-a", "Route A")],
            TimeSpan.FromSeconds(1));

    private sealed class StubProviderFactory(ILLMProvider provider) : ILLMProviderFactory
    {
        public ILLMProvider GetProvider(string name) => provider;
        public ILLMProvider GetDefault() => provider;
        public IReadOnlyList<string> GetAvailableProviders() => [provider.Name];
    }

    private sealed class StubProvider(IReadOnlyList<LLMStreamChunk> chunks) : ILLMProvider
    {
        public string Name => "classifier-test";
        public int CallCount { get; private set; }
        public List<LLMRequest> Requests { get; } = [];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            CallCount++;
            Requests.Add(request);
            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                yield return chunk;
            }

            await Task.CompletedTask;
        }
    }
}
