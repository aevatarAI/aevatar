using System.Net;
using System.Net.Http.Json;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Hosting.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ServiceEndpointsTests
{
    [Fact]
    public async Task CreateServiceAsync_ShouldDispatchTypedCommand()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/services/", new ServiceEndpoints.CreateServiceHttpRequest(
            " tenant ",
            " app ",
            " ns ",
            " service-a ",
            "Orders",
            [
                new ServiceEndpoints.ServiceEndpointHttpRequest(
                    " submit ",
                    "Submit",
                    "unexpected-kind",
                    "type.googleapis.com/demo.Submit",
                    string.Empty,
                    "submit command"),
            ]));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandPort.CreateServiceCommand.Should().NotBeNull();
        host.CommandPort.CreateServiceCommand!.Spec.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "tenant",
            AppId = "app",
            Namespace = "ns",
            ServiceId = "service-a",
        });
        host.CommandPort.CreateServiceCommand.Spec.Endpoints.Should().ContainSingle();
        host.CommandPort.CreateServiceCommand.Spec.Endpoints[0].Kind.Should().Be(ServiceEndpointKind.Command);
    }

    [Fact]
    public async Task CreateRevisionAsync_ShouldMapStaticImplementation()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/services/orders/revisions", new ServiceEndpoints.CreateRevisionHttpRequest(
            "tenant",
            "app",
            "ns",
            "rev-1",
            "static",
            new ServiceEndpoints.StaticRevisionHttpRequest(
                typeof(TestStaticAgent).AssemblyQualifiedName!,
                "static-actor",
                [
                    new ServiceEndpoints.ServiceEndpointHttpRequest(
                        "submit",
                        "Submit",
                        "command",
                        "type.googleapis.com/demo.Submit",
                        string.Empty,
                        "submit command"),
                ]),
            null,
            null));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandPort.CreateRevisionCommand.Should().NotBeNull();
        host.CommandPort.CreateRevisionCommand!.Spec.ImplementationKind.Should().Be(ServiceImplementationKind.Static);
        host.CommandPort.CreateRevisionCommand.Spec.StaticSpec.Should().NotBeNull();
        host.CommandPort.CreateRevisionCommand.Spec.StaticSpec.ActorTypeName.Should().Be(typeof(TestStaticAgent).AssemblyQualifiedName);
        host.CommandPort.CreateRevisionCommand.Spec.StaticSpec.PreferredActorId.Should().Be("static-actor");
        host.CommandPort.CreateRevisionCommand.Spec.StaticSpec.Endpoints.Should().ContainSingle();
    }

    [Fact]
    public async Task CreateRevisionAsync_ShouldMapScriptingAndWorkflowImplementations()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var scriptingResponse = await host.Client.PostAsJsonAsync("/api/services/orders/revisions", new ServiceEndpoints.CreateRevisionHttpRequest(
            "tenant",
            "app",
            "ns",
            "rev-script",
            "scripting",
            null,
            new ServiceEndpoints.ScriptingRevisionHttpRequest("script-a", "7", "script-definition"),
            null));
        scriptingResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandPort.CreateRevisionCommand!.Spec.ScriptingSpec.Should().NotBeNull();
        host.CommandPort.CreateRevisionCommand.Spec.ScriptingSpec.ScriptId.Should().Be("script-a");
        host.CommandPort.CreateRevisionCommand.Spec.ScriptingSpec.DefinitionActorId.Should().Be("script-definition");

        var workflowResponse = await host.Client.PostAsJsonAsync("/api/services/orders/revisions", new ServiceEndpoints.CreateRevisionHttpRequest(
            "tenant",
            "app",
            "ns",
            "rev-workflow",
            "workflow",
            null,
            null,
            new ServiceEndpoints.WorkflowRevisionHttpRequest(
                "approval",
                "name: approval",
                "workflow-definition",
                new Dictionary<string, string> { ["child.yaml"] = "name: child" })));

        workflowResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandPort.CreateRevisionCommand!.Spec.WorkflowSpec.Should().NotBeNull();
        host.CommandPort.CreateRevisionCommand.Spec.WorkflowSpec.WorkflowName.Should().Be("approval");
        host.CommandPort.CreateRevisionCommand.Spec.WorkflowSpec.InlineWorkflowYamls.Should().ContainKey("child.yaml");
    }

    [Fact]
    public async Task RevisionLifecycleEndpoints_ShouldDispatchCommandPortCalls()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var prepareResponse = await host.Client.PostAsJsonAsync(
            "/api/services/orders/revisions/rev-1:prepare",
            new ServiceEndpoints.ServiceIdentityHttpRequest("tenant", "app", "ns"));
        var publishResponse = await host.Client.PostAsJsonAsync(
            "/api/services/orders/revisions/rev-1:publish",
            new ServiceEndpoints.ServiceIdentityHttpRequest("tenant", "app", "ns"));
        var defaultResponse = await host.Client.PostAsJsonAsync(
            "/api/services/orders:default-serving",
            new ServiceEndpoints.SetDefaultServingRevisionHttpRequest("tenant", "app", "ns", "rev-1"));
        var activateResponse = await host.Client.PostAsJsonAsync(
            "/api/services/orders:activate",
            new ServiceEndpoints.ActivateServiceHttpRequest("tenant", "app", "ns", "rev-1"));

        prepareResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        publishResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        defaultResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        activateResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        host.CommandPort.PrepareRevisionCommand!.RevisionId.Should().Be("rev-1");
        host.CommandPort.PublishRevisionCommand!.RevisionId.Should().Be("rev-1");
        host.CommandPort.SetDefaultServingRevisionCommand!.RevisionId.Should().Be("rev-1");
        host.CommandPort.ActivateServingRevisionCommand!.RevisionId.Should().Be("rev-1");
    }

    [Fact]
    public async Task QueryEndpoints_ShouldReturnCatalogSnapshots()
    {
        await using var host = await EndpointTestHost.StartAsync();
        host.QueryPort.ListServicesResult =
        [
            new ServiceCatalogSnapshot(
                "tenant/app/ns/orders",
                "tenant",
                "app",
                "ns",
                "orders",
                "Orders",
                "rev-1",
                "rev-1",
                "dep-1",
                "actor-1",
                "active",
                [
                    new ServiceEndpointSnapshot("submit", "Submit", "command", "req", string.Empty, "desc"),
                ],
                [],
                DateTimeOffset.Parse("2026-03-14T00:00:00+00:00")),
        ];
        host.QueryPort.GetServiceResult = host.QueryPort.ListServicesResult[0];
        host.QueryPort.GetServiceRevisionsResult = new ServiceRevisionCatalogSnapshot(
            "tenant/app/ns/orders",
            [
                new ServiceRevisionSnapshot(
                    "rev-1",
                    "static",
                    "published",
                    "artifact",
                    string.Empty,
                    [
                        new ServiceEndpointSnapshot("submit", "Submit", "command", "req", string.Empty, "desc"),
                    ],
                    DateTimeOffset.Parse("2026-03-14T00:00:00+00:00"),
                    DateTimeOffset.Parse("2026-03-14T00:01:00+00:00"),
                    DateTimeOffset.Parse("2026-03-14T00:02:00+00:00"),
                    null),
            ],
            DateTimeOffset.Parse("2026-03-14T00:03:00+00:00"));

        var listResponse = await host.Client.GetFromJsonAsync<List<ServiceCatalogSnapshot>>("/api/services/?take=10");
        var getResponse = await host.Client.GetFromJsonAsync<ServiceCatalogSnapshot>("/api/services/orders?tenantId=tenant&appId=app&namespace=ns");
        var revisionResponse = await host.Client.GetFromJsonAsync<ServiceRevisionCatalogSnapshot>("/api/services/orders/revisions?tenantId=tenant&appId=app&namespace=ns");

        listResponse.Should().ContainSingle();
        getResponse!.ServiceId.Should().Be("orders");
        revisionResponse!.Revisions.Should().ContainSingle();
        host.QueryPort.LastListServicesTake.Should().Be(10);
        host.QueryPort.LastGetServiceIdentity!.ServiceId.Should().Be("orders");
        host.QueryPort.LastGetServiceRevisionsIdentity!.Namespace.Should().Be("ns");
    }

    [Fact]
    public async Task InvokeAsync_ShouldPackBase64PayloadIntoAny()
    {
        await using var host = await EndpointTestHost.StartAsync();
        var payload = Convert.ToBase64String([1, 2, 3, 4]);

        var response = await host.Client.PostAsJsonAsync("/api/services/orders/invoke/chat", new ServiceEndpoints.InvokeServiceHttpRequest(
            "tenant",
            "app",
            "ns",
            null,
            null,
            "type.googleapis.com/demo.Request",
            payload));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.InvocationPort.LastRequest.Should().NotBeNull();
        host.InvocationPort.LastRequest!.EndpointId.Should().Be("chat");
        host.InvocationPort.LastRequest.Payload.TypeUrl.Should().Be("type.googleapis.com/demo.Request");
        host.InvocationPort.LastRequest.Payload.Value.ToByteArray().Should().Equal([1, 2, 3, 4]);
        host.InvocationPort.LastRequest.CommandId.Should().BeEmpty();
        host.InvocationPort.LastRequest.CorrelationId.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_WithoutPayloadBase64_ShouldPackEmptyPayload()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/services/orders/invoke/chat", new ServiceEndpoints.InvokeServiceHttpRequest(
            "tenant",
            "app",
            "ns",
            "cmd-2",
            "corr-2",
            "type.googleapis.com/demo.Empty",
            null));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.InvocationPort.LastRequest.Should().NotBeNull();
        host.InvocationPort.LastRequest!.Payload.Value.Length.Should().Be(0);
        host.InvocationPort.LastRequest.CommandId.Should().Be("cmd-2");
        host.InvocationPort.LastRequest.CorrelationId.Should().Be("corr-2");
    }

    [Fact]
    public async Task CreateRevisionAsync_WithUnsupportedImplementationKind_ShouldFail()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/services/orders/revisions", new ServiceEndpoints.CreateRevisionHttpRequest(
            "tenant",
            "app",
            "ns",
            "rev-1",
            "unknown",
            null,
            null,
            null));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task CreateRevisionAsync_ShouldAllowMissingImplementationPayloads_AndFallbackToEmptySpecs()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var staticResponse = await host.Client.PostAsJsonAsync("/api/services/orders/revisions", new ServiceEndpoints.CreateRevisionHttpRequest(
            "tenant",
            "app",
            "ns",
            "rev-static-empty",
            "static",
            null,
            null,
            null));
        staticResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandPort.CreateRevisionCommand!.Spec.StaticSpec.Should().NotBeNull();
        host.CommandPort.CreateRevisionCommand.Spec.StaticSpec.ActorTypeName.Should().BeEmpty();
        host.CommandPort.CreateRevisionCommand.Spec.StaticSpec.Endpoints.Should().BeEmpty();

        var scriptingResponse = await host.Client.PostAsJsonAsync("/api/services/orders/revisions", new ServiceEndpoints.CreateRevisionHttpRequest(
            "tenant",
            "app",
            "ns",
            "rev-script-empty",
            "scripting",
            null,
            null,
            null));
        scriptingResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandPort.CreateRevisionCommand!.Spec.ScriptingSpec.Should().NotBeNull();
        host.CommandPort.CreateRevisionCommand.Spec.ScriptingSpec.ScriptId.Should().BeEmpty();
        host.CommandPort.CreateRevisionCommand.Spec.ScriptingSpec.DefinitionActorId.Should().BeEmpty();

        var workflowResponse = await host.Client.PostAsJsonAsync("/api/services/orders/revisions", new ServiceEndpoints.CreateRevisionHttpRequest(
            "tenant",
            "app",
            "ns",
            "rev-workflow-empty",
            "workflow",
            null,
            null,
            null));
        workflowResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandPort.CreateRevisionCommand!.Spec.WorkflowSpec.Should().NotBeNull();
        host.CommandPort.CreateRevisionCommand.Spec.WorkflowSpec.WorkflowName.Should().BeEmpty();
        host.CommandPort.CreateRevisionCommand.Spec.WorkflowSpec.DefinitionActorId.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateServiceAsync_ShouldMapChatEndpointKind()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/services/", new ServiceEndpoints.CreateServiceHttpRequest(
            "tenant",
            "app",
            "ns",
            "service-chat",
            "Chat Service",
            [
                new ServiceEndpoints.ServiceEndpointHttpRequest(
                    "chat",
                    "Chat",
                    "chat",
                    "type.googleapis.com/demo.ChatRequest",
                    "type.googleapis.com/demo.ChatResponse",
                    "chat endpoint"),
            ]));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.CommandPort.CreateServiceCommand!.Spec.Endpoints.Should().ContainSingle();
        host.CommandPort.CreateServiceCommand.Spec.Endpoints[0].Kind.Should().Be(ServiceEndpointKind.Chat);
    }

    [Fact]
    public async Task QueryEndpoints_ShouldUseEmptyIdentityPartsAndDefaultTake_WhenOmitted()
    {
        await using var host = await EndpointTestHost.StartAsync();
        host.QueryPort.GetServiceResult = new ServiceCatalogSnapshot(
            "tenant/app/ns/orders",
            string.Empty,
            string.Empty,
            string.Empty,
            "orders",
            "Orders",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            [],
            [],
            DateTimeOffset.UtcNow);
        host.QueryPort.GetServiceRevisionsResult = new ServiceRevisionCatalogSnapshot(
            "tenant/app/ns/orders",
            [],
            DateTimeOffset.UtcNow);

        var listResponse = await host.Client.GetFromJsonAsync<List<ServiceCatalogSnapshot>>("/api/services");
        var getResponse = await host.Client.GetFromJsonAsync<ServiceCatalogSnapshot>("/api/services/orders");
        var revisionsResponse = await host.Client.GetFromJsonAsync<ServiceRevisionCatalogSnapshot>("/api/services/orders/revisions");

        listResponse.Should().BeEmpty();
        getResponse.Should().NotBeNull();
        revisionsResponse.Should().NotBeNull();
        host.QueryPort.LastListServicesTake.Should().Be(200);
        host.QueryPort.LastGetServiceIdentity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = string.Empty,
            AppId = string.Empty,
            Namespace = string.Empty,
            ServiceId = "orders",
        });
        host.QueryPort.LastGetServiceRevisionsIdentity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = string.Empty,
            AppId = string.Empty,
            Namespace = string.Empty,
            ServiceId = "orders",
        });
    }

    [Fact]
    public async Task InvokeAsync_WithoutPayloadTypeUrl_ShouldFail()
    {
        await using var host = await EndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/services/orders/invoke/chat", new ServiceEndpoints.InvokeServiceHttpRequest(
            "tenant",
            "app",
            "ns",
            "cmd-1",
            "corr-1",
            null,
            null));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    private sealed class EndpointTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private EndpointTestHost(
            WebApplication app,
            HttpClient client,
            RecordingServiceCommandPort commandPort,
            RecordingServiceQueryPort queryPort,
            RecordingServiceInvocationPort invocationPort)
        {
            _app = app;
            Client = client;
            CommandPort = commandPort;
            QueryPort = queryPort;
            InvocationPort = invocationPort;
        }

        public HttpClient Client { get; }

        public RecordingServiceCommandPort CommandPort { get; }

        public RecordingServiceQueryPort QueryPort { get; }

        public RecordingServiceInvocationPort InvocationPort { get; }

        public static async Task<EndpointTestHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var commandPort = new RecordingServiceCommandPort();
            var queryPort = new RecordingServiceQueryPort();
            var invocationPort = new RecordingServiceInvocationPort();
            builder.Services.AddSingleton<IServiceCommandPort>(commandPort);
            builder.Services.AddSingleton<IServiceQueryPort>(queryPort);
            builder.Services.AddSingleton<IServiceInvocationPort>(invocationPort);

            var app = builder.Build();
            app.MapGAgentServiceEndpoints();
            await app.StartAsync();

            var addressFeature = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Server addresses are unavailable.");
            var address = addressFeature.Addresses.Single();
            var client = new HttpClient
            {
                BaseAddress = new Uri(address),
            };

            return new EndpointTestHost(app, client, commandPort, queryPort, invocationPort);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class RecordingServiceCommandPort : IServiceCommandPort
    {
        public CreateServiceDefinitionCommand? CreateServiceCommand { get; private set; }

        public CreateServiceRevisionCommand? CreateRevisionCommand { get; private set; }

        public PrepareServiceRevisionCommand? PrepareRevisionCommand { get; private set; }

        public PublishServiceRevisionCommand? PublishRevisionCommand { get; private set; }

        public SetDefaultServingRevisionCommand? SetDefaultServingRevisionCommand { get; private set; }

        public ActivateServingRevisionCommand? ActivateServingRevisionCommand { get; private set; }

        public Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(CreateServiceDefinitionCommand command, CancellationToken ct = default)
        {
            CreateServiceCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("definition-actor", "cmd-create-service", "corr-create-service"));
        }

        public Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(UpdateServiceDefinitionCommand command, CancellationToken ct = default) =>
            Task.FromResult(new ServiceCommandAcceptedReceipt("definition-actor", "cmd-update-service", "corr-update-service"));

        public Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(CreateServiceRevisionCommand command, CancellationToken ct = default)
        {
            CreateRevisionCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("revision-actor", "cmd-create-revision", "corr-create-revision"));
        }

        public Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(PrepareServiceRevisionCommand command, CancellationToken ct = default)
        {
            PrepareRevisionCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("revision-actor", "cmd-prepare-revision", "corr-prepare-revision"));
        }

        public Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(PublishServiceRevisionCommand command, CancellationToken ct = default)
        {
            PublishRevisionCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("revision-actor", "cmd-publish-revision", "corr-publish-revision"));
        }

        public Task<ServiceCommandAcceptedReceipt> SetDefaultServingRevisionAsync(SetDefaultServingRevisionCommand command, CancellationToken ct = default)
        {
            SetDefaultServingRevisionCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("definition-actor", "cmd-default-serving", "corr-default-serving"));
        }

        public Task<ServiceCommandAcceptedReceipt> ActivateServingRevisionAsync(ActivateServingRevisionCommand command, CancellationToken ct = default)
        {
            ActivateServingRevisionCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("deployment-actor", "cmd-activate-serving", "corr-activate-serving"));
        }
    }

    private sealed class RecordingServiceQueryPort : IServiceQueryPort
    {
        public IReadOnlyList<ServiceCatalogSnapshot> ListServicesResult { get; set; } = [];

        public ServiceCatalogSnapshot? GetServiceResult { get; set; }

        public ServiceRevisionCatalogSnapshot? GetServiceRevisionsResult { get; set; }

        public ServiceIdentity? LastGetServiceIdentity { get; private set; }

        public ServiceIdentity? LastGetServiceRevisionsIdentity { get; private set; }

        public int LastListServicesTake { get; private set; }

        public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            LastGetServiceIdentity = identity;
            return Task.FromResult(GetServiceResult);
        }

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default)
        {
            LastListServicesTake = take;
            return Task.FromResult(ListServicesResult);
        }

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            LastGetServiceRevisionsIdentity = identity;
            return Task.FromResult(GetServiceRevisionsResult);
        }
    }

    private sealed class RecordingServiceInvocationPort : IServiceInvocationPort
    {
        public ServiceInvocationRequest? LastRequest { get; private set; }

        public Task<ServiceInvocationAcceptedReceipt> InvokeAsync(ServiceInvocationRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new ServiceInvocationAcceptedReceipt
            {
                RequestId = "request-1",
                ServiceKey = "tenant/app/ns/orders",
                DeploymentId = "dep-1",
                TargetActorId = "target-actor",
                EndpointId = request.EndpointId,
                CommandId = request.CommandId,
                CorrelationId = request.CorrelationId,
            });
        }
    }

    private sealed class TestStaticAgent : Aevatar.Foundation.Abstractions.IAgent
    {
        public string Id => "test-static-agent";

        public Task HandleEventAsync(Aevatar.Foundation.Abstractions.EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("test");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
