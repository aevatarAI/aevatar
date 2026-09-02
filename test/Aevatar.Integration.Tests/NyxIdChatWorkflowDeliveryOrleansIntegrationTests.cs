using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.AevatarInvocation;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Implementations.Orleans.DependencyInjection;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Grains;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.Tests.Shared;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;

namespace Aevatar.Integration.Tests;

public sealed class NyxIdChatWorkflowDeliveryOrleansIntegrationTests
{
    [Theory]
    [InlineData(WorkflowDeliveryInvocationKind.Team, ActivationCheckingServiceInvocationDispatcher.RunId)]
    [InlineData(WorkflowDeliveryInvocationKind.Direct, ActivationCheckingWorkflowDispatchService.RunId)]
    public async Task WorkflowDispatch_FromActorTurn_ShouldPreserveActivationSchedulerAfterReservation(
        WorkflowDeliveryInvocationKind invocationKind,
        string expectedRunId)
    {
        var probeActorId = $"workflow-delivery-probe-{Guid.NewGuid():N}";
        var childActorId = $"workflow-delivery-child-{Guid.NewGuid():N}";
        var reservation = new AsynchronousReservationPort();
        var observer = new WorkflowDeliveryDispatchObserver(probeActorId, childActorId);
        var host = await StartSiloHostAsync(reservation, observer);

        try
        {
            var probe = host.Services.GetRequiredService<IGrainFactory>()
                .GetGrain<IRuntimeActorGrain>(probeActorId);
            var child = host.Services.GetRequiredService<IGrainFactory>()
                .GetGrain<IRuntimeActorGrain>(childActorId);
            (await probe.InitializeAgentByKindAsync(WorkflowDeliverySchedulerProbeGAgent.Kind))
                .Should().BeTrue();
            (await child.InitializeAgentByKindAsync(WorkflowDeliveryChildGAgent.Kind))
                .Should().BeTrue();

            var invocation = probe.HandleEnvelopeAsync(CreateInvocationEnvelope(probeActorId, invocationKind));
            await reservation.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            reservation.Release();

            await invocation;

            observer.Error.Should().BeNull();
            observer.Result.Should().NotBeNull();
            observer.Result!.ErrorCode.Should().BeEmpty();
            observer.Result.RunId.Should().Be(expectedRunId);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    private static Task<IHost> StartSiloHostAsync(
        AsynchronousReservationPort reservation,
        WorkflowDeliveryDispatchObserver observer) =>
        SharedOrleansPortAllocator.StartHostAsync(ports => Host.CreateDefaultBuilder()
            .UseOrleans(siloBuilder =>
            {
                siloBuilder.UseLocalhostClustering(
                    ports.SiloPort,
                    ports.GatewayPort,
                    serviceId: $"aevatar-workflow-delivery-service-{Guid.NewGuid():N}",
                    clusterId: $"aevatar-workflow-delivery-cluster-{Guid.NewGuid():N}");
                siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
                {
                    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendInMemory;
                    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendInMemory;
                });
                siloBuilder.ConfigureServices(services =>
                    services.AddAevatarAgentKindRegistry(builder => builder
                        .Register<WorkflowDeliverySchedulerProbeGAgent>()
                        .Register<WorkflowDeliveryChildGAgent>()));
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(observer);
                services.AddSingleton(reservation);
                services.AddSingleton<IWorkflowRunBackgroundDeliveryRegistrationPort>(reservation);
                services.AddSingleton<IActorDispatchPort, UnusedActorDispatchPort>();
                services.AddSingleton<IGAgentActorRegistryQueryPort, UnusedActorRegistryQueryPort>();
                services.AddSingleton<ITeamEntryMemberResolver, WorkflowTeamEntryMemberResolver>();
                services.AddSingleton<IMemberPublishedServiceResolver, UnusedMemberPublishedServiceResolver>();
                services.AddSingleton<IStaticGAgentStreamInvocationPort<Aevatar.AGUI.Contracts.AGUIEvent>, UnusedStaticInvocationPort>();
                services.AddSingleton<ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>, ActivationCheckingWorkflowDispatchService>();
                services.AddSingleton<IServiceInvocationResolutionPort, WorkflowServiceInvocationResolutionPort>();
                services.AddSingleton<IServiceInvocationDispatcher, ActivationCheckingServiceInvocationDispatcher>();
                services.AddSingleton<IInvokeAdmissionAuthorizer, AllowInvokeAdmissionAuthorizer>();
                services.AddSingleton<IServiceRunQueryPort, UnusedServiceRunQueryPort>();
                services.AddSingleton<IGAgentRunTerminalQueryPort, UnusedTerminalQueryPort>();
                services.AddSingleton<IWorkflowExecutionQueryApplicationService, UnusedWorkflowQueryService>();
                services.AddSingleton<AevatarInvocationDispatcher>();
            })
            .Build());

    private static byte[] CreateInvocationEnvelope(
        string probeActorId,
        WorkflowDeliveryInvocationKind invocationKind) =>
        new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(new WorkflowDeliverySchedulerProbeRequested
            {
                InvocationKind = invocationKind,
            }),
            Route = EnvelopeRouteSemantics.CreateDirect("test", probeActorId),
            Runtime = new EnvelopeRuntime
            {
                Dispatch = new EnvelopeDispatchControl { PropagateFailure = true },
            },
        }.ToByteArray();

    [GAgent(Kind)]
    public sealed class WorkflowDeliverySchedulerProbeGAgent : GAgentBase<Empty>
    {
        public const string Kind = "tests.workflow-delivery-scheduler-probe";
        private readonly AevatarInvocationDispatcher _dispatcher;
        private readonly WorkflowDeliveryDispatchObserver _observer;

        public WorkflowDeliverySchedulerProbeGAgent(
            AevatarInvocationDispatcher dispatcher,
            WorkflowDeliveryDispatchObserver observer)
        {
            _dispatcher = dispatcher;
            _observer = observer;
        }

        [EventHandler]
        public async Task HandleAsync(WorkflowDeliverySchedulerProbeRequested request)
        {
            using var scope = AgentToolContextScope.Push(CreateToolContext());
            try
            {
                _observer.Result = request.InvocationKind switch
                {
                    WorkflowDeliveryInvocationKind.Team =>
                        await _dispatcher.InvokeTeamForChatRunAsync(
                            null,
                            """
                            {
                              "team_id": "team-alpha",
                              "endpoint_id": "chat",
                              "payload": { "prompt": "run workflow" },
                              "wait": "ack"
                            }
                            """),
                    WorkflowDeliveryInvocationKind.Direct =>
                        await _dispatcher.StartWorkflowForChatRunAsync(
                            null,
                            """
                            {
                              "workflow_id": "workflow-alpha",
                              "inputs": { "prompt": "run workflow" },
                              "wait": "ack"
                            }
                            """),
                    _ => throw new ArgumentOutOfRangeException(nameof(request.InvocationKind)),
                };
            }
            catch (Exception ex)
            {
                _observer.Error = ex;
            }
        }

        private static AgentToolExecutionContext CreateToolContext() =>
            new(
                new AgentToolRequestIdentity("request-alpha", "command-alpha"),
                new AgentToolCredentials("access-token-alpha", null, null),
                new AgentToolCallerContext("scope-alpha", "owner-alpha", "response-alpha"),
                new AgentToolChannelContext(
                    "lark",
                    "sender-alpha",
                    "scope-alpha",
                    "reply-alpha",
                    "message-alpha",
                    WorkflowResultDeliveryCredential: new ChannelWorkflowResultDeliveryCredential
                    {
                        SecretReference = new()
                        {
                            Ref = "secret-alpha",
                            Purpose = "channel.workflow-result-delivery-agent-key",
                            OwnerScopeKey = "scope-alpha",
                        },
                        SubjectId = "subject-alpha",
                    },
                    BotRegistrationId: "bot-alpha"),
                AgentToolSenderBindingContext.Empty,
                LLMRequestRoutingContext.Empty,
                AgentToolConnectedServicesContext.Empty,
                AgentSkillRecoveryContext.Empty,
                new Dictionary<string, string>());
    }

    [GAgent(Kind)]
    public sealed class WorkflowDeliveryChildGAgent : GAgentBase<Empty>
    {
        public const string Kind = "tests.workflow-delivery-child";
    }

    public sealed class WorkflowDeliveryDispatchObserver(string parentActorId, string childActorId)
    {
        public string ParentActorId { get; } = parentActorId;
        public string ChildActorId { get; } = childActorId;
        public ChatRunToolCompletionRequest? Result { get; set; }
        public Exception? Error { get; set; }
    }

    public sealed class AsynchronousReservationPort : IWorkflowRunBackgroundDeliveryRegistrationPort
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public async Task<WorkflowRunBackgroundDeliveryReservationReceipt> ReserveAsync(
            WorkflowRunBackgroundDeliveryReservation reservation,
            CancellationToken ct = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            return new WorkflowRunBackgroundDeliveryReservationReceipt(
                "delivery-actor-alpha",
                reservation.DeliveryId,
                reservation.ExpectedWorkflowCommandId);
        }

        public Task<WorkflowRunBackgroundDeliveryReceipt> RegisterAsync(
            WorkflowRunBackgroundDeliveryReservationReceipt reservationReceipt,
            WorkflowRunBackgroundDeliveryRegistration registration,
            CancellationToken ct = default) =>
            Task.FromResult(new WorkflowRunBackgroundDeliveryReceipt
            {
                DeliveryActorId = reservationReceipt.DeliveryActorId,
                WorkflowActorId = registration.WorkflowActorId,
                WorkflowRunId = registration.WorkflowRunId,
                WorkflowCommandId = registration.WorkflowCommandId,
                WorkflowCorrelationId = registration.WorkflowCorrelationId,
                StreamTopic = registration.StreamTopic,
                ChannelPlatform = registration.ChannelPlatform,
                ReplyMessageId = registration.ReplyMessageId,
                PlatformMessageId = registration.PlatformMessageId,
                RegistrationScopeId = registration.RegistrationScopeId,
            });

        public Task AbandonAsync(
            WorkflowRunBackgroundDeliveryReservationReceipt reservationReceipt,
            string reason,
            CancellationToken ct = default) => Task.CompletedTask;

        public void Release() => _release.TrySetResult();
    }

    private sealed class WorkflowTeamEntryMemberResolver : ITeamEntryMemberResolver
    {
        public Task<TeamEntryMemberResolution> ResolveAsync(
            string scopeId,
            string teamId,
            string endpointId,
            CancellationToken ct = default) =>
            Task.FromResult(new TeamEntryMemberResolution(
                scopeId,
                teamId,
                "member-alpha",
                "service-alpha"));
    }

    private sealed class WorkflowServiceInvocationResolutionPort : IServiceInvocationResolutionPort
    {
        public Task<bool> HasServiceAsync(ServiceIdentity identity, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<ServiceInvocationResolvedTarget> ResolveAsync(
            ServiceInvocationRequest request,
            CancellationToken ct = default)
        {
            var endpoint = new ServiceEndpointDescriptor
            {
                EndpointId = "chat",
                DisplayName = "chat",
                Kind = ServiceEndpointKind.Chat,
                RequestTypeUrl = Any.Pack(new ChatRequestEvent()).TypeUrl,
                ResponseTypeUrl = Any.Pack(new Aevatar.AGUI.Contracts.AGUIEvent()).TypeUrl,
            };
            var artifact = new PreparedServiceRevisionArtifact
            {
                Identity = request.Identity.Clone(),
                RevisionId = "revision-alpha",
                ImplementationKind = ServiceImplementationKind.Workflow,
                ArtifactHash = "artifact-alpha",
                DeploymentPlan = new ServiceDeploymentPlan
                {
                    WorkflowPlan = new WorkflowServiceDeploymentPlan
                    {
                        WorkflowName = "workflow-alpha",
                        DefinitionActorId = "workflow-definition-alpha",
                    },
                },
            };
            artifact.Endpoints.Add(endpoint);
            return Task.FromResult(new ServiceInvocationResolvedTarget(
                new ServiceInvocationResolvedService(
                    "scope-alpha:aevatar-service:default:service-alpha",
                    "revision-alpha",
                    "deployment-alpha",
                    "workflow-definition-alpha",
                    "Active",
                    []),
                artifact,
                endpoint));
        }
    }

    private sealed class ActivationCheckingServiceInvocationDispatcher(
        IActorRuntime actorRuntime,
        WorkflowDeliveryDispatchObserver observer) : IServiceInvocationDispatcher
    {
        public const string RunId = "workflow-run-alpha";

        public async Task<ServiceInvocationAcceptedReceipt> DispatchAsync(
            ServiceInvocationResolvedTarget target,
            ServiceInvocationRequest request,
            CancellationToken ct = default)
        {
            await actorRuntime.LinkAsync(observer.ParentActorId, observer.ChildActorId, ct);

            return new ServiceInvocationAcceptedReceipt
            {
                RequestId = request.CommandId,
                ServiceKey = target.Service.ServiceKey,
                DeploymentId = target.Service.DeploymentId,
                TargetActorId = "workflow-run-actor-alpha",
                EndpointId = request.EndpointId,
                CommandId = request.CommandId,
                CorrelationId = request.CorrelationId,
                RunId = RunId,
            };
        }
    }

    private sealed class ActivationCheckingWorkflowDispatchService(
        IActorRuntime actorRuntime,
        WorkflowDeliveryDispatchObserver observer)
        : ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>
    {
        public const string RunId = "command-alpha";

        public async Task<CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>> DispatchAsync(
            WorkflowChatRunRequest command,
            CancellationToken ct = default)
        {
            await actorRuntime.LinkAsync(observer.ParentActorId, observer.ChildActorId, ct);
            return CommandDispatchResult<WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>.Success(
                new WorkflowChatRunAcceptedReceipt(
                    "workflow-run-actor-alpha",
                    "workflow-alpha",
                    command.CommandIdSeed!,
                    command.CorrelationIdSeed!));
        }
    }

    private sealed class AllowInvokeAdmissionAuthorizer : IInvokeAdmissionAuthorizer
    {
        public Task AuthorizeAsync(
            string serviceKey,
            string deploymentId,
            PreparedServiceRevisionArtifact artifact,
            ServiceEndpointDescriptor endpoint,
            ServiceInvocationRequest request,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class UnusedActorDispatchPort : IActorDispatchPort
    {
        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class UnusedActorRegistryQueryPort : IGAgentActorRegistryQueryPort
    {
        public Task<GAgentActorRegistrySnapshot> ListActorsAsync(
            string scopeId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedMemberPublishedServiceResolver : IMemberPublishedServiceResolver
    {
        public Task<MemberPublishedServiceResolution> ResolveAsync(
            MemberPublishedServiceResolveRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class UnusedStaticInvocationPort
        : IStaticGAgentStreamInvocationPort<Aevatar.AGUI.Contracts.AGUIEvent>
    {
        public Task<StaticGAgentStreamInvocationResult> InvokeAsync(
            StaticGAgentStreamInvocationRequest request,
            Func<Aevatar.AGUI.Contracts.AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<StaticGAgentStreamAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class UnusedServiceRunQueryPort : IServiceRunQueryPort
    {
        public Task<IReadOnlyList<ServiceRunSnapshot>> ListAsync(ServiceRunQuery query, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ServiceRunSnapshot?> GetByRunIdAsync(
            string scopeId,
            string serviceId,
            string runId,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ServiceRunSnapshot?> GetByCommandIdAsync(
            string scopeId,
            string serviceId,
            string commandId,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class UnusedTerminalQueryPort : IGAgentRunTerminalQueryPort
    {
        public Task<GAgentRunTerminalSnapshot?> GetByCorrelationIdAsync(
            string actorId,
            string correlationId,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<GAgentRunTerminalSnapshot?> GetBySessionIdAsync(
            string actorId,
            string sessionId,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class UnusedWorkflowQueryService : IWorkflowExecutionQueryApplicationService
    {
        public bool WorkflowActorCurrentStateQueryEnabled => false;
        public Task<IReadOnlyList<WorkflowAgentSummary>> ListAgentsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public IReadOnlyList<string> ListWorkflows() => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkflowCatalogItem>> ListWorkflowCatalogAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkflowCatalogItemDetail?> GetWorkflowDetailAsync(string workflowName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkflowCapabilitiesDocument> GetCapabilitiesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(string actorId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(WorkflowActorCurrentStateListQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkflowRunReport?> GetWorkflowRunReportArtifactAsync(string workflowRunId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkflowRunTimelineExportItem>> ListWorkflowRunTimelineExportAsync(string workflowRunId, int take = 200, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkflowRunGraphExportEdge>> ListWorkflowRunGraphExportEdgesAsync(string workflowRunId, int take = 200, WorkflowRunGraphExportQueryOptions? options = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkflowRunGraphExportSubgraph> GetWorkflowRunGraphExportSubgraphAsync(string workflowRunId, int depth = 2, int take = 200, WorkflowRunGraphExportQueryOptions? options = null, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
