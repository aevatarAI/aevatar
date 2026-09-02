using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ScopeWorkflowSaveAndBindApplicationServiceTests
{
    [Fact]
    public async Task SaveAndBindAsync_ShouldGenerateOneRevisionId_ForWorkflowAndBinding()
    {
        var workflowPort = new RecordingScopeWorkflowCommandPort();
        var bindingPort = new RecordingScopeBindingCommandPort();
        var admission = new RecordingAdmissionService();
        var service = new ScopeWorkflowSaveAndBindApplicationService(workflowPort, bindingPort, admission);

        var result = await service.SaveAndBindAsync(new ScopeWorkflowSaveAndBindRequest(
            "scope-a",
            "wf-alpha",
            "name: main\nsteps: []\n",
            WorkflowName: "main",
            DisplayName: "Alpha",
            InlineWorkflowYamls: new Dictionary<string, string>
            {
                ["child"] = "name: child\nsteps: []\n",
            },
            AppId: "studio",
            ExposureDesired: true));

        workflowPort.Request.Should().NotBeNull();
        bindingPort.Request.Should().NotBeNull();
        result.WorkflowId.Should().Be("wf-alpha");
        result.RevisionId.Should().StartWith("rev-");
        workflowPort.Request!.RevisionId.Should().Be(result.RevisionId);
        bindingPort.Request!.RevisionId.Should().Be(result.RevisionId);
        bindingPort.Request.Workflow!.WorkflowId.Should().Be("wf-alpha");
        bindingPort.Request.Workflow.WorkflowYamls.Should().Equal(
            "name: main\nsteps: []",
            "name: child\nsteps: []");
        bindingPort.Request.ServiceId.Should().Be("wf-alpha");
        bindingPort.Request.AppId.Should().Be("studio");
        bindingPort.Request.ExposureDesired.Should().BeTrue();
        admission.Requests.Should().ContainSingle();
        workflowPort.Request.CapabilityAdmission!.ExistingPlan!.AdmissionDigest.Should()
            .Be(admission.Plan.AdmissionDigest);
        bindingPort.Request.CapabilityAdmission!.ExistingPlan!.AdmissionDigest.Should()
            .Be(admission.Plan.AdmissionDigest);
    }

    [Fact]
    public async Task SaveAndBindAsync_ShouldGenerateWorkflowId_WhenMissing()
    {
        var workflowPort = new RecordingScopeWorkflowCommandPort();
        var bindingPort = new RecordingScopeBindingCommandPort();
        var service = new ScopeWorkflowSaveAndBindApplicationService(
            workflowPort,
            bindingPort,
            new RecordingAdmissionService());

        var result = await service.SaveAndBindAsync(new ScopeWorkflowSaveAndBindRequest(
            "scope-a",
            null,
            "name: main\nsteps: []\n"));

        result.WorkflowId.Should().StartWith("wf-");
        workflowPort.Request!.WorkflowId.Should().Be(result.WorkflowId);
        bindingPort.Request!.Workflow!.WorkflowId.Should().Be(result.WorkflowId);
    }

    [Fact]
    public async Task SaveAndBindAsync_WhenServiceIdIsExplicit_ShouldUseRequestedBindingServiceId()
    {
        var workflowPort = new RecordingScopeWorkflowCommandPort();
        var bindingPort = new RecordingScopeBindingCommandPort();
        var service = new ScopeWorkflowSaveAndBindApplicationService(
            workflowPort,
            bindingPort,
            new RecordingAdmissionService());

        await service.SaveAndBindAsync(new ScopeWorkflowSaveAndBindRequest(
            "scope-a",
            "wf-alpha",
            "name: main\nsteps: []\n",
            ServiceId: "svc-explicit"));

        workflowPort.Request!.WorkflowId.Should().Be("wf-alpha");
        bindingPort.Request!.Workflow!.WorkflowId.Should().Be("wf-alpha");
        bindingPort.Request.ServiceId.Should().Be("svc-explicit");
    }

    [Fact]
    public async Task SaveAndBindAsync_ShouldReplayWorkflowRevisionDuringBinding()
    {
        const string revisionId = "rev-explicit";
        var workflowPort = new RecordingScopeWorkflowCommandPort();
        var bindingPort = new RecordingScopeBindingCommandPort();
        var service = new ScopeWorkflowSaveAndBindApplicationService(
            workflowPort,
            bindingPort,
            new RecordingAdmissionService());

        var result = await service.SaveAndBindAsync(new ScopeWorkflowSaveAndBindRequest(
            "scope-a",
            "wf-alpha",
            "name: main\nsteps: []\n",
            RevisionId: revisionId));

        result.RevisionId.Should().Be(revisionId);
        workflowPort.Request!.RevisionId.Should().Be(revisionId);
        bindingPort.Request!.RevisionId.Should().Be(revisionId);
        bindingPort.Request.AllowExistingRevisionReplay.Should().BeTrue();
        bindingPort.Request.ReplayRevisionId.Should().Be(revisionId);
        bindingPort.Request.AcceptedRevisionCreation.Should().Be(
            new ScopeBindingAcceptedRevisionCreation(workflowPort.Result!.ServiceKey, revisionId));
    }

    [Fact]
    public async Task SaveAndBindAsync_ShouldRejectRevisionMismatch()
    {
        var workflowPort = new RecordingScopeWorkflowCommandPort();
        var bindingPort = new RecordingScopeBindingCommandPort("rev-other");
        var service = new ScopeWorkflowSaveAndBindApplicationService(
            workflowPort,
            bindingPort,
            new RecordingAdmissionService());

        var act = () => service.SaveAndBindAsync(new ScopeWorkflowSaveAndBindRequest(
            "scope-a",
            "wf-alpha",
            "name: main\nsteps: []\n"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*revision identity must match*");
    }

    [Fact]
    public async Task SaveAndBindAsync_WhenAdmissionFails_ShouldDispatchNoMutations()
    {
        var workflowPort = new RecordingScopeWorkflowCommandPort();
        var bindingPort = new RecordingScopeBindingCommandPort();
        var service = new ScopeWorkflowSaveAndBindApplicationService(
            workflowPort,
            bindingPort,
            new RecordingAdmissionService(new InvalidOperationException("not ready")));

        var act = () => service.SaveAndBindAsync(new ScopeWorkflowSaveAndBindRequest(
            "scope-a",
            "wf-alpha",
            "name: main\nsteps: []\n"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("not ready");
        workflowPort.Request.Should().BeNull();
        bindingPort.Request.Should().BeNull();
    }

    [Theory]
    [InlineData("missing", "NYXID_EXPLICIT_REQUEST_GRANT_REQUIRED")]
    [InlineData("stale_digest", "NYXID_EXPLICIT_REQUEST_CONFIRMATION_DIGEST_MISMATCH")]
    [InlineData("stale_risk", "NYXID_EXPLICIT_REQUEST_CONFIRMATION_RISK_MISMATCH")]
    [InlineData("unknown_call_site", "NYXID_EXPLICIT_REQUEST_CONFIRMATION_CALL_SITE_MISMATCH")]
    [InlineData("duplicate", "NYXID_EXPLICIT_REQUEST_CONFIRMATION_CALL_SITE_MISMATCH")]
    public async Task SaveAndBindAsync_WhenExplicitRequestConfirmationIsInvalid_ShouldDispatchNoMutation(
        string scenario,
        string expectedBlockerCode)
    {
        var workflowPort = new RecordingScopeWorkflowCommandPort();
        var bindingPort = new RecordingScopeBindingCommandPort();
        var service = new ScopeWorkflowSaveAndBindApplicationService(
            workflowPort,
            bindingPort,
            ScopeExplicitRequestAdmissionTestFixture.CreateAdmissionService());
        var request = new ScopeWorkflowSaveAndBindRequest(
            ScopeExplicitRequestAdmissionTestFixture.ScopeId,
            ScopeExplicitRequestAdmissionTestFixture.WorkflowId,
            ScopeExplicitRequestAdmissionTestFixture.WorkflowYaml,
            ServiceId: ScopeExplicitRequestAdmissionTestFixture.ServiceId,
            RevisionId: ScopeExplicitRequestAdmissionTestFixture.RevisionId)
        {
            CapabilityAdmission = ScopeExplicitRequestAdmissionTestFixture.CreateContext(scenario),
        };

        Func<Task> act = async () => await service.SaveAndBindAsync(request);

        var exception = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        exception.Which.Readiness.Blockers.Should().ContainSingle().Which.Code.Should()
            .Be(expectedBlockerCode);
        workflowPort.Request.Should().BeNull();
        bindingPort.Request.Should().BeNull();
    }

    [Fact]
    public async Task SaveAndBindAsync_WhenExplicitRequestConfirmationMatches_ShouldForwardCallerOwnedPlan()
    {
        var workflowPort = new RecordingScopeWorkflowCommandPort();
        var bindingPort = new RecordingScopeBindingCommandPort();
        var service = new ScopeWorkflowSaveAndBindApplicationService(
            workflowPort,
            bindingPort,
            ScopeExplicitRequestAdmissionTestFixture.CreateAdmissionService());

        var result = await service.SaveAndBindAsync(new ScopeWorkflowSaveAndBindRequest(
            ScopeExplicitRequestAdmissionTestFixture.ScopeId,
            ScopeExplicitRequestAdmissionTestFixture.WorkflowId,
            ScopeExplicitRequestAdmissionTestFixture.WorkflowYaml,
            ServiceId: ScopeExplicitRequestAdmissionTestFixture.ServiceId,
            RevisionId: ScopeExplicitRequestAdmissionTestFixture.RevisionId)
        {
            CapabilityAdmission = ScopeExplicitRequestAdmissionTestFixture.CreateContext("matching"),
        });

        result.ScopeId.Should().Be(ScopeExplicitRequestAdmissionTestFixture.ScopeId);
        result.WorkflowId.Should().Be(ScopeExplicitRequestAdmissionTestFixture.WorkflowId);
        result.RevisionId.Should().NotBe(ScopeExplicitRequestAdmissionTestFixture.ScopeId);
        result.RevisionId.Should().NotBe(ScopeExplicitRequestAdmissionTestFixture.WorkflowId);
        result.RevisionId.Should().NotBe(ScopeExplicitRequestAdmissionTestFixture.ServiceId);
        result.RevisionId.Should().NotBe(ScopeExplicitRequestAdmissionTestFixture.CallerId);
        bindingPort.Request!.ServiceId.Should().Be(ScopeExplicitRequestAdmissionTestFixture.ServiceId);
        ScopeExplicitRequestAdmissionTestFixture.AssertCallerOwnedGrant(
            workflowPort.Request!.CapabilityAdmission!.ExistingPlan);
        ScopeExplicitRequestAdmissionTestFixture.AssertCallerOwnedGrant(
            bindingPort.Request.CapabilityAdmission!.ExistingPlan);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveAndBindAsync_WithExistingPlan_ShouldUseCredentialAwareAdmissionPathAndDispatch(
        bool includeCallerCredential)
    {
        var existingPlan = await ScopeExplicitRequestAdmissionTestFixture.CreatePersistedPlanAsync(
            "scope_workflow_save_and_bind");
        var admission = new ScopeExplicitRequestAdmissionTestFixture.DelegatingAdmissionService(
            ScopeExplicitRequestAdmissionTestFixture.CreateAdmissionService());
        var workflowPort = new RecordingScopeWorkflowCommandPort();
        var bindingPort = new RecordingScopeBindingCommandPort();
        var service = new ScopeWorkflowSaveAndBindApplicationService(workflowPort, bindingPort, admission);

        var result = await service.SaveAndBindAsync(new ScopeWorkflowSaveAndBindRequest(
            ScopeExplicitRequestAdmissionTestFixture.ScopeId,
            ScopeExplicitRequestAdmissionTestFixture.WorkflowId,
            ScopeExplicitRequestAdmissionTestFixture.WorkflowYaml,
            ServiceId: ScopeExplicitRequestAdmissionTestFixture.ServiceId,
            RevisionId: ScopeExplicitRequestAdmissionTestFixture.RevisionId)
        {
            CapabilityAdmission = ScopeExplicitRequestAdmissionTestFixture.CreatePersistedContext(
                existingPlan,
                includeCallerCredential),
        });

        result.ScopeId.Should().Be(ScopeExplicitRequestAdmissionTestFixture.ScopeId);
        admission.RefreshPersistedCallCount.Should().Be(includeCallerCredential ? 1 : 0);
        admission.RevalidatePersistedCallCount.Should().Be(includeCallerCredential ? 0 : 1);
        admission.AdmitCallCount.Should().Be(0);
        workflowPort.Request.Should().NotBeNull();
        bindingPort.Request.Should().NotBeNull();
    }

    private sealed class RecordingAdmissionService : IWorkflowExternalCapabilityAdmissionService
    {
        private readonly Exception? _exception;

        public RecordingAdmissionService(Exception? exception = null)
        {
            _exception = exception;
            Plan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
                "name: main\nsteps: []",
                new Dictionary<string, string> { ["child"] = "name: child\nsteps: []" },
                ExternalCapabilityExecutionMode.Interactive,
                [],
                []);
        }

        public WorkflowCapabilityAdmissionPlan Plan { get; }

        public List<WorkflowExternalCapabilityAdmissionRequest> Requests { get; } = [];

        public List<PersistedWorkflowCapabilityAdmissionRequest> PersistedRequests { get; } = [];

        public List<RefreshPersistedWorkflowCapabilityAdmissionRequest> RefreshRequests { get; } = [];

        public Task<WorkflowCapabilityAdmissionPlan> AdmitAsync(
            WorkflowExternalCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return _exception is null
                ? Task.FromResult(Plan.Clone())
                : Task.FromException<WorkflowCapabilityAdmissionPlan>(_exception);
        }

        public Task<WorkflowCapabilityAdmissionPlan> RevalidatePersistedAsync(
            PersistedWorkflowCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            PersistedRequests.Add(request);
            return _exception is null
                ? Task.FromResult(Plan.Clone())
                : Task.FromException<WorkflowCapabilityAdmissionPlan>(_exception);
        }

        public Task<WorkflowCapabilityAdmissionPlan> RefreshPersistedAsync(
            RefreshPersistedWorkflowCapabilityAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            RefreshRequests.Add(request);
            return _exception is null
                ? Task.FromResult(request.Persisted.Plan.Clone())
                : Task.FromException<WorkflowCapabilityAdmissionPlan>(_exception);
        }
    }

    private sealed class RecordingScopeWorkflowCommandPort : IScopeWorkflowCommandPort
    {
        public ScopeWorkflowUpsertRequest? Request { get; private set; }

        public ScopeWorkflowUpsertResult? Result { get; private set; }

        public Task<ScopeWorkflowUpsertResult> UpsertAsync(
            ScopeWorkflowUpsertRequest request,
            CancellationToken ct = default)
        {
            Request = request;
            var revisionId = request.RevisionId ?? string.Empty;
            Result = new ScopeWorkflowUpsertResult(
                request.ScopeId,
                request.WorkflowId,
                $"scope:{request.ScopeId}:workflow:{request.WorkflowId}",
                revisionId,
                $"scope-workflow:{request.ScopeId}:{request.WorkflowId}",
                "actor-expected",
                "deployment-expected",
                DateTimeOffset.UtcNow,
                [],
                $"/api/scopes/{request.ScopeId}/workflows/{request.WorkflowId}",
                DisplayName: request.DisplayName ?? string.Empty,
                WorkflowName: request.WorkflowName ?? string.Empty);
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingScopeBindingCommandPort : IScopeBindingCommandPort
    {
        private readonly string? _resultRevisionId;

        public RecordingScopeBindingCommandPort(string? resultRevisionId = null)
        {
            _resultRevisionId = resultRevisionId;
        }

        public ScopeBindingUpsertRequest? Request { get; private set; }

        public Task<ScopeBindingUpsertResult> UpsertAsync(
            ScopeBindingUpsertRequest request,
            CancellationToken ct = default)
        {
            Request = request;
            var revisionId = _resultRevisionId ?? request.RevisionId ?? string.Empty;
            return Task.FromResult(new ScopeBindingUpsertResult(
                request.ScopeId,
                request.ServiceId ?? "default",
                request.DisplayName ?? "main",
                revisionId,
                request.ImplementationKind,
                "binding-actor-expected"));
        }
    }
}
