using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Helpers;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Core;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace Aevatar.Workflow.Core.Tests;

public sealed class WorkflowArtifactFactBuilderOperationTests
{
    private static readonly DateTimeOffset SourceTime =
        DateTimeOffset.Parse("2026-08-14T03:04:05.678+00:00");

    [Fact]
    public void TryBuild_ModelStarted_ShouldPreserveProviderInputToolsSequenceAndSourceTime()
    {
        var toolCatalogProof = AgentTurnToolCatalogProof.RestrictedEmpty(
            AgentTurnToolCatalogBudget.WorkflowOrAdmin);
        var progress = new RoleChatSessionProgressedEvent
        {
            SessionId = "session-alpha",
            Sequence = 11,
            ModelStarted = new RoleChatModelStartedProgress
            {
                OperationId = "model-round-0",
                Round = 0,
                Model = "deepseek-chat",
                Provider = "deepseek",
                InputSummary = "Summarize the deployment status.",
                ToolCatalogProof = toolCatalogProof.ToPayload(),
                ToolCatalogPolicyVersion = WorkflowToolCatalogPolicies.CurrentVersion,
                AvailableToolNames = { "search", "status" },
            },
        };

        var fact = BuildOperation(progress, committedVersion: 41);

        fact.RunId.Should().Be("run-alpha");
        fact.SessionId.Should().Be("session-alpha");
        fact.OperationId.Should().Be("model-round-0");
        fact.Kind.Should().Be(WorkflowRuntimeOperationKind.Model);
        fact.Phase.Should().Be(WorkflowRuntimeOperationPhase.Started);
        fact.Round.Should().Be(0);
        fact.Model.Should().Be("deepseek-chat");
        fact.Provider.Should().Be("deepseek");
        fact.InputSummary.Should().Be("Summarize the deployment status.");
        fact.AvailableToolNames.Should().Equal("search", "status");
        fact.ToolCatalogPolicyVersion.Should().Be(WorkflowToolCatalogPolicies.CurrentVersion);
        fact.ToolCatalogProof.Should().NotBeNull();
        fact.ToolCatalogProof.ToolCount.Should().Be(0);
        fact.ToolCatalogProof.SchemaBytes.Should().Be(0);
        fact.ToolCatalogProof.CatalogDigest.Should().Be(toolCatalogProof.CatalogDigest);
        fact.ToolCatalogProof.Budget.MaximumToolCount.Should()
            .Be(WorkflowToolCatalogPolicies.MaximumWorkflowToolCount);
        fact.ToolCatalogProof.Budget.MaximumSchemaBytes.Should()
            .Be(WorkflowToolCatalogPolicies.MaximumWorkflowSchemaBytes);
        fact.ProgressSequence.Should().Be(11);
        fact.EventTime.ToDateTimeOffset().Should().Be(SourceTime);
        fact.Source.PublisherActorId.Should().Be("role-actor-alpha");
        fact.Source.CommittedEventId.Should().Be("child-event-41");
        fact.Source.CommittedStateVersion.Should().Be(41);
    }

    [Fact]
    public void TryBuild_ModelCompleted_ShouldPreserveResponseReasoningUsageAndTerminalOutcome()
    {
        var progress = new RoleChatSessionProgressedEvent
        {
            SessionId = "session-alpha",
            Sequence = 18,
            ModelCompleted = new RoleChatModelCompletedProgress
            {
                OperationId = "model-round-0",
                Round = 0,
                Model = "deepseek-chat",
                Content = string.Empty,
                ReasoningContent = "A tool is required.",
                FinishReason = "tool_calls",
                Success = true,
                Usage = new TokenUsagePayload
                {
                    PromptTokens = 23,
                    CompletionTokens = 7,
                    TotalTokens = 30,
                },
            },
        };

        var fact = BuildOperation(progress, committedVersion: 42);

        fact.OperationId.Should().Be("model-round-0");
        fact.Kind.Should().Be(WorkflowRuntimeOperationKind.Model);
        fact.Phase.Should().Be(WorkflowRuntimeOperationPhase.Completed);
        fact.Output.Should().BeEmpty("tool-call-only model responses are still terminal operations");
        fact.ReasoningContent.Should().Be("A tool is required.");
        fact.FinishReason.Should().Be("tool_calls");
        fact.Success.Should().BeTrue();
        fact.Usage.Model.Should().Be("deepseek-chat");
        fact.Usage.PromptTokens.Should().Be(23);
        fact.Usage.CompletionTokens.Should().Be(7);
        fact.Usage.TotalTokens.Should().Be(30);
        fact.ProgressSequence.Should().Be(18);
        fact.EventTime.ToDateTimeOffset().Should().Be(SourceTime);
    }

    [Fact]
    public void TryBuild_ToolStarted_ShouldCreateOneToolOperationWithoutArguments()
    {
        var progress = new RoleChatSessionProgressedEvent
        {
            SessionId = "session-alpha",
            Sequence = 19,
            ToolStarted = new RoleChatToolStartedProgress
            {
                CallId = "call-search-1",
                ToolName = "search",
                OperationId = "transport-operation-id",
            },
        };

        var fact = BuildOperation(progress, committedVersion: 43);

        fact.OperationId.Should().Be("call-search-1");
        fact.ToolCallId.Should().Be("call-search-1");
        fact.ToolName.Should().Be("search");
        fact.Kind.Should().Be(WorkflowRuntimeOperationKind.Tool);
        fact.Phase.Should().Be(WorkflowRuntimeOperationPhase.Started);
        fact.ArgumentsJson.Should().BeEmpty(
            "raw tool arguments must not be copied from a start notification");
        fact.ProgressSequence.Should().Be(19);
        fact.EventTime.ToDateTimeOffset().Should().Be(SourceTime);
    }

    [Fact]
    public void TryBuild_ToolCompleted_ShouldUseOnlySafeArgumentsAndMapResult()
    {
        var progress = new RoleChatSessionProgressedEvent
        {
            SessionId = "session-alpha",
            Sequence = 20,
            ToolCompleted = new RoleChatToolCompletedProgress
            {
                OperationId = "transport-operation-id",
                ToolName = "search",
                SafeArgumentsJson = "{\"query\":\"weather\"}",
                Result = new ToolResultEvent
                {
                    CallId = "call-search-1",
                    ResultJson = "{\"hits\":3}",
                    Success = false,
                    Error = "upstream timeout",
                },
            },
        };

        var fact = BuildOperation(progress, committedVersion: 44);

        fact.OperationId.Should().Be("call-search-1");
        fact.ToolCallId.Should().Be("call-search-1");
        fact.ToolName.Should().Be("search");
        fact.Kind.Should().Be(WorkflowRuntimeOperationKind.Tool);
        fact.Phase.Should().Be(WorkflowRuntimeOperationPhase.Completed);
        fact.ArgumentsJson.Should().Be("{\"query\":\"weather\"}");
        fact.ResultJson.Should().Be("{\"hits\":3}");
        fact.Success.Should().BeFalse();
        fact.Error.Should().Be("upstream timeout");
        fact.ProgressSequence.Should().Be(20);
        fact.EventTime.ToDateTimeOffset().Should().Be(SourceTime);
    }

    private static WorkflowRuntimeOperationRecordedEvent BuildOperation(
        RoleChatSessionProgressedEvent progress,
        long committedVersion)
    {
        var envelope = new EventEnvelope
        {
            Id = $"outer-{committedVersion}",
            Route = EnvelopeRouteSemantics.CreateObserverPublication("role-actor-alpha"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = $"child-event-{committedVersion}",
                    Version = committedVersion,
                    Timestamp = Timestamp.FromDateTimeOffset(SourceTime),
                    EventData = Any.Pack(progress),
                },
            }),
        };

        WorkflowArtifactFactBuilder.TryBuild(envelope, "run-actor-alpha", "run-alpha", out var artifactFact)
            .Should().BeTrue();
        return artifactFact.Should().BeOfType<WorkflowRuntimeOperationRecordedEvent>().Subject;
    }
}
