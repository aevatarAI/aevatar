using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Responses;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.TestSupport;

internal sealed class LlmRunAcceptanceHarness
{
    public const string ResponseId = "resp_acceptance";
    public const string RunId = "run_acceptance";
    public const string ActorId = "response-session-actor-" + ResponseId;

    public static LlmRunRequested BuildRunRequest() =>
        new()
        {
            ResponseId = ResponseId,
            RunId = RunId,
            ScopeId = "user-1",
            OwnerSubject = "user-1",
            BearerToken = "token-1",
            Model = "test-model",
            Messages =
            {
                new LlmSessionRuntimeChatMessage
                {
                    Role = "user",
                    Content = "Write a short answer.",
                },
            },
        };

    public static LlmSessionRecord BuildRecord() =>
        new()
        {
            ResponseId = ResponseId,
            ScopeId = "user-1",
            OwnerSubject = "user-1",
            OriginKind = LlmSessionOriginKind.ApiKey,
            CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:00:00+00:00")),
            Ttl = Duration.FromTimeSpan(TimeSpan.FromHours(24)),
        };

    public static LlmRunCompleted Completed(string outputText = "hello world", long sequence = 0) =>
        new()
        {
            ResponseId = ResponseId,
            RunId = RunId,
            OutputText = outputText,
            Sequence = sequence,
            CompletedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:00:10+00:00")),
        };

    public static LlmRunFailed Failed(string code = "provider_error", long sequence = 0) =>
        new()
        {
            ResponseId = ResponseId,
            RunId = RunId,
            FailureCode = code,
            FailureMessage = "Provider failed.",
            Sequence = sequence,
            FailedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:00:10+00:00")),
        };

    public static LlmRunCancelled Cancelled(long sequence = 0) =>
        new()
        {
            ResponseId = ResponseId,
            RunId = RunId,
            Sequence = sequence,
            CancelledAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:00:10+00:00")),
        };

    public static LlmStreamChunkObserved Chunk(string deltaText, long sequence = 0) =>
        new()
        {
            ResponseId = ResponseId,
            RunId = RunId,
            DeltaText = deltaText,
            Sequence = sequence,
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:00:01+00:00")),
        };

    public static LlmToolCallObserved LocalToolCall(string callId, long sequence = 0) =>
        new()
        {
            ResponseId = ResponseId,
            RunId = RunId,
            Sequence = sequence,
            Forwarded = false,
            ToolCall = new LlmSessionRuntimeToolCall
            {
                CallId = callId,
                ToolName = "get_weather",
                Arguments = ResponsesJsonValues.ParseBoundaryPayload("""{"city":"Singapore"}""").StructValue,
            },
            LocalResult = ResponsesJsonValues.ParseBoundaryPayload("""{"temperature":28}"""),
            LocalResultJson = """{"temperature":28}""",
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:00:02+00:00")),
        };

    public static StateEvent StateEvent(long version, IMessage payload) =>
        new()
        {
            EventId = $"event-{version}",
            Version = version,
            EventData = Any.Pack(payload),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-06-19T00:00:00+00:00")),
        };

    public sealed class RecordingLlmRunSink : ILlmRunSink
    {
        public List<LlmStreamChunkObserved> StreamChunks { get; } = [];
        public List<LlmToolCallObserved> ToolCalls { get; } = [];
        public List<LlmSessionForwardedToolCallEmittedEvent> ForwardedToolCalls { get; } = [];
        public List<LlmRunCompleted> Completed { get; } = [];
        public List<LlmRunFailed> Failed { get; } = [];
        public List<LlmRunCancelled> Cancelled { get; } = [];

        public Task<LlmRunRecordDecision> RecordStreamChunkObservedAsync(
            LlmStreamChunkObserved observed,
            CancellationToken ct = default)
        {
            StreamChunks.Add(observed.Clone());
            return Task.FromResult(LlmRunRecordDecision.Continue);
        }

        public Task<LlmRunRecordDecision> RecordToolCallObservedAsync(
            LlmToolCallObserved observed,
            CancellationToken ct = default)
        {
            ToolCalls.Add(observed.Clone());
            return Task.FromResult(LlmRunRecordDecision.Continue);
        }

        public Task RecordForwardedToolCallEmittedAsync(
            LlmSessionForwardedToolCallEmittedEvent emitted,
            CancellationToken ct = default)
        {
            ForwardedToolCalls.Add(emitted.Clone());
            return Task.CompletedTask;
        }

        public Task<LlmRunRecordDecision> RecordRunCompletedAsync(
            LlmRunCompleted completed,
            CancellationToken ct = default)
        {
            Completed.Add(completed.Clone());
            return Task.FromResult(LlmRunRecordDecision.Continue);
        }

        public Task<LlmRunRecordDecision> RecordRunFailedAsync(
            LlmRunFailed failed,
            CancellationToken ct = default)
        {
            Failed.Add(failed.Clone());
            return Task.FromResult(LlmRunRecordDecision.Continue);
        }

        public Task<LlmRunRecordDecision> RecordRunCancelledAsync(
            LlmRunCancelled cancelled,
            CancellationToken ct = default)
        {
            Cancelled.Add(cancelled.Clone());
            return Task.FromResult(LlmRunRecordDecision.Continue);
        }
    }

    public sealed class NeverTerminalLlmProviderFactory(
        TaskCompletionSource streamEntered) : ILLMProviderFactory, ILLMProvider
    {
        private readonly TaskCompletionSource _neverCompletes =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "test";

        public ILLMProvider GetProvider(string name) => this;

        public ILLMProvider GetDefault() => this;

        public IReadOnlyList<string> GetAvailableProviders() => [Name];

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            streamEntered.TrySetResult();
            yield return new LLMStreamChunk { DeltaContent = "partial" };
            await _neverCompletes.Task.WaitAsync(ct);
        }
    }
}
