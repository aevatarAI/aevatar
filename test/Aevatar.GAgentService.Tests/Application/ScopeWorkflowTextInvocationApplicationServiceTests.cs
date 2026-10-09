using Aevatar.AI.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using CallerCredential = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential;
using CallerAuthority = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerNyxIdAuthority;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class ScopeWorkflowTextInvocationApplicationServiceTests
{
    [Fact]
    public async Task InvokeAsync_UsesExplicitPublishedIdentityAndActualDispatchReceipt()
    {
        var query = new WorkflowQuery();
        var invocation = new Invocation();
        var service = new ScopeWorkflowTextInvocationApplicationService(query, invocation);

        var result = await service.InvokeAsync(
            new("scope-alpha", "wf-alpha", " Keep prompt whitespace ", CommandId: "cmd-alpha", CorrelationId: "corr-alpha"),
            new CallerCredential("execution-token", Kind: NyxIdCallerCredentialKind.ProxyDelegation,
                SourceReadableUserBearerToken: "source-token"));

        query.LastLookup.Should().Be(("scope-alpha", "wf-alpha"));
        var request = invocation.Request!;
        request.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "scope-alpha", AppId = "app-published", Namespace = "namespace-published", ServiceId = "svc-alpha",
        });
        request.EndpointId.Should().Be("chat");
        request.RevisionId.Should().Be("revision-active");
        request.CommandId.Should().Be("cmd-alpha");
        request.CorrelationId.Should().Be("corr-alpha");
        var payload = request.Payload.Unpack<ChatRequestEvent>();
        payload.Prompt.Should().Be(" Keep prompt whitespace ");
        payload.ScopeId.Should().Be("scope-alpha");
        payload.ConnectorHttpAuthorization.Should().Be("Bearer execution-token");
        payload.CallerNyxIdCredentialKind.Should().Be(AgentToolNyxIdCredentialKindPayload.ProxyDelegation);
        payload.CallerSourceReadableNyxIdBearerToken.Should().Be("source-token");
        payload.Metadata.Should().BeEmpty();
        result.Should().BeSameAs(invocation.Receipt);
        result.RunId.Should().Be("run-alpha");
        result.RunId.Should().NotBe(result.CommandId).And.NotBe(request.Identity.ServiceId);
    }

    [Fact]
    public async Task InvokeAsync_LeavesExplicitRevisionAdmissionToOriginalInvocation()
    {
        var resolution = new Resolution();
        var admission = new Admission();
        var dispatcher = new Dispatcher();
        var service = new ScopeWorkflowTextInvocationApplicationService(new WorkflowQuery(),
            new ServiceInvocationApplicationService(resolution, admission, dispatcher));

        var receipt = await service.InvokeAsync(new("scope-alpha", "wf-alpha", "input", "revision-requested"), null);

        resolution.Request!.RevisionId.Should().Be("revision-requested");
        admission.Request.Should().BeSameAs(resolution.Request);
        dispatcher.Request.Should().BeSameAs(resolution.Request);
        admission.Request!.CommandId.Should().NotBeNullOrWhiteSpace();
        receipt.Should().BeSameAs(dispatcher.Receipt);
    }

    [Fact]
    public async Task InvokeAsync_AdmissionFailureDoesNotDispatch()
    {
        var admission = new Admission { Error = new InvalidOperationException("admission rejected") };
        var dispatcher = new Dispatcher();
        var service = new ScopeWorkflowTextInvocationApplicationService(new WorkflowQuery(),
            new ServiceInvocationApplicationService(new Resolution(), admission, dispatcher));

        var act = () => service.InvokeAsync(new("scope-alpha", "wf-alpha", "input"), null);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("admission rejected");
        dispatcher.Request.Should().BeNull();
    }

    [Theory]
    [InlineData(ScopeWorkflowLookupStatus.NotFound, "service_catalog_missing")]
    [InlineData(ScopeWorkflowLookupStatus.NotReady, "serving_readmodel_missing")]
    [InlineData(ScopeWorkflowLookupStatus.Stale, "published_service_descriptor_ambiguous")]
    public async Task InvokeAsync_PreservesLookupFailureWithoutInvoking(ScopeWorkflowLookupStatus status, string reason)
    {
        var lookup = new ScopeWorkflowLookupResult(status, null, reason);
        var query = new WorkflowQuery { Lookup = lookup };
        var invocation = new Invocation();
        var service = new ScopeWorkflowTextInvocationApplicationService(query, invocation);

        var act = () => service.InvokeAsync(new("scope-alpha", "wf-alpha", "input"), null);

        var error = await act.Should().ThrowAsync<ScopeWorkflowInvocationLookupException>();
        error.Which.Lookup.Should().BeSameAs(lookup);
        error.Which.ScopeId.Should().Be("scope-alpha");
        error.Which.WorkflowId.Should().Be("wf-alpha");
        invocation.Request.Should().BeNull();
    }

    [Theory]
    [InlineData("scope-foreign", "wf-alpha", "app-published", "namespace-published", "svc-alpha")]
    [InlineData("scope-alpha", "wf-foreign", "app-published", "namespace-published", "svc-alpha")]
    [InlineData("scope-alpha", "wf-alpha", "", "namespace-published", "svc-alpha")]
    [InlineData("scope-alpha", "wf-alpha", "app-published", "", "svc-alpha")]
    [InlineData("scope-alpha", "wf-alpha", "app-published", "namespace-published", "")]
    public async Task InvokeAsync_RejectsMissingOrMismatchedIdentityInsteadOfGuessing(
        string scopeId, string workflowId, string appId, string serviceNamespace, string serviceId)
    {
        var workflow = CreateWorkflow() with
        {
            ScopeId = scopeId, WorkflowId = workflowId,
            ServiceAppId = appId, ServiceNamespace = serviceNamespace, PublishedServiceId = serviceId,
        };
        var invocation = new Invocation();
        var service = new ScopeWorkflowTextInvocationApplicationService(
            new WorkflowQuery { Lookup = new(ScopeWorkflowLookupStatus.Runnable, workflow, "") }, invocation);

        var act = () => service.InvokeAsync(new("scope-alpha", "wf-alpha", "input"), null);

        await act.Should().ThrowAsync<InvalidOperationException>();
        invocation.Request.Should().BeNull();
    }

    [Theory]
    [InlineData(NyxIdCallerCredentialKind.SourceReadableUserBearer, AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer)]
    [InlineData(NyxIdCallerCredentialKind.ProxyDelegation, AgentToolNyxIdCredentialKindPayload.ProxyDelegation)]
    [InlineData(NyxIdCallerCredentialKind.AgentKey, AgentToolNyxIdCredentialKindPayload.AgentKey)]
    public async Task InvokeAsync_PreservesTypedCallerCredentialKind(
        NyxIdCallerCredentialKind kind, AgentToolNyxIdCredentialKindPayload expected)
    {
        var invocation = new Invocation();
        var service = new ScopeWorkflowTextInvocationApplicationService(new WorkflowQuery(), invocation);
        await service.InvokeAsync(new("scope-alpha", "wf-alpha", "input"), new CallerCredential("execution-token", Kind: kind));
        invocation.Request!.Payload.Unpack<ChatRequestEvent>().CallerNyxIdCredentialKind.Should().Be(expected);
    }

    [Fact]
    public async Task InvokeAsync_PreservesExtractedProxyAuthorityAsTypedContext()
    {
        var invocation = new Invocation();
        var service = new ScopeWorkflowTextInvocationApplicationService(new WorkflowQuery(), invocation);
        await service.InvokeAsync(new("scope-alpha", "wf-alpha", "input"),
            new CallerCredential("execution-token", new CallerAuthority("nyxid", "tenant-alpha", "user-alpha", "proxy", "binding-alpha"),
                NyxIdCallerCredentialKind.ProxyDelegation));

        var context = invocation.Request!.Payload.Unpack<ChatRequestEvent>().ToolContext;
        context.NyxIdAuthority.Platform.Should().Be("nyxid");
        context.NyxIdAuthority.Tenant.Should().Be("tenant-alpha");
        context.NyxIdAuthority.ExternalUserId.Should().Be("user-alpha");
        context.NyxIdAuthority.Scope.Should().Be("proxy");
        context.SenderBinding.BindingId.Should().Be("binding-alpha");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task InvokeAsync_RequiresTextBeforeLookup(string prompt)
    {
        var query = new WorkflowQuery();
        var service = new ScopeWorkflowTextInvocationApplicationService(query, new Invocation());
        var act = () => service.InvokeAsync(new("scope-alpha", "wf-alpha", prompt), null);
        await act.Should().ThrowAsync<ArgumentException>();
        query.LastLookup.Should().BeNull();
    }

    private static ScopeWorkflowSummary CreateWorkflow() => new(
        "scope-alpha", "wf-alpha", "Workflow Alpha", "opaque-service-key", "alpha", "actor-definition-alpha",
        "revision-active", "deployment-alpha", "Active", DateTimeOffset.UnixEpoch)
    {
        ServiceAppId = "app-published", ServiceNamespace = "namespace-published", PublishedServiceId = "svc-alpha",
    };

    private sealed class WorkflowQuery : IScopeWorkflowQueryPort
    {
        public ScopeWorkflowLookupResult Lookup { get; init; } = new(ScopeWorkflowLookupStatus.Runnable, CreateWorkflow(), "");
        public (string ScopeId, string WorkflowId)? LastLookup { get; private set; }
        public Task<ScopeWorkflowLookupResult> LookupByWorkflowIdAsync(string scopeId, string workflowId, CancellationToken ct = default)
        {
            LastLookup = (scopeId, workflowId);
            return Task.FromResult(Lookup);
        }
        public Task<IReadOnlyList<ScopeWorkflowSummary>> ListAsync(string scopeId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ScopeWorkflowSummary?> GetByWorkflowIdAsync(string scopeId, string workflowId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ScopeWorkflowSummary?> GetByActorIdAsync(string scopeId, string actorId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Invocation : IServiceInvocationPort
    {
        public ServiceInvocationRequest? Request { get; private set; }
        public ServiceInvocationAcceptedReceipt Receipt { get; } = new() { RunId = "run-alpha", CommandId = "cmd-alpha", CorrelationId = "corr-alpha" };
        public Task<ServiceInvocationAcceptedReceipt> InvokeAsync(ServiceInvocationRequest request, CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(Receipt);
        }
    }

    private sealed class Resolution : IServiceInvocationResolutionPort
    {
        public ServiceInvocationRequest? Request { get; private set; }
        public Task<bool> HasServiceAsync(ServiceIdentity identity, CancellationToken ct = default) => Task.FromResult(true);
        public Task<ServiceInvocationResolvedTarget> ResolveAsync(ServiceInvocationRequest request, CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(new ServiceInvocationResolvedTarget(
                new("opaque-service-key", "revision-requested", "deployment-alpha", "actor-definition-alpha", "Active", []),
                new PreparedServiceRevisionArtifact(), new ServiceEndpointDescriptor { EndpointId = "chat" }));
        }
    }

    private sealed class Admission : IInvokeAdmissionAuthorizer
    {
        public ServiceInvocationRequest? Request { get; private set; }
        public Exception? Error { get; init; }
        public Task AuthorizeAsync(string serviceKey, string deploymentId, PreparedServiceRevisionArtifact artifact,
            ServiceEndpointDescriptor endpoint, ServiceInvocationRequest request, CancellationToken ct = default)
        {
            Request = request;
            return Error == null ? Task.CompletedTask : Task.FromException(Error);
        }
    }

    private sealed class Dispatcher : IServiceInvocationDispatcher
    {
        public ServiceInvocationRequest? Request { get; private set; }
        public ServiceInvocationAcceptedReceipt Receipt { get; } = new() { RunId = "run-alpha", CommandId = "cmd-alpha", CorrelationId = "corr-alpha" };
        public Task<ServiceInvocationAcceptedReceipt> DispatchAsync(ServiceInvocationResolvedTarget target, ServiceInvocationRequest request, CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(Receipt);
        }
    }
}
