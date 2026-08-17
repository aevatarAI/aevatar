using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

/// <summary>
/// Locks in the most important invariants from issue #325:
///
///   - Bind admission is no longer read-model gated.
///   - Bind returns an honest async accepted receipt.
///   - workflow / script / gagent requests keep their typed payload shape.
/// </summary>
public sealed class StudioMemberServiceBindingTests
{
    private const string ScopeId = "scope-1";
    private const string MemberId = "m-bind-test";
    private const string PublishedServiceId = "member-m-bind-test";

    [Fact]
    public async Task BindAsync_Workflow_WithMatchingExplicitConfirmation_ShouldPersistOnlyAdmissionGrant()
    {
        var commandPort = new RecordingCommandPort();
        var admission = StudioExplicitRequestAdmissionTestKit.CreateAdmissionService();
        var service = NewService(
            commandPort,
            new ThrowingBindQueryPort(),
            capabilityAdmissionService: admission);

        await service.BindAsync(
            "scope-studio-alpha",
            "m-alpha",
            new UpdateStudioMemberBindingRequest(
                RevisionId: "rev-alpha",
                Workflow: new StudioMemberWorkflowBindingSpec(
                    "wf-alpha",
                    [StudioExplicitRequestAdmissionTestKit.WorkflowYaml]))
            {
                CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                    [StudioExplicitRequestAdmissionTestKit.MatchingConfirmation(
                        "wf-alpha",
                        "rev-alpha")]),
            });

        var admissionRequest = admission.Requests.Should().ContainSingle().Which;
        admissionRequest.Access.NyxIdCallerCredential?.SourceReadableUserBearerToken.Should()
            .Be(StudioExplicitRequestAdmissionTestKit.CallerBearer);
        admissionRequest.Access.NyxIdOrganizationBearerToken.Should()
            .Be(StudioExplicitRequestAdmissionTestKit.OrganizationBearer);
        admissionRequest.ExplicitRequestConfirmations.Should().ContainSingle();
        var started = commandPort.StartedRuns.Should().ContainSingle().Which;
        started.ScopeId.Should().Be("scope-studio-alpha");
        started.MemberId.Should().Be("m-alpha");
        started.Binding.RevisionId.Should().Be("rev-alpha");
        started.Binding.Workflow!.WorkflowId.Should().Be("wf-alpha");
        started.Binding.CapabilityAdmission.Should().BeNull();
        started.Binding.Workflow.CapabilityAdmissionPlan!.InvocationAdmissions
            .Should().ContainSingle().Which.NyxIdExplicitRequestGrant.GrantorOwnerSubject.Should()
            .Be(StudioExplicitRequestAdmissionTestKit.CallerId);
        started.Binding.Workflow.CapabilityAdmissionPlan.ToString().Should()
            .NotContain(StudioExplicitRequestAdmissionTestKit.CallerBearer);
        started.Binding.Workflow.CapabilityAdmissionPlan.ToString().Should()
            .NotContain(StudioExplicitRequestAdmissionTestKit.OrganizationBearer);
    }

    [Fact]
    public async Task BindAsync_Workflow_ForNewRevision_ShouldReadmitCurrentAuthenticatedRequest()
    {
        var commandPort = new RecordingCommandPort();
        var admission = StudioExplicitRequestAdmissionTestKit.CreateAdmissionService();
        var service = NewService(
            commandPort,
            new ThrowingBindQueryPort(),
            capabilityAdmissionService: admission);

        foreach (var revisionId in new[] { "rev-alpha", "rev-beta" })
        {
            await service.BindAsync(
                "scope-studio-alpha",
                "m-alpha",
                new UpdateStudioMemberBindingRequest(
                    RevisionId: revisionId,
                    Workflow: new StudioMemberWorkflowBindingSpec(
                        "wf-alpha",
                        [StudioExplicitRequestAdmissionTestKit.WorkflowYaml]))
                {
                    CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                        [StudioExplicitRequestAdmissionTestKit.MatchingConfirmation(
                            "wf-alpha",
                            revisionId)]),
                });
        }

        admission.Requests.Should().HaveCount(2);
        admission.PersistedRequests.Should().BeEmpty();
        admission.Requests.Should().OnlyContain(request =>
            request.Access.CallerId == StudioExplicitRequestAdmissionTestKit.CallerId &&
            request.ExecutionMode == ExternalCapabilityExecutionMode.Interactive &&
            request.WorkflowYamls != null &&
            request.WorkflowYamls.SequenceEqual(new[] { StudioExplicitRequestAdmissionTestKit.WorkflowYaml }));
        commandPort.StartedRuns.Select(static run => run.Binding.RevisionId)
            .Should().Equal("rev-alpha", "rev-beta");
        commandPort.StartedRuns.Should().OnlyContain(run =>
            run.Binding.Workflow!.CapabilityAdmissionPlan!.InvocationAdmissions
                .Single().NyxIdExplicitRequestGrant.GrantorOwnerSubject ==
            StudioExplicitRequestAdmissionTestKit.CallerId);
    }

    [Theory]
    [InlineData("missing", "NYXID_EXPLICIT_REQUEST_GRANT_REQUIRED")]
    [InlineData("unknown", "NYXID_EXPLICIT_REQUEST_CONFIRMATION_CALL_SITE_MISMATCH")]
    [InlineData("duplicate", "NYXID_EXPLICIT_REQUEST_CONFIRMATION_CALL_SITE_MISMATCH")]
    [InlineData("stale_digest", "NYXID_EXPLICIT_REQUEST_CONFIRMATION_DIGEST_MISMATCH")]
    [InlineData("stale_risk", "NYXID_EXPLICIT_REQUEST_CONFIRMATION_RISK_MISMATCH")]
    public async Task BindAsync_Workflow_WithInvalidExplicitConfirmation_ShouldDispatchNoCommand(
        string scenario,
        string expectedCode)
    {
        var commandPort = new RecordingCommandPort();
        var service = NewService(
            commandPort,
            new ThrowingBindQueryPort(),
            capabilityAdmissionService: StudioExplicitRequestAdmissionTestKit.CreateAdmissionService());

        var action = () => service.BindAsync(
            "scope-studio-alpha",
            "m-alpha",
            new UpdateStudioMemberBindingRequest(
                RevisionId: "rev-alpha",
                Workflow: new StudioMemberWorkflowBindingSpec(
                    "wf-alpha",
                    [StudioExplicitRequestAdmissionTestKit.WorkflowYaml]))
            {
                CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                    StudioExplicitRequestAdmissionTestKit.Confirmations(
                        scenario,
                        "wf-alpha",
                        "rev-alpha")),
            });

        var exception = await action.Should()
            .ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle()
            .Which.Code.Should().Be(expectedCode);
        commandPort.StartedRuns.Should().BeEmpty();
        commandPort.OperationsInOrder.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BindAsync_Workflow_WithExistingPlan_ShouldUseCredentialAwareAdmissionPath(
        bool includeCallerCredential)
    {
        var admission = StudioExplicitRequestAdmissionTestKit.CreateAdmissionService();
        var plan = await admission.AdmitAsync(new WorkflowExternalCapabilityAdmissionRequest(
            new ExternalWorkflowCapabilityAccessContext(
                "scope-studio-alpha",
                StudioExplicitRequestAdmissionTestKit.CallerId,
                NyxIdCallerCredentialSelection.SourceReadableUserBearer(
                    StudioExplicitRequestAdmissionTestKit.CallerBearer),
                StudioExplicitRequestAdmissionTestKit.OrganizationBearer),
            StudioExplicitRequestAdmissionTestKit.WorkflowYaml,
            new Dictionary<string, string>(),
            "test_prepare_plan",
            ExternalCapabilityExecutionMode.Interactive,
            [StudioExplicitRequestAdmissionTestKit.MatchingConfirmation("wf-alpha", "rev-alpha")],
            workflowId: "wf-alpha",
            revisionId: "rev-alpha"));
        admission.Requests.Clear();
        var commandPort = new RecordingCommandPort();
        var service = NewService(
            commandPort,
            new ThrowingBindQueryPort(),
            capabilityAdmissionService: admission);

        await service.BindAsync(
            "scope-studio-alpha",
            "m-alpha",
            new UpdateStudioMemberBindingRequest(
                RevisionId: "rev-alpha",
                Workflow: new StudioMemberWorkflowBindingSpec(
                    "wf-alpha",
                    [StudioExplicitRequestAdmissionTestKit.WorkflowYaml]))
            {
                CapabilityAdmission = StudioExplicitRequestAdmissionTestKit.Context(
                    existingPlan: plan,
                    includeCallerCredential: includeCallerCredential),
            });

        admission.Requests.Should().BeEmpty();
        admission.RefreshRequests.Should().HaveCount(includeCallerCredential ? 1 : 0);
        admission.PersistedRequests.Should().HaveCount(includeCallerCredential ? 0 : 1);
        commandPort.StartedRuns.Should().ContainSingle();
    }

    [Fact]
    public async Task BindAsync_Workflow_ShouldDispatchBindingRunWithoutReadingMember()
    {
        var queryPort = new ThrowingBindQueryPort();
        var commandPort = new RecordingCommandPort();

        var service = NewService(commandPort, queryPort);

        var response = await service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(
                Workflow: new StudioMemberWorkflowBindingSpec(
                    "workflow-stable-id",
                    ["workflow:\n  name: x"])),
            CancellationToken.None);

        response.Status.Should().Be(StudioMemberBindingRunStatusNames.Accepted);
        response.AckStage.Should().Be(StudioMemberBindingAckStageNames.DispatchAccepted);
        response.BindingRunRole.Should().Be(StudioMemberBindingRunRoleNames.Candidate);
        response.BindingRunId.Should().StartWith("bind-");
        response.ScopeId.Should().Be(ScopeId);
        response.MemberId.Should().Be(MemberId);

        var started = commandPort.StartedRuns.Should().ContainSingle().Which;
        started.BindingRunId.Should().Be(response.BindingRunId);
        started.ScopeId.Should().Be(ScopeId);
        started.MemberId.Should().Be(MemberId);
        started.ImplementationKind.Should().Be(MemberImplementationKindNames.Workflow);
        started.Binding.Workflow!.WorkflowId.Should().Be("workflow-stable-id");
        started.Binding.Workflow!.WorkflowYamls.Should().ContainSingle();
        started.Binding.Workflow.CapabilityAdmissionPlan.Should().NotBeNull();
    }

    [Fact]
    public async Task BindAsync_Workflow_WhenCapabilityAdmissionFails_ShouldNotDispatchBindingRun()
    {
        var commandPort = new RecordingCommandPort();
        var admission = new StudioWorkflowCapabilityAdmissionTestService(
            new InvalidOperationException("external capability is not ready"));
        var service = NewService(
            commandPort,
            new ThrowingBindQueryPort(),
            capabilityAdmissionService: admission);

        var act = () => service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(
                Workflow: new StudioMemberWorkflowBindingSpec(
                    "workflow-stable-id",
                    ["name: root-workflow\nsteps: []\n"]))
            {
                CapabilityAdmission = new WorkflowCapabilityAdmissionContext(
                    "caller-alpha",
                    NyxIdCallerCredentialSelection.SourceReadableUserBearer(
                        "runtime-caller-credential")),
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("external capability is not ready");
        var request = admission.Requests.Should().ContainSingle().Which;
        request.WorkflowYamls.Should().Equal("name: root-workflow\nsteps: []\n");
        request.Access.ScopeId.Should().Be(ScopeId);
        request.Access.CallerId.Should().Be("caller-alpha");
        request.Access.NyxIdCallerCredential?.SourceReadableUserBearerToken
            .Should().Be("runtime-caller-credential");
        request.SourceKind.Should().Be("studio_member_binding_run");
        commandPort.StartedRuns.Should().BeEmpty();
    }

    [Fact]
    public async Task BindAsync_Script_ShouldRouteThroughScriptingKind()
    {
        var queryPort = new ThrowingBindQueryPort();
        var commandPort = new RecordingCommandPort();

        var service = NewService(commandPort, queryPort);

        await service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(
                Script: new StudioMemberScriptBindingSpec(ScriptId: "s-1", ScriptRevision: "v3")),
            CancellationToken.None);

        var started = commandPort.StartedRuns.Should().ContainSingle().Which;
        started.ImplementationKind.Should().Be(MemberImplementationKindNames.Script);
        started.Binding.Script!.ScriptId.Should().Be("s-1");
        started.Binding.Script.ScriptRevision.Should().Be("v3");
    }

    [Fact]
    public async Task BindAsync_GAgent_ShouldRouteThroughGAgentKind()
    {
        var queryPort = new ThrowingBindQueryPort();
        var commandPort = new RecordingCommandPort();

        var service = NewService(commandPort, queryPort);

        await service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(
                GAgent: new StudioMemberGAgentBindingSpec(
                    AgentKind: "my.actor",
                    Endpoints: [
                        new StudioMemberGAgentEndpointSpec(
                            EndpointId: "chat",
                            DisplayName: "Chat",
                            Kind: "chat",
                            RequestTypeUrl: "type.googleapis.com/x.Request",
                            ResponseTypeUrl: "type.googleapis.com/x.Response")
                    ])),
            CancellationToken.None);

        var started = commandPort.StartedRuns.Should().ContainSingle().Which;
        started.ImplementationKind.Should().Be(MemberImplementationKindNames.GAgent);
        started.Binding.GAgent!.AgentKind.Should().Be("my.actor");
        started.Binding.GAgent.Endpoints.Should().ContainSingle()
            .Which.Kind.Should().Be("chat");
    }

    [Fact]
    public async Task BindAsync_ShouldAccept_WhenMemberReadModelDoesNotExistYet()
    {
        var commandPort = new RecordingCommandPort();
        var service = NewService(commandPort, new ThrowingBindQueryPort());

        var response = await service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(
                Workflow: new StudioMemberWorkflowBindingSpec(
                    "workflow-stable-id",
                    ["workflow:"])),
            CancellationToken.None);

        response.Status.Should().Be(StudioMemberBindingRunStatusNames.Accepted);
        commandPort.StartedRuns.Should().ContainSingle();
    }

    [Fact]
    public async Task BindAsync_Workflow_ShouldRequireWorkflowId()
    {
        var service = NewService(
            new RecordingCommandPort(),
            new ThrowingBindQueryPort());

        var act = () => service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(
                Workflow: new StudioMemberWorkflowBindingSpec(
                    string.Empty,
                    ["workflow:\n  name: x"])),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*workflowId is required for workflow members*");
    }

    [Fact]
    public async Task BindAsync_ShouldFail_WhenBindingImplementationIsMissing()
    {
        var service = NewService(
            new RecordingCommandPort(),
            new ThrowingBindQueryPort());

        var act = () => service.BindAsync(
            ScopeId,
            MemberId,
            new UpdateStudioMemberBindingRequest(),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exactly one binding implementation is required*");
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

    [Fact]
    public async Task GetBindingAsync_ShouldHydrateCurrentRunFromRunReadModel()
    {
        var detail = NewDetail(MemberImplementationKindNames.Script) with
        {
            CurrentBindingRun = new StudioMemberBindingRunStatusResponse(
                BindingRunId: "bind-1",
                ScopeId: ScopeId,
                MemberId: MemberId,
                Status: StudioMemberBindingRunStatusNames.PlatformBindingPending,
                StateVersion: 1,
                UpdatedAt: DateTimeOffset.UtcNow.AddMinutes(-1)),
        };
        var runQuery = new InMemoryBindingRunQueryPort(new StudioMemberBindingRunStatusResponse(
            BindingRunId: "bind-1",
            ScopeId: ScopeId,
            MemberId: MemberId,
            Status: StudioMemberBindingRunStatusNames.PlatformBindingPending,
            StateVersion: 2,
            UpdatedAt: DateTimeOffset.UtcNow)
        {
            PlatformBindingCommandId = "platform-bind-1",
        });

        var service = NewService(
            new RecordingCommandPort(),
            new InMemoryQueryPort(detail),
            runQuery);

        var binding = await service.GetBindingAsync(ScopeId, MemberId);

        binding.CurrentBindingRun.Should().NotBeNull();
        binding.CurrentBindingRun!.BindingRunId.Should().Be("bind-1");
        binding.CurrentBindingRun.PlatformBindingCommandId.Should().Be("platform-bind-1");
        runQuery.Requests.Should().ContainSingle().Which.Should().Be((ScopeId, MemberId, "bind-1"));
    }

    [Fact]
    public async Task GetAsync_ShouldHydrateCurrentRunFromRunReadModel()
    {
        var detail = NewDetail(MemberImplementationKindNames.Script) with
        {
            CurrentBindingRun = new StudioMemberBindingRunStatusResponse(
                BindingRunId: "bind-1",
                ScopeId: ScopeId,
                MemberId: MemberId,
                Status: StudioMemberBindingRunStatusNames.PlatformBindingPending,
                StateVersion: 1),
        };
        var runQuery = new InMemoryBindingRunQueryPort(new StudioMemberBindingRunStatusResponse(
            BindingRunId: "bind-1",
            ScopeId: ScopeId,
            MemberId: MemberId,
            Status: StudioMemberBindingRunStatusNames.PlatformBindingPending,
            StateVersion: 2)
        {
            PlatformBindingCommandId = "platform-bind-1",
        });

        var service = NewService(
            new RecordingCommandPort(),
            new InMemoryQueryPort(detail),
            runQuery);

        var hydrated = await service.GetAsync(ScopeId, MemberId);

        hydrated.CurrentBindingRun.Should().NotBeNull();
        hydrated.CurrentBindingRun!.PlatformBindingCommandId.Should().Be("platform-bind-1");
        runQuery.Requests.Should().ContainSingle().Which.Should().Be((ScopeId, MemberId, "bind-1"));
    }

    [Fact]
    public async Task GetBindingRunAsync_ShouldReadBindingRunQueryPort()
    {
        var runQuery = new InMemoryBindingRunQueryPort(new StudioMemberBindingRunStatusResponse(
            BindingRunId: "bind-1",
            ScopeId: ScopeId,
            MemberId: MemberId,
            Status: StudioMemberBindingRunStatusNames.PlatformBindingPending,
            StateVersion: 3,
            UpdatedAt: DateTimeOffset.UtcNow)
        {
            PlatformBindingCommandId = "platform-bind-1",
        });
        var service = NewService(
            new RecordingCommandPort(),
            new ThrowingBindQueryPort(),
            runQuery);

        var run = await service.GetBindingRunAsync(ScopeId, MemberId, "bind-1");

        run.BindingRunId.Should().Be("bind-1");
        run.PlatformBindingCommandId.Should().Be("platform-bind-1");
        runQuery.Requests.Should().ContainSingle().Which.Should().Be((ScopeId, MemberId, "bind-1"));
    }

    // Bind / GetBinding don't touch the lifecycle/command ports. We pass
    // throwing stubs so that any future regression which routes a bind
    // through the platform service ports — instead of through the existing
    // IScopeBindingCommandPort — fails loudly here rather than silently
    // green.
    private static StudioMemberService NewService(
        IStudioMemberCommandPort memberCommandPort,
        IStudioMemberQueryPort memberQueryPort,
        IStudioMemberBindingRunQueryPort? bindingRunQueryPort = null,
        IWorkflowExternalCapabilityAdmissionService? capabilityAdmissionService = null) =>
        new(
            memberCommandPort,
            memberQueryPort,
            bindingRunQueryPort ?? new InMemoryBindingRunQueryPort(null),
            new InertTeamQueryPort(),
            new ThrowingServiceLifecycleQueryPort(),
            new ReadyScopeBindingReadinessQueryPort(),
            new ThrowingServiceCommandPort(),
            capabilityAdmissionService ?? new StudioWorkflowCapabilityAdmissionTestService());

    private sealed class InertTeamQueryPort : IStudioTeamQueryPort
    {
        public Task<StudioTeamRosterResponse> ListAsync(
            string scopeId, StudioTeamRosterPageRequest? page = null, CancellationToken ct = default) =>
            Task.FromResult(new StudioTeamRosterResponse(scopeId, []));

        public Task<StudioTeamSummaryResponse?> GetAsync(
            string scopeId, string teamId, CancellationToken ct = default) =>
            Task.FromResult<StudioTeamSummaryResponse?>(null);
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

    private sealed class ThrowingBindQueryPort : IStudioMemberQueryPort
    {
        public Task<StudioMemberRosterResponse> ListAsync(
            string scopeId,
            StudioMemberRosterPageRequest? page = null,
            CancellationToken ct = default)
        {
            throw new InvalidOperationException("BindAsync must not query StudioMember read models.");
        }

        public Task<StudioMemberDetailResponse?> GetAsync(
            string scopeId, string memberId, CancellationToken ct = default)
        {
            throw new InvalidOperationException("BindAsync must not query StudioMember read models.");
        }
    }

    private sealed class InMemoryBindingRunQueryPort : IStudioMemberBindingRunQueryPort
    {
        private readonly StudioMemberBindingRunStatusResponse? _run;

        public InMemoryBindingRunQueryPort(StudioMemberBindingRunStatusResponse? run)
        {
            _run = run;
        }

        public List<(string ScopeId, string MemberId, string BindingRunId)> Requests { get; } = [];

        public Task<StudioMemberBindingRunStatusResponse?> GetAsync(
            string scopeId,
            string memberId,
            string bindingRunId,
            CancellationToken ct = default)
        {
            Requests.Add((scopeId, memberId, bindingRunId));
            return Task.FromResult(_run);
        }
    }

    private sealed class RecordingCommandPort : IStudioMemberCommandPort
    {
        public List<StudioMemberImplementationRefResponse> RecordedImplementationUpdates { get; } = [];

        public List<StudioMemberBindingRunStartRequest> StartedRuns { get; } = [];

        public List<string> OperationsInOrder { get; } = [];

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
            RecordedImplementationUpdates.Add(implementation);
            OperationsInOrder.Add("UpdateImplementation");
            return Task.CompletedTask;
        }

        public Task RecordPublishedBindingAsync(
            string scopeId,
            string memberId,
            StudioMemberPublishedBindingRecordRequest request,
            CancellationToken ct = default)
        {
            OperationsInOrder.Add("RecordPublishedBinding");
            return Task.CompletedTask;
        }

        public Task RenameAsync(
            string scopeId,
            string memberId,
            string displayName,
            CancellationToken ct = default)
        {
            OperationsInOrder.Add("Rename");
            return Task.CompletedTask;
        }

        public Task StartBindingRunAsync(
            StudioMemberBindingRunStartRequest request,
            CancellationToken ct = default)
        {
            StartedRuns.Add(request);
            OperationsInOrder.Add("StartBindingRun");
            return Task.CompletedTask;
        }

        public Task PatchTeamAssignmentAsync(
            string scopeId, string memberId, string? targetTeamId,
            CancellationToken ct = default)
        {
            OperationsInOrder.Add("PatchTeamAssignment");
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            string scopeId,
            string memberId,
            CancellationToken ct = default)
        {
            OperationsInOrder.Add("Delete");
            return Task.CompletedTask;
        }
    }

    private sealed class ReadyScopeBindingReadinessQueryPort : IScopeBindingReadinessQueryPort
    {
        public Task<ScopeBindingReadinessSnapshot> GetReadinessAsync(
            ScopeBindingReadinessRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new ScopeBindingReadinessSnapshot(
                request.ScopeId,
                request.ServiceId,
                ScopeBindingReadinessStatus.Ready,
                ServiceCatalogVisible: true,
                ServingSetVisible: true,
                EligibleServingTargetVisible: true,
                InvokeReady: true,
                RevisionId: request.ExpectedRevisionId,
                DeploymentId: "dep-1",
                ObservedAtUtc: DateTimeOffset.UtcNow));
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
