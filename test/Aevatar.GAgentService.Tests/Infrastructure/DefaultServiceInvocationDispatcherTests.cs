using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Infrastructure.Dispatch;
using Aevatar.GAgentService.Tests.TestSupport;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.ExternalCapabilities;
using Aevatar.Workflow.Application.Abstractions.Runs;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Infrastructure;

public sealed class DefaultServiceInvocationDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_ShouldDispatchStaticEnvelope()
    {
        var dispatchPort = new RecordingDispatchPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            dispatchPort,
            new RecordingScriptRuntimeCommandPort(),
            new RecordingWorkflowRunActorPort(),
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(ServiceImplementationKind.Static, endpointId: "run");
        var request = new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "run",
            CommandId = "cmd-1",
            CorrelationId = "corr-1",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        };

        var receipt = await dispatcher.DispatchAsync(target, request);

        receipt.TargetActorId.Should().Be("primary-actor");
        receipt.CommandId.Should().Be("cmd-1");
        dispatchPort.Calls.Should().ContainSingle();
        dispatchPort.Calls[0].actorId.Should().Be("primary-actor");
        dispatchPort.Calls[0].envelope.Route.GetTargetActorId().Should().Be("primary-actor");
    }

    [Fact]
    public async Task DispatchAsync_ShouldAttachRegisteredServiceRunTargetToStaticChat()
    {
        var registry = new RecordingServiceRunRegistrationPort
        {
            RegistrationResult = new ServiceRunRegistrationResult("service-run:tenant:svc:run-static", "run-static"),
        };
        var dispatchPort = new RecordingDispatchPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            dispatchPort,
            new RecordingScriptRuntimeCommandPort(),
            new RecordingWorkflowRunActorPort(),
            registry,
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Static,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        var request = new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            CommandId = "cmd-static",
            CorrelationId = "corr-static",
            RequestedRunId = "run-static",
            Payload = Any.Pack(new ChatRequestEvent { Prompt = "hello" }),
            ServiceRunCompletionNotificationTarget = new ServiceRunCompletionNotificationTarget
            {
                ActorId = "work-order:tenant:wo-1",
                DeliveryId = "work-order-terminal-1",
                ExpiresAtUnixMs = long.MaxValue,
            },
        };

        await dispatcher.DispatchAsync(target, request);

        var chatRequest = dispatchPort.Calls.Should().ContainSingle().Subject.envelope.Payload!
            .Unpack<ChatRequestEvent>();
        chatRequest.SessionId.Should().Be("run-static");
        chatRequest.RunContext.Should().NotBeNull();
        chatRequest.RunContext.RunId.Should().Be("run-static");
        chatRequest.RunContext.CommandId.Should().Be("cmd-static");
        chatRequest.RunContext.CorrelationId.Should().Be("corr-static");
        chatRequest.RunContext.CompletionNotificationActorId.Should()
            .Be("service-run:tenant:svc:run-static");
    }

    [Fact]
    public async Task StaticDispatch_ShouldForwardInternalDeliveryIdAndWorkOrderExpiry()
    {
        var registry = new RecordingServiceRunRegistrationPort
        {
            RegistrationResult = new ServiceRunRegistrationResult("service-run:tenant:svc:run-static", "run-static"),
        };
        var dispatchPort = new RecordingDispatchPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            dispatchPort,
            new RecordingScriptRuntimeCommandPort(),
            new RecordingWorkflowRunActorPort(),
            registry,
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Static,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        const long workOrderExpiry = 1_775_000_000_000;

        await dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            CommandId = "cmd-static",
            CorrelationId = "corr-static",
            RequestedRunId = "run-static",
            Payload = Any.Pack(new ChatRequestEvent { Prompt = "hello" }),
            ServiceRunCompletionNotificationTarget = new ServiceRunCompletionNotificationTarget
            {
                ActorId = "work-order:tenant:wo-1",
                DeliveryId = "work-order-terminal-1",
                ExpiresAtUnixMs = workOrderExpiry,
            },
        });

        var withWorkOrder = dispatchPort.Calls.Should().ContainSingle().Subject.envelope.Payload!
            .Unpack<ChatRequestEvent>();
        withWorkOrder.RunContext.CompletionNotificationDeliveryId.Should()
            .Be("service-run-source:run-static:cmd-static");
        withWorkOrder.RunContext.CompletionNotificationExpiresAtUnixMs.Should().Be(workOrderExpiry);

        dispatchPort.Calls.Clear();
        await dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            CommandId = "cmd-without-target",
            CorrelationId = "corr-without-target",
            RequestedRunId = "run-without-target",
            Payload = Any.Pack(new ChatRequestEvent { Prompt = "hello" }),
        });

        var withoutWorkOrder = dispatchPort.Calls.Should().ContainSingle().Subject.envelope.Payload!
            .Unpack<ChatRequestEvent>();
        withoutWorkOrder.RunContext.CompletionNotificationDeliveryId.Should()
            .Be("service-run-source:run-without-target:cmd-without-target");
        withoutWorkOrder.RunContext.CompletionNotificationExpiresAtUnixMs.Should().Be(0);
    }

    [Fact]
    public async Task DispatchAsync_ShouldDelegateScriptingRun()
    {
        var scriptPort = new RecordingScriptRuntimeCommandPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            scriptPort,
            new RecordingWorkflowRunActorPort(),
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Scripting,
            endpointId: "run",
            requestTypeUrl: Any.Pack(new StringValue()).TypeUrl);
        target.Artifact.DeploymentPlan.ScriptingPlan = new ScriptingServiceDeploymentPlan
        {
            Revision = "rev-1",
            DefinitionActorId = "definition-1",
        };
        var request = new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "run",
            CommandId = "cmd-2",
            CorrelationId = "corr-2",
            RequestedRunId = "run-2",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
            ServiceRunCompletionNotificationTarget = new ServiceRunCompletionNotificationTarget
            {
                ActorId = "work-order:tenant:wo-2",
                DeliveryId = "work-order-terminal-2",
                ExpiresAtUnixMs = long.MaxValue,
            },
        };

        var receipt = await dispatcher.DispatchAsync(target, request);

        receipt.TargetActorId.Should().Be("primary-actor");
        receipt.RunId.Should().Be("run-2");
        scriptPort.Calls.Should().ContainSingle();
        scriptPort.Calls[0].runtimeActorId.Should().Be("primary-actor");
        scriptPort.Calls[0].runId.Should().Be("run-2");
        scriptPort.Calls[0].commandId.Should().Be("cmd-2");
        scriptPort.Calls[0].correlationId.Should().Be("corr-2");
        scriptPort.Calls[0].definitionActorId.Should().Be("definition-1");
        scriptPort.Calls[0].scopeId.Should().Be(GAgentServiceTestKit.CreateIdentity().TenantId);
        scriptPort.Calls[0].completionNotificationActorId.Should().Be("service-run:run-2");
    }

    [Fact]
    public async Task ScriptingDispatch_ShouldForwardDeliveryIdAndExpiry()
    {
        var scriptPort = new RecordingScriptRuntimeCommandPort();
        var registry = new RecordingServiceRunRegistrationPort
        {
            RegistrationResult = new ServiceRunRegistrationResult("service-run:tenant:svc:run-script", "run-script"),
        };
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            scriptPort,
            new RecordingWorkflowRunActorPort(),
            registry,
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Scripting,
            endpointId: "run",
            requestTypeUrl: Any.Pack(new StringValue()).TypeUrl);
        target.Artifact.DeploymentPlan.ScriptingPlan = new ScriptingServiceDeploymentPlan
        {
            Revision = "rev-1",
            DefinitionActorId = "definition-1",
        };
        const long workOrderExpiry = 1_775_000_000_000;

        await dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "run",
            CommandId = "cmd-script",
            CorrelationId = "corr-script",
            RequestedRunId = "run-script",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
            ServiceRunCompletionNotificationTarget = new ServiceRunCompletionNotificationTarget
            {
                ActorId = "work-order:tenant:wo-script",
                DeliveryId = "work-order-terminal-script",
                ExpiresAtUnixMs = workOrderExpiry,
            },
        });

        var withWorkOrder = scriptPort.Calls.Should().ContainSingle().Subject;
        withWorkOrder.completionNotificationDeliveryId.Should()
            .Be("service-run-source:run-script:cmd-script");
        withWorkOrder.completionNotificationExpiresAtUnixMs.Should().Be(workOrderExpiry);

        scriptPort.Calls.Clear();
        await dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "run",
            CommandId = "cmd-script-without-target",
            CorrelationId = "corr-script-without-target",
            RequestedRunId = "run-script-without-target",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        var withoutWorkOrder = scriptPort.Calls.Should().ContainSingle().Subject;
        withoutWorkOrder.completionNotificationDeliveryId.Should()
            .Be("service-run-source:run-script-without-target:cmd-script-without-target");
        withoutWorkOrder.completionNotificationExpiresAtUnixMs.Should().Be(0);
    }

    [Fact]
    public async Task DispatchAsync_ShouldCreateWorkflowRun_AndSendEnvelope()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var dispatchPort = new RecordingDispatchPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            dispatchPort,
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        var capabilityAdmissionPlan = WorkflowCapabilityAdmissionPlanIntegrity.Create(
            "name: wf",
            new Dictionary<string, string> { ["child"] = "name: child" },
            ExternalCapabilityExecutionMode.Durable,
            [],
            []);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            InlineWorkflowYamls =
            {
                ["child"] = "name: child",
            },
            CapabilityAdmissionPlan = capabilityAdmissionPlan,
        };
        var request = new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            CommandId = "cmd-3",
            CorrelationId = "corr-3",
            Payload = Any.Pack(new ChatRequestEvent { Prompt = "hello" }),
        };

        var receipt = await dispatcher.DispatchAsync(target, request);

        receipt.TargetActorId.Should().Be("workflow-run");
        workflowPort.CreateRunCalls.Should().ContainSingle();
        workflowPort.CreateRunCalls[0].WorkflowName.Should().Be("wf");
        workflowPort.CreateRunCalls[0].WorkflowYaml.Should().Be("name: wf");
        workflowPort.CreateRunCalls[0].InlineWorkflowYamls.Should().ContainKey("child");
        workflowPort.CreateRunCalls[0].InlineWorkflowYamls["child"].Should().Be("name: child");
        workflowPort.CreateRunCalls[0].CapabilityAdmissionPlan!.AdmissionDigest.Should()
            .Be(capabilityAdmissionPlan.AdmissionDigest);
        workflowPort.RunActor.Envelopes.Should().BeEmpty();
        dispatchPort.Calls.Should().ContainSingle();
        dispatchPort.Calls[0].actorId.Should().Be("workflow-run");
        dispatchPort.Calls[0].envelope.Payload.Unpack<WorkflowChatRequestEvent>().Prompt.Should().Be("hello");
    }

    [Fact]
    public async Task DispatchAsync_WithRequestedWorkflowRun_ShouldEnsureAndExecuteInOneActorCommand()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var dispatchPort = new RecordingDispatchPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            dispatchPort,
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
        };

        var receipt = await dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            CommandId = "cmd-exact-run",
            CorrelationId = "corr-exact-run",
            RequestedRunId = "work-order-run-1",
            Payload = Any.Pack(new ChatRequestEvent { Prompt = "hello" }),
            WorkflowCompletionNotificationTarget = new WorkflowServiceCompletionNotificationTarget
            {
                ActorId = "work-order-actor-1",
                DeliveryId = "work-order-terminal-1",
                ExpiresAtUnixMs = long.MaxValue,
            },
        });

        receipt.TargetActorId.Should().Be("work-order-run-1");
        workflowPort.EnsureAndDispatchCalls.Should().ContainSingle();
        var call = workflowPort.EnsureAndDispatchCalls[0];
        call.RequestedRunId.Should().Be("work-order-run-1");
        call.CommandId.Should().Be("cmd-exact-run");
        call.CorrelationId.Should().Be("corr-exact-run");
        call.ExecutionRequest.Prompt.Should().Be("hello");
        call.ExecutionRequest.CompletionNotificationTarget.ActorId.Should().Be("work-order-actor-1");
        dispatchPort.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("WORKFLOW_DEFINITION_INVALID", "Workflow definition is invalid.")]
    [InlineData("NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED", "Workflow uses a retired NyxID tool contract.")]
    [InlineData("CAPABILITY_ADMISSION_REBIND_REQUIRED", "Saved workflow and capability admission no longer match.")]
    public async Task DispatchAsync_WithRequestedWorkflowRunAndRejectedPreflight_ShouldCreateNoRunArtifacts(
        string code,
        string safeMessage)
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var registry = new RecordingServiceRunRegistrationPort();
        var preflight = new RejectingArtifactCompatibilityPreflight(code, safeMessage);
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            registry,
            preflight);
        var target = CreateExplicitWorkflowTarget("r1", "r1", "r1");
        var request = CreateWorkflowInvocationRequest();
        request.RequestedRunId = "run-alpha";

        var act = () => dispatcher.DispatchAsync(target, request);

        var error = await act.Should().ThrowAsync<WorkflowExternalCapabilityAdmissionException>();
        error.Which.StableCode.Should().Be(code);
        error.Which.SafeMessage.Should().Be(safeMessage);
        preflight.Calls.Should().ContainSingle();
        workflowPort.CreateRunCalls.Should().BeEmpty();
        workflowPort.EnsureRunCalls.Should().BeEmpty();
        workflowPort.EnsureAndDispatchCalls.Should().BeEmpty();
        registry.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_ShouldMapTypedWorkflowCompletionNotificationTarget()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var dispatchPort = new RecordingDispatchPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            dispatchPort,
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
        };

        await dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            CommandId = "cmd-workflow-carrier",
            CorrelationId = "corr-workflow-carrier",
            Payload = Any.Pack(new ChatRequestEvent { Prompt = "hello" }),
            WorkflowCompletionNotificationTarget = new WorkflowServiceCompletionNotificationTarget
            {
                ActorId = "delivery-actor-alpha",
                DeliveryId = "delivery-alpha",
                ExpiresAtUnixMs = 1_770_000_000_000,
            },
        });

        var workflowRequest = dispatchPort.Calls.Should().ContainSingle().Which
            .envelope.Payload.Unpack<WorkflowChatRequestEvent>();
        workflowRequest.CompletionNotificationTarget.ActorId.Should().Be("delivery-actor-alpha");
        workflowRequest.CompletionNotificationTarget.DeliveryId.Should().Be("delivery-alpha");
        workflowRequest.CompletionNotificationTarget.ExpiresAtUnixMs.Should().Be(1_770_000_000_000);
    }

    [Fact]
    public async Task DispatchAsync_WhenWorkflowAdmissionIsRejected_ShouldDestroyRunAndNotReturnReceipt()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var dispatchPort = new RecordingDispatchPort
        {
            Admission = new DispatchAdmission(
                false,
                "cmd-workflow-rejected",
                DateTimeOffset.UtcNow,
                "workflow-run",
                "corr-workflow-rejected"),
        };
        var registry = new RecordingServiceRunRegistrationPort
        {
            RegistrationResult = new ServiceRunRegistrationResult(
                "service-run-actor-rejected",
                "service-run-id-rejected"),
        };
        var dispatcher = new DefaultServiceInvocationDispatcher(
            dispatchPort,
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            registry,
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
        };
        var request = new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            CommandId = "cmd-workflow-rejected",
            CorrelationId = "corr-workflow-rejected",
            Payload = Any.Pack(new ChatRequestEvent { Prompt = "hello" }),
        };

        var act = () => dispatcher.DispatchAsync(target, request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not accepted*workflow-run*");
        dispatchPort.Calls.Should().ContainSingle();
        workflowPort.DestroyCalls.Should().ContainSingle().Which.Should().Be("workflow-run");
        registry.Calls.Should().ContainSingle();
        registry.StatusUpdates.Should().ContainSingle().Which.Should().Be((
            "service-run-actor-rejected",
            "service-run-id-rejected",
            ServiceRunStatus.Failed));
    }

    [Fact]
    public async Task DispatchAsync_ShouldMapChatLlmControlToWorkflowChatRequest()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var dispatchPort = new RecordingDispatchPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            dispatchPort,
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
        };

        await dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            CommandId = "cmd-llm-control",
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                LlmControl = new LLMControlContextPayload
                {
                    NyxIdAccessToken = "owner-token",
                    ModelOverride = "sonnet",
                    NyxIdRoutePreference = "chrono-llm-public",
                    UserMemoryPrompt = "memory",
                    SenderNyxIdAccessToken = "sender-token",
                },
            }),
        });

        var workflowRequest = dispatchPort.Calls.Should().ContainSingle().Which
            .envelope.Payload.Unpack<WorkflowChatRequestEvent>();
        workflowRequest.CallerCredential.BearerToken.Should().Be("owner-token");
        workflowRequest.LlmControl.ModelOverride.Should().Be("sonnet");
        workflowRequest.LlmControl.RoutePreference.Should().Be("chrono-llm-public");
        workflowRequest.LlmControl.UserMemoryPrompt.Should().Be("memory");
        workflowRequest.LlmControl.SenderNyxIdAccessToken.Should().Be("sender-token");
    }

    [Fact]
    public async Task DispatchAsync_ShouldMapChatInputFileRefToWorkflowChatRequest()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var dispatchPort = new RecordingDispatchPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            dispatchPort,
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
        };

        await dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            CommandId = "cmd-file-ref",
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                InputParts =
                {
                    new ChatContentPart
                    {
                        Kind = ChatContentPartKind.Text,
                        Text = "see attachment",
                        MediaType = "application/pdf",
                        FileRef = new ChatFileRef
                        {
                            FileId = "file-1",
                            ArtifactId = "artifact-1",
                            SourceKind = ChatFileSourceKind.ConnectedServiceResource,
                            SourceMessageId = "om_1",
                            SourceResourceKey = "file_key_1",
                            FileName = "invoice.pdf",
                            MediaType = "application/pdf",
                            SizeBytes = 1234,
                            Sha256 = "abc",
                            CreatedAtUnixMs = 1710000000000,
                            ExpiresAtUnixMs = 1710003600000,
                            OwnerRunId = "run-1",
                            OwnerScopeId = "scope-1",
                        },
                    },
                },
            }),
        });

        var inputPart = dispatchPort.Calls.Should().ContainSingle().Which
            .envelope.Payload.Unpack<WorkflowChatRequestEvent>()
            .InputParts.Should().ContainSingle().Which;
        inputPart.Kind.Should().Be(Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.File);
        inputPart.FileRef.Should().NotBeNull();
        inputPart.FileRef.FileId.Should().Be("file-1");
        inputPart.FileRef.ArtifactId.Should().Be("artifact-1");
        inputPart.FileRef.SourceKind.Should().Be(WorkflowFileSourceKind.ConnectedServiceResource);
        inputPart.FileRef.SourceMessageId.Should().Be("om_1");
        inputPart.FileRef.SourceResourceKey.Should().Be("file_key_1");
        inputPart.FileRef.FileName.Should().Be("invoice.pdf");
        inputPart.FileRef.MediaType.Should().Be("application/pdf");
        inputPart.FileRef.SizeBytes.Should().Be(1234);
        inputPart.FileRef.Sha256.Should().Be("abc");
        inputPart.FileRef.CreatedAtUnixMs.Should().Be(1710000000000);
        inputPart.FileRef.ExpiresAtUnixMs.Should().Be(1710003600000);
        inputPart.FileRef.OwnerRunId.Should().Be("run-1");
        inputPart.FileRef.OwnerScopeId.Should().Be("scope-1");
    }

    [Theory]
    [InlineData("image/png", Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.Image)]
    [InlineData("audio/mpeg", Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.Audio)]
    [InlineData("video/mp4", Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.Video)]
    public async Task DispatchAsync_ShouldResolveWorkflowFileInputKindFromMediaType(
        string mediaType,
        Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind expectedKind)
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var dispatchPort = new RecordingDispatchPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            dispatchPort,
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
        };

        await dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            CommandId = $"cmd-{expectedKind}",
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                InputParts =
                {
                    new ChatContentPart
                    {
                        Kind = ChatContentPartKind.Text,
                        MediaType = mediaType,
                        FileRef = new ChatFileRef
                        {
                            FileId = $"file-{expectedKind}",
                            SourceKind = ChatFileSourceKind.FormUpload,
                            MediaType = mediaType,
                        },
                    },
                },
            }),
        });

        var inputPart = dispatchPort.Calls.Should().ContainSingle().Which
            .envelope.Payload.Unpack<WorkflowChatRequestEvent>()
            .InputParts.Should().ContainSingle().Which;
        inputPart.Kind.Should().Be(expectedKind);
        inputPart.FileRef.Should().NotBeNull();
        inputPart.FileRef.SourceKind.Should().Be(WorkflowFileSourceKind.FormUpload);
    }

    [Fact]
    public async Task DispatchAsync_ShouldMapConnectorAuthorizationToWorkflowCallerCredential()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var dispatchPort = new RecordingDispatchPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            dispatchPort,
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
        };

        await dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            CommandId = "cmd-auth",
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                ConnectorHttpAuthorization = "Bearer delegation-alpha",
                CallerNyxIdCredentialKind = AgentToolNyxIdCredentialKindPayload.ProxyDelegation,
                CallerSourceReadableNyxIdBearerToken = "source-alpha",
                LlmControl = new LLMControlContextPayload
                {
                    SenderNyxIdAccessToken = "llm-sender-alpha",
                },
            }),
        });

        var workflowRequest = dispatchPort.Calls.Should().ContainSingle().Which
            .envelope.Payload.Unpack<WorkflowChatRequestEvent>();
        workflowRequest.CallerCredential.BearerToken.Should().Be("delegation-alpha");
        workflowRequest.CallerCredential.Kind.Should().Be(NyxIdCallerCredentialKind.ProxyDelegation);
        workflowRequest.CallerCredential.SourceReadableUserBearerToken.Should().Be("source-alpha");
        workflowRequest.LlmControl.SenderNyxIdAccessToken.Should().Be("llm-sender-alpha");
    }

    [Fact]
    public async Task DispatchAsync_ShouldPreserveTypedProxyDelegationWithoutSupplementalSourceCredential()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var dispatchPort = new RecordingDispatchPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            dispatchPort,
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
        };

        await dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            CommandId = "cmd-delegation-only",
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                ConnectorHttpAuthorization = "Bearer delegation-only",
                CallerNyxIdCredentialKind = AgentToolNyxIdCredentialKindPayload.ProxyDelegation,
            }),
        });

        var workflowRequest = dispatchPort.Calls.Should().ContainSingle().Which
            .envelope.Payload.Unpack<WorkflowChatRequestEvent>();
        workflowRequest.CallerCredential.BearerToken.Should().Be("delegation-only");
        workflowRequest.CallerCredential.Kind.Should().Be(NyxIdCallerCredentialKind.ProxyDelegation);
        workflowRequest.CallerCredential.SourceReadableUserBearerToken.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_ShouldRejectSupplementalSourceCredentialWithoutExecutionCredential()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var dispatchPort = new RecordingDispatchPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            dispatchPort,
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
        };

        var act = () => dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            CommandId = "cmd-source-only",
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                CallerNyxIdCredentialKind = AgentToolNyxIdCredentialKindPayload.ProxyDelegation,
                CallerSourceReadableNyxIdBearerToken = "source-alpha",
            }),
        });

        await act.Should().ThrowAsync<ArgumentException>();
        dispatchPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_ShouldRejectSupplementalSourceCredentialForSourceReadableExecutionKind()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var dispatchPort = new RecordingDispatchPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            dispatchPort,
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
        };

        var act = () => dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            CommandId = "cmd-invalid-source-kind",
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                ConnectorHttpAuthorization = "Bearer source-alpha",
                CallerNyxIdCredentialKind = AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
                CallerSourceReadableNyxIdBearerToken = "source-alpha",
            }),
        });

        await act.Should().ThrowAsync<ArgumentException>();
        dispatchPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_ForScheduledWorkflow_ShouldIgnoreConnectorAuthorizationAndUseOwnerLlmToken()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var dispatchPort = new RecordingDispatchPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            dispatchPort,
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
        };

        await dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            CommandId = "cmd-scheduled-auth",
            ScheduleId = "schedule-1",
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                ConnectorHttpAuthorization = "Bearer stale-schedule-token",
                LlmControl = new LLMControlContextPayload
                {
                    NyxIdAccessToken = "owner-token",
                    SenderNyxIdAccessToken = "sender-token",
                },
            }),
        });

        var workflowRequest = dispatchPort.Calls.Should().ContainSingle().Which
            .envelope.Payload.Unpack<WorkflowChatRequestEvent>();
        workflowRequest.CallerCredential.BearerToken.Should().Be("owner-token");
        workflowRequest.LlmControl.SenderNyxIdAccessToken.Should().Be("sender-token");
    }

    [Fact]
    public async Task DispatchAsync_ForScheduledWorkflowWithoutOwnerToken_ShouldNotUseConnectorAuthorization()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var dispatchPort = new RecordingDispatchPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            dispatchPort,
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
        };

        await dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            CommandId = "cmd-scheduled-no-owner",
            ScheduleId = "schedule-1",
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                ConnectorHttpAuthorization = "Basic stale-schedule-token",
            }),
        });

        var workflowRequest = dispatchPort.Calls.Should().ContainSingle().Which
            .envelope.Payload.Unpack<WorkflowChatRequestEvent>();
        workflowRequest.CallerCredential.BearerToken.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_ShouldMapScheduledDurableCallerCredentialToWorkflowCallerCredential()
    {
        var dispatchPort = new RecordingDispatchPort();
        var workflowPort = new RecordingWorkflowRunActorPort();
        var registry = new RecordingServiceRunRegistrationPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            dispatchPort,
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            registry,
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
        };

        await dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            CommandId = "cmd-durable",
            ScheduleId = "schedule-1",
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                CallerDurableCredential = new DurableCallerCredentialRef
                {
                    Ref = "sec_scheduled",
                    Purpose = CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
                    OwnerScopeKey = "schedule:schedule-1",
                    SubjectId = "lark:tenant:user",
                    SourceKind = DurableCallerCredentialSourceKind.ScheduledDispatch,
                    ScheduledCallerNyxIdAuthority = new ScheduledCallerNyxIdAuthority
                    {
                        Platform = "lark",
                        Tenant = "tenant-a",
                        ExternalUserId = "external-user-42",
                        Scope = "proxy",
                        BindingId = "bnd-owner-alpha",
                    },
                },
            }),
        });

        var workflowRequest = dispatchPort.Calls.Should().ContainSingle().Which
            .envelope.Payload.Unpack<WorkflowChatRequestEvent>();
        workflowRequest.CallerCredential.BearerToken.Should().BeEmpty();
        workflowRequest.CallerCredential.DurableCallerCredential.Ref.Should().Be("sec_scheduled");
        workflowRequest.CallerCredential.DurableCallerCredential.SourceKind
            .Should().Be(DurableCallerCredentialSourceKind.ScheduledDispatch);
        workflowRequest.CallerCredential.NyxIdAuthority.Should().BeEquivalentTo(
            new Aevatar.Workflow.Abstractions.WorkflowCallerNyxIdAuthority
            {
                Platform = "lark",
                Tenant = "tenant-a",
                ExternalUserId = "external-user-42",
                Scope = "proxy",
                BindingId = "bnd-owner-alpha",
            });
    }

    [Fact]
    public async Task DispatchAsync_ShouldRejectCallerDurableCredentialWithoutScheduledDispatch()
    {
        var dispatchPort = new RecordingDispatchPort();
        var workflowPort = new RecordingWorkflowRunActorPort();
        var registry = new RecordingServiceRunRegistrationPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            dispatchPort,
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            registry,
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
        };
        var request = new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            CommandId = "cmd-forged-durable",
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                CallerDurableCredential = new DurableCallerCredentialRef
                {
                    Ref = "sec_forged",
                    Purpose = CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
                    OwnerScopeKey = "schedule:schedule-1",
                    SubjectId = "subject",
                    SourceKind = DurableCallerCredentialSourceKind.ScheduledDispatch,
                },
            }),
        };

        var act = () => dispatcher.DispatchAsync(target, request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*caller_durable_credential*scheduled dispatch*");
        workflowPort.CreateRunCalls.Should().BeEmpty();
        dispatchPort.Calls.Should().BeEmpty();
        registry.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DispatchAsync_ShouldRejectScheduledDurableCallerCredential_WhenRawCredentialOrReferenceIsInvalid(
        bool includeRawCredential)
    {
        var dispatchPort = new RecordingDispatchPort();
        var workflowPort = new RecordingWorkflowRunActorPort();
        var registry = new RecordingServiceRunRegistrationPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            dispatchPort,
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            registry,
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
        };
        var chatRequest = new ChatRequestEvent
        {
            Prompt = "hello",
            CallerDurableCredential = includeRawCredential
                ? CreateDurableCallerCredentialRef()
                : new DurableCallerCredentialRef(),
        };
        if (includeRawCredential)
            chatRequest.ConnectorHttpAuthorization = "Bearer raw-token";
        var request = new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            CommandId = "cmd-durable-reject",
            ScheduleId = "schedule-1",
            Payload = Any.Pack(chatRequest),
        };

        var act = () => dispatcher.DispatchAsync(target, request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage(includeRawCredential
                ? "*caller_durable_credential*must not be combined*"
                : "*caller_durable_credential*durable secret reference*");
        workflowPort.CreateRunCalls.Should().BeEmpty();
        dispatchPort.Calls.Should().BeEmpty();
        registry.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_ShouldPreferIdentityTenantIdOverPayloadScope()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
        };
        var request = new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                ScopeId = "payload-scope",
                Metadata =
                {
                    [WorkflowRunCommandMetadataKeys.ScopeId] = "workflow-metadata-scope",
                    ["scope_id"] = "legacy-scope",
                },
            }),
        };

        await dispatcher.DispatchAsync(target, request);

        workflowPort.CreateRunCalls.Should().ContainSingle();
        workflowPort.CreateRunCalls[0].ScopeId.Should().Be("tenant");
    }

    [Fact]
    public async Task DispatchAsync_ShouldResolveScopeIdFromTypedPayloadBeforeScopeAnnotations()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
        };
        var request = new ServiceInvocationRequest
        {
            Identity = new ServiceIdentity { TenantId = "", AppId = "app", Namespace = "default", ServiceId = "svc" },
            EndpointId = "chat",
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                ScopeId = "request-scope",
                Headers =
                {
                    [WorkflowRunCommandMetadataKeys.ScopeId] = "workflow-header-scope",
                    ["scope_id"] = "legacy-header-scope",
                },
                Metadata =
                {
                    [WorkflowRunCommandMetadataKeys.ScopeId] = "workflow-metadata-scope",
                    ["scope_id"] = "legacy-scope",
                },
            }),
        };

        await dispatcher.DispatchAsync(target, request);

        workflowPort.CreateRunCalls.Should().ContainSingle();
        workflowPort.CreateRunCalls[0].ScopeId.Should().Be("request-scope");
    }

    [Fact]
    public async Task DispatchAsync_ShouldRejectBlankTypedScope_WithoutFallingBackToWorkflowScopeAnnotations()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
        };

        var dispatch = async () => await dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = new ServiceIdentity { TenantId = "", AppId = "app", Namespace = "default", ServiceId = "svc" },
            EndpointId = "chat",
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                Headers =
                {
                    [WorkflowRunCommandMetadataKeys.ScopeId] = "workflow-header-scope",
                    ["scope_id"] = "legacy-header-scope",
                },
                Metadata =
                {
                    [WorkflowRunCommandMetadataKeys.ScopeId] = "workflow-metadata-scope",
                    ["scope_id"] = "legacy-scope",
                },
            }),
        });

        // 06-23 W3b: a blank typed scope no longer falls back to untrusted header/metadata scope nor creates
        // an empty-scope run; it fails fast so no unattributed run is materialized.
        await dispatch.Should().ThrowAsync<InvalidOperationException>().WithMessage("*requires a scope*");
        workflowPort.CreateRunCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_ShouldRejectBlankTypedScope_WithoutFallingBackToLegacyMetadata()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
        };

        var dispatch = async () => await dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = new ServiceIdentity { TenantId = "", AppId = "app", Namespace = "default", ServiceId = "svc" },
            EndpointId = "chat",
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                Metadata =
                {
                    ["scope_id"] = "legacy-scope",
                },
            }),
        });

        // 06-23 W3b: a blank typed scope no longer falls back to untrusted metadata scope nor creates an
        // empty-scope run; it fails fast so no unattributed run is materialized.
        await dispatch.Should().ThrowAsync<InvalidOperationException>().WithMessage("*requires a scope*");
        workflowPort.CreateRunCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_ShouldRejectPayloadTypeMismatch()
    {
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            new RecordingScriptRuntimeCommandPort(),
            new RecordingWorkflowRunActorPort(),
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Static,
            endpointId: "run",
            requestTypeUrl: "type.googleapis.com/expected");
        var request = new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "run",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        };

        var act = () => dispatcher.DispatchAsync(target, request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*expects payload*");
    }

    [Fact]
    public async Task DispatchAsync_ShouldGenerateCommandAndCorrelationIds_WhenMissing()
    {
        var dispatchPort = new RecordingDispatchPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            dispatchPort,
            new RecordingScriptRuntimeCommandPort(),
            new RecordingWorkflowRunActorPort(),
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(ServiceImplementationKind.Static, endpointId: "run");

        var receipt = await dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "run",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        receipt.CommandId.Should().NotBeNullOrWhiteSpace();
        receipt.CorrelationId.Should().Be(receipt.CommandId);
        dispatchPort.Calls.Should().ContainSingle();
        dispatchPort.Calls[0].envelope.Id.Should().Be(receipt.CommandId);
        dispatchPort.Calls[0].envelope.Propagation.CorrelationId.Should().Be(receipt.CommandId);
    }

    [Fact]
    public async Task DispatchAsync_ShouldRejectMissingPayload()
    {
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            new RecordingScriptRuntimeCommandPort(),
            new RecordingWorkflowRunActorPort(),
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(ServiceImplementationKind.Static, endpointId: "run");

        var act = () => dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "run",
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("payload is required.");
    }

    [Fact]
    public async Task DispatchAsync_ShouldRejectWorkflowPayloadThatIsNotChatRequest()
    {
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            new RecordingScriptRuntimeCommandPort(),
            new RecordingWorkflowRunActorPort(),
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new StringValue()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
        };

        var act = () => dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        await act.Should().ThrowAsync<Google.Protobuf.InvalidProtocolBufferException>();
    }

    [Fact]
    public async Task DispatchAsync_ShouldPassRequestedEventTypeAndGeneratedRunIdToScriptingRuntime()
    {
        var scriptPort = new RecordingScriptRuntimeCommandPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            scriptPort,
            new RecordingWorkflowRunActorPort(),
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Scripting,
            endpointId: "run",
            requestTypeUrl: Any.Pack(new StringValue()).TypeUrl);
        target.Artifact.DeploymentPlan.ScriptingPlan = new ScriptingServiceDeploymentPlan
        {
            Revision = "rev-1",
            DefinitionActorId = "definition-1",
        };
        var request = new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "run",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        };

        var receipt = await dispatcher.DispatchAsync(target, request);

        scriptPort.Calls.Should().ContainSingle();
        scriptPort.Calls[0].runId.Should().Be(receipt.CommandId);
        scriptPort.Calls[0].requestedEventType.Should().Be(Any.Pack(new StringValue()).TypeUrl);
        scriptPort.Calls[0].payload.Should().NotBeNull();
        scriptPort.Calls[0].payload!.TypeUrl.Should().Be(Any.Pack(new StringValue()).TypeUrl);
    }

    [Fact]
    public async Task DispatchAsync_ShouldRegisterServiceRun_ForStaticPath()
    {
        var registry = new RecordingServiceRunRegistrationPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            new RecordingScriptRuntimeCommandPort(),
            new RecordingWorkflowRunActorPort(),
            registry,
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(ServiceImplementationKind.Static, endpointId: "run");
        var request = new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "run",
            CommandId = "cmd-static",
            ScheduleId = "schedule-1",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        };

        var receipt = await dispatcher.DispatchAsync(target, request);

        registry.Calls.Should().ContainSingle();
        registry.Calls[0].RunId.Should().Be(receipt.CommandId);
        registry.Calls[0].CommandId.Should().Be("cmd-static");
        registry.Calls[0].ImplementationKind.Should().Be(ServiceImplementationKind.Static);
        registry.Calls[0].ScheduleId.Should().Be("schedule-1");
        registry.Calls[0].TargetActorId.Should().Be("primary-actor");
        registry.Calls[0].ScopeId.Should().Be("tenant");
        registry.Calls[0].ServiceId.Should().Be("svc");
    }

    [Fact]
    public async Task DispatchAsync_ShouldRegisterServiceRun_ForScriptingPath()
    {
        var registry = new RecordingServiceRunRegistrationPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            new RecordingScriptRuntimeCommandPort(),
            new RecordingWorkflowRunActorPort(),
            registry,
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Scripting,
            endpointId: "run",
            requestTypeUrl: Any.Pack(new StringValue()).TypeUrl);
        target.Artifact.DeploymentPlan.ScriptingPlan = new ScriptingServiceDeploymentPlan
        {
            Revision = "rev-1",
            DefinitionActorId = "definition-1",
        };
        var request = new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "run",
            CommandId = "cmd-script",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        };

        await dispatcher.DispatchAsync(target, request);

        registry.Calls.Should().ContainSingle();
        registry.Calls[0].ImplementationKind.Should().Be(ServiceImplementationKind.Scripting);
        registry.Calls[0].RunId.Should().Be("cmd-script");
        registry.Calls[0].CommandId.Should().Be("cmd-script");
    }

    [Fact]
    public async Task DispatchAsync_ShouldRegisterServiceRun_ForWorkflowPath()
    {
        var registry = new RecordingServiceRunRegistrationPort();
        var workflowPort = new RecordingWorkflowRunActorPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            registry,
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl,
            revisionId: "rev-artifact-alpha");
        var request = new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            CommandId = "cmd-wf",
            ScheduleId = "schedule-wf",
            Payload = Any.Pack(new ChatRequestEvent { Prompt = "hi" }),
        };
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "artifact-wf",
            WorkflowYaml = "name: artifact-wf",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
            DefinitionActorId = "artifact-definition-actor",
            WorkflowId = "wf-artifact-alpha",
            RevisionId = "rev-artifact-alpha",
            InlineWorkflowYamls =
            {
                ["helper"] = "name: helper",
            },
        };

        var receipt = await dispatcher.DispatchAsync(target, request);

        receipt.RunId.Should().Be(workflowPort.RunActor.Id);
        receipt.TargetActorId.Should().Be(workflowPort.RunActor.Id);
        receipt.CommandId.Should().Be("cmd-wf");
        registry.Calls.Should().ContainSingle();
        registry.Calls[0].ImplementationKind.Should().Be(ServiceImplementationKind.Workflow);
        registry.Calls[0].RunId.Should().Be(workflowPort.RunActor.Id);
        registry.Calls[0].TargetActorId.Should().Be(workflowPort.RunActor.Id);
        registry.Calls[0].CommandId.Should().Be("cmd-wf");
        workflowPort.CreateRunCalls.Should().ContainSingle();
        workflowPort.CreateRunCalls[0].DefinitionActorId.Should().Be("primary-actor");
        workflowPort.CreateRunCalls[0].WorkflowName.Should().Be("artifact-wf");
        workflowPort.CreateRunCalls[0].WorkflowYaml.Should().Be("name: artifact-wf");
        workflowPort.CreateRunCalls[0].InlineWorkflowYamls.Should().Contain("helper", "name: helper");
        workflowPort.CreateRunCalls[0].WorkflowId.Should().Be("wf-artifact-alpha");
        workflowPort.CreateRunCalls[0].RevisionId.Should().Be("rev-artifact-alpha");
        // 06-24: scheduleId must ride from the service-invocation request into the run binding so the
        // observatory can filter this schedule's runs (previously dropped on the workflow branch).
        workflowPort.CreateRunCalls[0].ScheduleId.Should().Be("schedule-wf");
    }

    [Fact]
    public async Task DispatchAsync_ShouldRejectWorkflowArtifactRevisionMismatch()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var registry = new RecordingServiceRunRegistrationPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            registry,
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateExplicitWorkflowTarget(
            resolvedRevisionId: "rev-resolved-alpha",
            artifactRevisionId: "rev-artifact-beta",
            planRevisionId: "rev-resolved-alpha");

        var act = () => dispatcher.DispatchAsync(target, CreateWorkflowInvocationRequest());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*artifact revision_id*");
        workflowPort.CreateRunCalls.Should().BeEmpty();
        registry.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_ShouldRejectWorkflowPlanRevisionMismatch()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var registry = new RecordingServiceRunRegistrationPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            registry,
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateExplicitWorkflowTarget(
            resolvedRevisionId: "rev-resolved-alpha",
            artifactRevisionId: "rev-resolved-alpha",
            planRevisionId: "rev-plan-beta");

        var act = () => dispatcher.DispatchAsync(target, CreateWorkflowInvocationRequest());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*workflow plan revision_id*");
        workflowPort.CreateRunCalls.Should().BeEmpty();
        registry.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_ShouldFallbackToArtifactDefinitionActor_WhenWorkflowServiceHasNoPrimaryActor()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            new RecordingScriptRuntimeCommandPort(),
            workflowPort,
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl,
            primaryActorId: "");
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "artifact-wf",
            WorkflowYaml = "name: artifact-wf",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            CapabilityAdmissionPlan = new WorkflowCapabilityAdmissionPlan
            {
                ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            },
            DefinitionActorId = "artifact-definition-actor",
        };
        var request = new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            CommandId = "cmd-wf",
            Payload = Any.Pack(new ChatRequestEvent { Prompt = "hi" }),
        };

        await dispatcher.DispatchAsync(target, request);

        workflowPort.CreateRunCalls.Should().ContainSingle();
        workflowPort.CreateRunCalls[0].DefinitionActorId.Should().Be("artifact-definition-actor");
    }

    [Fact]
    public async Task DispatchAsync_ShouldRejectUnsupportedImplementationKind()
    {
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            new RecordingScriptRuntimeCommandPort(),
            new RecordingWorkflowRunActorPort(),
            new RecordingServiceRunRegistrationPort(),
            new AcceptingArtifactCompatibilityPreflight());
        var target = CreateTarget(ServiceImplementationKind.Static, endpointId: "run");
        target.Artifact.ImplementationKind = ServiceImplementationKind.Unspecified;

        var act = () => dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "run",
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Unsupported service implementation*");
    }

    private static ServiceInvocationResolvedTarget CreateTarget(
        ServiceImplementationKind implementationKind,
        string endpointId,
        string requestTypeUrl = "",
        string primaryActorId = "primary-actor",
        string revisionId = "r1")
    {
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(
            GAgentServiceTestKit.CreateIdentity(),
            revisionId,
            GAgentServiceTestKit.CreateEndpointDescriptor(endpointId: endpointId, requestTypeUrl: requestTypeUrl));
        artifact.ImplementationKind = implementationKind;
        if (artifact.DeploymentPlan.PlanSpecCase == ServiceDeploymentPlan.PlanSpecOneofCase.StaticPlan &&
            implementationKind != ServiceImplementationKind.Static)
        {
            artifact.DeploymentPlan = new ServiceDeploymentPlan();
        }

        return new ServiceInvocationResolvedTarget(
            new ServiceInvocationResolvedService(
                "tenant:app:default:svc",
                revisionId,
                "dep-1",
                primaryActorId,
                ServiceDeploymentStatus.Active.ToString(),
                []),
            artifact,
            new ServiceEndpointDescriptor
            {
                EndpointId = endpointId,
                DisplayName = endpointId,
                Kind = ServiceEndpointKind.Command,
                RequestTypeUrl = requestTypeUrl,
            });
    }

    private static ServiceInvocationResolvedTarget CreateExplicitWorkflowTarget(
        string resolvedRevisionId,
        string artifactRevisionId,
        string planRevisionId)
    {
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl,
            revisionId: resolvedRevisionId);
        var admissionPlan = new WorkflowCapabilityAdmissionPlan();
        admissionPlan.ExecutionMode = ExternalCapabilityExecutionMode.Durable;
        admissionPlan.InvocationAdmissions.Add(new WorkflowCapabilityInvocationAdmission
        {
            CallSiteId = "workflow/request-alpha",
            NyxIdExplicitRequestGrant = new NyxIdExplicitRequestGrant(),
        });
        target.Artifact.RevisionId = artifactRevisionId;
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "workflow",
            WorkflowYaml = "name: workflow",
            ExecutionMode = ExternalCapabilityExecutionMode.Durable,
            WorkflowId = "wf-dispatch-alpha",
            RevisionId = planRevisionId,
            CapabilityAdmissionPlan = admissionPlan,
        };
        return target;
    }

    private static ServiceInvocationRequest CreateWorkflowInvocationRequest() =>
        new()
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            CommandId = "cmd-workflow-identity",
            Payload = Any.Pack(new ChatRequestEvent { Prompt = "hi" }),
        };

    private static DurableCallerCredentialRef CreateDurableCallerCredentialRef() =>
        new()
        {
            Ref = "sec_scheduled",
            Purpose = CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
            OwnerScopeKey = "schedule:schedule-1",
            SubjectId = "lark:tenant:user",
            SourceKind = DurableCallerCredentialSourceKind.ScheduledDispatch,
        };

    private sealed class RecordingServiceRunRegistrationPort : IServiceRunRegistrationPort
    {
        public List<ServiceRunRecord> Calls { get; } = [];
        public List<(string RunActorId, string RunId, ServiceRunStatus Status)> StatusUpdates { get; } = [];
        public ServiceRunRegistrationResult? RegistrationResult { get; init; }

        public Task<ServiceRunRegistrationResult> RegisterAsync(ServiceRunRecord record, CancellationToken ct = default)
        {
            Calls.Add(record.Clone());
            return Task.FromResult(
                RegistrationResult ?? new ServiceRunRegistrationResult($"service-run:{record.RunId}", record.RunId));
        }

        public Task UpdateStatusAsync(string runActorId, string runId, ServiceRunStatus status, CancellationToken ct = default)
        {
            StatusUpdates.Add((runActorId, runId, status));
            return Task.CompletedTask;
        }
    }

    private sealed class AcceptingArtifactCompatibilityPreflight : IWorkflowArtifactCompatibilityPreflight
    {
        public Task ValidateAsync(
            WorkflowArtifactCompatibilityRequest request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class RejectingArtifactCompatibilityPreflight(string code, string safeMessage)
        : IWorkflowArtifactCompatibilityPreflight
    {
        public List<WorkflowArtifactCompatibilityRequest> Calls { get; } = [];

        public Task ValidateAsync(
            WorkflowArtifactCompatibilityRequest request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ct.ThrowIfCancellationRequested();
            Calls.Add(request with { CapabilityAdmissionPlan = request.CapabilityAdmissionPlan?.Clone() });
            throw new WorkflowExternalCapabilityAdmissionException(new ExternalCapabilityReadiness
            {
                Status = ExternalCapabilityReadinessStatus.AdmissionRebindRequired,
                Blockers =
                {
                    new ExternalCapabilityBlocker
                    {
                        Status = ExternalCapabilityReadinessStatus.AdmissionRebindRequired,
                        Code = code,
                        SafeMessage = safeMessage,
                    },
                },
            });
        }
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<(string actorId, EventEnvelope envelope)> Calls { get; } = [];
        public DispatchAdmission? Admission { get; init; }

        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope));
            return Task.FromResult(Admission ?? DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingScriptRuntimeCommandPort : IScriptRuntimeCommandPort
    {
        public List<(string runtimeActorId, string runId, string commandId, string correlationId, Any? payload, string revision, string definitionActorId, string requestedEventType, string? scopeId, string? completionNotificationActorId, string? completionNotificationDeliveryId, long completionNotificationExpiresAtUnixMs)> Calls { get; } = [];

        public Task RunRuntimeAsync(
            string runtimeActorId,
            string runId,
            Any? inputPayload,
            string scriptRevision,
            string definitionActorId,
            string requestedEventType,
            CancellationToken ct)
        {
            Calls.Add((runtimeActorId, runId, runId, runId, inputPayload?.Clone(), scriptRevision, definitionActorId, requestedEventType, null, null, null, 0));
            return Task.CompletedTask;
        }

        public Task RunRuntimeAsync(
            string runtimeActorId,
            string runId,
            Any? inputPayload,
            string scriptRevision,
            string definitionActorId,
            string requestedEventType,
            string? scopeId,
            CancellationToken ct)
        {
            Calls.Add((runtimeActorId, runId, runId, runId, inputPayload?.Clone(), scriptRevision, definitionActorId, requestedEventType, scopeId, null, null, 0));
            return Task.CompletedTask;
        }

        public Task RunRuntimeAsync(
            string runtimeActorId,
            string runId,
            string commandId,
            string correlationId,
            Any? inputPayload,
            string scriptRevision,
            string definitionActorId,
            string requestedEventType,
            string? scopeId,
            CancellationToken ct)
        {
            Calls.Add((runtimeActorId, runId, commandId, correlationId, inputPayload?.Clone(), scriptRevision, definitionActorId, requestedEventType, scopeId, null, null, 0));
            return Task.CompletedTask;
        }

        public Task RunRuntimeAsync(
            string runtimeActorId,
            string runId,
            string commandId,
            string correlationId,
            Any? inputPayload,
            string scriptRevision,
            string definitionActorId,
            string requestedEventType,
            string? scopeId,
            string? completionNotificationActorId,
            string? completionNotificationDeliveryId,
            long completionNotificationExpiresAtUnixMs,
            CancellationToken ct)
        {
            Calls.Add((runtimeActorId, runId, commandId, correlationId, inputPayload?.Clone(), scriptRevision, definitionActorId, requestedEventType, scopeId, completionNotificationActorId, completionNotificationDeliveryId, completionNotificationExpiresAtUnixMs));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingWorkflowRunActorPort :
        IWorkflowDefinitionProvisioningPort,
        IWorkflowRunProvisioningPort,
        IWorkflowRunIdentityProvisioningPort,
        IWorkflowRunIdentityExecutionPort,
        IWorkflowDefinitionParser
    {
        public List<WorkflowDefinitionBinding> CreateRunCalls { get; } = [];
        public List<(WorkflowDefinitionBinding Definition, string RequestedRunId)> EnsureRunCalls { get; } = [];
        public List<(
            WorkflowDefinitionBinding Definition,
            string RequestedRunId,
            WorkflowChatRequestEvent ExecutionRequest,
            string CommandId,
            string CorrelationId)> EnsureAndDispatchCalls { get; } = [];
        public List<string> DestroyCalls { get; } = [];

        public RecordingActor RunActor { get; } = new("workflow-run");

        public Task<WorkflowDefinitionProvisioningReceipt> EnsureDefinitionAsync(WorkflowDefinitionBinding definition, string? preferredActorId = null, CancellationToken ct = default) =>
            Task.FromResult(new WorkflowDefinitionProvisioningReceipt(preferredActorId ?? definition.DefinitionActorId, CreatedNow: true));

        public Task<WorkflowRunCreationReceipt> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default)
        {
            CreateRunCalls.Add(definition);
            return Task.FromResult(new WorkflowRunCreationReceipt(RunActor.Id, definition.DefinitionActorId, [RunActor.Id]));
        }

        public Task<WorkflowRunCreationReceipt> EnsureRunAsync(
            WorkflowDefinitionBinding definition,
            string requestedRunId,
            CancellationToken ct = default)
        {
            EnsureRunCalls.Add((definition, requestedRunId));
            return Task.FromResult(
                new WorkflowRunCreationReceipt(requestedRunId, definition.DefinitionActorId, []));
        }

        public Task<WorkflowRunCreationReceipt> EnsureRunAndDispatchAsync(
            WorkflowDefinitionBinding definition,
            string requestedRunId,
            WorkflowChatRequestEvent executionRequest,
            string commandId,
            string correlationId,
            CancellationToken ct = default)
        {
            EnsureAndDispatchCalls.Add((
                definition,
                requestedRunId,
                executionRequest.Clone(),
                commandId,
                correlationId));
            return Task.FromResult(
                new WorkflowRunCreationReceipt(requestedRunId, definition.DefinitionActorId, []));
        }

        public Task DestroyAsync(string actorId, CancellationToken ct = default)
        {
            DestroyCalls.Add(actorId);
            return Task.CompletedTask;
        }

        public Task MarkStoppedAsync(string actorId, string runId, string reason, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task BindWorkflowDefinitionAsync(
            string actorId,
            string workflowYaml,
            string workflowName,
            IReadOnlyDictionary<string, string>? inlineWorkflowYamls,
            string? scopeId,
            string? sourceKind,
            WorkflowCapabilityAdmissionPlan? capabilityAdmissionPlan,
            string? workflowId,
            string? revisionId,
            ExternalCapabilityExecutionMode expectedExecutionMode,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(string workflowYaml, CancellationToken ct = default) =>
            Task.FromResult(WorkflowYamlParseResult.Success("wf"));

        public Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default) =>
            Task.FromResult(WorkflowInlineYamlBundleParseResult.Success(
                "wf",
                inlineWorkflowDocuments.FirstOrDefault()?.Yaml ?? string.Empty,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["wf"] = inlineWorkflowDocuments.FirstOrDefault()?.Yaml ?? string.Empty,
                }));
    }

    private sealed class RecordingActor : IActor
    {
        public RecordingActor(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public IAgent Agent { get; } = new TestStaticServiceAgent();

        public List<EventEnvelope> Envelopes { get; } = [];

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            Envelopes.Add(envelope);
            return Task.CompletedTask;
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
