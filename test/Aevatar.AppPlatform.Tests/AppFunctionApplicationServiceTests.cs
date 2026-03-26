using Aevatar.AppPlatform.Abstractions;
using Aevatar.AppPlatform.Abstractions.Ports;
using Aevatar.AppPlatform.Application.Services;
using Aevatar.AppPlatform.Infrastructure.Stores;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace Aevatar.AppPlatform.Tests;

public class AppFunctionApplicationServiceTests
{
    [Fact]
    public async Task ListAsync_ShouldResolveFunctionDescriptorFromRevisionEndpoint()
    {
        var registryReader = new StubAppRegistryReader();
        var lifecycleQueryPort = new StubServiceLifecycleQueryPort();
        var service = new AppFunctionQueryApplicationService(registryReader, lifecycleQueryPort);

        var functions = await service.ListAsync("copilot");
        var function = functions.Should().ContainSingle().Subject;

        function.FunctionId.Should().Be("default-chat");
        function.DisplayName.Should().Be("Chat");
        function.AppId.Should().Be("copilot");
        function.ReleaseId.Should().Be("prod");
        function.ServiceId.Should().Be("chat-gateway");
        function.EndpointId.Should().Be("chat");
        function.EndpointKind.Should().Be(AppFunctionEndpointKind.Chat);
        function.RequestTypeUrl.Should().Be("type.googleapis.com/aevatar.ai.ChatRequestEvent");
        function.ResponseTypeUrl.Should().Be("type.googleapis.com/aevatar.ai.ChatResponseEvent");
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturnAcceptedReceiptAndOpaqueOperationId()
    {
        var registryReader = new StubAppRegistryReader();
        var targetQueryPort = new AppFunctionExecutionTargetQueryApplicationService(registryReader, new StubServiceLifecycleQueryPort());
        var invocationPort = new RecordingServiceInvocationPort();
        var operationStore = new InMemoryOperationStore();
        var operationCommandPort = new OperationCommandApplicationService(operationStore);
        var operationQueryPort = new OperationQueryApplicationService(operationStore);
        var service = new AppFunctionInvocationApplicationService(
            targetQueryPort,
            invocationPort,
            operationCommandPort);

        var receipt = await service.InvokeAsync(
            "copilot",
            "default-chat",
            new AppFunctionInvokeRequest
            {
                Payload = new Any
                {
                    TypeUrl = "type.googleapis.com/aevatar.ai.ChatRequestEvent",
                    Value = ByteString.CopyFromUtf8("payload"),
                },
                CommandId = "cmd-123",
                CorrelationId = "corr-123",
                Caller = new AppFunctionCaller
                {
                    ServiceKey = "external-ai",
                    TenantId = "scope-dev",
                    AppId = "copilot",
                    ScopeId = "scope-dev",
                    SessionId = "session-1",
                },
            });

        receipt.AppId.Should().Be("copilot");
        receipt.ReleaseId.Should().Be("prod");
        receipt.FunctionId.Should().Be("default-chat");
        receipt.ServiceId.Should().Be("chat-gateway");
        receipt.EndpointId.Should().Be("chat");
        receipt.RequestId.Should().Be("request-123");
        receipt.TargetActorId.Should().Be("workflow-run-actor");
        receipt.CommandId.Should().Be("cmd-123");
        receipt.CorrelationId.Should().Be("corr-123");
        receipt.OperationId.Should().NotBeNullOrWhiteSpace();
        receipt.StatusUrl.Should().Be($"/api/operations/{receipt.OperationId}");
        receipt.EventsUrl.Should().Be($"/api/operations/{receipt.OperationId}/events");
        receipt.ResultUrl.Should().Be($"/api/operations/{receipt.OperationId}/result");
        receipt.StreamUrl.Should().Be($"/api/operations/{receipt.OperationId}:stream");

        invocationPort.Requests.Should().ContainSingle();
        invocationPort.Requests[0].Identity.ServiceId.Should().Be("chat-gateway");
        invocationPort.Requests[0].EndpointId.Should().Be("chat");
        invocationPort.Requests[0].Caller.ServiceKey.Should().Be("external-ai");
        invocationPort.Requests[0].Payload.TypeUrl.Should().Be("type.googleapis.com/aevatar.ai.ChatRequestEvent");

        var operation = await operationQueryPort.GetAsync(receipt.OperationId);
        operation.Should().NotBeNull();
        operation!.Status.Should().Be(AppOperationStatus.Accepted);
        operation.Kind.Should().Be(AppOperationKind.FunctionInvoke);
        operation.AppId.Should().Be("copilot");
        operation.ReleaseId.Should().Be("prod");
        operation.FunctionId.Should().Be("default-chat");
        operation.ServiceId.Should().Be("chat-gateway");
        operation.EndpointId.Should().Be("chat");
        operation.CommandId.Should().Be("cmd-123");
        operation.CorrelationId.Should().Be("corr-123");

        var events = await operationQueryPort.ListEventsAsync(receipt.OperationId);
        events.Should().ContainSingle();
        events[0].OperationId.Should().Be(receipt.OperationId);
        events[0].Status.Should().Be(AppOperationStatus.Accepted);
        events[0].EventCode.Should().Be("accepted");
    }

    [Fact]
    public async Task InvokeAsync_WhenRuntimeBridgeHandlesWorkflow_ShouldBypassGenericServiceInvocation()
    {
        var registryReader = new StubAppRegistryReader();
        var targetQueryPort = new AppFunctionExecutionTargetQueryApplicationService(registryReader, new StubServiceLifecycleQueryPort());
        var invocationPort = new RecordingServiceInvocationPort();
        var operationStore = new InMemoryOperationStore();
        var operationCommandPort = new OperationCommandApplicationService(operationStore);
        var runtimePort = new StubRuntimeInvocationPort();
        var service = new AppFunctionInvocationApplicationService(
            targetQueryPort,
            invocationPort,
            operationCommandPort,
            runtimePort);

        var receipt = await service.InvokeAsync(
            "copilot",
            "default-chat",
            new AppFunctionInvokeRequest
            {
                Payload = Any.Pack(new StringValue { Value = "ignored-by-test" }),
                CommandId = "cmd-runtime",
                CorrelationId = "corr-runtime",
            });

        receipt.RequestId.Should().Be("cmd-runtime");
        receipt.TargetActorId.Should().Be("workflow-run-runtime");
        receipt.CommandId.Should().Be("cmd-runtime");
        receipt.CorrelationId.Should().Be("corr-runtime");
        invocationPort.Requests.Should().BeEmpty();
        runtimePort.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnExecutionTargetWithPrimaryActorId()
    {
        var registryReader = new StubAppRegistryReader();
        var lifecycleQueryPort = new StubServiceLifecycleQueryPort();
        var service = new AppFunctionExecutionTargetQueryApplicationService(registryReader, lifecycleQueryPort);

        var target = await service.ResolveAsync("copilot", "default-chat");

        target.Should().NotBeNull();
        target!.App.AppId.Should().Be("copilot");
        target.Release.ReleaseId.Should().Be("prod");
        target.Entry.EntryId.Should().Be("default-chat");
        target.ServiceRef.ServiceId.Should().Be("chat-gateway");
        target.PrimaryActorId.Should().Be("service-actor-1");
        target.ActiveRevisionId.Should().Be("r1");
    }

    private sealed class StubAppRegistryReader : IAppRegistryReader
    {
        private readonly AppDefinitionSnapshot _app = new()
        {
            AppId = "copilot",
            OwnerScopeId = "scope-dev",
            DisplayName = "Copilot",
            Description = "Copilot app",
            Visibility = AppVisibility.Public,
            DefaultReleaseId = "prod",
            RoutePaths = { "/copilot" },
        };

        private readonly AppReleaseSnapshot _release = new()
        {
            ReleaseId = "prod",
            AppId = "copilot",
            DisplayName = "Production",
            Status = AppReleaseStatus.Published,
            ServiceRefs =
            {
                new AppServiceRef
                {
                    TenantId = "scope-dev",
                    AppId = "copilot",
                    Namespace = "prod",
                    ServiceId = "chat-gateway",
                    RevisionId = "r1",
                    ImplementationKind = AppImplementationKind.Workflow,
                    Role = AppServiceRole.Entry,
                },
            },
            EntryRefs =
            {
                new AppEntryRef
                {
                    EntryId = "default-chat",
                    ServiceId = "chat-gateway",
                    EndpointId = "chat",
                },
            },
        };

        public Task<IReadOnlyList<AppDefinitionSnapshot>> ListAppsAsync(string? ownerScopeId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AppDefinitionSnapshot>>([_app]);

        public Task<AppDefinitionSnapshot?> GetAppAsync(string appId, CancellationToken ct = default) =>
            Task.FromResult<AppDefinitionSnapshot?>(string.Equals(appId, _app.AppId, StringComparison.Ordinal) ? _app : null);

        public Task<IReadOnlyList<AppReleaseSnapshot>> ListReleasesAsync(string appId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AppReleaseSnapshot>>([_release]);

        public Task<AppReleaseSnapshot?> GetReleaseAsync(string appId, string releaseId, CancellationToken ct = default)
        {
            if (!string.Equals(appId, _release.AppId, StringComparison.Ordinal) ||
                !string.Equals(releaseId, _release.ReleaseId, StringComparison.Ordinal))
            {
                return Task.FromResult<AppReleaseSnapshot?>(null);
            }

            return Task.FromResult<AppReleaseSnapshot?>(_release);
        }

        public Task<IReadOnlyList<AppRouteSnapshot>> ListRoutesAsync(string appId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AppRouteSnapshot>>(
            [
                new AppRouteSnapshot
                {
                    RoutePath = "/copilot",
                    AppId = "copilot",
                    ReleaseId = "prod",
                    EntryId = "default-chat",
                },
            ]);

        public Task<AppRouteSnapshot?> GetRouteByPathAsync(string routePath, CancellationToken ct = default) =>
            Task.FromResult<AppRouteSnapshot?>(
                string.Equals(routePath, "/copilot", StringComparison.OrdinalIgnoreCase)
                    ? new AppRouteSnapshot
                    {
                        RoutePath = "/copilot",
                        AppId = "copilot",
                        ReleaseId = "prod",
                        EntryId = "default-chat",
                    }
                    : null);
    }

    private sealed class StubServiceLifecycleQueryPort : IServiceLifecycleQueryPort
    {
        public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceCatalogSnapshot?>(new ServiceCatalogSnapshot(
                ServiceKey: "scope-dev:copilot:prod:chat-gateway",
                TenantId: "scope-dev",
                AppId: "copilot",
                Namespace: "prod",
                ServiceId: "chat-gateway",
                DisplayName: "Chat Gateway",
                DefaultServingRevisionId: "r1",
                ActiveServingRevisionId: "r1",
                DeploymentId: "deploy-1",
                PrimaryActorId: "service-actor-1",
                DeploymentStatus: "Active",
                Endpoints:
                [
                    new ServiceEndpointSnapshot(
                        "chat",
                        "Chat",
                        "Chat",
                        "type.googleapis.com/aevatar.ai.ChatRequestEvent",
                        "type.googleapis.com/aevatar.ai.ChatResponseEvent",
                        "Chat with the deployed app"),
                ],
                PolicyIds: [],
                UpdatedAt: DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(string tenantId, string appId, string @namespace, int take = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceRevisionCatalogSnapshot?>(new ServiceRevisionCatalogSnapshot(
                ServiceKey: "scope-dev:copilot:prod:chat-gateway",
                Revisions:
                [
                    new ServiceRevisionSnapshot(
                        RevisionId: "r1",
                        ImplementationKind: "Workflow",
                        Status: "Published",
                        ArtifactHash: "hash-1",
                        FailureReason: string.Empty,
                        Endpoints:
                        [
                            new ServiceEndpointSnapshot(
                                "chat",
                                "Chat",
                                "Chat",
                                "type.googleapis.com/aevatar.ai.ChatRequestEvent",
                                "type.googleapis.com/aevatar.ai.ChatResponseEvent",
                                "Chat with the deployed app"),
                        ],
                        CreatedAt: DateTimeOffset.UtcNow,
                        PreparedAt: DateTimeOffset.UtcNow,
                        PublishedAt: DateTimeOffset.UtcNow,
                        RetiredAt: null),
                ],
                UpdatedAt: DateTimeOffset.UtcNow));

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult<ServiceDeploymentCatalogSnapshot?>(null);
    }

    private sealed class RecordingServiceInvocationPort : IServiceInvocationPort
    {
        public List<ServiceInvocationRequest> Requests { get; } = [];

        public Task<ServiceInvocationAcceptedReceipt> InvokeAsync(ServiceInvocationRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ServiceInvocationAcceptedReceipt
            {
                RequestId = "request-123",
                ServiceKey = "scope-dev:copilot:prod:chat-gateway",
                DeploymentId = "deploy-1",
                TargetActorId = "workflow-run-actor",
                EndpointId = request.EndpointId ?? string.Empty,
                CommandId = request.CommandId ?? string.Empty,
                CorrelationId = request.CorrelationId ?? string.Empty,
            });
        }
    }

    private sealed class StubRuntimeInvocationPort : IAppFunctionRuntimeInvocationPort
    {
        public List<(string AppId, string ReleaseId, string FunctionId)> Calls { get; } = [];

        public async Task<AppFunctionRuntimeInvokeAccepted?> TryInvokeAsync(
            AppFunctionExecutionTarget target,
            AppFunctionInvokeRequest request,
            Func<AppFunctionRuntimeInvokeAccepted, CancellationToken, ValueTask<string>> onAcceptedAsync,
            CancellationToken ct = default)
        {
            Calls.Add((target.App.AppId, target.Release.ReleaseId, target.Entry.EntryId));
            var accepted = new AppFunctionRuntimeInvokeAccepted(
                RequestId: "cmd-runtime",
                TargetActorId: "workflow-run-runtime",
                CommandId: "cmd-runtime",
                CorrelationId: "corr-runtime");
            await onAcceptedAsync(accepted, ct);
            return accepted;
        }
    }
}
