using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.RunForks;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.RunForks;
using Aevatar.Workflow.Application.Runs;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowForkRunDispatchTests
{
    private const string WorkflowYaml = """
        name: direct
        roles: []
        steps:
          - id: step-a
            type: llm_call
          - id: step-b
            type: llm_call
        """;

    [Fact]
    public async Task DispatchAsync_ShouldCreateRunAndDispatchRequestLevelResumeSeed()
    {
        var seedPort = new StaticSeedQueryPort(CreateSeedView());
        var provisioningPort = new RecordingRunProvisioningPort();
        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateDispatchService(seedPort, provisioningPort, dispatchPort);

        var result = await service.DispatchAsync(
            new WorkflowForkRunCommand(
                " source-run ",
                " step-b ",
                VariableOverrides: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["topic"] = "override-topic",
                    ["extra"] = "override-extra",
                },
                Input: "fallback-input",
                CommandId: "cmd-1",
                CorrelationId: "corr-1",
                ScopeId: "scope-command"),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().NotBeNull();
        result.Receipt!.NewRunActorId.Should().Be("run-actor-1");
        result.Receipt.SourceRunId.Should().Be("source-run");
        provisioningPort.CreateRunBindings.Should().ContainSingle();
        provisioningPort.CreateRunBindings[0].ScopeId.Should().Be("scope-command");
        dispatchPort.Dispatches.Should().ContainSingle();
        dispatchPort.Dispatches[0].ActorId.Should().Be("run-actor-1");

        var request = dispatchPort.Dispatches[0].Envelope.Payload.Unpack<WorkflowChatRequestEvent>();
        request.Prompt.Should().Be("seed-input");
        request.ScopeId.Should().Be("scope-command");
        request.ResumeSeed.SourceRunId.Should().Be("source-run");
        request.ResumeSeed.StartAtStepId.Should().Be("step-b");
        request.ResumeSeed.Variables.Should().Contain("topic", "override-topic");
        request.ResumeSeed.Variables.Should().Contain("extra", "override-extra");
        request.ResumeSeed.Variables.Should().Contain("step-a", "alpha");
    }

    [Fact]
    public async Task DispatchAsync_WhenSourceRunIsNotTerminal_ShouldNotCreateRun()
    {
        var seedPort = new StaticSeedQueryPort(CreateSeedView(status: "running"));
        var provisioningPort = new RecordingRunProvisioningPort();
        var service = CreateDispatchService(seedPort, provisioningPort, new RecordingActorDispatchPort());

        var result = await service.DispatchAsync(
            new WorkflowForkRunCommand("source-run", "step-b"),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Code.Should().Be(WorkflowForkRunStartErrorCode.SourceRunNotTerminal);
        provisioningPort.CreateRunBindings.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_WhenStepIsMissing_ShouldStillDispatchSeedForCoreValidation()
    {
        var seedPort = new StaticSeedQueryPort(CreateSeedView());
        var provisioningPort = new RecordingRunProvisioningPort();
        var service = CreateDispatchService(seedPort, provisioningPort, new RecordingActorDispatchPort());

        var result = await service.DispatchAsync(
            new WorkflowForkRunCommand("source-run", "missing-step"),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        provisioningPort.CreateRunBindings.Should().ContainSingle();
    }

    private static ICommandDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError> CreateDispatchService(
        IWorkflowRunSeedQueryPort seedPort,
        RecordingRunProvisioningPort provisioningPort,
        IActorDispatchPort dispatchPort)
    {
        var resolver = new WorkflowForkRunCommandTargetResolver(seedPort, provisioningPort, provisioningPort);
        var chatEnvelopeFactory = new WorkflowChatRequestEnvelopeFactory();
        var targetDispatcher = new ActorCommandTargetDispatcher<WorkflowForkRunCommandTarget>(dispatchPort);
        var receiptFactory = new WorkflowForkRunAcceptedReceiptFactory();
        var pipeline = new WorkflowForkRunDispatchPipeline(
            resolver,
            new DefaultCommandContextPolicy(),
            chatEnvelopeFactory,
            targetDispatcher,
            receiptFactory);
        return new DefaultCommandDispatchService<WorkflowForkRunCommand, WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>(pipeline);
    }

    private static WorkflowRunResumeSeedView CreateSeedView(string status = "failed") =>
        new(
            "source-run",
            "direct",
            WorkflowYaml,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["input"] = "seed-input",
                ["step-a"] = "alpha",
                ["topic"] = "seed-topic",
            },
            ["step-a"],
            "step-b",
            status,
            "boom",
            "scope-source");

    private sealed class StaticSeedQueryPort(WorkflowRunResumeSeedView? view) : IWorkflowRunSeedQueryPort
    {
        public Task<WorkflowRunResumeSeedView?> GetResumeSeedAsync(string runId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(view);
        }
    }

    private sealed class RecordingRunProvisioningPort : IWorkflowRunProvisioningPort, IWorkflowDefinitionParser
    {
        public List<WorkflowDefinitionBinding> CreateRunBindings { get; } = [];

        public Task<WorkflowRunCreationReceipt> CreateRunAsync(
            WorkflowDefinitionBinding definition,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CreateRunBindings.Add(definition);
            return Task.FromResult(new WorkflowRunCreationReceipt(
                "run-actor-1",
                "definition-actor-1",
                ["run-actor-1"]));
        }

        public Task DestroyAsync(string actorId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(WorkflowYamlParseResult.Success("direct"));
        }
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Dispatches.Add((actorId, envelope));
            return Task.FromResult(new DispatchAdmission(
                true,
                envelope.Id,
                DateTimeOffset.UtcNow,
                actorId,
                envelope.Propagation?.CorrelationId ?? string.Empty));
        }
    }
}
