using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgents.WorkOrder;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests.WorkOrders;

public sealed class WorkOrderAssignmentValidatorTests
{
    [Fact]
    public async Task ValidateAsync_ShouldReturnAuthoritativeDistinctIdentities()
    {
        var validator = CreateValidator();

        var result = await validator.ValidateAsync(
            "scope-1",
            "team-1",
            "member-1",
            "service-1",
            "run");

        result.MemberId.Should().Be("member-1");
        result.PublishedServiceId.Should().Be("service-1");
        result.WorkflowId.Should().Be("workflow-1");
        result.ServiceRevisionId.Should().Be("revision-1");
        result.ImplementationKind.Should().Be(MemberImplementationKindNames.Workflow);
    }

    [Theory]
    [InlineData("scope-other", "team-1", "service-1", "revision-1", "WorkOrder Team was not found")]
    [InlineData("scope-1", "team-other", "service-1", "revision-1", "does not belong")]
    [InlineData("scope-1", "team-1", "service-other", "revision-1", "does not match")]
    [InlineData("scope-1", "team-1", "service-1", "revision-stale", "stale revision")]
    public async Task ValidateAsync_ShouldFailClosed_WhenAuthorityRelationshipDoesNotMatch(
        string teamScopeId,
        string memberTeamId,
        string memberServiceId,
        string readinessRevisionId,
        string expectedMessage)
    {
        var validator = CreateValidator(
            teamScopeId: teamScopeId,
            memberTeamId: memberTeamId,
            memberServiceId: memberServiceId,
            readinessRevisionId: readinessRevisionId);

        var act = () => validator.ValidateAsync(
            "scope-1",
            "team-1",
            "member-1",
            "service-1",
            "run");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{expectedMessage}*");
    }

    [Fact]
    public async Task ValidateAsync_ShouldFailClosed_WhenServiceIsNotCallable()
    {
        var validator = CreateValidator(invokeReady: false);

        var act = () => validator.ValidateAsync(
            "scope-1",
            "team-1",
            "member-1",
            "service-1",
            "run");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not callable*");
    }

    [Fact]
    public async Task ValidateAsync_ShouldRequireWorkflowIdentityForWorkflowMember()
    {
        var validator = CreateValidator(workflowId: null);

        var act = () => validator.ValidateAsync(
            "scope-1",
            "team-1",
            "member-1",
            "service-1",
            "run");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no authoritative workflow identity*");
    }

    internal static WorkOrderAssignmentValidator CreateValidator(
        string teamScopeId = "scope-1",
        string memberTeamId = "team-1",
        string memberServiceId = "service-1",
        string bindingRevisionId = "revision-1",
        string readinessRevisionId = "revision-1",
        string? workflowId = "workflow-1",
        string implementationKind = MemberImplementationKindNames.Workflow,
        bool invokeReady = true)
    {
        var now = DateTimeOffset.Parse("2026-07-17T00:00:00Z");
        var team = new StudioTeamSummaryResponse(
            "team-1",
            teamScopeId,
            "Team One",
            string.Empty,
            TeamLifecycleStageNames.Active,
            1,
            now,
            now);
        var implementationRef = new StudioMemberImplementationRefResponse(
            implementationKind,
            WorkflowId: workflowId);
        var summary = new StudioMemberSummaryResponse(
            "member-1",
            "scope-1",
            "Member One",
            string.Empty,
            implementationKind,
            MemberLifecycleStageNames.BindReady,
            memberServiceId,
            bindingRevisionId,
            now,
            now)
        {
            TeamId = memberTeamId,
            ImplementationRef = implementationRef,
        };
        var member = new StudioMemberDetailResponse(
            summary,
            implementationRef,
            new StudioMemberBindingContractResponse(
                memberServiceId,
                bindingRevisionId,
                implementationKind,
                now));
        var readiness = new ScopeBindingReadinessSnapshot(
            "scope-1",
            "service-1",
            invokeReady ? ScopeBindingReadinessStatus.Ready : ScopeBindingReadinessStatus.PreparedArtifactMissing,
            ServiceCatalogVisible: true,
            ServingSetVisible: true,
            EligibleServingTargetVisible: invokeReady,
            InvokeReady: invokeReady,
            RevisionId: readinessRevisionId,
            DeploymentId: "deployment-1",
            ObservedAtUtc: now);
        return new WorkOrderAssignmentValidator(
            new FixedTeamQueryPort(team),
            new FixedMemberQueryPort(member),
            new FixedReadinessQueryPort(readiness));
    }

    private sealed class FixedTeamQueryPort(StudioTeamSummaryResponse? team) : IStudioTeamQueryPort
    {
        public Task<StudioTeamRosterResponse> ListAsync(
            string scopeId,
            StudioTeamRosterPageRequest? page = null,
            CancellationToken ct = default) =>
            Task.FromResult(new StudioTeamRosterResponse(scopeId, team == null ? [] : [team]));

        public Task<StudioTeamSummaryResponse?> GetAsync(
            string scopeId,
            string teamId,
            CancellationToken ct = default) => Task.FromResult(team);
    }

    private sealed class FixedMemberQueryPort(StudioMemberDetailResponse? member) : IStudioMemberQueryPort
    {
        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default) =>
            Task.FromResult(new StudioMemberRosterResponse(
                scopeId,
                member == null ? [] : [member.Summary]));

        public Task<StudioMemberDetailResponse?> GetAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) => Task.FromResult(member);
    }

    private sealed class FixedReadinessQueryPort(ScopeBindingReadinessSnapshot readiness)
        : IScopeBindingReadinessQueryPort
    {
        public Task<ScopeBindingReadinessSnapshot> GetReadinessAsync(
            ScopeBindingReadinessRequest request,
            CancellationToken ct = default) => Task.FromResult(readiness);
    }
}

public sealed class ValidatedWorkOrderExecutionPortTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldRevalidateAndPreserveWorkflowRunIdentityAndCallback()
    {
        var invocationPort = new RecordingInvocationPort();
        var port = new ValidatedWorkOrderExecutionPort(
            WorkOrderAssignmentValidatorTests.CreateValidator(),
            invocationPort);
        var request = BuildExecutionRequest();

        var result = await port.ExecuteAsync(request);

        result.ResultCase.Should().Be(WorkOrderExecutionResult.ResultOneofCase.Accepted);
        result.Accepted.RunId.Should().Be("run-1");
        result.Accepted.CommandId.Should().Be("command-1");
        invocationPort.Requests.Should().ContainSingle();
        var invoked = invocationPort.Requests[0];
        invoked.Identity.TenantId.Should().Be("scope-1");
        invoked.Identity.ServiceId.Should().Be("service-1");
        invoked.CommandId.Should().Be("command-1");
        invoked.CorrelationId.Should().Be("command-1");
        invoked.RequestedRunId.Should().Be("run-1");
        invoked.WorkflowCompletionNotificationTarget.ActorId.Should().Be("work-order:scope-1:wo-1");
        invoked.WorkflowCompletionNotificationTarget.DeliveryId.Should().Be("delivery-1");
        invoked.ServiceRunCompletionNotificationTarget.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldUseServiceRunCallbackForGAgentImplementation()
    {
        var invocationPort = new RecordingInvocationPort();
        var port = new ValidatedWorkOrderExecutionPort(
            WorkOrderAssignmentValidatorTests.CreateValidator(
                workflowId: null,
                implementationKind: MemberImplementationKindNames.GAgent),
            invocationPort);
        var request = BuildExecutionRequest();
        request.WorkflowId = string.Empty;
        request.ImplementationKind = MemberImplementationKindNames.GAgent;

        var result = await port.ExecuteAsync(request);

        result.ResultCase.Should().Be(WorkOrderExecutionResult.ResultOneofCase.Accepted);
        var invoked = invocationPort.Requests.Should().ContainSingle().Subject;
        invoked.WorkflowCompletionNotificationTarget.Should().BeNull();
        invoked.ServiceRunCompletionNotificationTarget.ActorId.Should().Be("work-order:scope-1:wo-1");
        invoked.ServiceRunCompletionNotificationTarget.DeliveryId.Should().Be("delivery-1");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFailBeforeInvocation_WhenAssignmentChangedAfterAuthorization()
    {
        var invocationPort = new RecordingInvocationPort();
        var port = new ValidatedWorkOrderExecutionPort(
            WorkOrderAssignmentValidatorTests.CreateValidator(bindingRevisionId: "revision-2", readinessRevisionId: "revision-2"),
            invocationPort);

        var result = await port.ExecuteAsync(BuildExecutionRequest());

        result.ResultCase.Should().Be(WorkOrderExecutionResult.ResultOneofCase.Failed);
        result.Failed.Failure.Code.Should().Be("WORK_ORDER_ASSIGNMENT_NOT_DISPATCHABLE");
        invocationPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFailClosed_WhenInvocationReceiptChangesRunIdentity()
    {
        var invocationPort = new RecordingInvocationPort
        {
            ReceiptRunId = "different-run",
        };
        var port = new ValidatedWorkOrderExecutionPort(
            WorkOrderAssignmentValidatorTests.CreateValidator(),
            invocationPort);

        var result = await port.ExecuteAsync(BuildExecutionRequest());

        result.ResultCase.Should().Be(WorkOrderExecutionResult.ResultOneofCase.Failed);
        result.Failed.Failure.Code.Should().Be("WORK_ORDER_RUN_IDENTITY_MISMATCH");
    }

    private static WorkOrderExecutionRequest BuildExecutionRequest() =>
        new()
        {
            WorkOrderActorId = "work-order:scope-1:wo-1",
            WorkOrderId = "wo-1",
            ScopeId = "scope-1",
            TeamId = "team-1",
            MemberId = "member-1",
            PublishedServiceId = "service-1",
            WorkflowId = "workflow-1",
            ServiceRevisionId = "revision-1",
            ImplementationKind = MemberImplementationKindNames.Workflow,
            EndpointId = "run",
            Input = new WorkOrderServiceInput
            {
                Chat = new WorkOrderChatInput { Prompt = "do the work" },
            },
            DispatchCommandId = "command-1",
            RequestedRunId = "run-1",
            TerminalDeliveryId = "delivery-1",
        };

    private sealed class RecordingInvocationPort : IServiceInvocationPort
    {
        public List<ServiceInvocationRequest> Requests { get; } = [];

        public string ReceiptRunId { get; init; } = "run-1";

        public Task<ServiceInvocationAcceptedReceipt> InvokeAsync(
            ServiceInvocationRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request.Clone());
            return Task.FromResult(new ServiceInvocationAcceptedReceipt
            {
                RunId = ReceiptRunId,
                TargetActorId = "workflow-run-actor-1",
                CommandId = request.CommandId,
                CorrelationId = request.CorrelationId,
                DeploymentId = "deployment-1",
            });
        }
    }
}
