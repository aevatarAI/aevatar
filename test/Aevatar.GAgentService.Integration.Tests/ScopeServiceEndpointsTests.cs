using System.Net;
using System.Net.Http.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Application.Workflows;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Hosting.Endpoints;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Integration.Tests;

public sealed class ScopeServiceEndpointsTests
{
    [Fact]
    public async Task BindingEndpoints_ShouldMapScopeToInternalServiceIdentity()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.QueryPort.BindingsResult = new ServiceBindingCatalogSnapshot(
            "scope-a:default:default:orders",
            [],
            DateTimeOffset.UtcNow);

        var createResponse = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/bindings", new
        {
            bindingId = "binding-a",
            displayName = "Dependency",
            bindingKind = "service",
            service = new
            {
                serviceId = "dependency",
                endpointId = "run",
            },
        });
        var getResponse = await host.Client.GetFromJsonAsync<ServiceBindingCatalogSnapshot>("/api/scopes/scope-a/services/orders/bindings");

        createResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        getResponse.Should().NotBeNull();
        host.CommandPort.CreateBindingCommand.Should().NotBeNull();
        host.CommandPort.CreateBindingCommand!.Spec.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "scope-a",
            AppId = "default",
            Namespace = "default",
            ServiceId = "orders",
        });
        host.CommandPort.CreateBindingCommand.Spec.ServiceRef!.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "scope-a",
            AppId = "default",
            Namespace = "default",
            ServiceId = "dependency",
        });
        host.QueryPort.LastBindingsIdentity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "scope-a",
            AppId = "default",
            Namespace = "default",
            ServiceId = "orders",
        });
    }

    [Fact]
    public async Task InvokeEndpoint_ShouldMapScopeToInternalServiceIdentity()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/invoke/chat", new
        {
            payloadTypeUrl = "type.googleapis.com/google.protobuf.Empty",
            payloadBase64 = "",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        host.InvocationPort.LastRequest.Should().NotBeNull();
        host.InvocationPort.LastRequest!.Identity.Should().BeEquivalentTo(new ServiceIdentity
        {
            TenantId = "scope-a",
            AppId = "default",
            Namespace = "default",
            ServiceId = "orders",
        });
        host.InvocationPort.LastRequest.EndpointId.Should().Be("chat");
        host.InvocationPort.LastRequest.Payload.TypeUrl.Should().Be("type.googleapis.com/google.protobuf.Empty");
    }

    [Fact]
    public async Task ResumeRunEndpoint_ShouldResolveRunFromServiceAndDispatch()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "orders", "def-actor-1");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:orders", "dep-1", "rev-1", "def-actor-1");
        host.RunBindingReader.BindingsByRunId["run-1"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-1",
                "def-actor-1",
                "run-1",
                "orders",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/runs/run-1:resume", new
        {
            stepId = "approval-1",
            approved = true,
            userInput = "approved",
            metadata = new Dictionary<string, string> { ["source"] = "test" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.ResumeDispatchService.LastCommand.Should().NotBeNull();
        host.ResumeDispatchService.LastCommand!.ActorId.Should().Be("run-actor-1");
        host.ResumeDispatchService.LastCommand.RunId.Should().Be("run-1");
        host.ResumeDispatchService.LastCommand.StepId.Should().Be("approval-1");
        host.ResumeDispatchService.LastCommand.Approved.Should().BeTrue();
    }

    [Fact]
    public async Task SignalRunEndpoint_ShouldResolveRunFromServiceAndDispatch()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "orders", "def-actor-1");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:orders", "dep-1", "rev-1", "def-actor-1");
        host.RunBindingReader.BindingsByRunId["run-2"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-2",
                "def-actor-1",
                "run-2",
                "orders",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/runs/run-2:signal", new
        {
            signalName = "ops_window_open",
            stepId = "wait-1",
            payload = "window=open",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.SignalDispatchService.LastCommand.Should().NotBeNull();
        host.SignalDispatchService.LastCommand!.ActorId.Should().Be("run-actor-2");
        host.SignalDispatchService.LastCommand.RunId.Should().Be("run-2");
        host.SignalDispatchService.LastCommand.SignalName.Should().Be("ops_window_open");
        host.SignalDispatchService.LastCommand.StepId.Should().Be("wait-1");
    }

    [Fact]
    public async Task StopRunEndpoint_ShouldResolveRunFromHistoricalDeploymentAndDispatch()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "orders", "def-actor-active");
        host.LifecycleQueryPort.Deployments = new ServiceDeploymentCatalogSnapshot(
            "scope-a:default:default:orders",
            [
                new ServiceDeploymentSnapshot("dep-active", "rev-2", "def-actor-active", "Active", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                new ServiceDeploymentSnapshot("dep-old", "rev-1", "def-actor-old", "Inactive", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow),
            ],
            DateTimeOffset.UtcNow);
        host.RunBindingReader.BindingsByRunId["run-3"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-3",
                "def-actor-old",
                "run-3",
                "orders",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/runs/run-3:stop", new
        {
            reason = "manual",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.StopDispatchService.LastCommand.Should().NotBeNull();
        host.StopDispatchService.LastCommand!.ActorId.Should().Be("run-actor-3");
        host.StopDispatchService.LastCommand.RunId.Should().Be("run-3");
        host.StopDispatchService.LastCommand.Reason.Should().Be("manual");
    }

    [Fact]
    public async Task ResumeRunEndpoint_ShouldReturnNotFound_WhenRunDoesNotBelongToService()
    {
        await using var host = await ScopeServiceEndpointTestHost.StartAsync();
        host.LifecycleQueryPort.Service = BuildService("scope-a", "orders", "def-actor-1");
        host.LifecycleQueryPort.Deployments = BuildDeployments("scope-a:default:default:orders", "dep-1", "rev-1", "def-actor-1");
        host.RunBindingReader.BindingsByRunId["run-miss"] =
        [
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "run-actor-x",
                "other-definition",
                "run-miss",
                "other",
                "yaml",
                new Dictionary<string, string>(StringComparer.Ordinal),
                "scope-a"),
        ];

        var response = await host.Client.PostAsJsonAsync("/api/scopes/scope-a/services/orders/runs/run-miss:resume", new
        {
            stepId = "approval-1",
            approved = true,
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static ServiceCatalogSnapshot BuildService(string scopeId, string serviceId, string primaryActorId) =>
        new(
            $"{scopeId}:default:default:{serviceId}",
            scopeId,
            "default",
            "default",
            serviceId,
            serviceId,
            "rev-1",
            "rev-1",
            "dep-1",
            primaryActorId,
            "Active",
            [],
            [],
            DateTimeOffset.UtcNow);

    private static ServiceDeploymentCatalogSnapshot BuildDeployments(
        string serviceKey,
        string deploymentId,
        string revisionId,
        string primaryActorId) =>
        new(
            serviceKey,
            [
                new ServiceDeploymentSnapshot(
                    deploymentId,
                    revisionId,
                    primaryActorId,
                    "Active",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ],
            DateTimeOffset.UtcNow);

    private sealed class ScopeServiceEndpointTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ScopeServiceEndpointTestHost(
            WebApplication app,
            HttpClient client,
            RecordingServiceGovernanceCommandPort commandPort,
            RecordingServiceGovernanceQueryPort queryPort,
            RecordingServiceInvocationPort invocationPort,
            RecordingServiceLifecycleQueryPort lifecycleQueryPort,
            FakeWorkflowRunBindingReader runBindingReader,
            RecordingResumeDispatchService resumeDispatchService,
            RecordingSignalDispatchService signalDispatchService,
            RecordingStopDispatchService stopDispatchService)
        {
            _app = app;
            Client = client;
            CommandPort = commandPort;
            QueryPort = queryPort;
            InvocationPort = invocationPort;
            LifecycleQueryPort = lifecycleQueryPort;
            RunBindingReader = runBindingReader;
            ResumeDispatchService = resumeDispatchService;
            SignalDispatchService = signalDispatchService;
            StopDispatchService = stopDispatchService;
        }

        public HttpClient Client { get; }

        public RecordingServiceGovernanceCommandPort CommandPort { get; }

        public RecordingServiceGovernanceQueryPort QueryPort { get; }

        public RecordingServiceInvocationPort InvocationPort { get; }

        public RecordingServiceLifecycleQueryPort LifecycleQueryPort { get; }

        public FakeWorkflowRunBindingReader RunBindingReader { get; }

        public RecordingResumeDispatchService ResumeDispatchService { get; }

        public RecordingSignalDispatchService SignalDispatchService { get; }

        public RecordingStopDispatchService StopDispatchService { get; }

        public static async Task<ScopeServiceEndpointTestHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var commandPort = new RecordingServiceGovernanceCommandPort();
            var queryPort = new RecordingServiceGovernanceQueryPort();
            var invocationPort = new RecordingServiceInvocationPort();
            var lifecycleQueryPort = new RecordingServiceLifecycleQueryPort();
            var runBindingReader = new FakeWorkflowRunBindingReader();
            var resumeDispatchService = new RecordingResumeDispatchService();
            var signalDispatchService = new RecordingSignalDispatchService();
            var stopDispatchService = new RecordingStopDispatchService();
            builder.Services.AddSingleton<IServiceGovernanceCommandPort>(commandPort);
            builder.Services.AddSingleton<IServiceGovernanceQueryPort>(queryPort);
            builder.Services.AddSingleton<IServiceInvocationPort>(invocationPort);
            builder.Services.AddSingleton<IServiceLifecycleQueryPort>(lifecycleQueryPort);
            builder.Services.AddSingleton<IWorkflowRunBindingReader>(runBindingReader);
            builder.Services.AddSingleton<ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>(resumeDispatchService);
            builder.Services.AddSingleton<ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>(signalDispatchService);
            builder.Services.AddSingleton<ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>>(stopDispatchService);
            builder.Services.AddSingleton<IOptions<ScopeWorkflowCapabilityOptions>>(
                Options.Create(new ScopeWorkflowCapabilityOptions
                {
                    ServiceAppId = "default",
                    ServiceNamespace = "default",
                }));

            var app = builder.Build();
            app.MapScopeServiceEndpoints();
            await app.StartAsync();

            var addressFeature = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Server addresses are unavailable.");
            var client = new HttpClient
            {
                BaseAddress = new Uri(addressFeature.Addresses.Single()),
            };

            return new ScopeServiceEndpointTestHost(
                app,
                client,
                commandPort,
                queryPort,
                invocationPort,
                lifecycleQueryPort,
                runBindingReader,
                resumeDispatchService,
                signalDispatchService,
                stopDispatchService);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class RecordingServiceGovernanceCommandPort : IServiceGovernanceCommandPort
    {
        public CreateServiceBindingCommand? CreateBindingCommand { get; private set; }

        public UpdateServiceBindingCommand? UpdateBindingCommand { get; private set; }

        public RetireServiceBindingCommand? RetireBindingCommand { get; private set; }

        public Task<ServiceCommandAcceptedReceipt> CreateBindingAsync(CreateServiceBindingCommand command, CancellationToken ct = default)
        {
            CreateBindingCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("binding-actor", "cmd-create-binding", "corr-create-binding"));
        }

        public Task<ServiceCommandAcceptedReceipt> UpdateBindingAsync(UpdateServiceBindingCommand command, CancellationToken ct = default)
        {
            UpdateBindingCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("binding-actor", "cmd-update-binding", "corr-update-binding"));
        }

        public Task<ServiceCommandAcceptedReceipt> RetireBindingAsync(RetireServiceBindingCommand command, CancellationToken ct = default)
        {
            RetireBindingCommand = command;
            return Task.FromResult(new ServiceCommandAcceptedReceipt("binding-actor", "cmd-retire-binding", "corr-retire-binding"));
        }

        public Task<ServiceCommandAcceptedReceipt> CreateEndpointCatalogAsync(CreateServiceEndpointCatalogCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> UpdateEndpointCatalogAsync(UpdateServiceEndpointCatalogCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> CreatePolicyAsync(CreateServicePolicyCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> UpdatePolicyAsync(UpdateServicePolicyCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceCommandAcceptedReceipt> RetirePolicyAsync(RetireServicePolicyCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingServiceGovernanceQueryPort : IServiceGovernanceQueryPort
    {
        public ServiceIdentity? LastBindingsIdentity { get; private set; }

        public ServiceBindingCatalogSnapshot? BindingsResult { get; set; }

        public Task<ServiceBindingCatalogSnapshot?> GetBindingsAsync(ServiceIdentity identity, CancellationToken ct = default)
        {
            LastBindingsIdentity = identity;
            return Task.FromResult(BindingsResult);
        }

        public Task<ServiceEndpointCatalogSnapshot?> GetEndpointCatalogAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServicePolicyCatalogSnapshot?> GetPoliciesAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingServiceInvocationPort : IServiceInvocationPort
    {
        public ServiceInvocationRequest? LastRequest { get; private set; }

        public Task<ServiceInvocationAcceptedReceipt> InvokeAsync(ServiceInvocationRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new ServiceInvocationAcceptedReceipt
            {
                DeploymentId = "dep-1",
                TargetActorId = "actor-1",
                CommandId = "cmd-1",
                CorrelationId = "corr-1",
            });
        }
    }

    private sealed class RecordingServiceLifecycleQueryPort : IServiceLifecycleQueryPort
    {
        public ServiceCatalogSnapshot? Service { get; set; }

        public ServiceDeploymentCatalogSnapshot? Deployments { get; set; }

        public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(Service);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(Deployments);
    }

    private sealed class FakeWorkflowRunBindingReader : IWorkflowRunBindingReader
    {
        public Dictionary<string, IReadOnlyList<WorkflowActorBinding>> BindingsByRunId { get; } =
            new(StringComparer.Ordinal);

        public Task<IReadOnlyList<WorkflowActorBinding>> ListByRunIdAsync(
            string runId,
            int take = 20,
            CancellationToken ct = default)
        {
            BindingsByRunId.TryGetValue(runId, out var bindings);
            return Task.FromResult<IReadOnlyList<WorkflowActorBinding>>(bindings ?? []);
        }
    }

    private sealed class RecordingResumeDispatchService
        : ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
    {
        public WorkflowResumeCommand? LastCommand { get; private set; }

        public Task<CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>> DispatchAsync(
            WorkflowResumeCommand command,
            CancellationToken ct = default)
        {
            LastCommand = command;
            return Task.FromResult(CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt(command.ActorId, command.RunId, "cmd-resume", "corr-resume")));
        }
    }

    private sealed class RecordingSignalDispatchService
        : ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
    {
        public WorkflowSignalCommand? LastCommand { get; private set; }

        public Task<CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>> DispatchAsync(
            WorkflowSignalCommand command,
            CancellationToken ct = default)
        {
            LastCommand = command;
            return Task.FromResult(CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt(command.ActorId, command.RunId, "cmd-signal", "corr-signal")));
        }
    }

    private sealed class RecordingStopDispatchService
        : ICommandDispatchService<WorkflowStopCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>
    {
        public WorkflowStopCommand? LastCommand { get; private set; }

        public Task<CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>> DispatchAsync(
            WorkflowStopCommand command,
            CancellationToken ct = default)
        {
            LastCommand = command;
            return Task.FromResult(CommandDispatchResult<WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>.Success(
                new WorkflowRunControlAcceptedReceipt(command.ActorId, command.RunId, "cmd-stop", "corr-stop")));
        }
    }
}
