using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Infrastructure.Dispatch;
using Aevatar.GAgentService.Tests.TestSupport;
using Aevatar.Scripting.Core.Ports;
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
            new RecordingWorkflowRunActorPort());
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
    public async Task DispatchAsync_ShouldDelegateScriptingRun()
    {
        var scriptPort = new RecordingScriptRuntimeCommandPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            scriptPort,
            new RecordingWorkflowRunActorPort());
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
            Payload = Any.Pack(new StringValue { Value = "payload" }),
        };

        var receipt = await dispatcher.DispatchAsync(target, request);

        receipt.TargetActorId.Should().Be("primary-actor");
        scriptPort.Calls.Should().ContainSingle();
        scriptPort.Calls[0].runtimeActorId.Should().Be("primary-actor");
        scriptPort.Calls[0].runId.Should().Be("cmd-2");
        scriptPort.Calls[0].definitionActorId.Should().Be("definition-1");
        scriptPort.Calls[0].scopeId.Should().Be(GAgentServiceTestKit.CreateIdentity().TenantId);
    }

    [Fact]
    public async Task DispatchAsync_ShouldCreateWorkflowRun_AndSendEnvelope()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var dispatchPort = new RecordingDispatchPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            dispatchPort,
            new RecordingScriptRuntimeCommandPort(),
            workflowPort);
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
            InlineWorkflowYamls =
            {
                ["child"] = "name: child",
            },
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
        workflowPort.RunActor.Envelopes.Should().BeEmpty();
        dispatchPort.Calls.Should().ContainSingle();
        dispatchPort.Calls[0].actorId.Should().Be("workflow-run");
        dispatchPort.Calls[0].envelope.Payload.Unpack<ChatRequestEvent>().Prompt.Should().Be("hello");
    }

    [Fact]
    public async Task DispatchAsync_ShouldResolveScopeIdFromRequestScopeBeforeMetadataFallbacks()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            new RecordingScriptRuntimeCommandPort(),
            workflowPort);
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
        };
        var request = new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                ScopeId = "request-scope",
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
    public async Task DispatchAsync_ShouldResolveScopeIdFromWorkflowMetadataKey_WhenRequestScopeIsBlank()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            new RecordingScriptRuntimeCommandPort(),
            workflowPort);
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
        };

        await dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            EndpointId = "chat",
            Payload = Any.Pack(new ChatRequestEvent
            {
                Prompt = "hello",
                Metadata =
                {
                    [WorkflowRunCommandMetadataKeys.ScopeId] = "workflow-metadata-scope",
                    ["scope_id"] = "legacy-scope",
                },
            }),
        });

        workflowPort.CreateRunCalls.Should().ContainSingle();
        workflowPort.CreateRunCalls[0].ScopeId.Should().Be("workflow-metadata-scope");
    }

    [Fact]
    public async Task DispatchAsync_ShouldResolveScopeIdFromLegacyMetadataKey_WhenOtherSourcesAreBlank()
    {
        var workflowPort = new RecordingWorkflowRunActorPort();
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            new RecordingScriptRuntimeCommandPort(),
            workflowPort);
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new ChatRequestEvent()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
        };

        await dispatcher.DispatchAsync(target, new ServiceInvocationRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
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

        workflowPort.CreateRunCalls.Should().ContainSingle();
        workflowPort.CreateRunCalls[0].ScopeId.Should().Be("legacy-scope");
    }

    [Fact]
    public async Task DispatchAsync_ShouldRejectPayloadTypeMismatch()
    {
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            new RecordingScriptRuntimeCommandPort(),
            new RecordingWorkflowRunActorPort());
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
            new RecordingWorkflowRunActorPort());
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
            new RecordingWorkflowRunActorPort());
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
            new RecordingWorkflowRunActorPort());
        var target = CreateTarget(
            ServiceImplementationKind.Workflow,
            endpointId: "chat",
            requestTypeUrl: Any.Pack(new StringValue()).TypeUrl);
        target.Artifact.DeploymentPlan.WorkflowPlan = new WorkflowServiceDeploymentPlan
        {
            WorkflowName = "wf",
            WorkflowYaml = "name: wf",
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
            new RecordingWorkflowRunActorPort());
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
    public async Task DispatchAsync_ShouldRejectUnsupportedImplementationKind()
    {
        var dispatcher = new DefaultServiceInvocationDispatcher(
            new RecordingDispatchPort(),
            new RecordingScriptRuntimeCommandPort(),
            new RecordingWorkflowRunActorPort());
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
        string requestTypeUrl = "")
    {
        var artifact = GAgentServiceTestKit.CreatePreparedStaticArtifact(
            GAgentServiceTestKit.CreateIdentity(),
            "r1",
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
                "r1",
                "dep-1",
                "primary-actor",
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

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<(string actorId, EventEnvelope envelope)> Calls { get; } = [];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            Calls.Add((actorId, envelope));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingScriptRuntimeCommandPort : IScriptRuntimeCommandPort
    {
        public List<(string runtimeActorId, string runId, Any? payload, string revision, string definitionActorId, string requestedEventType, string? scopeId)> Calls { get; } = [];

        public Task RunRuntimeAsync(
            string runtimeActorId,
            string runId,
            Any? inputPayload,
            string scriptRevision,
            string definitionActorId,
            string requestedEventType,
            CancellationToken ct)
        {
            Calls.Add((runtimeActorId, runId, inputPayload?.Clone(), scriptRevision, definitionActorId, requestedEventType, null));
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
            Calls.Add((runtimeActorId, runId, inputPayload?.Clone(), scriptRevision, definitionActorId, requestedEventType, scopeId));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingWorkflowRunActorPort : IWorkflowRunActorPort
    {
        public List<WorkflowDefinitionBinding> CreateRunCalls { get; } = [];

        public RecordingActor RunActor { get; } = new("workflow-run");

        public Task<IActor> CreateDefinitionAsync(string? actorId = null, CancellationToken ct = default) =>
            Task.FromResult<IActor>(new RecordingActor(actorId ?? "workflow-definition"));

        public Task<WorkflowRunCreationResult> CreateRunAsync(WorkflowDefinitionBinding definition, CancellationToken ct = default)
        {
            CreateRunCalls.Add(definition);
            return Task.FromResult(new WorkflowRunCreationResult(RunActor, definition.DefinitionActorId, [RunActor.Id]));
        }

        public Task DestroyAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;

        public Task MarkStoppedAsync(string actorId, string runId, string reason, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task BindWorkflowDefinitionAsync(
            IActor actor,
            string workflowYaml,
            string workflowName,
            IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
            string? scopeId = null,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(string workflowYaml, CancellationToken ct = default) =>
            Task.FromResult(WorkflowYamlParseResult.Success("wf"));
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
