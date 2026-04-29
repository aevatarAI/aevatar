using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Foundation.Abstractions;
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
    public async Task ContinueAsync_ShouldInvokeContinuation_WhenCommittedBindingRequestArrives()
    {
        var scopeBinding = new RecordingScopeBindingPort();
        var memberCommand = new RecordingMemberCommandPort();
        var service = NewService(scopeBinding, memberCommand);
        var continuation = new StudioMemberBindingContinuationHandler(service);

        await continuation.ContinueAsync(
            new StudioMaterializationContext
            {
                RootActorId = "studio-member:scope-1:m-1",
                ProjectionKind = "studio-current-state",
            },
            WrapCommitted(NewWorkflowRequest()),
            CancellationToken.None);

        scopeBinding.LastRequest.Should().NotBeNull();
        memberCommand.Completed.Should().ContainSingle()
            .Which.Request.BindingId.Should().Be("bind-1");
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
            return Task.FromResult(new ScopeBindingUpsertResult(
                ScopeId: request.ScopeId,
                ServiceId: request.ServiceId ?? string.Empty,
                DisplayName: request.DisplayName ?? string.Empty,
                RevisionId: "rev-1",
                ImplementationKind: request.ImplementationKind,
                ExpectedActorId: "actor-1",
                Workflow: new ScopeBindingWorkflowResult(
                    WorkflowName: $"wf-{request.ServiceId}",
                    DefinitionActorIdPrefix: $"def-{request.ServiceId}")));
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
}
