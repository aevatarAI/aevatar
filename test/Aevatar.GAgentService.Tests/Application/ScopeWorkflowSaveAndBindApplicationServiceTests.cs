using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.Workflows;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ScopeWorkflowSaveAndBindApplicationServiceTests
{
    [Fact]
    public async Task SaveAndBindAsync_ShouldGenerateOneRevisionId_ForWorkflowAndBinding()
    {
        var workflowPort = new RecordingScopeWorkflowCommandPort();
        var bindingPort = new RecordingScopeBindingCommandPort();
        var service = new ScopeWorkflowSaveAndBindApplicationService(workflowPort, bindingPort);

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
        bindingPort.Request.ServiceId.Should().BeNull();
        bindingPort.Request.AppId.Should().Be("studio");
        bindingPort.Request.ExposureDesired.Should().BeTrue();
    }

    [Fact]
    public async Task SaveAndBindAsync_ShouldGenerateWorkflowId_WhenMissing()
    {
        var workflowPort = new RecordingScopeWorkflowCommandPort();
        var bindingPort = new RecordingScopeBindingCommandPort();
        var service = new ScopeWorkflowSaveAndBindApplicationService(workflowPort, bindingPort);

        var result = await service.SaveAndBindAsync(new ScopeWorkflowSaveAndBindRequest(
            "scope-a",
            null,
            "name: main\nsteps: []\n"));

        result.WorkflowId.Should().StartWith("wf-");
        workflowPort.Request!.WorkflowId.Should().Be(result.WorkflowId);
        bindingPort.Request!.Workflow!.WorkflowId.Should().Be(result.WorkflowId);
    }

    [Fact]
    public async Task SaveAndBindAsync_ShouldRejectRevisionMismatch()
    {
        var workflowPort = new RecordingScopeWorkflowCommandPort();
        var bindingPort = new RecordingScopeBindingCommandPort("rev-other");
        var service = new ScopeWorkflowSaveAndBindApplicationService(workflowPort, bindingPort);

        var act = () => service.SaveAndBindAsync(new ScopeWorkflowSaveAndBindRequest(
            "scope-a",
            "wf-alpha",
            "name: main\nsteps: []\n"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*revision identity must match*");
    }

    private sealed class RecordingScopeWorkflowCommandPort : IScopeWorkflowCommandPort
    {
        public ScopeWorkflowUpsertRequest? Request { get; private set; }

        public Task<ScopeWorkflowUpsertResult> UpsertAsync(
            ScopeWorkflowUpsertRequest request,
            CancellationToken ct = default)
        {
            Request = request;
            var revisionId = request.RevisionId ?? string.Empty;
            return Task.FromResult(new ScopeWorkflowUpsertResult(
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
                WorkflowName: request.WorkflowName ?? string.Empty));
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
