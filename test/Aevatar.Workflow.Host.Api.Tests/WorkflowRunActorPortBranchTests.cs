using Aevatar.AI.Abstractions.Agents;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Core.TypeSystem;
using Aevatar.Foundation.Runtime.Callbacks;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Infrastructure.Runs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowRunActorPortBranchTests
{
    [Fact]
    public async Task EnsureDefinitionAsync_WithRealPort_ShouldBindDefinitionOnce()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = CreateWorkflowDefinitionAgent();
        runtime.ActorsToCreate.Enqueue(new RecordingActor("definition-once", definitionAgent, forwardToAgent: true));
        var port = CreatePort(runtime);

        var receipt = await port.EnsureDefinitionAsync(
            new WorkflowDefinitionBinding(
                "definition-once",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            "definition-once",
            CancellationToken.None);

        receipt.ActorId.Should().Be("definition-once");
        definitionAgent.State.Version.Should().Be(1);
        definitionAgent.State.WorkflowName.Should().Be("direct");
        definitionAgent.State.WorkflowYaml.Should().Be("name: direct\nroles: []\nsteps: []\n");
    }

    [Fact]
    public async Task EnsureDefinitionAsync_ShouldForwardPreferredActorId()
    {
        var runtime = new RecordingActorRuntime();
        runtime.ActorsToCreate.Enqueue(new RecordingActor("definition-preferred", new WorkflowGAgent()));
        var port = CreatePort(runtime);

        var receipt = await port.EnsureDefinitionAsync(
            new WorkflowDefinitionBinding(
                "definition-preferred",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            "definition-preferred",
            CancellationToken.None);

        receipt.ActorId.Should().Be("definition-preferred");
        runtime.CreateRequests.Should().ContainSingle()
            .Which.Should().Be((typeof(WorkflowGAgent), "definition-preferred"));
    }

    [Fact]
    public async Task CreateRunAsync_WhenExistingDefinitionMatches_ShouldReuseDefinitionActor()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "direct";
        definitionAgent.State.WorkflowYaml = "name: direct\nroles: []\nsteps: []\n";
        runtime.StoredActors["definition-1"] = new RecordingActor("definition-1", definitionAgent);
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-1", new StubAgent("run-1")));
        var port = CreatePort(runtime);

        var result = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-1",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        result.DefinitionActorId.Should().Be("definition-1");
        result.CreatedActorIds.Should().Equal("run-1");
        runtime.CreateRequests.Should().ContainSingle()
            .Which.Should().Match<(Type AgentType, string? RequestedId)>(x =>
                x.AgentType == typeof(WorkflowRunGAgent) &&
                x.RequestedId != null &&
                x.RequestedId.StartsWith("definition-1:run:", StringComparison.Ordinal));
        runtime.Linked.Should().ContainSingle(x => x.ParentId == "definition-1" && x.ChildId == "run-1");
    }

    [Fact]
    public async Task CreateRunAsync_ShouldPropagateScopeIdIntoRunBindingEvent()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "direct";
        definitionAgent.State.WorkflowYaml = "name: direct\nroles: []\nsteps: []\n";
        runtime.StoredActors["definition-scope"] = new RecordingActor("definition-scope", definitionAgent);
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-scope", new StubAgent("run-scope")));
        var port = CreatePort(runtime);

        await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-scope",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                "scope-user-1"),
            CancellationToken.None);

        var bindEvent = ((RecordingActor)runtime.StoredActors["run-scope"]).LastHandledEnvelope!.Payload!
            .Unpack<BindWorkflowRunDefinitionEvent>();
        bindEvent.ScopeId.Should().Be("scope-user-1");
    }

    [Fact]
    public async Task CreateRunAsync_WhenExistingDefinitionActorIsUnbound_ShouldBindItInPlace()
    {
        var runtime = new RecordingActorRuntime();
        var definitionActor = new RecordingActor("definition-2", new WorkflowGAgent());
        runtime.StoredActors["definition-2"] = definitionActor;
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-2", new StubAgent("run-2")));
        var port = CreatePort(runtime);

        var result = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-2",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        result.DefinitionActorId.Should().Be("definition-2");
        runtime.CreateRequests.Should().ContainSingle()
            .Which.Should().Match<(Type AgentType, string? RequestedId)>(x =>
                x.AgentType == typeof(WorkflowRunGAgent) &&
                x.RequestedId != null &&
                x.RequestedId.StartsWith("definition-2:run:", StringComparison.Ordinal));
        definitionActor.LastHandledEnvelope.Should().NotBeNull();
        definitionActor.LastHandledEnvelope!.Payload!.Is(BindWorkflowDefinitionEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task CreateRunAsync_WhenExistingDefinitionYamlDiffersButWorkflowNameMatches_ShouldRebindExistingDefinitionActor()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "direct";
        definitionAgent.State.WorkflowYaml = "name: direct\nroles: []\nsteps:\n  - id: old\n    type: delay\n";
        runtime.StoredActors["definition-3"] = new RecordingActor("definition-3", definitionAgent);
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-3", new StubAgent("run-3")));
        var port = CreatePort(runtime);

        var result = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-3",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        result.DefinitionActorId.Should().Be("definition-3");
        result.CreatedActorIds.Should().Equal("run-3");
        runtime.CreateRequests.Should().ContainSingle()
            .Which.Should().Match<(Type AgentType, string? RequestedId)>(x =>
                x.AgentType == typeof(WorkflowRunGAgent) &&
                x.RequestedId != null &&
                x.RequestedId.StartsWith("definition-3:run:", StringComparison.Ordinal));
        ((RecordingActor)runtime.StoredActors["definition-3"]).LastHandledEnvelope.Should().NotBeNull();
        ((RecordingActor)runtime.StoredActors["definition-3"]).LastHandledEnvelope!.Payload!
            .Is(BindWorkflowDefinitionEvent.Descriptor)
            .Should().BeTrue();
    }

    [Fact]
    public async Task CreateRunAsync_WhenExistingDefinitionWorkflowNameDiffers_ShouldFailFast()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "other";
        definitionAgent.State.WorkflowYaml = "name: other\nroles: []\nsteps: []\n";
        runtime.StoredActors["definition-3"] = new RecordingActor("definition-3", definitionAgent);
        var port = CreatePort(runtime);

        var act = async () => await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-3",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already bound to workflow 'other'*cannot switch to 'direct'*");
        runtime.CreateRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateRunAsync_WhenRequestedDefinitionIdBelongsToUnsupportedActor_ShouldFailFast()
    {
        var runtime = new RecordingActorRuntime();
        runtime.StoredActors["definition-4"] = new RecordingActor("definition-4", new StubAgent("unsupported"));
        runtime.ActorsToCreate.Enqueue(new RecordingActor("definition-4b", new StubAgent("definition-4b")));
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-4", new StubAgent("run-4")));
        var port = CreatePort(runtime);

        var act = async () => await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-4",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not a workflow definition actor*");
        runtime.CreateRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateRunAsync_WhenDefinitionBindingInvalid_ShouldThrow()
    {
        var runtime = new RecordingActorRuntime();
        var port = CreatePort(runtime);

        var act = async () => await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-5",
                " ",
                " ",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*valid workflow definition binding*");
        runtime.CreateRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateRunAsync_WhenBindingReaderReturnsNullForExistingActor_ShouldRebindFromCatalogPayload()
    {
        // A null binding doc on an existing definition actor is the signature of a clobbered binding
        // slot (the studio failure). With a definition payload in hand, the run self-heals by re-binding
        // the definition instead of failing fast.
        var runtime = new RecordingActorRuntime();
        var definitionActor = new RecordingActor("definition-missing-binding", new WorkflowGAgent());
        runtime.StoredActors["definition-missing-binding"] = definitionActor;
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-missing-binding", new StubAgent("run-missing-binding")));
        var port = CreatePort(
            runtime,
            new StaticWorkflowActorBindingReader(new Dictionary<string, WorkflowActorBinding?>(StringComparer.Ordinal)
            {
                ["definition-missing-binding"] = null,
            }));

        var result = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-missing-binding",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        result.DefinitionActorId.Should().Be("definition-missing-binding");
        definitionActor.LastHandledEnvelope.Should().NotBeNull();
        definitionActor.LastHandledEnvelope!.Payload!.Is(BindWorkflowDefinitionEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task CreateRunAsync_WhenExistingDefinitionSlotHoldsRunKind_ShouldRebindFromCatalogPayload()
    {
        // The exact studio clobber: the definition actor's binding read-model was overwritten with a
        // Run-kind document. The run heals by re-binding the definition from the catalog payload.
        var runtime = new RecordingActorRuntime();
        var definitionActor = new RecordingActor("workflow-definition:studio", new WorkflowGAgent());
        runtime.StoredActors["workflow-definition:studio"] = definitionActor;
        runtime.ActorsToCreate.Enqueue(new RecordingActor("studio-run", new StubAgent("studio-run")));
        var port = CreatePort(
            runtime,
            new StaticWorkflowActorBindingReader(new Dictionary<string, WorkflowActorBinding?>(StringComparer.Ordinal)
            {
                ["workflow-definition:studio"] = new(
                    WorkflowActorKind.Run,
                    "workflow-definition:studio",
                    "workflow-definition:studio",
                    "workflow-definition:studio:run:old",
                    "studio",
                    "name: studio\nroles: []\nsteps: []\n",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    SourceVersion: 701),
            }));

        var result = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "workflow-definition:studio",
                "studio",
                "name: studio\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        result.DefinitionActorId.Should().Be("workflow-definition:studio");
        definitionActor.LastHandledEnvelope.Should().NotBeNull();
        definitionActor.LastHandledEnvelope!.Payload!.Is(BindWorkflowDefinitionEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task ParseWorkflowYamlAsync_WhenEmptyOrMissingName_ShouldReturnInvalid()
    {
        var port = CreatePort(new RecordingActorRuntime());

        var empty = await port.ParseWorkflowYamlAsync(" ", CancellationToken.None);
        var missingName = await port.ParseWorkflowYamlAsync(
            """
            roles: []
            steps: []
            """,
            CancellationToken.None);

        empty.Succeeded.Should().BeFalse();
        empty.Error.Should().Contain("required");
        missingName.Succeeded.Should().BeFalse();
        missingName.Error.Should().Contain("name");
    }

    [Fact]
    public async Task ParseWorkflowYamlAsync_WhenStepTypeUnknown_ShouldReturnInvalid()
    {
        var port = CreatePort(new RecordingActorRuntime());

        var result = await port.ParseWorkflowYamlAsync(
            """
            name: sample
            steps:
              - id: step1
                type: does_not_exist
            """,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("does_not_exist");
    }

    [Theory]
    [InlineData("{\"slug\":\"home-assistant\",\"operation_id\":\"list-items\",\"method\":\"GET\",\"path\":\"/api/items\",\"contract_digest\":\"sha256:home-v1\"}", "service_id")]
    [InlineData("{\"service_id\":\"us-home-alpha\",\"slug\":\"home-assistant\",\"operation_id\":\"list-items\",\"method\":\"GET\",\"path\":\"/api/items\",\"contract_digest\":\"sha256:home-v1\",\"headers\":{\"Authorization\":\"forbidden\"}}", "sensitive header")]
    public async Task ParseWorkflowYamlAsync_WhenNyxIdCapabilityIsNotExact_ShouldReturnInvalid(
        string arguments,
        string expectedError)
    {
        var port = CreatePort(new RecordingActorRuntime());

        var result = await port.ParseWorkflowYamlAsync(
            $$"""
            name: sample
            roles: []
            steps:
              - id: proxy
                type: tool_call
                parameters:
                  tool: nyxid_proxy
                  arguments: '{{arguments}}'
            """,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain(expectedError);
        result.AuthorizationDependencies.Should().BeNull();
    }

    [Fact]
    public async Task ParseWorkflowYamlAsync_WhenRoleAgentKindIsDefaultPrimary_ShouldReturnSuccess()
    {
        var port = CreatePort(
            new RecordingActorRuntime(),
            agentKindRegistry: CreateRoleAgentKindRegistry());

        var result = await port.ParseWorkflowYamlAsync(
            """
            name: sample
            roles:
              - id: assistant
                name: Assistant
                agent_kind: workflow.role-agent
            steps:
              - id: step1
                type: llm_call
                target_role: assistant
            """,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.WorkflowName.Should().Be("sample");
    }

    [Fact]
    public async Task ParseWorkflowYamlAsync_WhenRoleAgentKindIsMissing_ShouldDefaultAndReturnSuccess()
    {
        var port = CreatePort(
            new RecordingActorRuntime(),
            agentKindRegistry: CreateRoleAgentKindRegistry());

        var result = await port.ParseWorkflowYamlAsync(
            """
            name: sample
            roles:
              - id: assistant
                name: Assistant
            steps:
              - id: step1
                type: llm_call
                target_role: assistant
            """,
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.WorkflowName.Should().Be("sample");
    }

    [Fact]
    public async Task ParseWorkflowYamlAsync_WhenRoleAgentKindIsUnknown_ShouldReturnActionableInvalidResult()
    {
        var port = CreatePort(
            new RecordingActorRuntime(),
            agentKindRegistry: CreateRoleAgentKindRegistry());

        var result = await port.ParseWorkflowYamlAsync(
            """
            name: sample
            roles:
              - id: bridge
                name: Bridge
                agent_kind: workflow.missing-kind
            steps:
              - id: step1
                type: llm_call
                target_role: bridge
            """,
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("bridge");
        result.Error.Should().Contain("workflow.missing-kind");
        result.Error.Should().Contain(WorkflowRoleConventions.DefaultAgentKind);
    }

    [Fact]
    public async Task CreateRunAsync_WhenInlineDefinitionsDiffer_ShouldRebindExistingDefinitionActor()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "direct";
        definitionAgent.State.WorkflowYaml = "name: direct\nroles: []\nsteps: []\n";
        definitionAgent.State.InlineWorkflowYamls["child"] = "name: child\nroles: []\nsteps: []\n";
        runtime.StoredActors["definition-inline"] = new RecordingActor("definition-inline", definitionAgent);
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-inline", new StubAgent("run-inline")));
        var port = CreatePort(runtime);

        var result = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-inline",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["child"] = "name: child-updated\nroles: []\nsteps: []\n",
                }),
            CancellationToken.None);

        result.DefinitionActorId.Should().Be("definition-inline");
        runtime.CreateRequests.Should().ContainSingle()
            .Which.Should().Match<(Type AgentType, string? RequestedId)>(x =>
                x.AgentType == typeof(WorkflowRunGAgent) &&
                x.RequestedId != null &&
                x.RequestedId.StartsWith("definition-inline:run:", StringComparison.Ordinal));
        ((RecordingActor)runtime.StoredActors["definition-inline"]).LastHandledEnvelope.Should().NotBeNull();
        ((RecordingActor)runtime.StoredActors["definition-inline"]).LastHandledEnvelope!.Payload!
            .Is(BindWorkflowDefinitionEvent.Descriptor)
            .Should().BeTrue();
    }

    [Fact]
    public async Task BindWorkflowDefinitionAsync_ShouldValidateMissingActorIdInput()
    {
        var port = CreatePort(new RecordingActorRuntime());

        await FluentActions.Invoking(() => port.BindWorkflowDefinitionAsync(" ", "name: x", "x", null, ct: CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task BindWorkflowDefinitionAsync_ShouldDispatchEnvelopeWithInlineWorkflowMap()
    {
        var runtime = new RecordingActorRuntime();
        var actor = new RecordingActor("definition-inline-bind", new WorkflowGAgent());
        runtime.StoredActors[actor.Id] = actor;
        var port = CreatePort(runtime);

        await port.BindWorkflowDefinitionAsync(
            actor.Id,
            "name: direct\nroles: []\nsteps: []\n",
            "direct",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["child"] = "name: child\nroles: []\nsteps: []\n",
            },
            ct: CancellationToken.None);

        actor.LastHandledEnvelope.Should().NotBeNull();
        actor.LastHandledEnvelope!.Payload!.Is(BindWorkflowDefinitionEvent.Descriptor).Should().BeTrue();
        var bind = actor.LastHandledEnvelope.Payload.Unpack<BindWorkflowDefinitionEvent>();
        bind.WorkflowName.Should().Be("direct");
        bind.InlineWorkflowYamls.Should().ContainKey("child");
    }

    [Fact]
    public async Task CreateRunAsync_ShouldDispatchRunBindingWithoutProjectionActivationPorts()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "direct";
        definitionAgent.State.WorkflowYaml = "name: direct\nroles: []\nsteps: []\n";
        runtime.StoredActors["definition-projection"] = new RecordingActor("definition-projection", definitionAgent);
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-projection", new StubAgent("run-projection")));
        var port = CreatePort(runtime);

        await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-projection",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        ((RecordingActor)runtime.StoredActors["run-projection"]).LastHandledEnvelope.Should().NotBeNull();
        ((RecordingActor)runtime.StoredActors["run-projection"]).LastHandledEnvelope!.Payload!
            .Is(BindWorkflowRunDefinitionEvent.Descriptor)
            .Should().BeTrue();
    }

    [Fact]
    public async Task CreateRunAsync_ShouldNotRollback_WhenProjectionActivationPortWouldHaveFailed()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "direct";
        definitionAgent.State.WorkflowYaml = "name: direct\nroles: []\nsteps: []\n";
        runtime.StoredActors["definition-projection-fail"] = new RecordingActor("definition-projection-fail", definitionAgent);
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-projection-fail", new StubAgent("run-projection-fail")));
        var port = CreatePort(runtime);

        await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-projection-fail",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        runtime.Destroyed.Should().BeEmpty();
        ((RecordingActor)runtime.StoredActors["run-projection-fail"]).LastHandledEnvelope.Should().NotBeNull();
    }

    [Fact]
    public async Task DestroyAsync_ShouldRejectBlankActorId()
    {
        var port = CreatePort(new RecordingActorRuntime());

        var act = async () => await port.DestroyAsync(" ", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateRunAsync_WhenBindingReaderMarksProxyAsDefinition_ShouldReuseDefinitionActor()
    {
        var runtime = new RecordingActorRuntime();
        runtime.StoredActors["definition-proxy"] = new RecordingActor("definition-proxy", new StubAgent("proxy"));
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-proxy", new StubAgent("run-proxy")));
        var port = CreatePort(
            runtime,
            new StaticWorkflowActorBindingReader(new Dictionary<string, WorkflowActorBinding?>(StringComparer.Ordinal)
            {
                ["definition-proxy"] = new(
                    WorkflowActorKind.Definition,
                    "definition-proxy",
                    "definition-proxy",
                    string.Empty,
                    "direct",
                    "name: direct\nroles: []\nsteps: []\n",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            }));

        var result = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-proxy",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        result.DefinitionActorId.Should().Be("definition-proxy");
        result.CreatedActorIds.Should().Equal("run-proxy");
        runtime.CreateRequests.Should().ContainSingle()
            .Which.Should().Match<(Type AgentType, string? RequestedId)>(x =>
                x.AgentType == typeof(WorkflowRunGAgent) &&
                x.RequestedId != null &&
                x.RequestedId.StartsWith("definition-proxy:run:", StringComparison.Ordinal));
        runtime.Linked.Should().ContainSingle(x => x.ParentId == "definition-proxy" && x.ChildId == "run-proxy");
    }

    [Fact]
    public async Task CreateRunAsync_WhenDefinitionBindFails_ShouldDestroyCreatedDefinitionActor()
    {
        var runtime = new RecordingActorRuntime
        {
            DispatchExceptionFactory = (actorId, envelope) =>
                actorId == "definition-fail" &&
                envelope.Payload?.Is(BindWorkflowDefinitionEvent.Descriptor) == true
                    ? new InvalidOperationException("definition bind failed")
                    : null,
        };
        runtime.ActorsToCreate.Enqueue(new RecordingActor("definition-fail", new WorkflowGAgent()));
        var port = CreatePort(runtime);

        var act = async () => await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                string.Empty,
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("definition bind failed");
        runtime.Destroyed.Should().Equal("definition-fail");
    }

    [Fact]
    public async Task CreateRunAsync_WhenRunBindFails_ShouldDestroyCreatedRunAndDefinitionActors()
    {
        var runtime = new RecordingActorRuntime
        {
            DispatchExceptionFactory = (actorId, envelope) =>
                actorId == "run-fail" &&
                envelope.Payload?.Is(BindWorkflowRunDefinitionEvent.Descriptor) == true
                    ? new InvalidOperationException("run bind failed")
                    : null,
        };
        runtime.ActorsToCreate.Enqueue(new RecordingActor("definition-fail", new WorkflowGAgent()));
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-fail", new StubAgent("run-fail")));
        var port = CreatePort(runtime);

        var act = async () => await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                string.Empty,
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("run bind failed");
        runtime.Destroyed.Should().Equal("run-fail", "definition-fail");
    }

    [Fact]
    public async Task CreateRunAsync_WhenDefinitionCreateRaces_ShouldReuseWinnerAndContinue()
    {
        var runtime = new RecordingActorRuntime();
        var racedDefinition = new RecordingActor("definition-race", new WorkflowGAgent());
        runtime.CreateExceptionFactory = (agentType, requestedId) =>
        {
            if (agentType == typeof(WorkflowGAgent) &&
                string.Equals(requestedId, "definition-race", StringComparison.Ordinal))
            {
                runtime.StoredActors["definition-race"] = racedDefinition;
                return new InvalidOperationException("Actor definition-race already exists");
            }

            return null;
        };
        runtime.ActorsToCreate.Enqueue(new RecordingActor("run-race", new StubAgent("run-race")));
        var port = CreatePort(runtime);

        var result = await port.CreateRunAsync(
            new WorkflowDefinitionBinding(
                "definition-race",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);

        result.DefinitionActorId.Should().Be("definition-race");
        result.CreatedActorIds.Should().Equal("run-race");
        runtime.CreateRequests.Should().Contain((typeof(WorkflowGAgent), "definition-race"));
        runtime.CreateRequests.Should().Contain(x =>
            x.AgentType == typeof(WorkflowRunGAgent) &&
            x.RequestedId != null &&
            x.RequestedId.StartsWith("definition-race:run:", StringComparison.Ordinal));
        racedDefinition.LastHandledEnvelope.Should().NotBeNull();
        racedDefinition.LastHandledEnvelope!.Payload!.Is(BindWorkflowDefinitionEvent.Descriptor).Should().BeTrue();
    }

    [Fact]
    public async Task EnsureRunAsync_ShouldUseExactRunIdentityAndIdempotentBindingCommand()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "direct";
        definitionAgent.State.WorkflowYaml = "name: direct\nroles: []\nsteps: []\n";
        runtime.StoredActors["definition-stable"] = new RecordingActor("definition-stable", definitionAgent);
        runtime.ActorsToCreate.Enqueue(new RecordingActor("work-order-run-1", new StubAgent("work-order-run-1")));
        var port = CreatePort(runtime);

        var result = await port.EnsureRunAsync(
            new WorkflowDefinitionBinding(
                "definition-stable",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                "scope-1",
                WorkflowRunOrigins.WorkOrder),
            "work-order-run-1",
            CancellationToken.None);

        result.ActorId.Should().Be("work-order-run-1");
        runtime.CreateRequests.Should().Contain((typeof(WorkflowRunGAgent), "work-order-run-1"));
        var envelope = ((RecordingActor)runtime.StoredActors["work-order-run-1"]).LastHandledEnvelope;
        envelope.Should().NotBeNull();
        envelope!.Id.Should().Be("ensure-workflow-run-work-order-run-1");
        var ensure = envelope.Payload!.Unpack<EnsureWorkflowRunDefinitionEvent>();
        ensure.Binding.RunId.Should().Be("work-order-run-1");
        ensure.Binding.ScopeId.Should().Be("scope-1");
        ensure.Binding.RunOrigin.Should().Be(WorkflowRunOrigins.WorkOrder);
    }

    [Fact]
    public async Task EnsureRunAsync_ShouldNotMutateTopologyBeforeAcceptedBindingIsHandled()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "direct";
        definitionAgent.State.WorkflowYaml = "name: direct\nroles: []\nsteps: []\n";
        runtime.StoredActors["definition-stable"] = new RecordingActor("definition-stable", definitionAgent);
        runtime.ActorsToCreate.Enqueue(new RecordingActor("work-order-run-1", new StubAgent("work-order-run-1")));
        var acceptedOnlyDispatch = new AcceptedOnlyDispatchPort();
        var port = new WorkflowRunActorPort(
            runtime,
            acceptedOnlyDispatch,
            new RuntimeBackedWorkflowActorBindingReader(runtime),
            [new WorkflowCoreModulePack()]);

        await port.EnsureRunAsync(
            new WorkflowDefinitionBinding(
                "definition-stable",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                "scope-1",
                WorkflowRunOrigins.WorkOrder),
            "work-order-run-1",
            CancellationToken.None);

        runtime.Linked.Should().BeEmpty();
        acceptedOnlyDispatch.Envelopes.Should().ContainSingle();
    }

    [Fact]
    public async Task EnsureRunAndDispatchAsync_ShouldSendOneCombinedExactRunCommand()
    {
        var runtime = new RecordingActorRuntime();
        var definitionAgent = new WorkflowGAgent();
        definitionAgent.State.WorkflowName = "direct";
        definitionAgent.State.WorkflowYaml = "name: direct\nroles: []\nsteps: []\n";
        runtime.StoredActors["definition-stable"] = new RecordingActor("definition-stable", definitionAgent);
        runtime.ActorsToCreate.Enqueue(new RecordingActor("work-order-run-1", new StubAgent("work-order-run-1")));
        var acceptedOnlyDispatch = new AcceptedOnlyDispatchPort();
        var port = new WorkflowRunActorPort(
            runtime,
            acceptedOnlyDispatch,
            new RuntimeBackedWorkflowActorBindingReader(runtime),
            [new WorkflowCoreModulePack()]);

        var result = await port.EnsureRunAndDispatchAsync(
            new WorkflowDefinitionBinding(
                "definition-stable",
                "direct",
                "name: direct\nroles: []\nsteps: []\n",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                "scope-1",
                WorkflowRunOrigins.WorkOrder),
            "work-order-run-1",
            new WorkflowChatRequestEvent { Prompt = "execute once" },
            "work-order-command-1",
            "work-order-correlation-1",
            CancellationToken.None);

        result.ActorId.Should().Be("work-order-run-1");
        var envelope = acceptedOnlyDispatch.Envelopes.Should().ContainSingle().Subject;
        envelope.Id.Should().Be("work-order-command-1");
        envelope.Propagation.CorrelationId.Should().Be("work-order-correlation-1");
        var command = envelope.Payload!.Unpack<EnsureWorkflowRunDefinitionEvent>();
        command.Binding.RunId.Should().Be("work-order-run-1");
        command.ExecutionRequest.Prompt.Should().Be("execute once");
        runtime.Linked.Should().BeEmpty();
    }

    private static WorkflowRunActorPort CreatePort(
        RecordingActorRuntime runtime,
        IWorkflowActorBindingReader? bindingReader = null,
        IAgentKindRegistry? agentKindRegistry = null) =>
        new(
            runtime,
            runtime,
            bindingReader ?? new RuntimeBackedWorkflowActorBindingReader(runtime),
            [new WorkflowCoreModulePack()],
            agentKindRegistry);

    private static IAgentKindRegistry CreateRoleAgentKindRegistry() =>
        new AgentKindRegistry(
        [
            new AgentRegistration(
                Kind: "workflow.role-agent",
                ImplementationType: typeof(StubAgent),
                StateContractType: typeof(object)),
        ]);

    private static WorkflowGAgent CreateWorkflowDefinitionAgent()
    {
        var eventStore = new InMemoryEventStore();
        var services = new ServiceCollection()
            .AddSingleton(eventStore)
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton<IStreamProvider, InMemoryStreamProvider>()
            .AddSingleton<InMemoryActorRuntimeCallbackScheduler>()
            .AddSingleton<IActorRuntimeCallbackScheduler>(sp =>
                sp.GetRequiredService<InMemoryActorRuntimeCallbackScheduler>())
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();
        var agent = new WorkflowGAgent();
        agent.Services = services;
        agent.EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowState>>();
        return agent;
    }

    private static string ResolveRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (File.Exists(Path.Combine(current, "aevatar.slnx")))
                return current;

            current = Path.GetDirectoryName(current) ?? string.Empty;
        }

        throw new InvalidOperationException("Could not resolve repository root.");
    }

    private sealed class RecordingActorRuntime : IActorRuntime, IActorDispatchPort
    {
        private IActor? _lastCreatedActor;

        public Dictionary<string, IActor> StoredActors { get; } = new(StringComparer.Ordinal);

        public Queue<IActor> ActorsToCreate { get; } = new();

        public List<(Type AgentType, string? RequestedId)> CreateRequests { get; } = [];

        public List<(string ParentId, string ChildId)> Linked { get; } = [];
        public List<string> Destroyed { get; } = [];
        public Func<Type, string?, Exception?>? CreateExceptionFactory { get; set; }
        public Func<string, EventEnvelope, Exception?>? DispatchExceptionFactory { get; set; }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CreateRequests.Add((agentType, id));
            var createException = CreateExceptionFactory?.Invoke(agentType, id);
            if (createException != null)
                throw createException;

            if (ActorsToCreate.Count > 0)
            {
                var createdActor = ActorsToCreate.Dequeue();
                StoredActors[createdActor.Id] = createdActor;
                _lastCreatedActor = createdActor;
                return Task.FromResult(createdActor);
            }

            var generatedActor = new RecordingActor(id ?? Guid.NewGuid().ToString("N"), new StubAgent("generated"));
            StoredActors[generatedActor.Id] = generatedActor;
            _lastCreatedActor = generatedActor;
            return Task.FromResult<IActor>(generatedActor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Destroyed.Add(id);
            StoredActors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(
                StoredActors.TryGetValue(id, out var actor)
                    ? actor
                    : _lastCreatedActor != null && string.Equals(_lastCreatedActor.Id, id, StringComparison.Ordinal)
                        ? _lastCreatedActor
                        : null);

        public async Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var dispatchException = DispatchExceptionFactory?.Invoke(actorId, envelope);
            if (dispatchException != null)
                throw dispatchException;

            var actor = await GetAsync(actorId) ?? throw new InvalidOperationException($"Actor {actorId} not found.");
            await actor.HandleEventAsync(envelope, ct);
            return DispatchAdmissionFactory.Create(actorId, envelope);
        }

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(StoredActors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Linked.Add((parentId, childId));
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class AcceptedOnlyDispatchPort : IActorDispatchPort
    {
        public List<EventEnvelope> Envelopes { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Envelopes.Add(envelope.Clone());
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingActor : IActor
    {
        private readonly bool _forwardToAgent;

        public RecordingActor(string id, IAgent agent, bool forwardToAgent = false)
        {
            Id = id;
            Agent = agent;
            _forwardToAgent = forwardToAgent;
        }

        public string Id { get; }

        public IAgent Agent { get; }

        public EventEnvelope? LastHandledEnvelope { get; private set; }

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            LastHandledEnvelope = envelope;
            if (_forwardToAgent)
                await Agent.HandleEventAsync(envelope, ct);
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("stub");

        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RuntimeBackedWorkflowActorBindingReader(RecordingActorRuntime runtime) : IWorkflowActorBindingReader
    {
        public async Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var actor = await runtime.GetAsync(actorId);
            if (actor == null)
                return null;

            return actor.Agent switch
            {
                WorkflowGAgent definition => new WorkflowActorBinding(
                    WorkflowActorKind.Definition,
                    actor.Id,
                    actor.Id,
                    string.Empty,
                    definition.State.WorkflowName,
                    definition.State.WorkflowYaml,
                    definition.State.InlineWorkflowYamls.ToDictionary(
                        static x => x.Key,
                        static x => x.Value,
                        StringComparer.OrdinalIgnoreCase)),
                WorkflowRunGAgent run => new WorkflowActorBinding(
                    WorkflowActorKind.Run,
                    actor.Id,
                    run.State.DefinitionActorId,
                    run.State.RunId,
                    run.State.WorkflowName.Trim(),
                    run.State.WorkflowYaml,
                    run.State.InlineWorkflowYamls.ToDictionary(
                        static x => x.Key,
                        static x => x.Value,
                        StringComparer.OrdinalIgnoreCase)),
                _ => WorkflowActorBinding.Unsupported(actor.Id),
            };
        }
    }

    private sealed class StaticWorkflowActorBindingReader(IReadOnlyDictionary<string, WorkflowActorBinding?> mappings) : IWorkflowActorBindingReader
    {
        public Task<WorkflowActorBinding?> GetAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            mappings.TryGetValue(actorId, out var binding);
            return Task.FromResult(binding);
        }
    }

    private sealed class FakeStepExecutorFactory : IEventModuleFactory<IWorkflowExecutionContext>
    {
        public bool TryCreate(string name, out IEventModule<IWorkflowExecutionContext>? module)
        {
            _ = name;
            module = null;
            return false;
        }
    }
}
