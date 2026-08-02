using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.ScopeGAgents;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Application.Services;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Tests.TestSupport;
using Aevatar.AGUI.Contracts;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Application;

public sealed class StaticGAgentStreamInvocationApplicationServiceTests
{
    [Fact]
    public async Task InvokeAsync_ShouldThrow_WhenResolvedServiceIsNotStatic()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var interaction = new RecordingGAgentDraftRunInteractionService();
        var registration = new RecordingServiceRunRegistrationPort();
        var service = await CreateServiceAsync(
            identity,
            CreateArtifact(identity, ServiceImplementationKind.Workflow),
            interaction,
            registration);

        var act = () => service.InvokeAsync(
            NewRequest(identity),
            (_, _) => ValueTask.CompletedTask);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Only static GAgent services support stream invocation*");
        interaction.Requests.Should().BeEmpty();
        registration.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_ShouldRegisterStaticRunWithGAgentCommandId()
    {
        const long issuedAtUnixMs = 1_785_000_000_000;
        var identity = GAgentServiceTestKit.CreateIdentity();
        var toolContext = NewToolContext(issuedAtUnixMs);
        var receipt = new GAgentDraftRunAcceptedReceipt(
            ActorId: "gagent-actor-1",
            DiagnosticClrTypeName: typeof(TestStaticServiceAgent).AssemblyQualifiedName!,
            CommandId: "cmd-gagent-1",
            CorrelationId: "corr-gagent-1");
        var interaction = new RecordingGAgentDraftRunInteractionService
        {
            Receipt = receipt,
        };
        var registration = new RecordingServiceRunRegistrationPort();
        var service = await CreateServiceAsync(
            identity,
            CreateArtifact(identity, ServiceImplementationKind.Static),
            interaction,
            registration);

        StaticGAgentStreamAcceptedReceipt? accepted = null;

        var result = await service.InvokeAsync(
            NewRequest(
                identity,
                input: new StaticGAgentStreamInvocationInput(
                    Prompt: "  hello static  ",
                    PreferredActorId: "  preferred-actor  ",
                    SessionId: "session-1",
                    Headers: new Dictionary<string, string>
                    {
                        ["x-trace"] = "trace-1",
                    },
                    InputParts:
                    [
                        new GAgentDraftRunInputPart
                        {
                            Kind = GAgentDraftRunInputPartKind.Text,
                            Text = "part-1",
                        },
                    ],
                    ToolContext: toolContext,
                    LlmControl: NewLlmControl())),
            (_, _) => ValueTask.CompletedTask,
            (receipt, _) =>
            {
                accepted = receipt;
                return ValueTask.CompletedTask;
            });

        result.Succeeded.Should().BeTrue();
        result.Accepted.Should().BeSameAs(accepted);
        result.CompletionStatus.Should().Be(GAgentDraftRunCompletionStatus.RunFinished);
        result.CompletionObserved.Should().BeTrue();

        registration.Records.Should().ContainSingle();
        var record = registration.Records[0];
        record.ScopeId.Should().Be(identity.TenantId);
        record.ServiceId.Should().Be(identity.ServiceId);
        record.ServiceKey.Should().Be(ServiceKeys.Build(identity));
        record.RunId.Should().Be(receipt.CommandId);
        record.CommandId.Should().Be(receipt.CommandId);
        record.CorrelationId.Should().Be(receipt.CorrelationId);
        record.TargetActorId.Should().Be(receipt.ActorId);
        record.ImplementationKind.Should().Be(ServiceImplementationKind.Static);
        record.Status.Should().Be(ServiceRunStatus.Accepted);
        record.Identity.Should().BeEquivalentTo(identity);
        registration.StatusUpdates.Should().ContainSingle()
            .Which.Should().Be(("service-run-actor", receipt.CommandId, ServiceRunStatus.Completed, string.Empty, string.Empty));

        accepted.Should().NotBeNull();
        accepted!.ServiceReceipt.CommandId.Should().Be(receipt.CommandId);
        accepted.ServiceReceipt.CorrelationId.Should().Be(receipt.CorrelationId);
        accepted.ServiceReceipt.TargetActorId.Should().Be(receipt.ActorId);
        accepted.GAgentReceipt.Should().Be(receipt);

        interaction.Requests.Should().ContainSingle();
        var delegated = interaction.Requests[0];
        delegated.ScopeId.Should().Be(identity.TenantId);
        delegated.AgentKind.Should().Be(GAgentServiceTestKit.TestStaticServiceAgentKind);
        delegated.Prompt.Should().Be("hello static");
        delegated.PreferredActorId.Should().Be("preferred-actor");
        delegated.SessionId.Should().Be("session-1");
        delegated.Headers.Should().Contain("x-trace", "trace-1");
        delegated.UseCorrelationIdAsFallbackSessionId.Should().BeFalse();
        delegated.InputParts.Should().ContainSingle()
            .Which.Text.Should().Be("part-1");
        delegated.ToolContext.Should().BeEquivalentTo(toolContext);
        delegated.ToolContext.Request.IssuedAtUnixMs.Should().Be(issuedAtUnixMs);
        delegated.LlmControl.Should().BeEquivalentTo(NewLlmControl());
    }

    [Fact]
    public async Task InvokeAsync_ShouldForwardAGUIFramesThroughEmitCallback()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var frame = new AGUIEvent
        {
            TextMessageContent = new Aevatar.AGUI.Contracts.TextMessageContentEvent
            {
                MessageId = "msg-1",
                Delta = "hello",
            },
        };
        var interaction = new RecordingGAgentDraftRunInteractionService
        {
            Frames = [frame],
        };
        var service = await CreateServiceAsync(
            identity,
            CreateArtifact(identity, ServiceImplementationKind.Static),
            interaction,
            new RecordingServiceRunRegistrationPort());

        var emitted = new List<AGUIEvent>();

        var result = await service.InvokeAsync(
            NewRequest(identity),
            (aguiEvent, _) =>
            {
                emitted.Add(aguiEvent);
                return ValueTask.CompletedTask;
            });

        result.Succeeded.Should().BeTrue();
        emitted.Should().ContainSingle()
            .Which.TextMessageContent.Delta.Should().Be("hello");
    }

    [Fact]
    public async Task InvokeAsync_ShouldPersistTerminalOutput_WhenRunFinishedCarriesResult()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var interaction = new RecordingGAgentDraftRunInteractionService
        {
            Frames =
            [
                new AGUIEvent
                {
                    RunFinished = new RunFinishedEvent
                    {
                        Result = Any.Pack(new GAgentDraftRunResultPayload
                        {
                            Output = "static result",
                        }),
                    },
                },
            ],
        };
        var registration = new RecordingServiceRunRegistrationPort();
        var service = await CreateServiceAsync(
            identity,
            CreateArtifact(identity, ServiceImplementationKind.Static),
            interaction,
            registration);

        var result = await service.InvokeAsync(
            NewRequest(identity),
            (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeTrue();
        registration.StatusUpdates.Should().ContainSingle()
            .Which.Should().Be(("service-run-actor", "cmd-default", ServiceRunStatus.Completed, "static result", string.Empty));
    }

    [Fact]
    public async Task InvokeAsync_ShouldPersistTerminalError_WhenRunErrorObserved()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var interaction = new RecordingGAgentDraftRunInteractionService
        {
            Completion = GAgentDraftRunCompletionStatus.Failed,
            Frames =
            [
                new AGUIEvent
                {
                    RunError = new RunErrorEvent
                    {
                        Message = "static failed",
                    },
                },
            ],
        };
        var registration = new RecordingServiceRunRegistrationPort();
        var service = await CreateServiceAsync(
            identity,
            CreateArtifact(identity, ServiceImplementationKind.Static),
            interaction,
            registration);

        var result = await service.InvokeAsync(
            NewRequest(identity),
            (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeTrue();
        registration.StatusUpdates.Should().ContainSingle()
            .Which.Should().Be(("service-run-actor", "cmd-default", ServiceRunStatus.Failed, string.Empty, "static failed"));
    }

    [Fact]
    public async Task InvokeAsync_ShouldPersistOutcomeUncertain_WhenDurableCompletionHasNoLiveFrame()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var interaction = new RecordingGAgentDraftRunInteractionService
        {
            Completion = GAgentDraftRunCompletionStatus.OutcomeUncertain,
        };
        var registration = new RecordingServiceRunRegistrationPort();
        var service = await CreateServiceAsync(
            identity,
            CreateArtifact(identity, ServiceImplementationKind.Static),
            interaction,
            registration);

        var result = await service.InvokeAsync(
            NewRequest(identity),
            (_, _) => ValueTask.CompletedTask);

        result.CompletionStatus.Should().Be(GAgentDraftRunCompletionStatus.OutcomeUncertain);
        registration.StatusUpdates.Should().ContainSingle()
            .Which.Should().Be(("service-run-actor", "cmd-default", ServiceRunStatus.OutcomeUncertain, string.Empty, string.Empty));
    }

    [Fact]
    public async Task InvokeAsync_ShouldNotOverwriteOutcomeUncertain_WithLaterSyntheticTerminalFrames()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var interaction = new RecordingGAgentDraftRunInteractionService
        {
            Completion = GAgentDraftRunCompletionStatus.OutcomeUncertain,
            Frames =
            [
                new AGUIEvent
                {
                    RunError = new RunErrorEvent
                    {
                        Code = GAgentRunFailureCodes.OutcomeUncertain,
                        Message = "The interrupted session may have produced side effects.",
                    },
                },
                new AGUIEvent { RunError = new RunErrorEvent { Message = "synthetic failure" } },
                new AGUIEvent { RunFinished = new RunFinishedEvent() },
            ],
        };
        var registration = new RecordingServiceRunRegistrationPort();
        var service = await CreateServiceAsync(
            identity,
            CreateArtifact(identity, ServiceImplementationKind.Static),
            interaction,
            registration);

        await service.InvokeAsync(
            NewRequest(identity),
            (_, _) => ValueTask.CompletedTask);

        registration.StatusUpdates.Should().ContainSingle()
            .Which.Should().Be((
                "service-run-actor",
                "cmd-default",
                ServiceRunStatus.OutcomeUncertain,
                string.Empty,
                "The interrupted session may have produced side effects."));
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturnStartError_WhenGAgentInteractionFailsBeforeAccepted()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var interaction = new RecordingGAgentDraftRunInteractionService
        {
            Failure = GAgentDraftRunStartError.UnknownAgentKind,
        };
        var registration = new RecordingServiceRunRegistrationPort();
        var service = await CreateServiceAsync(
            identity,
            CreateArtifact(identity, ServiceImplementationKind.Static),
            interaction,
            registration);

        var result = await service.InvokeAsync(
            NewRequest(identity),
            (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeFalse();
        result.Accepted.Should().BeNull();
        result.StartError.Should().Be(GAgentDraftRunStartError.UnknownAgentKind);
        registration.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_ShouldThrow_WhenResolvedEndpointIsNotChat()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var interaction = new RecordingGAgentDraftRunInteractionService();
        var registration = new RecordingServiceRunRegistrationPort();
        var service = await CreateServiceAsync(
            identity,
            CreateArtifact(
                identity,
                ServiceImplementationKind.Static,
                endpointKind: ServiceEndpointKind.Command,
                requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl),
            interaction,
            registration);

        var act = () => service.InvokeAsync(
            NewRequest(identity),
            (_, _) => ValueTask.CompletedTask);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Only chat endpoints support static GAgent stream invocation*");
        interaction.Requests.Should().BeEmpty();
        registration.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_ShouldThrow_WhenEndpointRequestTypeDoesNotMatchChatPayload()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var interaction = new RecordingGAgentDraftRunInteractionService();
        var registration = new RecordingServiceRunRegistrationPort();
        var service = await CreateServiceAsync(
            identity,
            CreateArtifact(
                identity,
                ServiceImplementationKind.Static,
                requestTypeUrl: "type.googleapis.com/test.other"),
            interaction,
            registration);

        var act = () => service.InvokeAsync(
            NewRequest(identity),
            (_, _) => ValueTask.CompletedTask);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*expects payload 'type.googleapis.com/test.other'*");
        interaction.Requests.Should().BeEmpty();
        registration.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_ShouldUseAgentKind_WhenStaticPlanHasNoActorType()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var interaction = new RecordingGAgentDraftRunInteractionService();
        var registration = new RecordingServiceRunRegistrationPort();
        var artifact = CreateArtifact(identity, ServiceImplementationKind.Static);
        artifact.DeploymentPlan.StaticPlan!.ActorTypeName = " ";
        var service = await CreateServiceAsync(
            identity,
            artifact,
            interaction,
            registration);

        var result = await service.InvokeAsync(
            NewRequest(identity),
            (_, _) => ValueTask.CompletedTask);

        result.Succeeded.Should().BeTrue();
        interaction.Requests.Should().ContainSingle()
            .Which.AgentKind.Should().Be(GAgentServiceTestKit.TestStaticServiceAgentKind);
    }

    [Fact]
    public async Task InvokeAsync_ShouldThrow_WhenStaticPlanHasNoAgentKindOrActorType()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var interaction = new RecordingGAgentDraftRunInteractionService();
        var registration = new RecordingServiceRunRegistrationPort();
        var artifact = CreateArtifact(identity, ServiceImplementationKind.Static);
        artifact.DeploymentPlan.StaticPlan!.AgentKind = " ";
        artifact.DeploymentPlan.StaticPlan!.ActorTypeName = " ";
        var service = await CreateServiceAsync(
            identity,
            artifact,
            interaction,
            registration);

        var act = () => service.InvokeAsync(
            NewRequest(identity),
            (_, _) => ValueTask.CompletedTask);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Static GAgent service has no agent kind configured*");
        interaction.Requests.Should().BeEmpty();
        registration.Records.Should().BeEmpty();
    }

    private static async Task<StaticGAgentStreamInvocationApplicationService> CreateServiceAsync(
        ServiceIdentity identity,
        PreparedServiceRevisionArtifact artifact,
        RecordingGAgentDraftRunInteractionService interaction,
        RecordingServiceRunRegistrationPort registration)
    {
        var revisionCatalog = new FakeServiceRevisionCatalogQueryReader();
        await revisionCatalog.UpsertRevisionAsync(ServiceKeys.Build(identity), "r1", artifact);

        var resolutionService = new ServiceInvocationResolutionService(
            new CatalogQueryReader(identity),
            new InvocationCatalogQueryReader(identity),
            revisionCatalog);

        return new StaticGAgentStreamInvocationApplicationService(
            resolutionService,
            new NoOpInvokeAdmissionAuthorizer(),
            registration,
            interaction);
    }

    private static StaticGAgentStreamInvocationRequest NewRequest(
        ServiceIdentity identity,
        StaticGAgentStreamInvocationInput? input = null) =>
        new(
            identity.Clone(),
            "chat",
            input ?? new StaticGAgentStreamInvocationInput("hello"));

    private static AgentToolExecutionContext NewToolContext(long issuedAtUnixMs) =>
        new(
            new AgentToolRequestIdentity("request-1", "call-1", null, issuedAtUnixMs),
            new AgentToolCredentials("access-token", "org-token", "sender-token"),
            new AgentToolCallerContext("scope-a", "owner-a", "response-1"),
            new AgentToolChannelContext("telegram", "sender-1", "registration-scope-1", "message-1", "platform-message-1"),
            new AgentToolSenderBindingContext("binding-1"),
            new LLMRequestRoutingContext("model-1", "route-1", 3, "remember"),
            new AgentToolConnectedServicesContext("connected"),
            AgentSkillRecoveryContext.Empty,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["external"] = "value",
            });

    private static LLMControlContext NewLlmControl() =>
        new("access-token", "org-token", "sender-token", "model-1", "route-1", 3, "remember");

    private static PreparedServiceRevisionArtifact CreateArtifact(
        ServiceIdentity identity,
        ServiceImplementationKind implementationKind,
        ServiceEndpointKind endpointKind = ServiceEndpointKind.Chat,
        string? requestTypeUrl = null)
    {
        var endpoint = GAgentServiceTestKit.CreateEndpointDescriptor(
            endpointId: "chat",
            kind: endpointKind,
            requestTypeUrl: requestTypeUrl ?? Any.Pack(new ChatRequestEvent()).TypeUrl);

        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(identity, "r1", endpoint);
        artifact.ImplementationKind = implementationKind;
        if (implementationKind == ServiceImplementationKind.Workflow)
        {
            artifact.DeploymentPlan = new ServiceDeploymentPlan
            {
                WorkflowPlan = new WorkflowServiceDeploymentPlan
                {
                    WorkflowName = "wf",
                    WorkflowYaml = "name: wf",
                    DefinitionActorId = "workflow-definition-1",
                },
            };
        }

        return artifact;
    }

    private sealed class CatalogQueryReader(ServiceIdentity identity) : IServiceCatalogQueryReader
    {
        public Task<ServiceCatalogSnapshot?> GetAsync(ServiceIdentity requestedIdentity, CancellationToken ct = default) =>
            Task.FromResult<ServiceCatalogSnapshot?>(new ServiceCatalogSnapshot(
                ServiceKeys.Build(identity),
                identity.TenantId,
                identity.AppId,
                identity.Namespace,
                identity.ServiceId,
                "Static Service",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                ServiceDeploymentStatus.Active.ToString(),
                [],
                [],
                DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryAllAsync(int take = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);

        public Task<IReadOnlyList<ServiceCatalogSnapshot>> QueryByScopeAsync(
            string tenantId,
            string appId,
            string @namespace,
            int take = 200,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCatalogSnapshot>>([]);
    }

    private sealed class InvocationCatalogQueryReader(ServiceIdentity identity) : IServiceInvocationCatalogQueryReader
    {
        public Task<ServiceInvocationCatalogSnapshot?> GetAsync(ServiceIdentity requestedIdentity, CancellationToken ct = default) =>
            Task.FromResult<ServiceInvocationCatalogSnapshot?>(new ServiceInvocationCatalogSnapshot(
                ServiceKeys.Build(identity),
                [
                    new ServiceInvokeReadinessSnapshot(
                        ServiceKeys.Build(identity),
                        "chat",
                        ServiceInvokeReadinessStatus.Ready,
                        ServiceInvokeUnavailableReason.Unspecified,
                        "r1",
                        "dep-1",
                        "primary-actor-1",
                        DateTimeOffset.Parse("2026-06-05T00:00:00+00:00"),
                        1,
                        $"{ServiceKeys.Build(identity)}:invocation-catalog:1",
                        1,
                        1,
                        1),
                ],
                DateTimeOffset.Parse("2026-06-05T00:00:00+00:00"),
                1,
                $"{ServiceKeys.Build(identity)}:invocation-catalog:1",
                1,
                1,
                1));
    }

    private sealed class NoOpInvokeAdmissionAuthorizer : IInvokeAdmissionAuthorizer
    {
        public Task AuthorizeAsync(
            string serviceKey,
            string deploymentId,
            PreparedServiceRevisionArtifact artifact,
            ServiceEndpointDescriptor endpoint,
            ServiceInvocationRequest request,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingServiceRunRegistrationPort : IServiceRunRegistrationPort
    {
        public List<ServiceRunRecord> Records { get; } = [];
        public List<(string RunActorId, string RunId, ServiceRunStatus Status, string LastOutput, string LastError)> StatusUpdates { get; } = [];

        public Task<ServiceRunRegistrationResult> RegisterAsync(
            ServiceRunRecord record,
            CancellationToken ct = default)
        {
            Records.Add(record.Clone());
            return Task.FromResult(new ServiceRunRegistrationResult("service-run-actor", record.RunId));
        }

        public Task UpdateStatusAsync(
            string runActorId,
            string runId,
            ServiceRunStatus status,
            CancellationToken ct = default) =>
            UpdateStatusAsync(runActorId, runId, status, null, null, ct);

        public Task UpdateStatusAsync(
            string runActorId,
            string runId,
            ServiceRunStatus status,
            string? lastOutput,
            string? lastError,
            CancellationToken ct = default)
        {
            StatusUpdates.Add((runActorId, runId, status, lastOutput ?? string.Empty, lastError ?? string.Empty));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingGAgentDraftRunInteractionService
        : IGAgentDraftRunInteractionPort
    {
        public List<GAgentDraftRunInteractionRequest> Requests { get; } = [];

        public IReadOnlyList<AGUIEvent> Frames { get; init; } = [];

        public GAgentDraftRunAcceptedReceipt Receipt { get; init; } = new(
            "gagent-actor-default",
            typeof(TestStaticServiceAgent).AssemblyQualifiedName!,
            "cmd-default",
            "corr-default");

        public GAgentDraftRunStartError? Failure { get; init; }

        public GAgentDraftRunCompletionStatus Completion { get; init; } = GAgentDraftRunCompletionStatus.RunFinished;

        public async Task<CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>> ExecuteAsync(
            GAgentDraftRunInteractionRequest request,
            Func<AGUIEvent, CancellationToken, ValueTask> emitAsync,
            Func<GAgentDraftRunAcceptedReceipt, CancellationToken, ValueTask>? onAcceptedAsync = null,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            if (Failure.HasValue)
                return CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>
                    .Failure(Failure.Value);

            if (onAcceptedAsync != null)
                await onAcceptedAsync(Receipt, ct);

            foreach (var frame in Frames)
                await emitAsync(frame, ct);

            return CommandInteractionResult<GAgentDraftRunAcceptedReceipt, GAgentDraftRunStartError, GAgentDraftRunCompletionStatus>
                .Success(
                    Receipt,
                    new CommandInteractionFinalizeResult<GAgentDraftRunCompletionStatus>(
                        Completion,
                        true));
        }
    }
}
