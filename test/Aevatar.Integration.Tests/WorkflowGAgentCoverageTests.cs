using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Agents;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Async;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Async;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Composition;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;

namespace Aevatar.Integration.Tests;

public class WorkflowGAgentCoverageTests
{
    [Fact]
    public async Task ConfigureWorkflow_WhenSwitchingWorkflowName_ShouldThrow()
    {
        var agent = CreateAgent();
        await agent.BindWorkflowDefinitionAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_a");

        Func<Task> act = () => agent.BindWorkflowDefinitionAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_b");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot switch*");
    }

    [Fact]
    public async Task ConfigureWorkflow_WithEmptyYaml_ShouldMarkInvalidAndDescribe()
    {
        var agent = CreateAgent();

        await agent.BindWorkflowDefinitionAsync("", "wf_empty");
        var description = await agent.GetDescriptionAsync();

        agent.State.Compiled.Should().BeFalse();
        agent.State.CompilationError.Should().Be("workflow yaml is empty");
        description.Should().Contain("invalid");
        description.Should().Contain("wf_empty");
    }

    [Fact]
    public async Task HandleChatRequest_WhenNotCompiled_ShouldPublishFailureResponse()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var agent = CreateAgent(runtime: runtime);
        agent.EventPublisher = publisher;
        await agent.BindWorkflowDefinitionAsync("", "wf_invalid");

        await agent.HandleChatRequest(new ChatRequestEvent
        {
            Prompt = "hello",
            SessionId = "s1",
        });

        runtime.CreateCalls.Should().Be(0);
        publisher.Published.Should().ContainSingle();
        var response = publisher.Published[0].evt.Should().BeOfType<ChatResponseEvent>().Subject;
        response.Content.Should().Contain("not compiled");
        response.SessionId.Should().Be("s1");
    }

    [Fact]
    public async Task HandleChatRequest_WhenCompiled_ShouldCreateRoleActorsOnlyOnceAndStartWorkflow()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var resolver = new StaticRoleAgentTypeResolver(typeof(FakeRoleAgent));
        var agent = CreateAgent(runtime, resolver);
        agent.EventPublisher = publisher;
        await agent.BindWorkflowDefinitionAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_ok");

        await agent.HandleChatRequest(new ChatRequestEvent { Prompt = "first", SessionId = "s1" });
        await agent.HandleChatRequest(new ChatRequestEvent { Prompt = "second", SessionId = "s2" });

        runtime.CreateCalls.Should().Be(1);
        runtime.Linked.Should().ContainSingle();
        runtime.Linked[0].child.Should().EndWith(":role_a");

        var roleAgent = runtime.CreatedActors.Single().Agent.Should().BeOfType<FakeRoleAgent>().Subject;
        roleAgent.RoleName.Should().Be("RoleA");
        roleAgent.LastInitializeEvent.Should().NotBeNull();
        roleAgent.LastInitializeEvent!.ProviderName.Should().BeEmpty();
        roleAgent.LastInitializeEvent.Model.Should().BeEmpty();
        roleAgent.LastInitializeEvent.SystemPrompt.Should().Be("helpful role");

        var starts = publisher.Published.Select(x => x.evt).OfType<StartWorkflowEvent>().ToList();
        starts.Should().HaveCount(2);
        starts.Should().OnlyContain(x => x.WorkflowName == "wf_valid");
    }

    [Fact]
    public async Task HandleChatRequest_ShouldPassThroughFullRoleConfigurationToInitializeEvent()
    {
        var runtime = new RecordingActorRuntime();
        var resolver = new StaticRoleAgentTypeResolver(typeof(FakeRoleAgent));
        var agent = CreateAgent(runtime, resolver);
        await agent.BindWorkflowDefinitionAsync(BuildWorkflowYamlWithFullRoleConfig(), "wf_role_fields");

        await agent.HandleChatRequest(new ChatRequestEvent { Prompt = "hello", SessionId = "s1" });

        var roleAgent = runtime.CreatedActors.Single().Agent.Should().BeOfType<FakeRoleAgent>().Subject;
        roleAgent.LastInitializeEvent.Should().NotBeNull();
        var initializeEvent = roleAgent.LastInitializeEvent!;
        initializeEvent.RoleName.Should().Be("RoleA");
        initializeEvent.ProviderName.Should().Be("openai");
        initializeEvent.Model.Should().Be("gpt-4o-mini");
        initializeEvent.SystemPrompt.Should().Be("helpful role");
        initializeEvent.HasTemperature.Should().BeTrue();
        initializeEvent.Temperature.Should().Be(0.2);
        initializeEvent.MaxTokens.Should().Be(256);
        initializeEvent.MaxToolRounds.Should().Be(4);
        initializeEvent.MaxHistoryMessages.Should().Be(30);
        initializeEvent.StreamBufferCapacity.Should().Be(64);
        initializeEvent.EventModules.Should().Be("llm_handler,tool_handler");
        initializeEvent.EventRoutes.Should().Contain("event.type");
    }

    [Fact]
    public async Task HandleChatRequest_WhenResolvedAgentNotIRoleAgent_ShouldThrow()
    {
        var runtime = new RecordingActorRuntime();
        var resolver = new StaticRoleAgentTypeResolver(typeof(FakeNonRoleAgent));
        var agent = CreateAgent(runtime, resolver);
        await agent.BindWorkflowDefinitionAsync(BuildValidWorkflowYaml("role_x", "RoleX"), "wf_error");

        var act = async () => await agent.HandleChatRequest(new ChatRequestEvent { Prompt = "x", SessionId = "s" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not implement IRoleAgent*");
    }

    [Fact]
    public async Task HandleChatRequest_WhenWorkflowRoleSpecifiesProviderAndModel_ShouldPreserveValues()
    {
        var runtime = new RecordingActorRuntime();
        var resolver = new StaticRoleAgentTypeResolver(typeof(FakeRoleAgent));
        var agent = CreateAgent(runtime, resolver);
        await agent.BindWorkflowDefinitionAsync(
            BuildValidWorkflowYaml(
                roleId: "role_cfg",
                roleName: "RoleCfg",
                provider: "openai-godgpt-doubao",
                model: "godgpt-testnet"),
            "wf_provider_model");

        await agent.HandleChatRequest(new ChatRequestEvent { Prompt = "hello", SessionId = "s1" });

        var roleAgent = runtime.CreatedActors.Single().Agent.Should().BeOfType<FakeRoleAgent>().Subject;
        roleAgent.LastInitializeEvent.Should().NotBeNull();
        roleAgent.LastInitializeEvent!.ProviderName.Should().Be("openai-godgpt-doubao");
        roleAgent.LastInitializeEvent.Model.Should().Be("godgpt-testnet");
    }

    [Fact]
    public async Task HandleChatRequest_WhenRoleIdMissing_ShouldThrow()
    {
        var runtime = new RecordingActorRuntime();
        var resolver = new StaticRoleAgentTypeResolver(typeof(FakeRoleAgent));
        var agent = CreateAgent(runtime, resolver);
        await agent.BindWorkflowDefinitionAsync(BuildValidWorkflowYaml("", "RoleNoId"), "wf_missing_role");

        var act = async () => await agent.HandleChatRequest(new ChatRequestEvent { Prompt = "x", SessionId = "s" });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Role id is required*");
    }

    [Fact]
    public async Task HandleWorkflowCompleted_ShouldUpdateCountersAndPublishFinalText()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent();
        agent.EventPublisher = publisher;

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "wf",
            Success = true,
            Output = "done",
        });

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "wf",
            Success = false,
            Error = "boom",
        });

        agent.State.TotalExecutions.Should().Be(2);
        agent.State.SuccessfulExecutions.Should().Be(1);
        agent.State.FailedExecutions.Should().Be(1);

        var outputs = publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Select(x => x.Content).ToList();
        outputs.Should().Contain("done");
        outputs.Should().Contain(x => x.Contains("failed") && x.Contains("boom"));
    }

    [Fact]
    public async Task HandleEventAsync_WhenSelfWorkflowCompletedEnvelope_ShouldProcessCompletion()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent();
        agent.EventPublisher = publisher;

        await agent.HandleEventAsync(Envelope(
            new WorkflowCompletedEvent
            {
                WorkflowName = "wf",
                RunId = "run-self",
                Success = true,
                Output = "done-via-dispatch",
            },
            publisherId: agent.Id,
            direction: EventDirection.Self));

        agent.State.TotalExecutions.Should().Be(1);
        agent.State.SuccessfulExecutions.Should().Be(1);
        agent.State.FailedExecutions.Should().Be(0);
        publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Single().Content.Should().Be("done-via-dispatch");
    }

    [Fact]
    public async Task HandleEventAsync_WhenExternalWorkflowCompletedEnvelopeWithoutPending_ShouldIgnore()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent();
        agent.EventPublisher = publisher;

        await agent.HandleEventAsync(Envelope(
            new WorkflowCompletedEvent
            {
                WorkflowName = "wf_external",
                RunId = "run-external",
                Success = true,
                Output = "ok",
            },
            publisherId: "external-child",
            direction: EventDirection.Both));

        agent.State.TotalExecutions.Should().Be(0);
        publisher.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleReconfigureAndExecute_WhenYamlInvalid_ShouldKeepPreviousWorkflowState()
    {
        var sharedEventStore = new InMemoryEventStore();
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent(eventStore: sharedEventStore);
        agent.EventPublisher = publisher;

        var originalYaml = BuildValidWorkflowYaml("role_a", "RoleA");
        await agent.BindWorkflowDefinitionAsync(originalYaml, "wf_valid");
        var previousVersion = agent.State.Version;

        await agent.HandleReplaceWorkflowDefinitionAndExecute(new ReplaceWorkflowDefinitionAndExecuteEvent
        {
            WorkflowYaml = "name: broken\nroles: [",
            Input = "hello",
        });

        agent.State.WorkflowYaml.Should().Be(originalYaml);
        agent.State.Compiled.Should().BeTrue();
        agent.State.Version.Should().Be(previousVersion);

        publisher.Published.Select(x => x.evt).OfType<StartWorkflowEvent>().Should().BeEmpty();
        publisher.Published.Select(x => x.evt).OfType<ChatResponseEvent>()
            .Should().ContainSingle(x => x.Content.Contains("Dynamic workflow YAML compilation failed", StringComparison.Ordinal));

        var persisted = await sharedEventStore.GetEventsAsync(agent.Id);
        persisted.Count(x => x.EventType.Contains(nameof(BindWorkflowDefinitionEvent), StringComparison.Ordinal))
            .Should().Be(1, "invalid reconfigure must not persist additional BindWorkflowDefinitionEvent");
    }

    [Fact]
    public async Task HandleReconfigureAndExecute_WhenYamlValid_ShouldPreservePendingInvocationsFromOtherRuns()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var resolver = new StaticWorkflowDefinitionResolver(new Dictionary<string, string>
        {
            ["sub_flow"] = BuildValidWorkflowYaml("sub_role", "SubRole"),
        });
        var agent = CreateAgent(runtime: runtime, workflowResolver: resolver);
        agent.EventPublisher = publisher;
        await agent.BindWorkflowDefinitionAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_valid");

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "pending-1",
            ParentRunId = "run-other",
            ParentStepId = "step-1",
            WorkflowName = "sub_flow",
            Input = "payload",
            Lifecycle = "singleton",
        });
        var pendingBefore = agent.State.PendingSubWorkflowInvocations.Should().ContainSingle().Subject;
        var bindingBefore = agent.State.SubWorkflowBindings.Should().ContainSingle().Subject;

        await agent.HandleReplaceWorkflowDefinitionAndExecute(new ReplaceWorkflowDefinitionAndExecuteEvent
        {
            WorkflowYaml = BuildValidWorkflowYaml("role_b", "RoleB"),
            Input = "dynamic-input",
        });

        var pendingAfter = agent.State.PendingSubWorkflowInvocations.Should().ContainSingle().Subject;
        pendingAfter.InvocationId.Should().Be(pendingBefore.InvocationId);
        pendingAfter.ParentRunId.Should().Be("run-other");
        pendingAfter.ParentStepId.Should().Be("step-1");
        var bindingAfter = agent.State.SubWorkflowBindings.Should().ContainSingle().Subject;
        bindingAfter.WorkflowName.Should().Be(bindingBefore.WorkflowName);
        bindingAfter.Lifecycle.Should().Be(bindingBefore.Lifecycle);
        bindingAfter.ChildActorId.Should().Be(bindingBefore.ChildActorId);

        publisher.Published.Select(x => x.evt).OfType<StartWorkflowEvent>()
            .Should().Contain(x => x.Input == "dynamic-input");
    }

    [Fact]
    public async Task HandleReconfigureAndExecute_WhenRoleActorAlreadyExists_ShouldReuseExistingActor()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime { ThrowOnDuplicateCreate = true };
        var resolver = new StaticRoleAgentTypeResolver(typeof(FakeRoleAgent));
        var agent = CreateAgent(runtime, resolver);
        agent.EventPublisher = publisher;

        await agent.BindWorkflowDefinitionAsync(BuildValidWorkflowYaml("role_same", "RoleSame"), "wf_valid");
        await agent.HandleChatRequest(new ChatRequestEvent { Prompt = "first", SessionId = "s1" });
        runtime.CreateCalls.Should().Be(1);

        await agent.HandleReplaceWorkflowDefinitionAndExecute(new ReplaceWorkflowDefinitionAndExecuteEvent
        {
            WorkflowYaml = BuildValidWorkflowYaml("role_same", "RoleSame"),
            Input = "dynamic-input",
        });

        runtime.CreateCalls.Should().Be(1, "reconfigure should reuse existing role actor id instead of creating duplicate");
        runtime.Linked.Should().Contain(x => x.parent == agent.Id && x.child.EndsWith(":role_same", StringComparison.Ordinal));
        publisher.Published.Select(x => x.evt).OfType<StartWorkflowEvent>()
            .Should().Contain(x => x.Input == "dynamic-input");
    }

    [Fact]
    public async Task HandleReconfigureAndExecute_WhenSingletonBindingIsIdleAndUnreferenced_ShouldEvictBinding()
    {
        var runtime = new RecordingActorRuntime();
        var resolver = new StaticWorkflowDefinitionResolver(new Dictionary<string, string>
        {
            ["sub_flow"] = BuildValidWorkflowYaml("sub_role", "SubRole"),
        });
        var agent = CreateAgent(runtime: runtime, workflowResolver: resolver);
        await agent.BindWorkflowDefinitionAsync(BuildWorkflowYamlWithSingletonWorkflowCall("sub_flow"), "wf_with_call");

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-idle-1",
            ParentRunId = "run-idle",
            ParentStepId = "step-idle",
            WorkflowName = "sub_flow",
            Input = "payload",
            Lifecycle = "singleton",
        });
        var pending = agent.State.PendingSubWorkflowInvocations.Should().ContainSingle().Subject;
        var childActorId = pending.ChildActorId;

        await agent.HandleWorkflowCompletionEnvelope(Envelope(
            new WorkflowCompletedEvent
            {
                WorkflowName = "sub_flow",
                RunId = pending.ChildRunId,
                Success = true,
                Output = "ok",
            },
            publisherId: childActorId,
            direction: EventDirection.Both));

        agent.State.PendingSubWorkflowInvocations.Should().BeEmpty("completed invocation should clear pending state");
        agent.State.SubWorkflowBindings.Should().ContainSingle("singleton binding remains reusable before reconfigure");

        await agent.HandleReplaceWorkflowDefinitionAndExecute(new ReplaceWorkflowDefinitionAndExecuteEvent
        {
            WorkflowYaml = BuildValidWorkflowYaml("role_b", "RoleB"),
            Input = "dynamic-input",
        });

        agent.State.SubWorkflowBindings
            .Should()
            .BeEmpty("idle singleton bindings not referenced by latest workflow should be evicted during reconfigure");
    }

    [Fact]
    public async Task HandleSubWorkflowInvokeRequested_ShouldPersistPendingAndReuseSingletonChild()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var resolver = new StaticWorkflowDefinitionResolver(new Dictionary<string, string>
        {
            ["sub_flow"] = BuildValidWorkflowYaml("sub_role", "SubRole"),
        });
        var agent = CreateAgent(runtime: runtime, workflowResolver: resolver);
        agent.EventPublisher = publisher;

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-1",
            ParentRunId = "parent-run",
            ParentStepId = "step-a",
            WorkflowName = "sub_flow",
            Input = "payload-a",
            Lifecycle = "singleton",
        });

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-2",
            ParentRunId = "parent-run",
            ParentStepId = "step-b",
            WorkflowName = "sub_flow",
            Input = "payload-b",
            Lifecycle = "singleton",
        });

        runtime.CreateCalls.Should().Be(1, "singleton lifecycle should reuse one child WorkflowGAgent");
        runtime.Linked.Should().ContainSingle(x => x.parent == agent.Id);

        agent.State.SubWorkflowBindings.Should().ContainSingle();
        var binding = agent.State.SubWorkflowBindings.Single();
        binding.WorkflowName.Should().Be("sub_flow");
        binding.Lifecycle.Should().Be("singleton");

        agent.State.PendingSubWorkflowInvocations.Should().HaveCount(2);
        agent.State.PendingSubWorkflowInvocationIndexByChildRunId.Should().HaveCount(2);
        foreach (var pending in agent.State.PendingSubWorkflowInvocations)
            agent.State.PendingSubWorkflowInvocationIndexByChildRunId.Should().ContainKey(pending.ChildRunId);
        agent.State.PendingChildRunIdsByParentRunId.Should().ContainKey("parent-run");
        agent.State.PendingChildRunIdsByParentRunId["parent-run"].ChildRunIds.Should().HaveCount(2);
        publisher.Sent.Should().HaveCount(2);
        publisher.Sent.Select(x => x.targetActorId).Distinct().Should().ContainSingle(binding.ChildActorId);
    }

    [Fact]
    public async Task HandleSubWorkflowInvokeRequested_WhenInvocationIdMissing_ShouldGenerateCanonicalInvocationId()
    {
        var runtime = new RecordingActorRuntime();
        var resolver = new StaticWorkflowDefinitionResolver(new Dictionary<string, string>
        {
            ["sub_flow"] = BuildValidWorkflowYaml("sub_role", "SubRole"),
        });
        var agent = CreateAgent(runtime: runtime, workflowResolver: resolver);

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "",
            ParentRunId = " parent-run ",
            ParentStepId = "step-a",
            WorkflowName = "sub_flow",
            Input = "payload-a",
            Lifecycle = "singleton",
        });

        var pending = agent.State.PendingSubWorkflowInvocations.Should().ContainSingle().Subject;
        Regex.IsMatch(pending.InvocationId, "^parent-run:workflow_call:step-a:[0-9a-f]{32}$")
            .Should().BeTrue("missing invocation id should be generated from normalized run/step ids");
        pending.ChildRunId.Should().Be(pending.InvocationId, "child run id reuses invocation id for correlation");
    }

    [Theory]
    [InlineData("transient")]
    [InlineData("scope")]
    public async Task HandleSubWorkflowInvokeRequested_WithNonSingletonLifecycle_ShouldNotPersistBinding_AndDestroyAfterCompletion(string lifecycle)
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var resolver = new StaticWorkflowDefinitionResolver(new Dictionary<string, string>
        {
            ["sub_flow"] = BuildValidWorkflowYaml("sub_role", "SubRole"),
        });
        var agent = CreateAgent(runtime: runtime, workflowResolver: resolver);
        agent.EventPublisher = publisher;

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = $"invoke-{lifecycle}",
            ParentRunId = "parent-run",
            ParentStepId = $"step-{lifecycle}",
            WorkflowName = "sub_flow",
            Input = "payload",
            Lifecycle = lifecycle,
        });

        runtime.CreateCalls.Should().Be(1);
        agent.State.SubWorkflowBindings.Should().BeEmpty("non-singleton lifecycles must not persist reusable bindings");
        var pending = agent.State.PendingSubWorkflowInvocations.Should().ContainSingle().Subject;
        pending.Lifecycle.Should().Be(lifecycle);

        await agent.HandleWorkflowCompletionEnvelope(Envelope(
            new WorkflowCompletedEvent
            {
                WorkflowName = "sub_flow",
                RunId = pending.ChildRunId,
                Success = true,
                Output = "done",
            },
            publisherId: pending.ChildActorId,
            direction: EventDirection.Both));

        agent.State.PendingSubWorkflowInvocations.Should().BeEmpty();
        runtime.Unlinked.Should().Contain(pending.ChildActorId);
        runtime.Destroyed.Should().Contain(pending.ChildActorId);
    }

    [Fact]
    public async Task HandleSubWorkflowInvokeRequested_WhenWorkflowNotRegistered_ShouldPublishFailureAndKeepPendingEmpty()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var resolver = new StaticWorkflowDefinitionResolver(new Dictionary<string, string>());
        var agent = CreateAgent(runtime: runtime, workflowResolver: resolver);
        agent.EventPublisher = publisher;

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-missing",
            ParentRunId = "run-missing",
            ParentStepId = "missing-step",
            WorkflowName = "missing-flow",
            Input = "payload",
            Lifecycle = "singleton",
        });

        runtime.CreateCalls.Should().Be(0);
        agent.State.PendingSubWorkflowInvocations.Should().BeEmpty();
        var failure = publisher.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        failure.Success.Should().BeFalse();
        failure.StepId.Should().Be("missing-step");
        failure.RunId.Should().Be("run-missing");
        failure.Error.Should().Contain("unregistered workflow");
    }

    [Fact]
    public async Task HandleSubWorkflowInvokeRequested_WhenResolverMissing_ShouldPublishClearFailure()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var agent = CreateAgent(runtime: runtime, workflowResolver: null);
        agent.EventPublisher = publisher;

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-missing-resolver",
            ParentRunId = "run-missing-resolver",
            ParentStepId = "missing-resolver-step",
            WorkflowName = "sub_flow",
            Input = "payload",
            Lifecycle = "singleton",
        });

        runtime.CreateCalls.Should().Be(0);
        agent.State.PendingSubWorkflowInvocations.Should().BeEmpty();
        var failure = publisher.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        failure.Success.Should().BeFalse();
        failure.StepId.Should().Be("missing-resolver-step");
        failure.RunId.Should().Be("run-missing-resolver");
        failure.Error.Should().Contain("IWorkflowDefinitionResolver");
    }

    [Fact]
    public async Task HandleSubWorkflowInvokeRequested_WhenParentStepIdMissing_ShouldPublishFailureAndKeepPendingEmpty()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var resolver = new StaticWorkflowDefinitionResolver(new Dictionary<string, string>
        {
            ["sub_flow"] = BuildValidWorkflowYaml("sub_role", "SubRole"),
        });
        var agent = CreateAgent(runtime: runtime, workflowResolver: resolver);
        agent.EventPublisher = publisher;

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-missing-step",
            ParentRunId = "run-missing-step",
            ParentStepId = "   ",
            WorkflowName = "sub_flow",
            Input = "payload",
            Lifecycle = "singleton",
        });

        runtime.CreateCalls.Should().Be(0);
        agent.State.PendingSubWorkflowInvocations.Should().BeEmpty();
        var failure = publisher.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        failure.Success.Should().BeFalse();
        failure.RunId.Should().Be("run-missing-step");
        failure.Error.Should().Contain("parent_step_id");
    }

    [Fact]
    public async Task HandleSubWorkflowInvokeRequested_WhenLifecycleInvalid_ShouldPublishFailureAndKeepPendingEmpty()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var resolver = new StaticWorkflowDefinitionResolver(new Dictionary<string, string>
        {
            ["sub_flow"] = BuildValidWorkflowYaml("sub_role", "SubRole"),
        });
        var agent = CreateAgent(runtime: runtime, workflowResolver: resolver);
        agent.EventPublisher = publisher;

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-invalid-lifecycle",
            ParentRunId = "run-invalid-lifecycle",
            ParentStepId = "step-invalid-lifecycle",
            WorkflowName = "sub_flow",
            Input = "payload",
            Lifecycle = "isolate",
        });

        runtime.CreateCalls.Should().Be(0);
        agent.State.PendingSubWorkflowInvocations.Should().BeEmpty();
        var failure = publisher.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        failure.Success.Should().BeFalse();
        failure.RunId.Should().Be("run-invalid-lifecycle");
        failure.Error.Should().Contain("lifecycle must be singleton/transient/scope");
    }

    [Fact]
    public async Task HandleWorkflowCompletionEnvelope_WhenChildCompletion_ShouldTranslateToParentStepCompleted()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var resolver = new StaticWorkflowDefinitionResolver(new Dictionary<string, string>
        {
            ["sub_flow"] = BuildValidWorkflowYaml("sub_role", "SubRole"),
        });
        var agent = CreateAgent(runtime: runtime, workflowResolver: resolver);
        agent.EventPublisher = publisher;

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-child",
            ParentRunId = "parent-run",
            ParentStepId = "step-child",
            WorkflowName = "sub_flow",
            Input = "payload",
            Lifecycle = "singleton",
        });

        var pending = agent.State.PendingSubWorkflowInvocations.Single();
        await agent.HandleWorkflowCompletionEnvelope(Envelope(
            new WorkflowCompletedEvent
            {
                WorkflowName = "sub_flow",
                RunId = pending.ChildRunId,
                Success = true,
                Output = "child-done",
            },
            publisherId: pending.ChildActorId,
            direction: EventDirection.Both));

        agent.State.PendingSubWorkflowInvocations.Should().BeEmpty();
        agent.State.PendingSubWorkflowInvocationIndexByChildRunId.Should().BeEmpty();
        agent.State.PendingChildRunIdsByParentRunId.Should().BeEmpty();
        var parentCompletion = publisher.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
        parentCompletion.StepId.Should().Be("step-child");
        parentCompletion.RunId.Should().Be("parent-run");
        parentCompletion.Success.Should().BeTrue();
        parentCompletion.Output.Should().Be("child-done");
        parentCompletion.Metadata["workflow_call.child_run_id"].Should().Be(pending.ChildRunId);
        publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task HandleWorkflowCompletionEnvelope_WhenRunMatchesPendingButPublisherMismatch_ShouldIgnoreAndKeepPending()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var resolver = new StaticWorkflowDefinitionResolver(new Dictionary<string, string>
        {
            ["sub_flow"] = BuildValidWorkflowYaml("sub_role", "SubRole"),
        });
        var agent = CreateAgent(runtime: runtime, workflowResolver: resolver);
        agent.EventPublisher = publisher;

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-mismatch",
            ParentRunId = "parent-run",
            ParentStepId = "step-child",
            WorkflowName = "sub_flow",
            Input = "payload",
            Lifecycle = "singleton",
        });

        var pending = agent.State.PendingSubWorkflowInvocations.Single();
        await agent.HandleWorkflowCompletionEnvelope(Envelope(
            new WorkflowCompletedEvent
            {
                WorkflowName = "sub_flow",
                RunId = pending.ChildRunId,
                Success = true,
                Output = "child-done",
            },
            publisherId: "untrusted-child",
            direction: EventDirection.Both));

        agent.State.PendingSubWorkflowInvocations.Should().ContainSingle();
        agent.State.PendingSubWorkflowInvocations.Single().InvocationId.Should().Be(pending.InvocationId);
        publisher.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Should().BeEmpty();
        publisher.Published.Select(x => x.evt).OfType<TextMessageEndEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task HandleWorkflowCompletionEnvelope_WhenPublisherMismatchThenValidCompletion_ShouldProcessOnlyValidCompletion()
    {
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var resolver = new StaticWorkflowDefinitionResolver(new Dictionary<string, string>
        {
            ["sub_flow"] = BuildValidWorkflowYaml("sub_role", "SubRole"),
        });
        var agent = CreateAgent(runtime: runtime, workflowResolver: resolver);
        agent.EventPublisher = publisher;

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-mismatch-then-valid",
            ParentRunId = "parent-run",
            ParentStepId = "step-child",
            WorkflowName = "sub_flow",
            Input = "payload",
            Lifecycle = "singleton",
        });

        var pending = agent.State.PendingSubWorkflowInvocations.Single();
        await agent.HandleWorkflowCompletionEnvelope(Envelope(
            new WorkflowCompletedEvent
            {
                WorkflowName = "sub_flow",
                RunId = pending.ChildRunId,
                Success = true,
                Output = "bad-child-done",
            },
            publisherId: "untrusted-child",
            direction: EventDirection.Both));

        await agent.HandleWorkflowCompletionEnvelope(Envelope(
            new WorkflowCompletedEvent
            {
                WorkflowName = "sub_flow",
                RunId = pending.ChildRunId,
                Success = true,
                Output = "child-done",
            },
            publisherId: pending.ChildActorId,
            direction: EventDirection.Both));

        agent.State.PendingSubWorkflowInvocations.Should().BeEmpty();
        var completions = publisher.Published.Select(x => x.evt).OfType<StepCompletedEvent>().ToList();
        completions.Should().ContainSingle();
        completions[0].Output.Should().Be("child-done");
    }

    [Fact]
    public async Task HandleWorkflowCompleted_WhenParentRunCompletes_ShouldCleanupMatchingPendingInvocationsAndPersistFailureEvents()
    {
        const string parentRunId = "run-parent";
        const int staleCount = 40;
        const int retainedCount = 24;

        var sharedEventStore = new InMemoryEventStore();
        var publisher = new RecordingEventPublisher();
        var runtime = new RecordingActorRuntime();
        var resolver = new StaticWorkflowDefinitionResolver(new Dictionary<string, string>
        {
            ["sub_flow"] = BuildValidWorkflowYaml("sub_role", "SubRole"),
        });
        var agent = CreateAgent(runtime: runtime, eventStore: sharedEventStore, workflowResolver: resolver);
        agent.EventPublisher = publisher;

        foreach (var i in Enumerable.Range(0, staleCount + retainedCount))
        {
            var runId = i < staleCount ? parentRunId : "run-other";
            await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
            {
                InvocationId = $"invoke-{i}",
                ParentRunId = runId,
                ParentStepId = $"step-{i}",
                WorkflowName = "sub_flow",
                Input = $"payload-{i}",
                Lifecycle = "singleton",
            });
        }

        agent.State.PendingSubWorkflowInvocations.Count(x => x.ParentRunId == parentRunId).Should().Be(staleCount);

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "wf_parent",
            RunId = parentRunId,
            Success = true,
            Output = "done",
        });

        agent.State.PendingSubWorkflowInvocations.Should().HaveCount(retainedCount);
        agent.State.PendingSubWorkflowInvocations.Should().OnlyContain(x => x.ParentRunId == "run-other");
        agent.State.PendingSubWorkflowInvocationIndexByChildRunId.Should().HaveCount(retainedCount);
        agent.State.PendingChildRunIdsByParentRunId.Should().NotContainKey(parentRunId);
        agent.State.PendingChildRunIdsByParentRunId.Should().ContainKey("run-other");
        agent.State.PendingChildRunIdsByParentRunId["run-other"].ChildRunIds.Should().HaveCount(retainedCount);

        var persisted = await sharedEventStore.GetEventsAsync(agent.Id);
        var cleanupEvents = persisted
            .Where(x => x.EventType.Contains(nameof(SubWorkflowInvocationCompletedEvent), StringComparison.Ordinal))
            .Select(x => x.EventData.Unpack<SubWorkflowInvocationCompletedEvent>())
            .ToList();
        cleanupEvents.Should().HaveCount(staleCount);
        cleanupEvents.Should().OnlyContain(x => x.Success == false);
        cleanupEvents.Should().OnlyContain(x => x.Error.Contains("parent workflow completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleWorkflowCompleted_WhenParentRunCompletes_ShouldFinalizeOnlyNonSingletonChildren()
    {
        const string parentRunId = "run-parent";

        var runtime = new RecordingActorRuntime();
        var resolver = new StaticWorkflowDefinitionResolver(new Dictionary<string, string>
        {
            ["sub_flow"] = BuildValidWorkflowYaml("sub_role", "SubRole"),
        });
        var agent = CreateAgent(runtime: runtime, workflowResolver: resolver);

        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-singleton",
            ParentRunId = parentRunId,
            ParentStepId = "step-singleton",
            WorkflowName = "sub_flow",
            Input = "payload-singleton",
            Lifecycle = "singleton",
        });
        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-transient",
            ParentRunId = parentRunId,
            ParentStepId = "step-transient",
            WorkflowName = "sub_flow",
            Input = "payload-transient",
            Lifecycle = "transient",
        });
        await agent.HandleSubWorkflowInvokeRequested(new SubWorkflowInvokeRequestedEvent
        {
            InvocationId = "invoke-scope",
            ParentRunId = parentRunId,
            ParentStepId = "step-scope",
            WorkflowName = "sub_flow",
            Input = "payload-scope",
            Lifecycle = "scope",
        });

        var childActorByLifecycle = agent.State.PendingSubWorkflowInvocations
            .ToDictionary(x => x.Lifecycle, x => x.ChildActorId, StringComparer.OrdinalIgnoreCase);

        await agent.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "wf_parent",
            RunId = parentRunId,
            Success = true,
            Output = "done",
        });

        agent.State.PendingSubWorkflowInvocations.Should().BeEmpty();
        agent.State.PendingSubWorkflowInvocationIndexByChildRunId.Should().BeEmpty();
        agent.State.PendingChildRunIdsByParentRunId.Should().BeEmpty();

        runtime.Unlinked.Should().Contain(childActorByLifecycle["transient"]);
        runtime.Unlinked.Should().Contain(childActorByLifecycle["scope"]);
        runtime.Unlinked.Should().NotContain(childActorByLifecycle["singleton"]);

        runtime.Destroyed.Should().Contain(childActorByLifecycle["transient"]);
        runtime.Destroyed.Should().Contain(childActorByLifecycle["scope"]);
        runtime.Destroyed.Should().NotContain(childActorByLifecycle["singleton"]);
    }

    [Fact]
    public async Task HandleWorkflowCompletionEnvelope_WhenExternalUnknownCompletion_ShouldIgnore()
    {
        var publisher = new RecordingEventPublisher();
        var agent = CreateAgent();
        agent.EventPublisher = publisher;

        await agent.HandleWorkflowCompletionEnvelope(Envelope(
            new WorkflowCompletedEvent
            {
                WorkflowName = "wf_external",
                RunId = "run-external",
                Success = true,
                Output = "ok",
            },
            publisherId: "external-child",
            direction: EventDirection.Both));

        agent.State.TotalExecutions.Should().Be(0);
        publisher.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfigureAndCompletionEvents_ShouldPersistAndReplayAfterReactivate()
    {
        var sharedEventStore = new InMemoryEventStore();

        var agent1 = CreateAgent(eventStore: sharedEventStore);
        await agent1.ActivateAsync();
        await agent1.BindWorkflowDefinitionAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_replay");
        await agent1.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "wf_replay",
            Success = true,
            Output = "ok",
        });
        await agent1.HandleWorkflowCompleted(new WorkflowCompletedEvent
        {
            WorkflowName = "wf_replay",
            Success = false,
            Error = "err",
        });
        await agent1.DeactivateAsync();

        var persisted = await sharedEventStore.GetEventsAsync(agent1.Id);
        persisted.Should().HaveCount(3);
        persisted.Should().Contain(x => x.EventType.Contains(nameof(BindWorkflowDefinitionEvent), StringComparison.Ordinal));
        persisted.Count(x => x.EventType.Contains(nameof(WorkflowCompletedEvent), StringComparison.Ordinal)).Should().Be(2);

        var agent2 = CreateAgent(eventStore: sharedEventStore);
        await agent2.ActivateAsync();

        agent2.State.Compiled.Should().BeTrue();
        agent2.State.TotalExecutions.Should().Be(2);
        agent2.State.SuccessfulExecutions.Should().Be(1);
        agent2.State.FailedExecutions.Should().Be(1);
    }

    [Fact]
    public async Task ConfigureWorkflow_ShouldInstallAndConfigureModules()
    {
        var factory = new RecordingEventModuleFactory();
        var expander = new StaticDependencyExpander(10, "module_a", "module_b");
        var configurator = new RecordingModuleConfigurator();
        var pack = new TestModulePack([expander], [configurator]);
        var agent = CreateAgent(eventModuleFactory: factory, packs: [pack]);

        await agent.BindWorkflowDefinitionAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_modules");

        agent.GetModules().Select(x => x.Name).Should().BeEquivalentTo(["module_a", "module_b"]);
        configurator.Configured.Should().BeEquivalentTo(["module_a:wf_valid", "module_b:wf_valid"]);
        factory.CreatedNames.Should().BeEquivalentTo(["module_a", "module_b"]);
    }

    [Fact]
    public async Task ConfigureWorkflow_WhenRequiredModuleCannotBeCreated_ShouldFailFast()
    {
        var factory = new SelectiveFailingEventModuleFactory("missing_module");
        var expander = new StaticDependencyExpander(0, "missing_module");
        var pack = new TestModulePack([expander], []);
        var agent = CreateAgent(eventModuleFactory: factory, packs: [pack]);

        Func<Task> act = () => agent.BindWorkflowDefinitionAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_modules");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*requires module 'missing_module'*");
    }

    [Fact]
    public async Task ActivateAsync_WhenStateContainsWorkflowYaml_ShouldCompileAndInstallModules()
    {
        var factory = new RecordingEventModuleFactory();
        var expander = new StaticDependencyExpander(0, "module_on_activate");
        var configurator = new RecordingModuleConfigurator();
        var pack = new TestModulePack([expander], [configurator]);
        var sharedEventStore = new InMemoryEventStore();

        var agent1 = CreateAgent(eventModuleFactory: factory, packs: [pack], eventStore: sharedEventStore);
        await agent1.ActivateAsync();
        await agent1.BindWorkflowDefinitionAsync(BuildValidWorkflowYaml("role_a", "RoleA"), "wf_activate");
        await agent1.DeactivateAsync();

        var agent2 = CreateAgent(eventModuleFactory: factory, packs: [pack], eventStore: sharedEventStore);
        await agent2.ActivateAsync();

        agent2.State.Compiled.Should().BeTrue();
        agent2.GetModules().Should().ContainSingle(x => x.Name == "module_on_activate");
        configurator.Configured.Should().Contain(x => x == "module_on_activate:wf_valid");
    }

    private static WorkflowGAgent CreateAgent(
        RecordingActorRuntime? runtime = null,
        IRoleAgentTypeResolver? roleResolver = null,
        IEventModuleFactory? eventModuleFactory = null,
        IEnumerable<IWorkflowModulePack>? packs = null,
        IEventStore? eventStore = null,
        IWorkflowDefinitionResolver? workflowResolver = null)
    {
        runtime ??= new RecordingActorRuntime();
        roleResolver ??= new StaticRoleAgentTypeResolver(typeof(FakeRoleAgent));
        eventModuleFactory ??= new RecordingEventModuleFactory();
        packs ??= [];
        eventStore ??= new InMemoryEventStore();
        var serviceCollection = new ServiceCollection()
            .AddSingleton(eventStore)
            .AddSingleton<IStreamProvider, InMemoryStreamProvider>()
            .AddSingleton<IActorRuntimeAsyncScheduler, InMemoryActorRuntimeAsyncScheduler>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        if (workflowResolver != null)
            serviceCollection.AddSingleton(workflowResolver);

        var agent = new WorkflowGAgent(runtime, roleResolver, eventModuleFactory, packs, workflowResolver)
        {
            Services = serviceCollection.BuildServiceProvider(),
        };
        agent.EventSourcingBehaviorFactory =
            agent.Services.GetRequiredService<IEventSourcingBehaviorFactory<WorkflowState>>();
        return agent;
    }

    private static EventEnvelope Envelope(IMessage message, string publisherId, EventDirection direction)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow),
            PublisherId = publisherId,
            Direction = direction,
            Payload = Google.Protobuf.WellKnownTypes.Any.Pack(message),
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
    }

    private static string BuildValidWorkflowYaml(
        string roleId,
        string roleName,
        string? provider = null,
        string? model = null)
    {
        var providerLine = string.IsNullOrWhiteSpace(provider) ? string.Empty : $"\n    provider: \"{provider}\"";
        var modelLine = string.IsNullOrWhiteSpace(model) ? string.Empty : $"\n    model: \"{model}\"";
        return $$"""
                 name: wf_valid
                 roles:
                   - id: "{{roleId}}"
                     name: "{{roleName}}"
                     system_prompt: "helpful role"
                     {{providerLine}}{{modelLine}}
                 steps:
                   - id: step_1
                     type: transform
                 """;
    }

    private static string BuildWorkflowYamlWithSingletonWorkflowCall(string subWorkflowName)
    {
        return $$"""
                 name: wf_with_call
                 roles:
                   - id: "role_a"
                     name: "RoleA"
                     system_prompt: "helpful role"
                 steps:
                   - id: invoke_sub
                     type: workflow_call
                     parameters:
                       workflow: "{{subWorkflowName}}"
                       lifecycle: "singleton"
                 """;
    }

    private static string BuildWorkflowYamlWithFullRoleConfig()
    {
        return """
               name: wf_valid
               roles:
                 - id: role_a
                   name: RoleA
                   system_prompt: "helpful role"
                   provider: openai
                   model: gpt-4o-mini
                   temperature: 0.2
                   max_tokens: 256
                   max_tool_rounds: 4
                   max_history_messages: 30
                   stream_buffer_capacity: 64
                   event_modules: "llm_handler,tool_handler"
                   event_routes: |
                     event.type == ChatRequestEvent -> llm_handler
               steps:
                 - id: step_1
                   type: transform
               """;
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public List<(IMessage evt, EventDirection direction)> Published { get; } = [];
        public List<(string targetActorId, IMessage evt)> Sent { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            EventDirection direction = EventDirection.Down,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null)
            where TEvent : IMessage
        {
            Published.Add((evt, direction));
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null)
            where TEvent : IMessage
        {
            Sent.Add((targetActorId, evt));
            Published.Add((evt, EventDirection.Self));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public bool ThrowOnDuplicateCreate { get; init; }
        public int CreateCalls { get; private set; }
        public List<IActor> CreatedActors { get; } = [];
        public List<(string parent, string child)> Linked { get; } = [];
        public List<string> Destroyed { get; } = [];
        public List<string> Unlinked { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent
        {
            return CreateAsync(typeof(TAgent), id, ct);
        }

        public Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? $"actor-{CreateCalls + 1}";
            var existing = CreatedActors.FirstOrDefault(x => x.Id == actorId);
            if (existing != null)
            {
                if (ThrowOnDuplicateCreate)
                    throw new InvalidOperationException($"Actor {actorId} already exists");

                return Task.FromResult(existing);
            }

            CreateCalls++;
            IAgent agent = agentType == typeof(FakeRoleAgent)
                ? new FakeRoleAgent(actorId)
                : new FakeNonRoleAgent(actorId);

            var actor = new FakeActor(actorId, agent);
            CreatedActors.Add(actor);
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            Destroyed.Add(id);
            return Task.CompletedTask;
        }
        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(CreatedActors.FirstOrDefault(x => x.Id == id));
        public Task<bool> ExistsAsync(string id) => Task.FromResult(CreatedActors.Any(x => x.Id == id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            Linked.Add((parentId, childId));
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default)
        {
            Unlinked.Add(childId);
            return Task.CompletedTask;
        }
    }

    private sealed class StaticWorkflowDefinitionResolver(IReadOnlyDictionary<string, string> definitions)
        : IWorkflowDefinitionResolver
    {
        public Task<string?> GetWorkflowYamlAsync(string workflowName, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(definitions.TryGetValue(workflowName, out var yaml) ? yaml : null);
        }
    }

    private sealed class FakeActor(string id, IAgent agent) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = agent;
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Agent.HandleEventAsync(envelope, ct);
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeRoleAgent(string id) : IRoleAgent
    {
        public string Id { get; } = id;
        public string RoleName { get; private set; } = "";
        public InitializeRoleAgentEvent? LastInitializeEvent { get; private set; }

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            if (envelope.Payload?.Is(InitializeRoleAgentEvent.Descriptor) == true)
            {
                var evt = envelope.Payload.Unpack<InitializeRoleAgentEvent>();
                LastInitializeEvent = evt;
                RoleName = evt.RoleName;
            }

            return Task.CompletedTask;
        }
        public Task<string> GetDescriptionAsync() => Task.FromResult("fake-role");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeNonRoleAgent(string id) : IAgent
    {
        public string Id { get; } = id;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("fake-non-role");
        public Task<IReadOnlyList<Type>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StaticRoleAgentTypeResolver(Type roleAgentType) : IRoleAgentTypeResolver
    {
        public Type ResolveRoleAgentType() => roleAgentType;
    }

    private sealed class RecordingEventModuleFactory : IEventModuleFactory
    {
        public List<string> CreatedNames { get; } = [];

        public bool TryCreate(string name, out IEventModule? module)
        {
            CreatedNames.Add(name);
            module = new RecordingEventModule(name);
            return true;
        }
    }

    private sealed class SelectiveFailingEventModuleFactory(string failingModuleName) : IEventModuleFactory
    {
        public bool TryCreate(string name, out IEventModule? module)
        {
            if (string.Equals(name, failingModuleName, StringComparison.OrdinalIgnoreCase))
            {
                module = null;
                return false;
            }

            module = new RecordingEventModule(name);
            return true;
        }
    }

    private sealed class RecordingEventModule(string name) : IEventModule
    {
        public string Name { get; } = name;
        public int Priority => 0;
        public bool CanHandle(EventEnvelope envelope) => false;
        public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StaticDependencyExpander(int order, params string[] moduleNames) : IWorkflowModuleDependencyExpander
    {
        public int Order { get; } = order;

        public void Expand(WorkflowDefinition? workflow, ISet<string> names)
        {
            _ = workflow;
            foreach (var moduleName in moduleNames)
                names.Add(moduleName);
        }
    }

    private sealed class RecordingModuleConfigurator : IWorkflowModuleConfigurator
    {
        public int Order => 0;
        public List<string> Configured { get; } = [];

        public void Configure(IEventModule module, WorkflowDefinition workflow)
        {
            Configured.Add($"{module.Name}:{workflow.Name}");
        }
    }

    private sealed class TestModulePack(
        IReadOnlyList<IWorkflowModuleDependencyExpander> expanders,
        IReadOnlyList<IWorkflowModuleConfigurator> configurators) : IWorkflowModulePack
    {
        public string Name => "test-pack";
        public IReadOnlyList<WorkflowModuleRegistration> Modules => [];
        public IReadOnlyList<IWorkflowModuleDependencyExpander> DependencyExpanders { get; } = expanders;
        public IReadOnlyList<IWorkflowModuleConfigurator> Configurators { get; } = configurators;
    }
}
