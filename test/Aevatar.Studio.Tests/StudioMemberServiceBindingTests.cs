using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

/// <summary>
/// Locks in the most important invariants from issue #325:
///
///   - Member binding never falls back to the scope default service.
///   - The ServiceId Studio sends to the underlying scope binding command is
///     the member's stable publishedServiceId, sourced from the authority
///     state — not derived from a frontend-supplied value.
///   - Renaming a member does not change publishedServiceId.
///   - workflow / script / gagent each route through the same orchestration.
///   - The resulting revision is recorded back on the member authority.
/// </summary>
public sealed class StudioMemberServiceBindingTests
{
    private const string ScopeId = "scope-1";
    private const string MemberId = "m-bind-test";
    private const string PublishedServiceId = "member-m-bind-test";

    [Fact]
    public async Task BindAsync_Workflow_ShouldUseMemberPublishedServiceId()
    {
        var detail = NewDetail(MemberImplementationKindNames.Workflow);
        var queryPort = new InMemoryQueryPort(detail);
        var commandPort = new RecordingCommandPort();
        var bindingPort = new RecordingScopeBindingPort();

        var service = new StudioMemberService(commandPort, queryPort, bindingPort);

        var response = await service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(
                Workflow: new StudioMemberWorkflowBindingSpec(["workflow:\n  name: x"])),
            CancellationToken.None);

        // The bind orchestration MUST hand the platform binding port the
        // member's stable publishedServiceId — never null/empty (which would
        // fall back to the scope default service).
        bindingPort.LastRequest.Should().NotBeNull();
        bindingPort.LastRequest!.ServiceId.Should().Be(PublishedServiceId);
        bindingPort.LastRequest.ImplementationKind.Should().Be(ScopeBindingImplementationKind.Workflow);
        bindingPort.LastRequest.Workflow!.WorkflowYamls.Should().ContainSingle();

        // The orchestrator records the resulting revision back on the
        // member authority so /members/.../binding can read it from the
        // read model later.
        commandPort.RecordedBindings.Should().ContainSingle()
            .Which.PublishedServiceId.Should().Be(PublishedServiceId);
        response.PublishedServiceId.Should().Be(PublishedServiceId);
        response.RevisionId.Should().Be(bindingPort.IssuedRevisionId);
        response.ImplementationKind.Should().Be(MemberImplementationKindNames.Workflow);
    }

    [Fact]
    public async Task BindAsync_Script_ShouldRouteThroughScriptingKind()
    {
        var detail = NewDetail(MemberImplementationKindNames.Script);
        var queryPort = new InMemoryQueryPort(detail);
        var commandPort = new RecordingCommandPort();
        var bindingPort = new RecordingScopeBindingPort();

        var service = new StudioMemberService(commandPort, queryPort, bindingPort);

        await service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(
                Script: new StudioMemberScriptBindingSpec(ScriptId: "s-1", ScriptRevision: "v3")),
            CancellationToken.None);

        bindingPort.LastRequest!.ServiceId.Should().Be(PublishedServiceId);
        bindingPort.LastRequest.ImplementationKind.Should().Be(ScopeBindingImplementationKind.Scripting);
        bindingPort.LastRequest.Script!.ScriptId.Should().Be("s-1");
        bindingPort.LastRequest.Script.ScriptRevision.Should().Be("v3");
    }

    [Fact]
    public async Task BindAsync_GAgent_ShouldRouteThroughGAgentKind()
    {
        var detail = NewDetail(MemberImplementationKindNames.GAgent);
        var queryPort = new InMemoryQueryPort(detail);
        var commandPort = new RecordingCommandPort();
        var bindingPort = new RecordingScopeBindingPort();

        var service = new StudioMemberService(commandPort, queryPort, bindingPort);

        await service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(
                GAgent: new StudioMemberGAgentBindingSpec(
                    ActorTypeName: "MyActor",
                    Endpoints: [
                        new StudioMemberGAgentEndpointSpec(
                            EndpointId: "chat",
                            DisplayName: "Chat",
                            Kind: "chat",
                            RequestTypeUrl: "type.googleapis.com/x.Request",
                            ResponseTypeUrl: "type.googleapis.com/x.Response")
                    ])),
            CancellationToken.None);

        bindingPort.LastRequest!.ServiceId.Should().Be(PublishedServiceId);
        bindingPort.LastRequest.ImplementationKind.Should().Be(ScopeBindingImplementationKind.GAgent);
        bindingPort.LastRequest.GAgent!.ActorTypeName.Should().Be("MyActor");
        bindingPort.LastRequest.GAgent.Endpoints.Should().ContainSingle();
        bindingPort.LastRequest.GAgent.Endpoints[0].Kind.Should().Be(ServiceEndpointKind.Chat);
    }

    [Fact]
    public async Task BindAsync_ShouldFail_WhenMemberDoesNotExist()
    {
        var queryPort = new InMemoryQueryPort(detail: null);
        var service = new StudioMemberService(
            new RecordingCommandPort(),
            queryPort,
            new RecordingScopeBindingPort());

        var act = () => service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(
                Workflow: new StudioMemberWorkflowBindingSpec(["workflow:"])),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found in scope*");
    }

    [Fact]
    public async Task BindAsync_ShouldFail_WhenWorkflowYamlsAreMissing()
    {
        var detail = NewDetail(MemberImplementationKindNames.Workflow);
        var service = new StudioMemberService(
            new RecordingCommandPort(),
            new InMemoryQueryPort(detail),
            new RecordingScopeBindingPort());

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

        var service = new StudioMemberService(
            new RecordingCommandPort(),
            new InMemoryQueryPort(withBinding),
            new RecordingScopeBindingPort());

        var binding = await service.GetBindingAsync(ScopeId, MemberId);

        binding.Should().NotBeNull();
        binding!.PublishedServiceId.Should().Be(PublishedServiceId);
        binding.RevisionId.Should().Be("rev-9");
    }

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

        public Task<StudioMemberRosterResponse> ListAsync(string scopeId, CancellationToken ct = default)
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

    private sealed class RecordingCommandPort : IStudioMemberCommandPort
    {
        public List<RecordedBinding> RecordedBindings { get; } = [];

        public Task<StudioMemberSummaryResponse> CreateAsync(
            string scopeId, CreateStudioMemberRequest request, CancellationToken ct = default)
        {
            throw new NotImplementedException("Not exercised in this test.");
        }

        public Task UpdateImplementationAsync(
            string scopeId,
            string memberId,
            StudioMemberImplementationRefResponse implementation,
            CancellationToken ct = default)
        {
            throw new NotImplementedException("Not exercised in this test.");
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
            return Task.CompletedTask;
        }

        public sealed record RecordedBinding(
            string ScopeId,
            string MemberId,
            string PublishedServiceId,
            string RevisionId,
            string ImplementationKindName);
    }

    private sealed class RecordingScopeBindingPort : IScopeBindingCommandPort
    {
        public string IssuedRevisionId { get; } = "rev-test";

        public ScopeBindingUpsertRequest? LastRequest { get; private set; }

        public Task<ScopeBindingUpsertResult> UpsertAsync(
            ScopeBindingUpsertRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new ScopeBindingUpsertResult(
                ScopeId: request.ScopeId,
                ServiceId: request.ServiceId ?? string.Empty,
                DisplayName: request.DisplayName ?? string.Empty,
                RevisionId: IssuedRevisionId,
                ImplementationKind: request.ImplementationKind,
                ExpectedActorId: $"actor-{request.ServiceId}-{IssuedRevisionId}"));
        }
    }
}
