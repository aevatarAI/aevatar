using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgents.StudioMember;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Projection.CommandServices;
using Aevatar.Studio.Projection.Continuations;
using Aevatar.Studio.Projection.Orchestration;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Tests;

public sealed class StudioMemberBindingContinuationServiceTests
{
    [Fact]
    public async Task HandleRequestedAsync_ShouldUpsertScopeBindingAndCompleteRun()
    {
        var scopeBinding = new RecordingScopeBindingPort();
        var memberCommand = new RecordingMemberCommandPort();
        var service = NewService(scopeBinding, memberCommand);

        await service.HandleRequestedAsync(NewWorkflowRequest());

        scopeBinding.LastRequest.Should().NotBeNull();
        scopeBinding.LastRequest!.ServiceId.Should().Be("member-m-1");
        scopeBinding.LastRequest.RevisionId.Should().Be("bind-1");
        scopeBinding.LastRequest.ImplementationKind.Should().Be(ScopeBindingImplementationKind.Workflow);
        memberCommand.Completed.Should().ContainSingle();
        var completed = memberCommand.Completed[0];
        completed.ScopeId.Should().Be("scope-1");
        completed.Request.BindingId.Should().Be("bind-1");
        completed.Request.RevisionId.Should().Be("rev-1");
        completed.Request.ResolvedImplementationRef!.WorkflowId.Should().Be("wf-member-m-1");
        memberCommand.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleRequestedAsync_ShouldFailRun_WhenScopeBindingThrows()
    {
        var scopeBinding = new RecordingScopeBindingPort
        {
            Exception = new InvalidOperationException("binding backend unavailable"),
        };
        var memberCommand = new RecordingMemberCommandPort();
        var service = NewService(scopeBinding, memberCommand);

        await service.HandleRequestedAsync(NewWorkflowRequest());

        memberCommand.Completed.Should().BeEmpty();
        memberCommand.Failed.Should().ContainSingle();
        var failed = memberCommand.Failed[0];
        failed.Request.BindingId.Should().Be("bind-1");
        failed.Request.FailureCode.Should().Be("scope_binding_failed");
        failed.Request.FailureSummary.Should().Contain("binding backend unavailable");
        failed.Request.Retryable.Should().BeTrue();
    }

    [Fact]
    public async Task HandleRequestedAsync_ShouldMapScriptBindingAndCompleteWithScriptRef()
    {
        var scopeBinding = new RecordingScopeBindingPort();
        var memberCommand = new RecordingMemberCommandPort();
        var service = NewService(scopeBinding, memberCommand);

        await service.HandleRequestedAsync(new StudioMemberBindingRequestedEvent
        {
            BindingId = "bind-script",
            ScopeId = "scope-1",
            MemberId = "m-1",
            PublishedServiceId = "member-m-1",
            ImplementationKind = StudioMemberImplementationKind.Script,
            DisplayName = "Script Member",
            Request = new StudioMemberBindingSpec
            {
                RevisionId = "script-rev-request",
                Script = new Aevatar.GAgents.StudioMember.StudioMemberScriptBindingSpec
                {
                    ScriptId = "script-1",
                    ScriptRevision = "draft-2",
                },
            },
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        scopeBinding.LastRequest.Should().NotBeNull();
        scopeBinding.LastRequest!.ImplementationKind.Should().Be(ScopeBindingImplementationKind.Scripting);
        scopeBinding.LastRequest.RevisionId.Should().Be("script-rev-request");
        scopeBinding.LastRequest.Script.Should().BeEquivalentTo(
            new ScopeBindingScriptSpec("script-1", "draft-2"));

        var completed = memberCommand.Completed.Should().ContainSingle().Subject;
        completed.Request.ResolvedImplementationRef.Should().NotBeNull();
        completed.Request.ResolvedImplementationRef!.ImplementationKind
            .Should().Be(MemberImplementationKindNames.Script);
        completed.Request.ResolvedImplementationRef.ScriptId.Should().Be("script-1");
        completed.Request.ResolvedImplementationRef.ScriptRevision.Should().Be("script-result-rev");
        memberCommand.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleRequestedAsync_ShouldMapGAgentBindingAndEndpointKinds()
    {
        var scopeBinding = new RecordingScopeBindingPort();
        var memberCommand = new RecordingMemberCommandPort();
        var service = NewService(scopeBinding, memberCommand);

        await service.HandleRequestedAsync(new StudioMemberBindingRequestedEvent
        {
            BindingId = "bind-gagent",
            ScopeId = "scope-1",
            MemberId = "m-1",
            PublishedServiceId = "member-m-1",
            ImplementationKind = StudioMemberImplementationKind.Gagent,
            DisplayName = "GAgent Member",
            Request = new StudioMemberBindingSpec
            {
                Gagent = new Aevatar.GAgents.StudioMember.StudioMemberGAgentBindingSpec
                {
                    ActorTypeName = "DemoAgent",
                    Endpoints =
                    {
                        new Aevatar.GAgents.StudioMember.StudioMemberGAgentEndpointSpec
                        {
                            EndpointId = "cmd",
                            DisplayName = "Command",
                            Kind = "command",
                            RequestTypeUrl = "type.googleapis.com/demo.Command",
                            ResponseTypeUrl = "type.googleapis.com/demo.CommandResult",
                            Description = "command endpoint",
                        },
                        new Aevatar.GAgents.StudioMember.StudioMemberGAgentEndpointSpec
                        {
                            EndpointId = "chat",
                            DisplayName = "Chat",
                            Kind = "CHAT",
                            RequestTypeUrl = "type.googleapis.com/demo.Chat",
                            ResponseTypeUrl = "type.googleapis.com/demo.ChatResult",
                        },
                        new Aevatar.GAgents.StudioMember.StudioMemberGAgentEndpointSpec
                        {
                            EndpointId = "custom",
                            DisplayName = "Custom",
                            Kind = "custom",
                            RequestTypeUrl = "type.googleapis.com/demo.Custom",
                            ResponseTypeUrl = "type.googleapis.com/demo.CustomResult",
                        },
                    },
                },
            },
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        scopeBinding.LastRequest.Should().NotBeNull();
        scopeBinding.LastRequest!.ImplementationKind.Should().Be(ScopeBindingImplementationKind.GAgent);
        scopeBinding.LastRequest.GAgent.Should().NotBeNull();
        scopeBinding.LastRequest.GAgent!.ActorTypeName.Should().Be("DemoAgent");
        scopeBinding.LastRequest.GAgent.Endpoints.Select(endpoint => endpoint.Kind)
            .Should().Equal(ServiceEndpointKind.Command, ServiceEndpointKind.Chat, ServiceEndpointKind.Unspecified);

        var completed = memberCommand.Completed.Should().ContainSingle().Subject;
        completed.Request.ResolvedImplementationRef.Should().NotBeNull();
        completed.Request.ResolvedImplementationRef!.ImplementationKind
            .Should().Be(MemberImplementationKindNames.GAgent);
        completed.Request.ResolvedImplementationRef.ActorTypeName.Should().Be("DemoAgent");
        memberCommand.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleRequestedAsync_ShouldFailRun_WhenImplementationKindCannotBuildScopeRequest()
    {
        var scopeBinding = new RecordingScopeBindingPort();
        var memberCommand = new RecordingMemberCommandPort();
        var service = NewService(scopeBinding, memberCommand);

        await service.HandleRequestedAsync(new StudioMemberBindingRequestedEvent
        {
            BindingId = "bind-weird",
            ScopeId = "scope-1",
            MemberId = "m-1",
            PublishedServiceId = "member-m-1",
            ImplementationKind = (StudioMemberImplementationKind)999,
            Request = new StudioMemberBindingSpec(),
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

        scopeBinding.LastRequest.Should().BeNull();
        memberCommand.Completed.Should().BeEmpty();
        var failed = memberCommand.Failed.Should().ContainSingle().Subject;
        failed.Request.BindingId.Should().Be("bind-weird");
        failed.Request.FailureCode.Should().Be("scope_binding_failed");
        failed.Request.FailureSummary.Should().Contain("Unsupported StudioMember implementationKind");
    }

    [Fact]
    public async Task HandleRequestedAsync_ShouldPropagateCompletionFailure_WithoutFailingRun()
    {
        var scopeBinding = new RecordingScopeBindingPort();
        var memberCommand = new RecordingMemberCommandPort
        {
            CompleteException = new InvalidOperationException("actor dispatch unavailable"),
        };
        var service = NewService(scopeBinding, memberCommand);

        var act = () => service.HandleRequestedAsync(NewWorkflowRequest());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("actor dispatch unavailable");
        scopeBinding.LastRequest.Should().NotBeNull();
        memberCommand.Completed.Should().ContainSingle();
        memberCommand.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task ContinueAsync_ShouldDispatchContinuationRun_WhenCommittedBindingRequestArrives()
    {
        var dispatcher = new RecordingContinuationDispatcher();
        var continuation = new StudioMemberBindingContinuationHandler(dispatcher);

        await continuation.ContinueAsync(
            new StudioMaterializationContext
            {
                RootActorId = "studio-member:scope-1:m-1",
                ProjectionKind = "studio-current-state",
            },
            WrapCommitted(NewWorkflowRequest()),
            CancellationToken.None);

        dispatcher.Requests.Should().ContainSingle()
            .Which.BindingId.Should().Be("bind-1");
    }

    [Fact]
    public async Task Dispatcher_ShouldCreateContinuationActorAndDispatchTypedCommand()
    {
        var runtime = new RecordingActorRuntime();
        var streamProvider = new RecordingStreamProvider();
        var dispatcher = new StudioMemberBindingContinuationDispatcher(runtime, streamProvider);
        var request = NewWorkflowRequest();

        await dispatcher.DispatchAsync(request);

        var expectedActorId = StudioMemberBindingContinuationDispatcher.BuildActorId(request);
        runtime.CreateCalls.Should().ContainSingle(call =>
            call.Type == typeof(StudioMemberBindingContinuationGAgent) &&
            call.Id == expectedActorId);
        streamProvider.Streams.Should().ContainKey(expectedActorId);
        streamProvider.Streams[expectedActorId].Envelopes.Should().ContainSingle();
        var envelope = streamProvider.Streams[expectedActorId].Envelopes[0];
        envelope.Payload.Should().NotBeNull();
        envelope.Payload!.Is(StudioMemberBindingContinuationRequestedCommand.Descriptor).Should().BeTrue();
        var command = envelope.Payload.Unpack<StudioMemberBindingContinuationRequestedCommand>();
        command.Request.BindingId.Should().Be("bind-1");
        command.Request.ScopeId.Should().Be("scope-1");
    }

    private static StudioMemberBindingRequestedEvent NewWorkflowRequest() =>
        new()
        {
            BindingId = "bind-1",
            ScopeId = "scope-1",
            MemberId = "m-1",
            PublishedServiceId = "member-m-1",
            ImplementationKind = StudioMemberImplementationKind.Workflow,
            DisplayName = "Workflow Member",
            Request = new StudioMemberBindingSpec
            {
                Workflow = new Aevatar.GAgents.StudioMember.StudioMemberWorkflowBindingSpec
                {
                    WorkflowYamls = { "workflow: test" },
                },
            },
            RequestedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

    private static StudioMemberBindingContinuationService NewService(
        IScopeBindingCommandPort scopeBinding,
        IStudioMemberCommandPort memberCommand) =>
        new(scopeBinding, memberCommand, NullLogger<StudioMemberBindingContinuationService>.Instance);

    private static EventEnvelope WrapCommitted(IMessage payload)
    {
        return new EventEnvelope
        {
            Id = "evt-1",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("studio-member:scope-1:m-1"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = "evt-1",
                    Version = 1,
                    EventData = Any.Pack(payload),
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                },
            }),
        };
    }

    private sealed class RecordingScopeBindingPort : IScopeBindingCommandPort
    {
        public ScopeBindingUpsertRequest? LastRequest { get; private set; }

        public Exception? Exception { get; set; }

        public Task<ScopeBindingUpsertResult> UpsertAsync(
            ScopeBindingUpsertRequest request, CancellationToken ct = default)
        {
            if (Exception is not null)
                throw Exception;

            LastRequest = request;
            return Task.FromResult(request.ImplementationKind switch
            {
                ScopeBindingImplementationKind.Scripting => new ScopeBindingUpsertResult(
                    ScopeId: request.ScopeId,
                    ServiceId: request.ServiceId ?? string.Empty,
                    DisplayName: request.DisplayName ?? string.Empty,
                    RevisionId: "rev-1",
                    ImplementationKind: request.ImplementationKind,
                    ExpectedActorId: "script-actor-1",
                    Script: new ScopeBindingScriptResult(
                        ScriptId: request.Script?.ScriptId ?? string.Empty,
                        ScriptRevision: "script-result-rev",
                        DefinitionActorId: "script-definition-1")),
                ScopeBindingImplementationKind.GAgent => new ScopeBindingUpsertResult(
                    ScopeId: request.ScopeId,
                    ServiceId: request.ServiceId ?? string.Empty,
                    DisplayName: request.DisplayName ?? string.Empty,
                    RevisionId: "rev-1",
                    ImplementationKind: request.ImplementationKind,
                    ExpectedActorId: "gagent-actor-1",
                    GAgent: new ScopeBindingGAgentResult(
                        ActorTypeName: request.GAgent?.ActorTypeName ?? string.Empty)),
                _ => new ScopeBindingUpsertResult(
                    ScopeId: request.ScopeId,
                    ServiceId: request.ServiceId ?? string.Empty,
                    DisplayName: request.DisplayName ?? string.Empty,
                    RevisionId: "rev-1",
                    ImplementationKind: request.ImplementationKind,
                    ExpectedActorId: "actor-1",
                    Workflow: new ScopeBindingWorkflowResult(
                        WorkflowName: $"wf-{request.ServiceId}",
                        DefinitionActorIdPrefix: $"def-{request.ServiceId}")),
            });
        }
    }

    private sealed class RecordingMemberCommandPort : IStudioMemberCommandPort
    {
        public List<CompletedCall> Completed { get; } = [];

        public List<FailedCall> Failed { get; } = [];

        public Exception? CompleteException { get; set; }

        public Task<StudioMemberSummaryResponse> CreateAsync(
            string scopeId, CreateStudioMemberRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<StudioMemberBindingAcceptedResponse> RequestBindingAsync(
            string scopeId, string memberId, UpdateStudioMemberBindingRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task UpdateImplementationAsync(
            string scopeId, string memberId, StudioMemberImplementationRefResponse implementation, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task RecordBindingAsync(
            string scopeId,
            string memberId,
            string publishedServiceId,
            string revisionId,
            string implementationKindName,
            CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task CompleteBindingAsync(
            string scopeId,
            string memberId,
            StudioMemberBindingCompletionRequest request,
            CancellationToken ct = default)
        {
            Completed.Add(new CompletedCall(scopeId, memberId, request));
            if (CompleteException is not null)
                throw CompleteException;

            return Task.CompletedTask;
        }

        public Task FailBindingAsync(
            string scopeId,
            string memberId,
            StudioMemberBindingFailureRequest request,
            CancellationToken ct = default)
        {
            Failed.Add(new FailedCall(scopeId, memberId, request));
            return Task.CompletedTask;
        }

        public Task ReassignTeamAsync(
            string scopeId,
            string memberId,
            string? fromTeamId,
            string? toTeamId,
            CancellationToken ct = default) =>
            throw new NotImplementedException();

        public sealed record CompletedCall(
            string ScopeId,
            string MemberId,
            StudioMemberBindingCompletionRequest Request);

        public sealed record FailedCall(
            string ScopeId,
            string MemberId,
            StudioMemberBindingFailureRequest Request);
    }

    private sealed class RecordingContinuationDispatcher : IStudioMemberBindingContinuationDispatcher
    {
        public List<StudioMemberBindingRequestedEvent> Requests { get; } = [];

        public Task DispatchAsync(StudioMemberBindingRequestedEvent request, CancellationToken ct = default)
        {
            Requests.Add(request.Clone());
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _actors = [];

        public List<(System.Type Type, string? Id)> CreateCalls { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent => CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actor = new RecordingActor(id ?? Guid.NewGuid().ToString("N"));
            _actors[actor.Id] = actor;
            CreateCalls.Add((agentType, id));
            return Task.FromResult<IActor>(actor);
        }

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(_actors.GetValueOrDefault(id));

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            _actors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));
        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingStreamProvider : IStreamProvider
    {
        public Dictionary<string, RecordingStream> Streams { get; } = [];

        public IStream GetStream(string actorId)
        {
            if (!Streams.TryGetValue(actorId, out var stream))
            {
                stream = new RecordingStream(actorId);
                Streams[actorId] = stream;
            }

            return stream;
        }
    }

    private sealed class RecordingStream : IStream
    {
        public RecordingStream(string streamId) => StreamId = streamId;

        public string StreamId { get; }

        public List<EventEnvelope> Envelopes { get; } = [];

        public Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage
        {
            message.Should().BeOfType<EventEnvelope>();
            Envelopes.Add((EventEnvelope)(object)message);
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
            where T : IMessage, new() =>
            throw new NotImplementedException();

        public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    private sealed class RecordingActor : IActor
    {
        public RecordingActor(string id) => Id = id;

        public string Id { get; }
        public IAgent Agent { get; } = new RecordingAgent();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingAgent : IAgent
    {
        public string Id => "recording-agent";
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
