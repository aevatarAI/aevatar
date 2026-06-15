using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioMemberServicePatchTests
{
    private const string ScopeId = "scope-1";
    private const string MemberId = "m-alpha";
    private const string PublishedServiceId = "svc-alpha";

    [Fact]
    public async Task PatchAsync_DisplayName_ShouldDispatchRenameAndPreserveOtherFields()
    {
        var original = NewDetail(MemberImplementationKindNames.Workflow);
        var updatedSummary = original.Summary with
        {
            DisplayName = "Renamed Workflow",
            Description = "existing description",
        };
        var commandPort = new RecordingMemberCommandPort();
        var queryPort = new InMemoryQueryPort(original with { Summary = updatedSummary });
        var service = NewService(commandPort, queryPort);

        var response = await service.UpdateAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberRequest(
                DisplayName: PatchValue<string>.Of("  Renamed Workflow  ")),
            CancellationToken.None);

        commandPort.Renames.Should().ContainSingle()
            .Which.Should().Be(new RenameUpdate(ScopeId, MemberId, "Renamed Workflow"));
        commandPort.ImplementationUpdates.Should().BeEmpty();
        commandPort.RecordedBindings.Should().BeEmpty();
        response.Summary.DisplayName.Should().Be("Renamed Workflow");
        response.Summary.Description.Should().Be("existing description");
        response.Summary.MemberId.Should().Be(MemberId);
        response.Summary.PublishedServiceId.Should().Be(PublishedServiceId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PatchAsync_DisplayName_ShouldRejectEmptyName(string displayName)
    {
        var service = NewService(
            new RecordingMemberCommandPort(),
            NewQueryPort(MemberImplementationKindNames.Workflow));

        var act = () => service.UpdateAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberRequest(DisplayName: PatchValue<string>.Of(displayName)),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*displayName is required*");
    }

    [Fact]
    public async Task PatchAsync_DisplayName_ShouldUseCreateTimeLengthLimit()
    {
        var service = NewService(
            new RecordingMemberCommandPort(),
            NewQueryPort(MemberImplementationKindNames.Workflow));
        var tooLong = new string('x', StudioMemberInputLimits.MaxDisplayNameLength + 1);

        var act = () => service.UpdateAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberRequest(DisplayName: PatchValue<string>.Of(tooLong)),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{StudioMemberInputLimits.MaxDisplayNameLength} characters*");
    }

    [Fact]
    public async Task PatchAsync_WorkflowImplementationRef_ShouldUpdateMemberAuthorityOnly()
    {
        var commandPort = new RecordingMemberCommandPort();
        var service = NewService(
            commandPort,
            NewQueryPort(MemberImplementationKindNames.Workflow));

        var response = await service.UpdateAsync(
            ScopeId,
            MemberId,
            ImplementationPatch(new StudioMemberImplementationRefResponse(
                    ImplementationKind: MemberImplementationKindNames.Workflow,
                    WorkflowId: "wf-alpha")),
            CancellationToken.None);

        commandPort.ImplementationUpdates.Should().ContainSingle();
        var update = commandPort.ImplementationUpdates[0];
        update.ScopeId.Should().Be(ScopeId);
        update.MemberId.Should().Be(MemberId);
        update.Implementation.Should().Be(new StudioMemberImplementationRefResponse(
            ImplementationKind: MemberImplementationKindNames.Workflow,
            WorkflowId: "wf-alpha"));
        commandPort.RecordedBindings.Should().BeEmpty();
        response.Summary.MemberId.Should().Be(MemberId);
    }

    [Fact]
    public async Task PatchAsync_ScriptImplementationRef_ShouldAllowOptionalRevision()
    {
        var commandPort = new RecordingMemberCommandPort();
        var service = NewService(
            commandPort,
            NewQueryPort(MemberImplementationKindNames.Script));

        await service.UpdateAsync(
            ScopeId,
            MemberId,
            ImplementationPatch(new StudioMemberImplementationRefResponse(
                    ImplementationKind: MemberImplementationKindNames.Script,
                    ScriptId: "script-alpha",
                    ScriptRevision: "rev-script-1")),
            CancellationToken.None);

        commandPort.ImplementationUpdates.Should().ContainSingle()
            .Which.Implementation.Should().Be(new StudioMemberImplementationRefResponse(
                ImplementationKind: MemberImplementationKindNames.Script,
                ScriptId: "script-alpha",
                ScriptRevision: "rev-script-1"));
        commandPort.RecordedBindings.Should().BeEmpty();
    }

    [Fact]
    public async Task PatchAsync_GAgentImplementationRef_ShouldUpdateActorTypeName()
    {
        var commandPort = new RecordingMemberCommandPort();
        var service = NewService(
            commandPort,
            NewQueryPort(MemberImplementationKindNames.GAgent));

        await service.UpdateAsync(
            ScopeId,
            MemberId,
            ImplementationPatch(new StudioMemberImplementationRefResponse(
                    ImplementationKind: MemberImplementationKindNames.GAgent,
                    DiagnosticActorTypeName: "Aevatar.SomeAgent")),
            CancellationToken.None);

        commandPort.ImplementationUpdates.Should().ContainSingle()
            .Which.Implementation.Should().Be(new StudioMemberImplementationRefResponse(
                ImplementationKind: MemberImplementationKindNames.GAgent,
                DiagnosticActorTypeName: "Aevatar.SomeAgent"));
        commandPort.RecordedBindings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PatchAsync_WorkflowImplementationRef_ShouldRejectEmptyWorkflowId(string workflowId)
    {
        var service = NewService(
            new RecordingMemberCommandPort(),
            NewQueryPort(MemberImplementationKindNames.Workflow));

        var act = () => service.UpdateAsync(
            ScopeId,
            MemberId,
            ImplementationPatch(new StudioMemberImplementationRefResponse(
                    ImplementationKind: MemberImplementationKindNames.Workflow,
                    WorkflowId: workflowId)),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*implementationRef.workflowId is required*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PatchAsync_ScriptImplementationRef_ShouldRejectEmptyScriptId(string scriptId)
    {
        var service = NewService(
            new RecordingMemberCommandPort(),
            NewQueryPort(MemberImplementationKindNames.Script));

        var act = () => service.UpdateAsync(
            ScopeId,
            MemberId,
            ImplementationPatch(new StudioMemberImplementationRefResponse(
                    ImplementationKind: MemberImplementationKindNames.Script,
                    ScriptId: scriptId)),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*implementationRef.scriptId is required*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PatchAsync_GAgentImplementationRef_ShouldRejectEmptyActorTypeName(string actorTypeName)
    {
        var service = NewService(
            new RecordingMemberCommandPort(),
            NewQueryPort(MemberImplementationKindNames.GAgent));

        var act = () => service.UpdateAsync(
            ScopeId,
            MemberId,
            ImplementationPatch(new StudioMemberImplementationRefResponse(
                    ImplementationKind: MemberImplementationKindNames.GAgent,
                    DiagnosticActorTypeName: actorTypeName)),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*implementationRef.actorTypeName is required*");
    }

    [Theory]
    [InlineData("scriptId")]
    [InlineData("scriptRevision")]
    [InlineData("actorTypeName")]
    public async Task PatchAsync_WorkflowImplementationRef_ShouldRejectNonWorkflowFields(string disallowedField)
    {
        var service = NewService(
            new RecordingMemberCommandPort(),
            NewQueryPort(MemberImplementationKindNames.Workflow));

        var implementation = disallowedField switch
        {
            "scriptId" => new StudioMemberImplementationRefResponse(
                MemberImplementationKindNames.Workflow,
                WorkflowId: "wf-alpha",
                ScriptId: "script-alpha"),
            "scriptRevision" => new StudioMemberImplementationRefResponse(
                MemberImplementationKindNames.Workflow,
                WorkflowId: "wf-alpha",
                ScriptRevision: "rev-script-1"),
            "actorTypeName" => new StudioMemberImplementationRefResponse(
                MemberImplementationKindNames.Workflow,
                WorkflowId: "wf-alpha",
                DiagnosticActorTypeName: "Aevatar.SomeAgent"),
            _ => throw new ArgumentOutOfRangeException(nameof(disallowedField)),
        };

        var act = () => service.UpdateAsync(
            ScopeId,
            MemberId,
            ImplementationPatch(implementation),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*implementationRef.{disallowedField} is not allowed*");
    }

    [Theory]
    [InlineData("workflowId")]
    [InlineData("workflowRevision")]
    [InlineData("actorTypeName")]
    public async Task PatchAsync_ScriptImplementationRef_ShouldRejectNonScriptFields(string disallowedField)
    {
        var service = NewService(
            new RecordingMemberCommandPort(),
            NewQueryPort(MemberImplementationKindNames.Script));

        var implementation = disallowedField switch
        {
            "workflowId" => new StudioMemberImplementationRefResponse(
                MemberImplementationKindNames.Script,
                WorkflowId: "wf-alpha",
                ScriptId: "script-alpha"),
            "workflowRevision" => new StudioMemberImplementationRefResponse(
                MemberImplementationKindNames.Script,
                WorkflowRevision: "wf-rev-1",
                ScriptId: "script-alpha"),
            "actorTypeName" => new StudioMemberImplementationRefResponse(
                MemberImplementationKindNames.Script,
                ScriptId: "script-alpha",
                DiagnosticActorTypeName: "Aevatar.SomeAgent"),
            _ => throw new ArgumentOutOfRangeException(nameof(disallowedField)),
        };

        var act = () => service.UpdateAsync(
            ScopeId,
            MemberId,
            ImplementationPatch(implementation),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*implementationRef.{disallowedField} is not allowed*");
    }

    [Theory]
    [InlineData("workflowId")]
    [InlineData("workflowRevision")]
    [InlineData("scriptId")]
    [InlineData("scriptRevision")]
    public async Task PatchAsync_GAgentImplementationRef_ShouldRejectNonGAgentFields(string disallowedField)
    {
        var service = NewService(
            new RecordingMemberCommandPort(),
            NewQueryPort(MemberImplementationKindNames.GAgent));

        var implementation = disallowedField switch
        {
            "workflowId" => new StudioMemberImplementationRefResponse(
                MemberImplementationKindNames.GAgent,
                WorkflowId: "wf-alpha",
                DiagnosticActorTypeName: "Aevatar.SomeAgent"),
            "workflowRevision" => new StudioMemberImplementationRefResponse(
                MemberImplementationKindNames.GAgent,
                WorkflowRevision: "wf-rev-1",
                DiagnosticActorTypeName: "Aevatar.SomeAgent"),
            "scriptId" => new StudioMemberImplementationRefResponse(
                MemberImplementationKindNames.GAgent,
                ScriptId: "script-alpha",
                DiagnosticActorTypeName: "Aevatar.SomeAgent"),
            "scriptRevision" => new StudioMemberImplementationRefResponse(
                MemberImplementationKindNames.GAgent,
                ScriptRevision: "rev-script-1",
                DiagnosticActorTypeName: "Aevatar.SomeAgent"),
            _ => throw new ArgumentOutOfRangeException(nameof(disallowedField)),
        };

        var act = () => service.UpdateAsync(
            ScopeId,
            MemberId,
            ImplementationPatch(implementation),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*implementationRef.{disallowedField} is not allowed*");
    }

    [Fact]
    public async Task PatchAsync_ImplementationRef_ShouldRequireImplementationKind()
    {
        var service = NewService(
            new RecordingMemberCommandPort(),
            NewQueryPort(MemberImplementationKindNames.Workflow));

        var act = () => service.UpdateAsync(
            ScopeId,
            MemberId,
            ImplementationPatch(new StudioMemberImplementationRefResponse(
                    ImplementationKind: string.Empty,
                    WorkflowId: "wf-alpha")),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*implementationRef.implementationKind is required*");
    }

    [Fact]
    public async Task PatchAsync_ImplementationRef_ShouldRejectKindMismatch()
    {
        var service = NewService(
            new RecordingMemberCommandPort(),
            NewQueryPort(MemberImplementationKindNames.Workflow));

        var act = () => service.UpdateAsync(
            ScopeId,
            MemberId,
            ImplementationPatch(new StudioMemberImplementationRefResponse(
                    ImplementationKind: MemberImplementationKindNames.Script,
                    ScriptId: "script-alpha")),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*implementationKind is locked at create*");
    }

    private static UpdateStudioMemberRequest ImplementationPatch(
        StudioMemberImplementationRefResponse implementation) =>
        new(ImplementationRef: PatchValue<StudioMemberImplementationRefResponse>.Of(implementation));

    private static StudioMemberService NewService(
        IStudioMemberCommandPort memberCommandPort,
        IStudioMemberQueryPort memberQueryPort) =>
        new(
            memberCommandPort,
            memberQueryPort,
            new ThrowingBindingRunQueryPort(),
            new ThrowingTeamQueryPort(),
            new ThrowingServiceLifecycleQueryPort(),
            new ThrowingScopeBindingReadinessQueryPort(),
            new ThrowingServiceCommandPort());

    private static InMemoryQueryPort NewQueryPort(string implementationKind) =>
        new(NewDetail(implementationKind));

    private static StudioMemberDetailResponse NewDetail(string implementationKind)
    {
        var summary = new StudioMemberSummaryResponse(
            MemberId: MemberId,
            ScopeId: ScopeId,
            DisplayName: "Alpha",
            Description: "existing description",
            ImplementationKind: implementationKind,
            LifecycleStage: MemberLifecycleStageNames.BindReady,
            PublishedServiceId: PublishedServiceId,
            LastBoundRevisionId: "rev-bound-1",
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        return new StudioMemberDetailResponse(
            Summary: summary,
            ImplementationRef: null,
            LastBinding: new StudioMemberBindingContractResponse(
                PublishedServiceId,
                "rev-bound-1",
                implementationKind,
                DateTimeOffset.UtcNow.AddMinutes(-5)));
    }

    private sealed class InMemoryQueryPort : IStudioMemberQueryPort
    {
        private readonly Queue<StudioMemberDetailResponse> _details;

        public InMemoryQueryPort(params StudioMemberDetailResponse[] details)
        {
            _details = new Queue<StudioMemberDetailResponse>(details);
        }

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default) =>
            Task.FromResult(new StudioMemberRosterResponse(scopeId, [_details.Peek().Summary]));

        public Task<StudioMemberDetailResponse?> GetAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default) =>
            Task.FromResult<StudioMemberDetailResponse?>(
                _details.Count > 1 ? _details.Dequeue() : _details.Peek());
    }

    private sealed class RecordingMemberCommandPort : IStudioMemberCommandPort
    {
        public List<ImplementationUpdate> ImplementationUpdates { get; } = [];
        public List<RenameUpdate> Renames { get; } = [];
        public List<string> RecordedBindings { get; } = [];

        public Task<StudioMemberSummaryResponse> CreateAsync(
            string scopeId,
            CreateStudioMemberRequest request,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("patch must not create members.");

        public Task UpdateImplementationAsync(
            string scopeId,
            string memberId,
            StudioMemberImplementationRefResponse implementation,
            CancellationToken ct = default)
        {
            ImplementationUpdates.Add(new ImplementationUpdate(scopeId, memberId, implementation));
            return Task.CompletedTask;
        }

        public Task RenameAsync(
            string scopeId,
            string memberId,
            string displayName,
            CancellationToken ct = default)
        {
            Renames.Add(new RenameUpdate(scopeId, memberId, displayName));
            return Task.CompletedTask;
        }

        public Task StartBindingRunAsync(
            StudioMemberBindingRunStartRequest request,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("member patch must not start binding runs.");

        public Task PatchTeamAssignmentAsync(
            string scopeId,
            string memberId,
            string? targetTeamId,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("implementationRef patch must not patch team assignment.");
    }

    private sealed record ImplementationUpdate(
        string ScopeId,
        string MemberId,
        StudioMemberImplementationRefResponse Implementation);

    private sealed record RenameUpdate(string ScopeId, string MemberId, string DisplayName);

    private sealed class ThrowingBindingRunQueryPort : IStudioMemberBindingRunQueryPort
    {
        public Task<StudioMemberBindingRunStatusResponse?> GetAsync(
            string scopeId,
            string memberId,
            string bindingRunId,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("member patch must not query binding runs.");
    }

    private sealed class ThrowingTeamQueryPort : IStudioTeamQueryPort
    {
        public Task<StudioTeamRosterResponse> ListAsync(
            string scopeId,
            StudioTeamRosterPageRequest? page = null,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("implementationRef patch must not list teams.");

        public Task<StudioTeamSummaryResponse?> GetAsync(
            string scopeId,
            string teamId,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("implementationRef patch must not query teams.");
    }

    private sealed class ThrowingScopeBindingReadinessQueryPort : IScopeBindingReadinessQueryPort
    {
        public Task<ScopeBindingReadinessSnapshot> GetReadinessAsync(
            ScopeBindingReadinessRequest request,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("member patch must not query scope binding readiness.");
    }

    private sealed class ThrowingServiceLifecycleQueryPort : IServiceLifecycleQueryPort
    {
        public Task<ServiceCatalogSnapshot?> GetServiceAsync(
            ServiceIdentity identity,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("member patch must not query service lifecycle.");

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("member patch must not list services.");

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(
            ServiceIdentity identity,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("member patch must not query service revisions.");

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(
            ServiceIdentity identity,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("member patch must not query service deployments.");
    }

    private sealed class ThrowingServiceCommandPort : IServiceCommandPort
    {
        private static InvalidOperationException Reject(string method) =>
            new($"member patch must not call IServiceCommandPort.{method}.");

        public Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(
            CreateServiceDefinitionCommand command, CancellationToken ct = default) => throw Reject(nameof(CreateServiceAsync));
        public Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(
            UpdateServiceDefinitionCommand command, CancellationToken ct = default) => throw Reject(nameof(UpdateServiceAsync));
        public Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(
            CreateServiceRevisionCommand command, CancellationToken ct = default) => throw Reject(nameof(CreateRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(
            PrepareServiceRevisionCommand command, CancellationToken ct = default) => throw Reject(nameof(PrepareRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(
            PublishServiceRevisionCommand command, CancellationToken ct = default) => throw Reject(nameof(PublishRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> RetireRevisionAsync(
            RetireServiceRevisionCommand command, CancellationToken ct = default) => throw Reject(nameof(RetireRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(
            SetDefaultServingRevisionCommand command, CancellationToken ct = default) => throw Reject(nameof(SetDefaultServingRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(
            ActivateServiceRevisionCommand command, CancellationToken ct = default) => throw Reject(nameof(ActivateServiceRevisionAsync));
        public Task<ServiceCommandAcceptedReceipt> DeactivateServiceDeploymentAsync(
            DeactivateServiceDeploymentCommand command, CancellationToken ct = default) => throw Reject(nameof(DeactivateServiceDeploymentAsync));
        public Task<ServiceCommandAcceptedReceipt> ReplaceServiceServingTargetsAsync(
            ReplaceServiceServingTargetsCommand command, CancellationToken ct = default) => throw Reject(nameof(ReplaceServiceServingTargetsAsync));
        public Task<ServiceCommandAcceptedReceipt> StartServiceRolloutAsync(
            StartServiceRolloutCommand command, CancellationToken ct = default) => throw Reject(nameof(StartServiceRolloutAsync));
        public Task<ServiceCommandAcceptedReceipt> AdvanceServiceRolloutAsync(
            AdvanceServiceRolloutCommand command, CancellationToken ct = default) => throw Reject(nameof(AdvanceServiceRolloutAsync));
        public Task<ServiceCommandAcceptedReceipt> PauseServiceRolloutAsync(
            PauseServiceRolloutCommand command, CancellationToken ct = default) => throw Reject(nameof(PauseServiceRolloutAsync));
        public Task<ServiceCommandAcceptedReceipt> ResumeServiceRolloutAsync(
            ResumeServiceRolloutCommand command, CancellationToken ct = default) => throw Reject(nameof(ResumeServiceRolloutAsync));
        public Task<ServiceCommandAcceptedReceipt> RollbackServiceRolloutAsync(
            RollbackServiceRolloutCommand command, CancellationToken ct = default) => throw Reject(nameof(RollbackServiceRolloutAsync));
    }
}
