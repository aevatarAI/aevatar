using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Infrastructure.Adapters;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Infrastructure;

public sealed class ResponseSessionRegistrationAdapterTests
{
    [Fact]
    public void Constructor_ShouldRejectNullDependencies()
    {
        var runtime = new RecordingRuntime();
        var dispatch = new RecordingDispatchPort();
        var projection = new RecordingProjectionPort();

        ((Action)(() => new ResponseSessionRegistrationAdapter(null!, dispatch, projection)))
            .Should().Throw<ArgumentNullException>().WithMessage("*runtime*");
        ((Action)(() => new ResponseSessionRegistrationAdapter(runtime, null!, projection)))
            .Should().Throw<ArgumentNullException>().WithMessage("*dispatchPort*");
        ((Action)(() => new ResponseSessionRegistrationAdapter(runtime, dispatch, null!)))
            .Should().Throw<ArgumentNullException>().WithMessage("*projectionPort*");
    }

    [Fact]
    public async Task RegisterAsync_ShouldCreateActor_DefaultTimestampsAndStatus()
    {
        var (adapter, runtime, dispatch, projection) = CreateAdapter();
        var record = BuildRecord();
        record.CreatedAt = null;
        record.Status = ResponseSessionStatus.Unspecified;

        var result = await adapter.RegisterAsync(record);

        result.ResponseId.Should().Be("resp_1");
        result.ActorId.Should().StartWith("response-session-");
        runtime.CreateCalls.Should().ContainSingle();
        runtime.CreateCalls[0].agentType.Should().Be(typeof(ResponseSessionGAgent));
        projection.EnsureCalls.Should().ContainSingle().Which.Should().Be(result.ActorId);
        dispatch.Calls.Should().ContainSingle();
        dispatch.Calls[0].envelope.Payload.TypeUrl.Should().Contain("RegisterResponseSessionRequested");
        var packed = dispatch.Calls[0].envelope.Payload.Unpack<RegisterResponseSessionRequested>();
        packed.Record.Status.Should().Be(ResponseSessionStatus.Accepted);
        packed.Record.CreatedAt.Should().NotBeNull();
        packed.Record.UpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RegisterAsync_ShouldPreservePreSetTimestampsAndStatus()
    {
        var (adapter, _, dispatch, _) = CreateAdapter();
        var preset = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-04-01T00:00:00+00:00"));
        var record = BuildRecord();
        record.CreatedAt = preset;
        record.Status = ResponseSessionStatus.Failed;

        await adapter.RegisterAsync(record);

        var packed = dispatch.Calls[0].envelope.Payload.Unpack<RegisterResponseSessionRequested>();
        packed.Record.Status.Should().Be(ResponseSessionStatus.Failed);
        packed.Record.CreatedAt.Should().Be(preset);
        packed.Record.UpdatedAt.Should().Be(preset);
    }

    [Fact]
    public async Task RegisterAsync_ShouldRejectMissingRequiredFields()
    {
        var (adapter, _, _, _) = CreateAdapter();

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
        var (adapter, _, dispatch, _) = CreateAdapter();

        await adapter.UpdateStatusAsync("session-actor-1", "resp_1", ResponseSessionStatus.Completed);

        dispatch.Calls.Should().ContainSingle();
        dispatch.Calls[0].actorId.Should().Be("session-actor-1");
        dispatch.Calls[0].envelope.Payload.TypeUrl.Should().Contain("UpdateResponseSessionStatusRequested");
        var packed = dispatch.Calls[0].envelope.Payload.Unpack<UpdateResponseSessionStatusRequested>();
        packed.ResponseId.Should().Be("resp_1");
        packed.Status.Should().Be(ResponseSessionStatus.Completed);
    }

    [Fact]
    public async Task UpdateStatusAsync_ShouldNoOp_WhenStatusUnspecified()
    {
        var (adapter, _, dispatch, _) = CreateAdapter();

        await adapter.UpdateStatusAsync("session-actor-1", "resp_1", ResponseSessionStatus.Unspecified);

        dispatch.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "resp_1", "sessionActorId")]
    [InlineData("actor-1", "", "responseId")]
    public async Task UpdateStatusAsync_ShouldRejectMissingArguments(string actorId, string respId, string param)
    {
        var (adapter, _, _, _) = CreateAdapter();

        var act = () => adapter.UpdateStatusAsync(actorId, respId, ResponseSessionStatus.Completed);

        await act.Should().ThrowAsync<ArgumentException>().Where(ex => ex.ParamName == param);
    }

    [Fact]
    public async Task RecordForwardedToolCallAsync_ShouldDispatch_WithDefaultStatusAndTimestamp()
    {
        var (adapter, _, dispatch, _) = CreateAdapter();
        var call = new ResponseSessionForwardedToolCall
        {
            CallId = "call-1",
            ToolName = "WebFetch",
        };

        await adapter.RecordForwardedToolCallAsync("actor-1", "resp_1", call);

        dispatch.Calls.Should().ContainSingle();
        var packed = dispatch.Calls[0].envelope.Payload.Unpack<RecordForwardedToolCallRequested>();
        packed.Call.Status.Should().Be(ResponseSessionForwardedToolCallStatus.Pending);
        packed.Call.EmittedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RecordForwardedToolCallAsync_ShouldPreservePreSetStatusAndTimestamp()
    {
        var (adapter, _, dispatch, _) = CreateAdapter();
        var preset = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-04-01T00:00:00+00:00"));
        var call = new ResponseSessionForwardedToolCall
        {
            CallId = "call-1",
            ToolName = "WebFetch",
            Status = ResponseSessionForwardedToolCallStatus.Resolved,
            EmittedAt = preset,
        };

        await adapter.RecordForwardedToolCallAsync("actor-1", "resp_1", call);

        var packed = dispatch.Calls[0].envelope.Payload.Unpack<RecordForwardedToolCallRequested>();
        packed.Call.Status.Should().Be(ResponseSessionForwardedToolCallStatus.Resolved);
        packed.Call.EmittedAt.Should().Be(preset);
    }

    [Fact]
    public async Task RecordForwardedToolCallAsync_ShouldRejectMissingArguments()
    {
        var (adapter, _, _, _) = CreateAdapter();
        var call = new ResponseSessionForwardedToolCall { CallId = "call-1" };

        await ((Func<Task>)(() => adapter.RecordForwardedToolCallAsync("", "resp_1", call)))
            .Should().ThrowAsync<ArgumentException>().Where(ex => ex.ParamName == "sessionActorId");
        await ((Func<Task>)(() => adapter.RecordForwardedToolCallAsync("actor-1", "", call)))
            .Should().ThrowAsync<ArgumentException>().Where(ex => ex.ParamName == "responseId");
        await ((Func<Task>)(() => adapter.RecordForwardedToolCallAsync("actor-1", "resp_1", null!)))
            .Should().ThrowAsync<ArgumentNullException>();

        var emptyCallId = new ResponseSessionForwardedToolCall { CallId = string.Empty };
        await ((Func<Task>)(() => adapter.RecordForwardedToolCallAsync("actor-1", "resp_1", emptyCallId)))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("call_id*");
    }

    [Fact]
    public async Task ReceiveForwardedToolResultAsync_ShouldDispatchAndAcceptNullJson()
    {
        var (adapter, _, dispatch, _) = CreateAdapter();

        await adapter.ReceiveForwardedToolResultAsync("actor-1", "resp_1", "call-1", "hash-1", resultJson: null!);

        dispatch.Calls.Should().ContainSingle();
        var packed = dispatch.Calls[0].envelope.Payload.Unpack<ReceiveForwardedToolResultRequested>();
        packed.CallId.Should().Be("call-1");
        packed.SchemaHash.Should().Be("hash-1");
        packed.ResultJson.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "resp_1", "call-1", "hash", "sessionActorId")]
    [InlineData("actor-1", "", "call-1", "hash", "responseId")]
    [InlineData("actor-1", "resp_1", "", "hash", "callId")]
    [InlineData("actor-1", "resp_1", "call-1", "", "schemaHash")]
    public async Task ReceiveForwardedToolResultAsync_ShouldRejectMissingArguments(
        string actorId, string respId, string callId, string hash, string param)
    {
        var (adapter, _, _, _) = CreateAdapter();

        var act = () => adapter.ReceiveForwardedToolResultAsync(actorId, respId, callId, hash, "{}");

        await act.Should().ThrowAsync<ArgumentException>().Where(ex => ex.ParamName == param);
    }

    [Fact]
    public async Task ResolveForwardedToolResultAsync_ShouldDispatchResolvedEnvelope()
    {
        var (adapter, _, dispatch, _) = CreateAdapter();

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
        var (adapter, _, _, _) = CreateAdapter();

        var act = () => adapter.ResolveForwardedToolResultAsync(actorId, respId, callId);

        await act.Should().ThrowAsync<ArgumentException>().Where(ex => ex.ParamName == param);
    }

    private static (ResponseSessionRegistrationAdapter adapter, RecordingRuntime runtime, RecordingDispatchPort dispatch, RecordingProjectionPort projection) CreateAdapter()
    {
        var runtime = new RecordingRuntime();
        var dispatch = new RecordingDispatchPort();
        var projection = new RecordingProjectionPort();
        var adapter = new ResponseSessionRegistrationAdapter(runtime, dispatch, projection);
        return (adapter, runtime, dispatch, projection);
    }

    private static ResponseSessionRecord BuildRecord() => new()
    {
        ResponseId = "resp_1",
        ScopeId = "scope-1",
        OwnerSubject = "owner-1",
        OriginKind = ResponseSessionOriginKind.ApiKey,
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

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProjectionPort : IResponseSessionCurrentStateProjectionPort
    {
        public List<string> EnsureCalls { get; } = [];

        public Task EnsureProjectionAsync(string actorId, CancellationToken ct = default)
        {
            EnsureCalls.Add(actorId);
            return Task.CompletedTask;
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
