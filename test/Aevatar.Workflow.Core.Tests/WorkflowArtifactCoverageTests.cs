using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Core.Primitives;
using FluentAssertions;
using Google.Protobuf;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace Aevatar.Workflow.Core.Tests;

public sealed class WorkflowArtifactCoverageTests
{
    [Fact]
    public void WorkflowArtifactFactBuilder_TryBuild_ShouldTranslateCommittedChildRoleReply()
    {
        var envelope = new EventEnvelope
        {
            Id = "env-role-reply",
            Route = EnvelopeRouteSemantics.CreateObserverPublication("workflow-run:role_a"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = "evt-role-reply",
                    EventData = Any.Pack(new RoleChatSessionCompletedEvent
                    {
                        SessionId = "session-1",
                        Content = "reply",
                        ReasoningContent = "reasoning",
                        Prompt = "prompt",
                        ContentEmitted = true,
                        ToolCalls =
                        {
                            new ToolCallEvent
                            {
                                ToolName = "search",
                                CallId = "call-1",
                            },
                        },
                    }),
                },
                StateRoot = Any.Pack(new RoleGAgentState()),
            }),
        };

        var ok = WorkflowArtifactFactBuilder.TryBuild(
            envelope,
            "workflow-run",
            "run-1",
            out var artifactFact);

        ok.Should().BeTrue();
        var fact = artifactFact.Should().BeOfType<WorkflowRoleReplyRecordedEvent>().Subject;
        fact.RunId.Should().Be("run-1");
        fact.RoleActorId.Should().Be("workflow-run:role_a");
        fact.RoleId.Should().Be("role_a");
        fact.SessionId.Should().Be("session-1");
        fact.Content.Should().Be("reply");
        fact.ReasoningContent.Should().Be("reasoning");
        fact.Prompt.Should().Be("prompt");
        fact.ContentEmitted.Should().BeTrue();
        fact.ToolCalls.Should().ContainSingle(x => x.ToolName == "search" && x.CallId == "call-1");
    }

    [Fact]
    public void WorkflowArtifactFactBuilder_TryBuild_ShouldRejectInvalidCommittedRoleReplyEnvelopes()
    {
        WorkflowArtifactFactBuilder.TryBuild(
                new EventEnvelope
                {
                    Id = "env-empty",
                },
                "workflow-run",
                "run-1",
                out _)
            .Should()
            .BeFalse();

        WorkflowArtifactFactBuilder.TryBuild(
                new EventEnvelope
                {
                    Id = "env-completed",
                    Payload = Any.Pack(new WorkflowCompletedEvent()),
                },
                "workflow-run",
                "run-1",
                out _)
            .Should()
            .BeFalse();

        WorkflowArtifactFactBuilder.TryBuild(
                new EventEnvelope
                {
                    Id = "env-missing-data",
                    Route = EnvelopeRouteSemantics.CreateObserverPublication("workflow-run:role_a"),
                    Payload = Any.Pack(new CommittedStateEventPublished
                    {
                        StateEvent = new StateEvent(),
                    }),
                },
                "workflow-run",
                "run-1",
                out _)
            .Should()
            .BeFalse();

        WorkflowArtifactFactBuilder.TryBuild(
                new EventEnvelope
                {
                    Id = "env-external",
                    Route = EnvelopeRouteSemantics.CreateObserverPublication("external-role"),
                    Payload = Any.Pack(new CommittedStateEventPublished
                    {
                        StateEvent = new StateEvent
                        {
                            EventData = Any.Pack(new RoleChatSessionCompletedEvent
                            {
                                SessionId = "ignored",
                            }),
                        },
                    }),
                },
                "workflow-run",
                "run-1",
                out _)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void WorkflowRunDefinitionValidationSupport_Validate_ShouldExpandFactoryTypesFromWorkflowGraph()
    {
        var factory = new RecordingModuleFactory("custom_root", "custom_child");
        var workflow = new WorkflowDefinition
        {
            Name = "wf-validation",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "step-root",
                    Type = "custom_root",
                    Parameters =
                    {
                        ["step"] = "judge",
                        ["sub_step_type"] = "custom_child",
                    },
                    Children =
                    [
                        new StepDefinition
                        {
                            Id = "step-leaf",
                            Type = "missing_module",
                        },
                    ],
                },
            ],
        };

        var errors = WorkflowRunDefinitionValidationSupport.Validate(workflow, [], factory);

        errors.Should().ContainSingle();
        errors[0].Should().Contain("missing_module");
        factory.Requested.Should().ContainInOrder("custom_root", "custom_child", "missing_module");
        factory.Requested.Should().NotContain("evaluate");
    }

    [Fact]
    public void WorkflowRunDefinitionValidationSupport_Validate_ShouldReuseKnownTypes_AndIgnoreBlankReferencedTypes()
    {
        var factory = new RecordingModuleFactory();
        var workflow = new WorkflowDefinition
        {
            Name = "wf-known",
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "step-root",
                    Type = "custom_root",
                    Parameters =
                    {
                        ["sub_step_type"] = "   ",
                        ["custom_value"] = "missing",
                    },
                },
            ],
        };

        var errors = WorkflowRunDefinitionValidationSupport.Validate(workflow, ["custom_root"], factory);

        errors.Should().BeEmpty();
        factory.Requested.Should().BeEmpty();
    }

    [Fact]
    public void WorkflowRoleAgentEnvelopeFactory_CreateInitializeEnvelope_ShouldPopulateOptionalFields()
    {
        var withTemperature = WorkflowRoleAgentEnvelopeFactory.CreateInitializeEnvelope(
            new RoleDefinition
            {
                Id = "planner",
                Name = "Planner",
                Provider = "openai",
                Model = "gpt-5.4",
                SystemPrompt = "You are helpful.",
                Temperature = 0.25,
                MaxTokens = 256,
            },
            "workflow-run");
        var withoutTemperature = WorkflowRoleAgentEnvelopeFactory.CreateInitializeEnvelope(
            new RoleDefinition
            {
                Id = "reviewer",
                Name = "Reviewer",
            },
            "workflow-run");

        var withTemperaturePayload = withTemperature.Payload!.Unpack<InitializeRoleAgentEvent>();
        var withoutTemperaturePayload = withoutTemperature.Payload!.Unpack<InitializeRoleAgentEvent>();

        withTemperaturePayload.RoleName.Should().Be("Planner");
        withTemperaturePayload.ProviderName.Should().Be("openai");
        withTemperaturePayload.Model.Should().Be("gpt-5.4");
        withTemperaturePayload.SystemPrompt.Should().Be("You are helpful.");
        withTemperaturePayload.HasTemperature.Should().BeTrue();
        withTemperaturePayload.Temperature.Should().BeApproximately(0.25f, 0.0001f);
        withTemperature.Route!.PublisherActorId.Should().Be("workflow-run");
        withTemperature.Propagation!.CorrelationId.Should().NotBeNullOrWhiteSpace();

        withoutTemperaturePayload.RoleName.Should().Be("Reviewer");
        withoutTemperaturePayload.HasTemperature.Should().BeFalse();
    }

    private sealed class RecordingModuleFactory(params string[] creatableNames)
        : IEventModuleFactory<IWorkflowExecutionContext>
    {
        private readonly HashSet<string> _creatableNames = new(creatableNames, StringComparer.OrdinalIgnoreCase);

        public List<string> Requested { get; } = [];

        public bool TryCreate(string name, out IEventModule<IWorkflowExecutionContext>? module)
        {
            Requested.Add(name);
            if (_creatableNames.Contains(name))
            {
                module = new NoOpWorkflowModule(name);
                return true;
            }

            module = null;
            return false;
        }
    }

    private sealed class NoOpWorkflowModule(string name) : IEventModule<IWorkflowExecutionContext>
    {
        public string Name { get; } = name;
        public int Priority => 0;
        public bool CanHandle(EventEnvelope envelope) => false;
        public Task HandleAsync(EventEnvelope envelope, IWorkflowExecutionContext ctx, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
