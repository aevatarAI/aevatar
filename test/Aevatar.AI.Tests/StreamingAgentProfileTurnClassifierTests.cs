using System.Runtime.CompilerServices;
using System.Text.Json;
using Aevatar.AI.Abstractions;
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
    public async Task ClassifyAsync_ShouldSendTypedSideEffectClassToProvider()
    {
        var provider = new StubProvider([
            new LLMStreamChunk { DeltaContent = "{\"status\":\"matched\",\"intent_id\":\"service_connect\"}" },
        ]);
        var classifier = new StreamingAgentProfileTurnClassifier(new StubProviderFactory(provider));
        var request = new AgentProfileTurnClassificationRequest(
            "我要连接 AWS Cost Explorer",
            [
                new AgentProfileTurnClassificationCandidate(
                    "service_discovery",
                    "Browse available services.",
                    AgentProfileSideEffectClass.ReadOnly),
                new AgentProfileTurnClassificationCandidate(
                    "service_connect",
                    "Connect a requested service.",
                    AgentProfileSideEffectClass.ExternalHandoff),
            ],
            TimeSpan.FromSeconds(1));

        await classifier.ClassifyAsync(request);

        var userMessage = provider.Requests.Should().ContainSingle().Which.Messages
            .Single(message => message.Role == "user").Content;
        using var document = JsonDocument.Parse(userMessage!);
        document.RootElement.GetProperty("intents")[0]
            .GetProperty("side_effect_class").GetString().Should().Be("read_only");
        document.RootElement.GetProperty("intents")[1]
            .GetProperty("side_effect_class").GetString().Should().Be("external_handoff");
    }

    [Fact]
    public async Task ClassifyAsync_ShouldSelectTheFinalOutcomeInsteadOfDiscoveryPrerequisites()
    {
        var provider = new StubProvider([
            new LLMStreamChunk { DeltaContent = "{\"status\":\"matched\",\"intent_id\":\"service_connect\"}" },
        ]);
        var classifier = new StreamingAgentProfileTurnClassifier(new StubProviderFactory(provider));
        var request = new AgentProfileTurnClassificationRequest(
            "我要连接 AWS Cost Explorer",
            [
                new AgentProfileTurnClassificationCandidate(
                    "service_discovery",
                    "Browse available services.",
                    AgentProfileSideEffectClass.ReadOnly),
                new AgentProfileTurnClassificationCandidate(
                    "service_connect",
                    "Connect a requested service.",
                    AgentProfileSideEffectClass.ExternalHandoff),
            ],
            TimeSpan.FromSeconds(1));

        await classifier.ClassifyAsync(request);

        var systemMessage = provider.Requests.Should().ContainSingle().Which.Messages
            .Single(message => message.Role == "system").Content;
        systemMessage.Should().Contain("final requested outcome")
            .And.Contain("not an intermediate prerequisite or discovery step")
            .And.Contain("external_handoff")
            .And.Contain("read_only");
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
            .Select(index => new AgentProfileTurnClassificationCandidate(
                $"intent-{index}",
                "route",
                AgentProfileSideEffectClass.ReadOnly))
            .ToArray();

        var countResult = await classifier.ClassifyAsync(new AgentProfileTurnClassificationRequest(
            "message",
            tooManyCandidates,
            TimeSpan.FromSeconds(1)));
        var sizeResult = await classifier.ClassifyAsync(new AgentProfileTurnClassificationRequest(
            new string('x', StreamingAgentProfileTurnClassifier.MaximumInputUtf8Bytes + 1),
            [new AgentProfileTurnClassificationCandidate(
                "intent-a",
                "route",
                AgentProfileSideEffectClass.ReadOnly)],
            TimeSpan.FromSeconds(1)));

        countResult.FailureCode.Should().Be("candidate_count_out_of_bounds");
        sizeResult.FailureCode.Should().Be("input_too_large");
        provider.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ClassifyAsync_NonPositiveTimeout_ShouldFailBeforeCallingProvider()
    {
        var provider = new StubProvider([]);
        var classifier = new StreamingAgentProfileTurnClassifier(new StubProviderFactory(provider));

        var result = await classifier.ClassifyAsync(NewRequest() with { Timeout = TimeSpan.Zero });

        result.Should().Be(AgentProfileTurnClassificationResult.Failed("timeout_out_of_bounds"));
        provider.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ClassifyAsync_InternalTimeout_ShouldFailClosed()
    {
        var timeProvider = new ManualDeadlineTimeProvider();
        var provider = new CancellationBlockingProvider();
        var classifier = new StreamingAgentProfileTurnClassifier(
            new StubProviderFactory(provider),
            timeProvider);

        var classification = classifier.ClassifyAsync(
            NewRequest() with { Timeout = TimeSpan.FromSeconds(1) });
        await provider.Started;

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var result = await classification;

        result.Should().Be(AgentProfileTurnClassificationResult.Failed("timeout"));
        provider.CancellationObserved.Should().BeTrue();
    }

    [Fact]
    public async Task ClassifyAsync_ProviderException_ShouldFailClosed()
    {
        var classifier = new StreamingAgentProfileTurnClassifier(
            new StubProviderFactory(new ThrowingProvider(new InvalidOperationException("provider failed"))));

        var result = await classifier.ClassifyAsync(NewRequest());

        result.Should().Be(AgentProfileTurnClassificationResult.Failed("provider_failure"));
    }

    [Fact]
    public async Task ClassifyAsync_ProviderJsonException_ShouldReturnMalformedOutput()
    {
        var classifier = new StreamingAgentProfileTurnClassifier(
            new StubProviderFactory(new ThrowingProvider(new JsonException("malformed provider output"))));

        var result = await classifier.ClassifyAsync(NewRequest());

        result.Should().Be(AgentProfileTurnClassificationResult.Failed("malformed_output"));
    }

    [Fact]
    public async Task ClassifyAsync_CallerCancellation_ShouldPropagate()
    {
        var classifier = new StreamingAgentProfileTurnClassifier(
            new StubProviderFactory(new CancellationBlockingProvider()));
        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();

        var act = async () => await classifier.ClassifyAsync(NewRequest(), callerCts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ClassifyAsync_ToolCallOutput_ShouldFailClosed()
    {
        var classifier = new StreamingAgentProfileTurnClassifier(
            new StubProviderFactory(new StubProvider(
            [
                new LLMStreamChunk
                {
                    DeltaToolCall = new ToolCall { Id = "call", Name = "tool", ArgumentsJson = "{}" },
                },
            ])));

        var result = await classifier.ClassifyAsync(NewRequest());

        result.Should().Be(AgentProfileTurnClassificationResult.Failed("tool_call_not_allowed"));
    }

    [Fact]
    public async Task ClassifyAsync_EmptyDeltaBeforeValidContent_ShouldStillMatch()
    {
        var classifier = new StreamingAgentProfileTurnClassifier(
            new StubProviderFactory(new StubProvider(
            [
                new LLMStreamChunk { DeltaContent = string.Empty },
                new LLMStreamChunk { DeltaContent = "{\"status\":\"matched\",\"intent_id\":\"intent-a\"}" },
            ])));

        var result = await classifier.ClassifyAsync(NewRequest());

        result.Should().Be(AgentProfileTurnClassificationResult.Matched("intent-a"));
    }

    [Theory]
    [InlineData(null, "empty_output")]
    [InlineData(" \t\n", "empty_output")]
    [InlineData("not-json", "malformed_output")]
    [InlineData("[]", "malformed_output")]
    [InlineData("{\"status\":\"matched\",\"intent_id\":\"intent-a\",\"extra\":true}", "unexpected_output_field")]
    [InlineData("{\"intent_id\":\"intent-a\"}", "status_missing")]
    [InlineData("{\"status\":1,\"intent_id\":\"intent-a\"}", "status_missing")]
    [InlineData("{\"status\":\"matched\"}", "malformed_output")]
    [InlineData("{\"status\":\"matched\",\"intent_id\":1}", "malformed_output")]
    [InlineData("{\"status\":\"matched\",\"intent_id\":\"unknown\"}", "unknown_intent")]
    public async Task ClassifyAsync_RejectedTextOutput_ShouldFailClosed(
        string? deltaContent,
        string failureCode)
    {
        IReadOnlyList<LLMStreamChunk> chunks = deltaContent is null
            ? []
            : [new LLMStreamChunk { DeltaContent = deltaContent }];
        var classifier = new StreamingAgentProfileTurnClassifier(
            new StubProviderFactory(new StubProvider(chunks)));

        var result = await classifier.ClassifyAsync(NewRequest());

        result.Should().Be(AgentProfileTurnClassificationResult.Failed(failureCode));
    }

    [Fact]
    public async Task ClassifyAsync_OversizedOutput_ShouldFailClosed()
    {
        var classifier = new StreamingAgentProfileTurnClassifier(
            new StubProviderFactory(new StubProvider(
            [
                new LLMStreamChunk
                {
                    DeltaContent = new string('x', StreamingAgentProfileTurnClassifier.MaximumOutputUtf8Bytes + 1),
                },
            ])));

        var result = await classifier.ClassifyAsync(NewRequest());

        result.Should().Be(AgentProfileTurnClassificationResult.Failed("output_too_large"));
    }

    private static AgentProfileTurnClassificationRequest NewRequest() =>
        new(
            "please route this",
            [new AgentProfileTurnClassificationCandidate(
                "intent-a",
                "Route A",
                AgentProfileSideEffectClass.ReadOnly)],
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

    private sealed class CancellationBlockingProvider : ILLMProvider
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "blocking-classifier-test";
        public Task Started => _started.Task;
        public bool CancellationObserved { get; private set; }

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _started.TrySetResult();
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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

    private sealed class ThrowingProvider(Exception exception) : ILLMProvider
    {
        public string Name => "throwing-classifier-test";

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            yield return await Task.FromException<LLMStreamChunk>(exception);
        }
    }
}

internal sealed class ManualDeadlineTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public int PendingTimerCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    public void Advance(TimeSpan delta)
    {
        ManualTimer[] timers;
        lock (_gate)
        {
            _utcNow = _utcNow.Add(delta);
            timers = _timers.ToArray();
        }

        foreach (var timer in timers)
            timer.FireIfDue();
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, dueTime, period);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.FireIfDue();
        return timer;
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(
        ManualDeadlineTimeProvider owner,
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period) : ITimer
    {
        private readonly object _gate = new();
        private TimeSpan _period = period;
        private DateTimeOffset? _dueAt = ResolveDueAt(owner.GetUtcNow(), dueTime);
        private bool _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                if (_disposed)
                    return false;

                _period = period;
                _dueAt = ResolveDueAt(owner.GetUtcNow(), dueTime);
            }

            FireIfDue();
            return true;
        }

        public void FireIfDue()
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_disposed || !_dueAt.HasValue || owner.GetUtcNow() < _dueAt.Value)
                        return;

                    _dueAt = _period == Timeout.InfiniteTimeSpan
                        ? null
                        : owner.GetUtcNow().Add(_period);
                }

                callback(state);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
            }

            owner.Remove(this);
        }

        private static DateTimeOffset? ResolveDueAt(DateTimeOffset now, TimeSpan dueTime) =>
            dueTime == Timeout.InfiniteTimeSpan ? null : now.Add(dueTime);
    }
}
