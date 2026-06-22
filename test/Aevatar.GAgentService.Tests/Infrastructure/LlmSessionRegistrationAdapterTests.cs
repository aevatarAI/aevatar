using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Infrastructure.Adapters;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Infrastructure;

public sealed class LlmSessionRegistrationAdapterTests
{
    [Fact]
    public void Constructor_ShouldRejectNullDependencies()
    {
        var runtime = new RecordingRuntime();
        var dispatch = new RecordingDispatchPort();

        ((Action)(() => new LlmSessionRegistrationAdapter(null!, dispatch)))
            .Should().Throw<ArgumentNullException>().WithMessage("*runtime*");
        ((Action)(() => new LlmSessionRegistrationAdapter(runtime, null!)))
            .Should().Throw<ArgumentNullException>().WithMessage("*dispatchPort*");
    }

    [Fact]
    public async Task RegisterAsync_ShouldCreateActor_DefaultTimestampsAndStatus()
    {
        var (adapter, runtime, dispatch) = CreateAdapter();
        var record = BuildRecord();
        record.CreatedAt = null;
        record.Status = LlmSessionStatus.Unspecified;

        var result = await adapter.RegisterAsync(record);

        result.ResponseId.Should().Be("resp_1");
        result.ActorId.Should().StartWith("response-session-");
        runtime.CreateCalls.Should().ContainSingle();
        runtime.CreateCalls[0].agentType.Should().Be(typeof(LlmSessionGAgent));
        dispatch.Calls.Should().ContainSingle();
        dispatch.Calls[0].envelope.Payload.TypeUrl.Should().Contain("RegisterResponseSessionRequested");
        var packed = dispatch.Calls[0].envelope.Payload.Unpack<RegisterResponseSessionRequested>();
        packed.Record.Status.Should().Be(LlmSessionStatus.Accepted);
        packed.Record.CreatedAt.Should().NotBeNull();
        packed.Record.UpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RegisterAsync_ShouldPreservePreSetTimestampsAndStatus()
    {
        var (adapter, _, dispatch) = CreateAdapter();
        var preset = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-04-01T00:00:00+00:00"));
        var record = BuildRecord();
        record.CreatedAt = preset;
        record.Status = LlmSessionStatus.Failed;

        await adapter.RegisterAsync(record);

        var packed = dispatch.Calls[0].envelope.Payload.Unpack<RegisterResponseSessionRequested>();
        packed.Record.Status.Should().Be(LlmSessionStatus.Failed);
        packed.Record.CreatedAt.Should().Be(preset);
        packed.Record.UpdatedAt.Should().Be(preset);
    }

    [Fact]
    public async Task RegisterAsync_ShouldRejectMissingRequiredFields()
    {
        var (adapter, _, _) = CreateAdapter();

        await ((Func<Task>)(() => adapter.RegisterAsync(null!)))
            .Should().ThrowAsync<ArgumentNullException>();

        var noResp = BuildRecord(); noResp.ResponseId = string.Empty;
        await ((Func<Task>)(() => adapter.RegisterAsync(noResp)))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("response_id*");

        var noScope = BuildRecord(); noScope.ScopeId = string.Empty;
        await ((Func<Task>)(() => adapter.RegisterAsync(noScope)))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("scope_id*");

        var noOwner = BuildRecord(); noOwner.OwnerSubject = string.Empty;
        await ((Func<Task>)(() => adapter.RegisterAsync(noOwner)))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("owner_subject*");
    }

    [Fact]
    public async Task UpdateStatusAsync_ShouldDispatchUpdateEnvelope()
    {
        var (adapter, _, dispatch) = CreateAdapter();

        await adapter.UpdateStatusAsync("session-actor-1", "resp_1", LlmSessionStatus.Completed);

        dispatch.Calls.Should().ContainSingle();
        dispatch.Calls[0].actorId.Should().Be("session-actor-1");
        dispatch.Calls[0].envelope.Payload.TypeUrl.Should().Contain("UpdateResponseSessionStatusRequested");
        var packed = dispatch.Calls[0].envelope.Payload.Unpack<UpdateResponseSessionStatusRequested>();
        packed.ResponseId.Should().Be("resp_1");
        packed.Status.Should().Be(LlmSessionStatus.Completed);
    }

    [Fact]
    public async Task UpdateStatusAsync_ShouldNoOp_WhenStatusUnspecified()
    {
        var (adapter, _, dispatch) = CreateAdapter();

        await adapter.UpdateStatusAsync("session-actor-1", "resp_1", LlmSessionStatus.Unspecified);

        dispatch.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelRunAsync_ShouldDispatchActorOwnedCancellationEnvelope()
    {
        var (adapter, _, dispatch) = CreateAdapter();

        await adapter.CancelRunAsync("session-actor-1", "resp_1", "resp_1:llm-run");

        dispatch.Calls.Should().ContainSingle();
        dispatch.Calls[0].actorId.Should().Be("session-actor-1");
        var packed = dispatch.Calls[0].envelope.Payload.Unpack<CancelLlmRunRequested>();
        packed.ResponseId.Should().Be("resp_1");
        packed.RunId.Should().Be("resp_1:llm-run");
        packed.CancelledAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData("", "resp_1", "sessionActorId")]
    [InlineData("actor-1", "", "responseId")]
    public async Task UpdateStatusAsync_ShouldRejectMissingArguments(string actorId, string respId, string param)
    {
        var (adapter, _, _) = CreateAdapter();

        var act = () => adapter.UpdateStatusAsync(actorId, respId, LlmSessionStatus.Completed);

        await act.Should().ThrowAsync<ArgumentException>().Where(ex => ex.ParamName == param);
    }

    [Theory]
    [InlineData("", "resp_1", "run-1", "sessionActorId")]
    [InlineData("actor-1", "", "run-1", "responseId")]
    [InlineData("actor-1", "resp_1", "", "runId")]
    public async Task CancelRunAsync_ShouldRejectMissingArguments(string actorId, string respId, string runId, string param)
    {
        var (adapter, _, _) = CreateAdapter();

        var act = () => adapter.CancelRunAsync(actorId, respId, runId);

        await act.Should().ThrowAsync<ArgumentException>().Where(ex => ex.ParamName == param);
    }

    [Fact]
    public async Task RecordForwardedToolCallAsync_ShouldDispatch_WithDefaultStatusAndTimestamp()
    {
        var (adapter, _, dispatch) = CreateAdapter();
        var call = new LlmSessionForwardedToolCall
        {
            CallId = "call-1",
            ToolName = "WebFetch",
            SchemaHash = "schema-1",
            Arguments = ResponsesJsonValues.ParseBoundaryPayload("""{"url":"https://example.com"}"""),
        };

        await adapter.RecordForwardedToolCallAsync("actor-1", "resp_1", call);

        dispatch.Calls.Should().ContainSingle();
        var packed = dispatch.Calls[0].envelope.Payload.Unpack<RecordForwardedToolCallRequested>();
        packed.Call.Status.Should().Be(LlmSessionForwardedToolCallStatus.Pending);
        packed.Call.EmittedAt.Should().NotBeNull();
        ResponsesJsonValues.ToBoundaryJson(packed.Call.Arguments)
            .Should().Be("""{"url":"https://example.com"}""");
    }

    [Fact]
    public async Task RecordForwardedToolCallAsync_ShouldPreservePreSetStatusAndTimestamp()
    {
        var (adapter, _, dispatch) = CreateAdapter();
        var preset = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-04-01T00:00:00+00:00"));
        var call = new LlmSessionForwardedToolCall
        {
            CallId = "call-1",
            ToolName = "WebFetch",
            Status = LlmSessionForwardedToolCallStatus.Resolved,
            EmittedAt = preset,
        };

        await adapter.RecordForwardedToolCallAsync("actor-1", "resp_1", call);

        var packed = dispatch.Calls[0].envelope.Payload.Unpack<RecordForwardedToolCallRequested>();
        packed.Call.Status.Should().Be(LlmSessionForwardedToolCallStatus.Resolved);
        packed.Call.EmittedAt.Should().Be(preset);
    }

    [Fact]
    public async Task RecordForwardedToolCallAsync_ShouldRejectMissingArguments()
    {
        var (adapter, _, _) = CreateAdapter();
        var call = new LlmSessionForwardedToolCall { CallId = "call-1" };

        await ((Func<Task>)(() => adapter.RecordForwardedToolCallAsync("", "resp_1", call)))
            .Should().ThrowAsync<ArgumentException>().Where(ex => ex.ParamName == "sessionActorId");
        await ((Func<Task>)(() => adapter.RecordForwardedToolCallAsync("actor-1", "", call)))
            .Should().ThrowAsync<ArgumentException>().Where(ex => ex.ParamName == "responseId");
        await ((Func<Task>)(() => adapter.RecordForwardedToolCallAsync("actor-1", "resp_1", null!)))
            .Should().ThrowAsync<ArgumentNullException>();

        var emptyCallId = new LlmSessionForwardedToolCall { CallId = string.Empty };
        await ((Func<Task>)(() => adapter.RecordForwardedToolCallAsync("actor-1", "resp_1", emptyCallId)))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("call_id*");
    }

    [Fact]
    public async Task ReceiveForwardedToolResultAsync_ShouldDispatchAndAcceptNullJson()
    {
        var (adapter, _, dispatch) = CreateAdapter();

        await adapter.ReceiveForwardedToolResultAsync("actor-1", "resp_1", "call-1", "hash-1", resultJson: null!);

        dispatch.Calls.Should().ContainSingle();
        var packed = dispatch.Calls[0].envelope.Payload.Unpack<ReceiveForwardedToolResultRequested>();
        packed.CallId.Should().Be("call-1");
        packed.SchemaHash.Should().Be("hash-1");
        ResponsesJsonValues.ToBoundaryJson(packed.Result).Should().Be("{}");
    }

    [Fact]
    public async Task ReceiveForwardedToolResultAsync_ShouldParseBoundaryJsonResult()
    {
        var (adapter, _, dispatch) = CreateAdapter();

        await adapter.ReceiveForwardedToolResultAsync(
            " actor-1 ",
            " resp_1 ",
            " call-1 ",
            " hash-1 ",
            """{ "ok": true }""");

        var packed = dispatch.Calls[0].envelope.Payload.Unpack<ReceiveForwardedToolResultRequested>();
        packed.ResponseId.Should().Be("resp_1");
        packed.CallId.Should().Be("call-1");
        packed.SchemaHash.Should().Be("hash-1");
        ResponsesJsonValues.ToBoundaryJson(packed.Result).Should().Be("""{"ok":true}""");
    }

    [Theory]
    [InlineData("", "resp_1", "call-1", "hash", "sessionActorId")]
    [InlineData("actor-1", "", "call-1", "hash", "responseId")]
    [InlineData("actor-1", "resp_1", "", "hash", "callId")]
    [InlineData("actor-1", "resp_1", "call-1", "", "schemaHash")]
    public async Task ReceiveForwardedToolResultAsync_ShouldRejectMissingArguments(
        string actorId, string respId, string callId, string hash, string param)
    {
        var (adapter, _, _) = CreateAdapter();

        var act = () => adapter.ReceiveForwardedToolResultAsync(actorId, respId, callId, hash, "{}");

        await act.Should().ThrowAsync<ArgumentException>().Where(ex => ex.ParamName == param);
    }

    [Fact]
    public async Task ResolveForwardedToolResultAsync_ShouldDispatchResolvedEnvelope()
    {
        var (adapter, _, dispatch) = CreateAdapter();

        await adapter.ResolveForwardedToolResultAsync("actor-1", "resp_1", "call-1");

        dispatch.Calls.Should().ContainSingle();
        var packed = dispatch.Calls[0].envelope.Payload.Unpack<ResolveForwardedToolResultRequested>();
        packed.ResponseId.Should().Be("resp_1");
        packed.CallId.Should().Be("call-1");
        packed.ResolvedAt.Should().NotBeNull();
    }

    [Theory]
    [InlineData("", "resp_1", "call-1", "sessionActorId")]
    [InlineData("actor-1", "", "call-1", "responseId")]
    [InlineData("actor-1", "resp_1", "", "callId")]
    public async Task ResolveForwardedToolResultAsync_ShouldRejectMissingArguments(
        string actorId, string respId, string callId, string param)
    {
        var (adapter, _, _) = CreateAdapter();

        var act = () => adapter.ResolveForwardedToolResultAsync(actorId, respId, callId);

        await act.Should().ThrowAsync<ArgumentException>().Where(ex => ex.ParamName == param);
    }

    [Fact]
    public async Task RecordCompletionAsync_ShouldDispatchCompletionEnvelope_WithDefaultTimestamp()
    {
        var (adapter, _, dispatch) = CreateAdapter();
        var completion = new LlmSessionCompletion
        {
            OutputText = "forwarded done",
            ToolCalls =
            {
                new LlmSessionCompletedToolCall
                {
                    CallId = "call-1",
                    ToolName = "WebFetch",
                    Result = ResponsesJsonValues.ParseBoundaryPayload("""{"ok":true}"""),
                },
            },
        };

        await adapter.RecordCompletionAsync("actor-1", "resp_1", completion);

        dispatch.Calls.Should().ContainSingle();
        dispatch.Calls[0].actorId.Should().Be("actor-1");
        dispatch.Calls[0].envelope.Payload.TypeUrl.Should().Contain("RecordResponseSessionCompletionRequested");
        var packed = dispatch.Calls[0].envelope.Payload.Unpack<RecordResponseSessionCompletionRequested>();
        packed.ResponseId.Should().Be("resp_1");
        packed.Completion.OutputText.Should().Be("forwarded done");
        packed.Completion.CompletedAt.Should().NotBeNull();
        packed.Completion.ToolCalls.Should().ContainSingle()
            .Which.CallId.Should().Be("call-1");
    }

    [Fact]
    public async Task RecordCompletionAsync_ShouldPreservePresetTimestamp()
    {
        var (adapter, _, dispatch) = CreateAdapter();
        var preset = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-04-01T00:00:00+00:00"));
        var completion = new LlmSessionCompletion
        {
            OutputText = "done",
            CompletedAt = preset,
        };

        await adapter.RecordCompletionAsync("actor-1", "resp_1", completion);

        var packed = dispatch.Calls[0].envelope.Payload.Unpack<RecordResponseSessionCompletionRequested>();
        packed.Completion.CompletedAt.Should().Be(preset);
    }

    [Theory]
    [InlineData("", "resp_1", "sessionActorId")]
    [InlineData("actor-1", "", "responseId")]
    public async Task RecordCompletionAsync_ShouldRejectMissingArguments(string actorId, string respId, string param)
    {
        var (adapter, _, _) = CreateAdapter();
        var completion = new LlmSessionCompletion();

        var act = () => adapter.RecordCompletionAsync(actorId, respId, completion);

        await act.Should().ThrowAsync<ArgumentException>().Where(ex => ex.ParamName == param);
    }

    [Fact]
    public async Task RecordCompletionAsync_ShouldRejectNullCompletion()
    {
        var (adapter, _, _) = CreateAdapter();

        var act = () => adapter.RecordCompletionAsync("actor-1", "resp_1", null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private static (LlmSessionRegistrationAdapter adapter, RecordingRuntime runtime, RecordingDispatchPort dispatch) CreateAdapter()
    {
        var runtime = new RecordingRuntime();
        var dispatch = new RecordingDispatchPort();
        var adapter = new LlmSessionRegistrationAdapter(runtime, dispatch);
        return (adapter, runtime, dispatch);
    }

    private static LlmSessionRecord BuildRecord() => new()
    {
        ResponseId = "resp_1",
        ScopeId = "scope-1",
        OwnerSubject = "owner-1",
        OriginKind = LlmSessionOriginKind.ApiKey,
    };

    private sealed class RecordingRuntime : IActorRuntime
    {
        public List<(System.Type agentType, string actorId)> CreateCalls { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? $"created:{agentType.Name}";
            CreateCalls.Add((agentType, actorId));
            return Task.FromResult<IActor>(new RecordingActor(actorId));
        }

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);
        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<(string actorId, EventEnvelope envelope)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }
    private sealed class RecordingActor : IActor
    {
        public RecordingActor(string id) { Id = id; }
        public string Id { get; }
        public IAgent Agent { get; } = new TestStaticServiceAgent();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
