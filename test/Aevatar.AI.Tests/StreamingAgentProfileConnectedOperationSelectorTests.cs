using System.Runtime.CompilerServices;
using System.Text.Json;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class StreamingAgentProfileConnectedOperationSelectorTests
{
    [Fact]
    public async Task SelectAsync_ShouldUseToolFreeBoundedPresentationIndex()
    {
        var provider = new StubProvider([
            new LLMStreamChunk
            {
                DeltaContent = "{\"status\":\"selected\",\"candidate_ids\":[\"operation-002\"]}",
            },
        ]);
        var selector = new StreamingAgentProfileConnectedOperationSelector(
            new StubProviderFactory(provider));
        var llmControl = new LLMControlContext(
            "caller-token",
            null,
            null,
            "model-a",
            "/api/v1/proxy/s/route-a",
            null,
            null);

        var result = await selector.SelectAsync(NewRequest() with
        {
            LlmControl = llmControl,
            RequestId = "turn-alpha:connected-operation-selector",
        });

        result.Status.Should().Be(AgentProfileConnectedOperationSelectionStatus.Selected);
        result.CandidateIds.Should().Equal("operation-002");
        result.FailureCode.Should().BeNull();
        var request = provider.Requests.Should().ContainSingle().Subject;
        request.Tools.Should().BeNull();
        request.ResponseFormat.Should().NotBeNull();
        request.MaxTokens.Should().Be(192);
        request.Temperature.Should().Be(0);
        request.LlmControl.Should().BeSameAs(llmControl);
        request.RoutingContext.Should().BeEquivalentTo(llmControl.ToRoutingContext());
        var input = request.Messages.Single(message => message.Role == "user").Content;
        using var document = JsonDocument.Parse(input!);
        document.RootElement.GetProperty("maximum_read_selections").GetInt32().Should().Be(3);
        document.RootElement.GetProperty("maximum_write_selections").GetInt32().Should().Be(1);
        var operation = document.RootElement.GetProperty("operations")[1];
        operation.GetProperty("candidate_id").GetString().Should().Be("operation-002");
        operation.GetProperty("risk").GetString().Should().Be("read_only");
        input.Should().NotContain("opaque-internal-tool-name")
            .And.NotContain("parameters_schema")
            .And.NotContain("access_token");
        request.Messages.Single(message => message.Role == "system").Content.Should()
            .Contain("untrusted data")
            .And.Contain("never mix read and write");
    }

    [Fact]
    public async Task SelectAsync_UnambiguousNoMatch_ShouldReturnNoMatch()
    {
        var selector = new StreamingAgentProfileConnectedOperationSelector(
            new StubProviderFactory(new StubProvider([
                new LLMStreamChunk
                {
                    DeltaContent = "{\"status\":\"no_match\",\"candidate_ids\":[]}",
                },
            ])));

        var result = await selector.SelectAsync(NewRequest());

        result.Should().Be(AgentProfileConnectedOperationSelectionResult.NoMatch());
    }

    [Theory]
    [InlineData("{\"status\":\"selected\",\"candidate_ids\":[]}", "candidate_selection_invalid")]
    [InlineData("{\"status\":\"selected\",\"candidate_ids\":[\"unknown\"]}", "unknown_candidate")]
    [InlineData("{\"status\":\"selected\",\"candidate_ids\":[\"operation-001\",\"operation-001\"]}", "candidate_selection_invalid")]
    [InlineData("{\"status\":\"selected\",\"candidate_ids\":[\"operation-001\",\"operation-003\"]}", "mixed_risk_selection")]
    [InlineData("{\"status\":\"selected\",\"candidate_ids\":[\"operation-001\",\"operation-002\"]}", "selection_budget_exceeded")]
    [InlineData("{\"status\":\"no_match\",\"candidate_ids\":[\"operation-001\"]}", "unexpected_candidate_ids")]
    [InlineData("{\"status\":\"selected\",\"candidate_ids\":[\"operation-001\"],\"extra\":true}", "unexpected_output_field")]
    [InlineData("not-json", "malformed_output")]
    public async Task SelectAsync_InvalidOutput_ShouldFailClosed(
        string output,
        string failureCode)
    {
        var selector = new StreamingAgentProfileConnectedOperationSelector(
            new StubProviderFactory(new StubProvider([
                new LLMStreamChunk { DeltaContent = output },
            ])));

        var result = await selector.SelectAsync(NewRequest() with { MaximumReadSelections = 1 });

        result.Should().Be(AgentProfileConnectedOperationSelectionResult.Failed(failureCode));
    }

    [Fact]
    public async Task SelectAsync_OutOfBoundsCatalogAndInput_ShouldNotCallProvider()
    {
        var provider = new StubProvider([]);
        var selector = new StreamingAgentProfileConnectedOperationSelector(
            new StubProviderFactory(provider));
        var tooMany = Enumerable.Range(
                1,
                StreamingAgentProfileConnectedOperationSelector.MaximumCandidates + 1)
            .Select(index => Candidate($"operation-{index:D3}", AgentToolOperationRisk.ReadOnly))
            .ToArray();

        var countResult = await selector.SelectAsync(NewRequest() with { Candidates = tooMany });
        var inputResult = await selector.SelectAsync(NewRequest() with
        {
            UserMessage = new string(
                'x',
                StreamingAgentProfileConnectedOperationSelector.MaximumInputUtf8Bytes + 1),
        });

        countResult.FailureCode.Should().Be("candidate_catalog_out_of_bounds");
        inputResult.FailureCode.Should().Be("input_too_large");
        provider.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task SelectAsync_MultipleWriteCandidates_ShouldStillPermitReadSelection()
    {
        var provider = new StubProvider([
            new LLMStreamChunk
            {
                DeltaContent =
                    "{\"status\":\"selected\",\"candidate_ids\":[\"operation-002\"]}",
            },
        ]);
        var selector = new StreamingAgentProfileConnectedOperationSelector(
            new StubProviderFactory(provider));
        var request = NewRequest() with
        {
            Candidates =
            [
                .. NewRequest().Candidates,
                Candidate("operation-005", AgentToolOperationRisk.Write),
            ],
        };

        var result = await selector.SelectAsync(request);

        result.Status.Should().Be(AgentProfileConnectedOperationSelectionStatus.Selected);
        result.CandidateIds.Should().Equal("operation-002");
        provider.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task SelectAsync_MultipleWriteCandidates_ShouldRejectWriteSelection()
    {
        var provider = new StubProvider([
            new LLMStreamChunk
            {
                DeltaContent =
                    "{\"status\":\"selected\",\"candidate_ids\":[\"operation-003\"]}",
            },
        ]);
        var selector = new StreamingAgentProfileConnectedOperationSelector(
            new StubProviderFactory(provider));
        var request = NewRequest() with
        {
            Candidates =
            [
                .. NewRequest().Candidates,
                Candidate("operation-005", AgentToolOperationRisk.Write),
            ],
        };

        var result = await selector.SelectAsync(request);

        result.Should().Be(
            AgentProfileConnectedOperationSelectionResult.Failed(
                "multiple_write_candidates"));
        provider.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task SelectAsync_InternalTimeout_ShouldFailClosed()
    {
        var timeProvider = new ManualDeadlineTimeProvider();
        var provider = new CancellationBlockingProvider();
        var selector = new StreamingAgentProfileConnectedOperationSelector(
            new StubProviderFactory(provider),
            timeProvider);
        var selection = selector.SelectAsync(NewRequest());
        await provider.Started;

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var result = await selection;

        result.Should().Be(AgentProfileConnectedOperationSelectionResult.Failed("timeout"));
        provider.CancellationObserved.Should().BeTrue();
    }

    [Fact]
    public async Task SelectAsync_ToolCallAndOversizedOutput_ShouldFailClosed()
    {
        var toolCalling = new StreamingAgentProfileConnectedOperationSelector(
            new StubProviderFactory(new StubProvider([
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall
                    {
                        Id = "call-alpha",
                        Name = "forbidden",
                        ArgumentsJson = "{}",
                    },
                },
            ])));
        var oversized = new StreamingAgentProfileConnectedOperationSelector(
            new StubProviderFactory(new StubProvider([
                new LLMStreamChunk
                {
                    DeltaContent = new string(
                        'x',
                        StreamingAgentProfileConnectedOperationSelector.MaximumOutputUtf8Bytes + 1),
                },
            ])));

        var toolResult = await toolCalling.SelectAsync(NewRequest());
        var sizeResult = await oversized.SelectAsync(NewRequest());

        toolResult.FailureCode.Should().Be("tool_call_not_allowed");
        sizeResult.FailureCode.Should().Be("output_too_large");
    }

    private static AgentProfileConnectedOperationSelectionRequest NewRequest() =>
        new(
            "read the repository metadata",
            [
                Candidate("operation-001", AgentToolOperationRisk.ReadOnly),
                Candidate("operation-002", AgentToolOperationRisk.ReadOnly),
                Candidate("operation-003", AgentToolOperationRisk.Write),
                Candidate("operation-004", AgentToolOperationRisk.ReadOnly),
            ],
            MaximumReadSelections: 3,
            MaximumWriteSelections: 1,
            TimeSpan.FromSeconds(1));

    private static AgentProfileConnectedOperationSelectionCandidate Candidate(
        string candidateId,
        AgentToolOperationRisk risk) =>
        new(
            candidateId,
            "api-github",
            "GitHub",
            "Primary GitHub",
            candidateId,
            "Read or update repository data.",
            risk == AgentToolOperationRisk.ReadOnly ? "GET" : "POST",
            "/repos/{owner}/{repo}",
            risk);

    private sealed class StubProviderFactory(ILLMProvider provider) : ILLMProviderFactory
    {
        public ILLMProvider GetProvider(string name) => provider;
        public ILLMProvider GetDefault() => provider;
        public IReadOnlyList<string> GetAvailableProviders() => [provider.Name];
    }

    private sealed class StubProvider(IReadOnlyList<LLMStreamChunk> chunks) : ILLMProvider
    {
        public string Name => "connected-operation-selector-test";
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

    private sealed class CancellationBlockingProvider : ILLMProvider
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "blocking-connected-operation-selector-test";
        public Task Started => _started.Task;
        public bool CancellationObserved { get; private set; }

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _started.TrySetResult();
            var canceled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
                canceled);
            try
            {
                await canceled.Task;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }

            yield break;
        }
    }
}
