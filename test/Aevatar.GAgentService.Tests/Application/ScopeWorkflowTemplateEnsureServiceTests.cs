using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ScopeWorkflowTemplateEnsureServiceTests
{
    [Fact]
    public async Task EnsureAsync_WhenTemplateIsNotConfigured_ShouldNotQueryOrMutate()
    {
        var queryPort = new RecordingScopeWorkflowQueryPort();
        var saveAndBindPort = new RecordingScopeWorkflowSaveAndBindPort();
        var service = CreateService(queryPort, saveAndBindPort);

        var result = await service.EnsureAsync(new ScopeWorkflowTemplateEnsureRequest("scope-1", "wf-other"));

        result.Status.Should().Be(ScopeWorkflowTemplateEnsureStatus.NotConfigured);
        queryPort.Lookups.Should().BeEmpty();
        saveAndBindPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureAsync_WhenWorkflowRevisionIsCurrent_ShouldNotSaveAndBind()
    {
        var queryPort = new RecordingScopeWorkflowQueryPort(
            new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.Runnable,
                BuildWorkflow("scope-1", "wf-default", "rev-expected"),
                "runnable"));
        var saveAndBindPort = new RecordingScopeWorkflowSaveAndBindPort();
        var service = CreateService(queryPort, saveAndBindPort, BuildTemplate());

        var result = await service.EnsureAsync(new ScopeWorkflowTemplateEnsureRequest("scope-1", "wf-default"));

        result.Status.Should().Be(ScopeWorkflowTemplateEnsureStatus.AlreadyCurrent);
        result.RevisionId.Should().Be("rev-expected");
        queryPort.Lookups.Should().ContainSingle().Which.Should().Be(("scope-1", "wf-default"));
        saveAndBindPort.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureAsync_WhenWorkflowIsMissing_ShouldSaveAndBindConfiguredTemplate()
    {
        var queryPort = new RecordingScopeWorkflowQueryPort(
            new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.NotFound,
                null,
                "service_catalog_missing"),
            new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.Runnable,
                BuildWorkflow("scope-1", "wf-default", "rev-expected"),
                "runnable"));
        var saveAndBindPort = new RecordingScopeWorkflowSaveAndBindPort();
        var service = CreateService(queryPort, saveAndBindPort, BuildTemplate());

        var result = await service.EnsureAsync(new ScopeWorkflowTemplateEnsureRequest("scope-1", "wf-default"));

        result.Status.Should().Be(ScopeWorkflowTemplateEnsureStatus.SaveAndBindAccepted);
        result.Reason.Should().Be("workflow_template_missing");
        var request = saveAndBindPort.Requests.Should().ContainSingle().Subject;
        request.ScopeId.Should().Be("scope-1");
        request.WorkflowId.Should().Be("wf-default");
        request.WorkflowYaml.Should().Be("name: wf_default\nsteps: []");
        request.WorkflowName.Should().Be("wf_default");
        request.DisplayName.Should().Be("Workflow Default");
        request.RevisionId.Should().Be("rev-expected");
        request.ServiceId.Should().Be("svc-default");
        request.AppId.Should().Be("app-default");
        request.ExposureDesired.Should().BeTrue();
    }

    [Fact]
    public async Task EnsureAsync_ShouldPassCapabilityAdmissionContextToSaveAndBind()
    {
        var queryPort = new RecordingScopeWorkflowQueryPort(
            new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.NotFound,
                null,
                "service_catalog_missing"),
            new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.Runnable,
                BuildWorkflow("scope-1", "wf-default", "rev-expected"),
                "runnable"));
        var saveAndBindPort = new RecordingScopeWorkflowSaveAndBindPort();
        var service = CreateService(queryPort, saveAndBindPort, BuildTemplate());
        var admission = new WorkflowCapabilityAdmissionContext(
            "caller-1",
            NyxIdCallerCredentialSelection.SourceReadableUserBearer("source-token"));

        await service.EnsureAsync(new ScopeWorkflowTemplateEnsureRequest("scope-1", "wf-default")
        {
            CapabilityAdmission = admission,
        });

        saveAndBindPort.Requests.Should().ContainSingle().Which.CapabilityAdmission.Should().BeSameAs(admission);
    }

    [Fact]
    public async Task EnsureAsync_WhenSaveAndBindReadModelIsNotObserved_ShouldFail()
    {
        var queryPort = new RecordingScopeWorkflowQueryPort(
            new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.NotFound,
                null,
                "service_catalog_missing"));
        var saveAndBindPort = new RecordingScopeWorkflowSaveAndBindPort();
        var service = CreateService(
            queryPort,
            saveAndBindPort,
            options => options.TemplateEnsureProjectionWaitTimeout = TimeSpan.Zero,
            BuildTemplate());

        var result = await service.EnsureAsync(new ScopeWorkflowTemplateEnsureRequest("scope-1", "wf-default"));

        result.Status.Should().Be(ScopeWorkflowTemplateEnsureStatus.Failed);
        result.Reason.Should().Be("workflow_template_readmodel_not_observed");
        result.SaveAndBind.Should().NotBeNull();
        queryPort.Lookups.Should().HaveCount(2);
    }

    [Fact]
    public async Task EnsureAsync_WhenTemplateUsesYamlPath_ShouldSaveAndBindFileContent()
    {
        var workflowYamlPath = Path.Combine(Path.GetTempPath(), $"scope-workflow-template-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(workflowYamlPath, "name: wf_default_from_path\nsteps: []");
        try
        {
            var queryPort = new RecordingScopeWorkflowQueryPort(
                new ScopeWorkflowLookupResult(
                    ScopeWorkflowLookupStatus.NotFound,
                    null,
                    "service_catalog_missing"),
                new ScopeWorkflowLookupResult(
                    ScopeWorkflowLookupStatus.Runnable,
                    BuildWorkflow("scope-1", "wf-default", "rev-expected"),
                    "runnable"));
            var saveAndBindPort = new RecordingScopeWorkflowSaveAndBindPort();
            var service = CreateService(queryPort, saveAndBindPort, BuildTemplateFromPath(workflowYamlPath));

            var result = await service.EnsureAsync(new ScopeWorkflowTemplateEnsureRequest("scope-1", "wf-default"));

            result.Status.Should().Be(ScopeWorkflowTemplateEnsureStatus.SaveAndBindAccepted);
            saveAndBindPort.Requests.Should().ContainSingle().Which.WorkflowYaml.Should()
                .Be("name: wf_default_from_path\nsteps: []");
        }
        finally
        {
            File.Delete(workflowYamlPath);
        }
    }

    [Fact]
    public async Task EnsureAsync_WhenWorkflowRevisionDiffers_ShouldSaveAndBindConfiguredTemplate()
    {
        var queryPort = new RecordingScopeWorkflowQueryPort(
            new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.Runnable,
                BuildWorkflow("scope-1", "wf-default", "rev-old"),
                "runnable"),
            new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.Runnable,
                BuildWorkflow("scope-1", "wf-default", "rev-expected"),
                "runnable"));
        var saveAndBindPort = new RecordingScopeWorkflowSaveAndBindPort();
        var service = CreateService(queryPort, saveAndBindPort, BuildTemplate());

        var result = await service.EnsureAsync(new ScopeWorkflowTemplateEnsureRequest("scope-1", "wf-default"));

        result.Status.Should().Be(ScopeWorkflowTemplateEnsureStatus.SaveAndBindAccepted);
        result.Reason.Should().Be("workflow_template_stale");
        saveAndBindPort.Requests.Should().ContainSingle().Which.RevisionId.Should().Be("rev-expected");
    }

    private static ScopeWorkflowTemplateEnsureService CreateService(
        RecordingScopeWorkflowQueryPort queryPort,
        RecordingScopeWorkflowSaveAndBindPort saveAndBindPort,
        params ScopeWorkflowConfiguredTemplateOptions[] templates) =>
        CreateService(queryPort, saveAndBindPort, null, templates);

    private static ScopeWorkflowTemplateEnsureService CreateService(
        RecordingScopeWorkflowQueryPort queryPort,
        RecordingScopeWorkflowSaveAndBindPort saveAndBindPort,
        Action<ScopeWorkflowCapabilityOptions>? configure,
        params ScopeWorkflowConfiguredTemplateOptions[] templates)
    {
        var options = new ScopeWorkflowCapabilityOptions
        {
            ConfiguredTemplates = templates.ToList(),
        };
        configure?.Invoke(options);
        return new ScopeWorkflowTemplateEnsureService(
            queryPort,
            saveAndBindPort,
            Options.Create(options));
    }

    private static ScopeWorkflowConfiguredTemplateOptions BuildTemplate() =>
        new()
        {
            WorkflowId = "wf-default",
            RevisionId = "rev-expected",
            WorkflowYaml = "name: wf_default\nsteps: []",
            WorkflowName = "wf_default",
            DisplayName = "Workflow Default",
            AppId = "app-default",
            ServiceId = "svc-default",
            ExposureDesired = true,
        };

    private static ScopeWorkflowConfiguredTemplateOptions BuildTemplateFromPath(string workflowYamlPath) =>
        new()
        {
            WorkflowId = "wf-default",
            RevisionId = "rev-expected",
            WorkflowYamlPath = workflowYamlPath,
            WorkflowName = "wf_default",
            DisplayName = "Workflow Default",
            AppId = "app-default",
            ServiceId = "svc-default",
            ExposureDesired = true,
        };

    private static ScopeWorkflowSummary BuildWorkflow(string scopeId, string workflowId, string revisionId) =>
        new(
            scopeId,
            workflowId,
            "Workflow Default",
            $"scope:{scopeId}:workflow:{workflowId}",
            "wf_default",
            $"workflow-definition-actor-{workflowId}",
            revisionId,
            "deployment-1",
            "Active",
            DateTimeOffset.UtcNow);

    private sealed class RecordingScopeWorkflowQueryPort : IScopeWorkflowQueryPort
    {
        private readonly Queue<ScopeWorkflowLookupResult> _lookupResults;
        private ScopeWorkflowLookupResult _lastLookupResult;

        public RecordingScopeWorkflowQueryPort(params ScopeWorkflowLookupResult[] lookupResults)
        {
            _lookupResults = new Queue<ScopeWorkflowLookupResult>(lookupResults);
            _lastLookupResult = lookupResults.LastOrDefault() ?? new ScopeWorkflowLookupResult(
                ScopeWorkflowLookupStatus.NotFound,
                null,
                "service_catalog_missing");
        }

        public List<(string ScopeId, string WorkflowId)> Lookups { get; } = [];

        public Task<IReadOnlyList<ScopeWorkflowSummary>> ListAsync(
            string scopeId,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScopeWorkflowSummary>>([]);

        public Task<ScopeWorkflowLookupResult> LookupByWorkflowIdAsync(
            string scopeId,
            string workflowId,
            CancellationToken ct = default)
        {
            Lookups.Add((scopeId, workflowId));
            if (_lookupResults.Count > 0)
                _lastLookupResult = _lookupResults.Dequeue();

            return Task.FromResult(_lastLookupResult);
        }

        public Task<ScopeWorkflowSummary?> GetByWorkflowIdAsync(
            string scopeId,
            string workflowId,
            CancellationToken ct = default) =>
            Task.FromResult<ScopeWorkflowSummary?>(null);

        public Task<ScopeWorkflowSummary?> GetByActorIdAsync(
            string scopeId,
            string actorId,
            CancellationToken ct = default) =>
            Task.FromResult<ScopeWorkflowSummary?>(null);
    }

    private sealed class RecordingScopeWorkflowSaveAndBindPort : IScopeWorkflowSaveAndBindPort
    {
        public List<ScopeWorkflowSaveAndBindRequest> Requests { get; } = [];

        public Task<ScopeWorkflowSaveAndBindResult> SaveAndBindAsync(
            ScopeWorkflowSaveAndBindRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            var revisionId = request.RevisionId ?? string.Empty;
            var workflow = new ScopeWorkflowUpsertResult(
                request.ScopeId,
                request.WorkflowId ?? string.Empty,
                $"scope:{request.ScopeId}:workflow:{request.WorkflowId}",
                revisionId,
                "scope-workflow",
                "workflow-definition-actor",
                "deployment-expected",
                DateTimeOffset.UtcNow,
                [],
                $"/api/scopes/{request.ScopeId}/workflows/{request.WorkflowId}");
            var binding = new ScopeBindingUpsertResult(
                request.ScopeId,
                request.ServiceId ?? "default",
                request.DisplayName ?? string.Empty,
                revisionId,
                ScopeBindingImplementationKind.Workflow,
                "binding-actor");
            return Task.FromResult(new ScopeWorkflowSaveAndBindResult(
                request.ScopeId,
                request.WorkflowId ?? string.Empty,
                revisionId,
                workflow,
                binding));
        }
    }
}
