using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

/// <summary>
/// Locks in the most important binding invariants:
///
///   - BindAsync is now an accepted command surface, not a synchronous scope
///     binding orchestration.
///   - The request path does not read IStudioMemberQueryPort for admission.
///   - The request path does not call the platform scope binding port.
///   - Missing-member and validation failures come from the actor-owned
///     command dispatch path.
/// </summary>
public sealed class StudioMemberServiceBindingTests
{
    private const string ScopeId = "scope-1";
    private const string MemberId = "m-bind-test";
    private const string PublishedServiceId = "member-m-bind-test";

    [Fact]
    public async Task BindAsync_ShouldReturnAcceptedReceiptFromCommandPort()
    {
        var commandPort = new RecordingCommandPort();
        var service = NewService(commandPort, new ThrowingQueryPort());

        var response = await service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(
                Workflow: new StudioMemberWorkflowBindingSpec(["workflow:\n  name: x"])),
            CancellationToken.None);

        response.Status.Should().Be(StudioMemberBindingStatusNames.Accepted);
        response.ScopeId.Should().Be(ScopeId);
        response.MemberId.Should().Be(MemberId);
        response.BindingId.Should().Be(commandPort.AcceptedBindingId);
        commandPort.RequestedBindings.Should().ContainSingle()
            .Which.Request.Workflow!.WorkflowYamls.Should().ContainSingle();
    }

    [Fact]
    public async Task BindAsync_ShouldNotReadMemberQueryPort()
    {
        var commandPort = new RecordingCommandPort();

        var service = NewService(
            commandPort,
            new ThrowingQueryPort());

        await service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(
                Script: new StudioMemberScriptBindingSpec(ScriptId: "s-1", ScriptRevision: "v3")),
            CancellationToken.None);

        commandPort.RequestedBindings.Should().ContainSingle()
            .Which.Request.Script!.ScriptId.Should().Be("s-1");
    }

    [Fact]
    public async Task BindAsync_ShouldPropagateActorOwnedMissingMemberFailure()
    {
        var commandPort = new RecordingCommandPort
        {
            RequestException = new StudioMemberNotFoundException(ScopeId, MemberId),
        };

        var service = NewService(
            commandPort,
            new ThrowingQueryPort());

        var act = () => service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(
                Workflow: new StudioMemberWorkflowBindingSpec(["workflow:"])),
            CancellationToken.None);

        // Assert the typed exception so a regression that swaps it for a
        // plain InvalidOperationException is caught — endpoints map the
        // typed one to 404 and untyped IOEx to 400.
        await act.Should().ThrowAsync<StudioMemberNotFoundException>()
            .WithMessage("*not found in scope*");
    }

    [Fact]
    public async Task BindAsync_ShouldPropagateActorOwnedValidationFailure()
    {
        var commandPort = new RecordingCommandPort
        {
            RequestException = new InvalidOperationException("workflow yamls are required for workflow members."),
        };
        var service = NewService(
            commandPort,
            new ThrowingQueryPort());

        var act = () => service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*workflow yamls are required*");
    }

    [Fact]
    public async Task GetBindingAsync_ShouldReturnLastRecordedBinding()
    {
        var detail = NewDetail(MemberImplementationKindNames.Workflow);
        var withBinding = detail with
        {
            LastBinding = new StudioMemberBindingContractResponse(
                PublishedServiceId: PublishedServiceId,
                RevisionId: "rev-9",
                ImplementationKind: MemberImplementationKindNames.Workflow,
                BoundAt: DateTimeOffset.UtcNow),
        };

        var service = NewService(
            new RecordingCommandPort(),
            new InMemoryQueryPort(withBinding));

        var binding = await service.GetBindingAsync(ScopeId, MemberId);

        binding.LastBinding.Should().NotBeNull();
        binding.LastBinding!.PublishedServiceId.Should().Be(PublishedServiceId);
        binding.LastBinding.RevisionId.Should().Be("rev-9");
    }

    // Bind / GetBinding don't touch the lifecycle/command ports. We pass
    // throwing stubs so any future regression that routes bind through
    // platform service ports fails loudly instead of silently green.
    private static StudioMemberService NewService(
        IStudioMemberCommandPort memberCommandPort,
        IStudioMemberQueryPort memberQueryPort) =>
        new(
            memberCommandPort,
            memberQueryPort,
            new ThrowingServiceLifecycleQueryPort(),
            new ThrowingServiceCommandPort());

    private static StudioMemberDetailResponse NewDetail(string implementationKindWire)
    {
        var summary = new StudioMemberSummaryResponse(
            MemberId: MemberId,
            ScopeId: ScopeId,
            DisplayName: "Test Member",
            Description: string.Empty,
            ImplementationKind: implementationKindWire,
            LifecycleStage: MemberLifecycleStageNames.BuildReady,
            PublishedServiceId: PublishedServiceId,
            LastBoundRevisionId: null,
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt: DateTimeOffset.UtcNow.AddHours(-1));

        return new StudioMemberDetailResponse(
            Summary: summary,
            ImplementationRef: null,
            LastBinding: null);
    }

    private sealed class InMemoryQueryPort : IStudioMemberQueryPort
    {
        private readonly StudioMemberDetailResponse? _detail;

        public InMemoryQueryPort(StudioMemberDetailResponse? detail)
        {
            _detail = detail;
        }

        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default)
        {
            return Task.FromResult(new StudioMemberRosterResponse(
                ScopeId: scopeId,
                Members: _detail == null ? [] : [_detail.Summary]));
        }

        public Task<StudioMemberDetailResponse?> GetAsync(
            string scopeId, string memberId, CancellationToken ct = default)
        {
            return Task.FromResult(_detail);
        }
    }

    private sealed class ThrowingQueryPort : IStudioMemberQueryPort
    {
        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("bind command path must not read StudioMember read models.");

        public Task<StudioMemberDetailResponse?> GetAsync(
            string scopeId, string memberId, CancellationToken ct = default) =>
            throw new InvalidOperationException("bind command path must not read StudioMember read models.");
    }

    private sealed class RecordingCommandPort : IStudioMemberCommandPort
    {
        public List<RecordedBinding> RecordedBindings { get; } = [];

        public List<StudioMemberImplementationRefResponse> RecordedImplementationUpdates { get; } = [];

        public List<string> OperationsInOrder { get; } = [];

        public List<RequestedBinding> RequestedBindings { get; } = [];

        public string AcceptedBindingId { get; } = "bind-test";

        public Exception? RequestException { get; set; }

        public Task<StudioMemberSummaryResponse> CreateAsync(
            string scopeId, CreateStudioMemberRequest request, CancellationToken ct = default)
        {
            throw new NotImplementedException("Not exercised in this test.");
        }

        public Task<StudioMemberBindingAcceptedResponse> RequestBindingAsync(
            string scopeId,
            string memberId,
            UpdateStudioMemberBindingRequest request,
            CancellationToken ct = default)
        {
            if (RequestException != null)
                throw RequestException;

            RequestedBindings.Add(new RequestedBinding(scopeId, memberId, request));
            OperationsInOrder.Add("RequestBinding");
            return Task.FromResult(new StudioMemberBindingAcceptedResponse(
                scopeId,
                memberId,
                AcceptedBindingId,
                StudioMemberBindingStatusNames.Accepted,
                DateTimeOffset.UtcNow));
        }

        public Task UpdateImplementationAsync(
            string scopeId,
            string memberId,
            StudioMemberImplementationRefResponse implementation,
            CancellationToken ct = default)
        {
            RecordedImplementationUpdates.Add(implementation);
            OperationsInOrder.Add("UpdateImplementation");
            return Task.CompletedTask;
        }

        public Task RecordBindingAsync(
            string scopeId,
            string memberId,
            string publishedServiceId,
            string revisionId,
            string implementationKindName,
            CancellationToken ct = default)
        {
            RecordedBindings.Add(new RecordedBinding(
                scopeId, memberId, publishedServiceId, revisionId, implementationKindName));
            OperationsInOrder.Add("RecordBinding");
            return Task.CompletedTask;
        }

        public Task CompleteBindingAsync(
            string scopeId,
            string memberId,
            StudioMemberBindingCompletionRequest request,
            CancellationToken ct = default) =>
            throw new NotImplementedException("Not exercised in this test.");

        public Task FailBindingAsync(
            string scopeId,
            string memberId,
            StudioMemberBindingFailureRequest request,
            CancellationToken ct = default) =>
            throw new NotImplementedException("Not exercised in this test.");

        public sealed record RecordedBinding(
            string ScopeId,
            string MemberId,
            string PublishedServiceId,
            string RevisionId,
            string ImplementationKindName);

        public sealed record RequestedBinding(
            string ScopeId,
            string MemberId,
            UpdateStudioMemberBindingRequest Request);
    }

    private sealed class ThrowingServiceLifecycleQueryPort : IServiceLifecycleQueryPort
    {
        public Task<ServiceCatalogSnapshot?> GetServiceAsync(
            ServiceIdentity identity, CancellationToken ct = default) =>
            throw new InvalidOperationException("bind orchestration must not query the platform lifecycle port.");

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(
            string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default) =>
            throw new InvalidOperationException("bind orchestration must not list services on the platform lifecycle port.");

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(
            ServiceIdentity identity, CancellationToken ct = default) =>
            throw new InvalidOperationException("bind orchestration must not read revisions through the platform lifecycle port.");

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(
            ServiceIdentity identity, CancellationToken ct = default) =>
            throw new InvalidOperationException("bind orchestration must not read deployments through the platform lifecycle port.");
    }

    private sealed class ThrowingServiceCommandPort : IServiceCommandPort
    {
        private static InvalidOperationException Reject(string method) =>
            new($"bind orchestration must not call IServiceCommandPort.{method} — that surface belongs to revision lifecycle, not bind.");

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
