using Aevatar.CQRS.Core.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.RunForks;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.RunForks;
using Aevatar.Workflow.Application.Runs;
using FluentAssertions;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowForkRunServiceTests
{
    [Theory]
    [InlineData(null, WorkflowForkRunStartErrorCode.SourceRunNotFound)]
    [InlineData("running", WorkflowForkRunStartErrorCode.SourceRunNotTerminal)]
    public async Task ForkAsync_WhenSourceRunMissingOrActive_ShouldReturnErrorWithoutCreateOrDispatch(
        string? status,
        WorkflowForkRunStartErrorCode expectedCode)
    {
        var seedPort = new RecordingSeedQueryPort
        {
            View = status == null ? null : CreateSeedView(status),
        };
        var runPort = new RecordingRunProvisioningPort();
        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateService(seedPort, runPort, dispatchPort);

        var result = await service.ForkAsync(new WorkflowForkRunCommand(
            " source-run ",
            "step-b"));

        result.Succeeded.Should().BeFalse();
        result.Error!.Code.Should().Be(expectedCode);
        seedPort.RequestedRunIds.Should().Equal("source-run");
        runPort.CreateRunBindings.Should().BeEmpty();
        dispatchPort.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task ForkAsync_WhenYamlDoesNotCompile_ShouldReturnStructuredErrorWithoutCreate()
    {
        var seedPort = new RecordingSeedQueryPort
        {
            View = CreateSeedView("failed", workflowYaml: "broken yaml"),
        };
        var runPort = new RecordingRunProvisioningPort
        {
            ParseResult = WorkflowYamlParseResult.Invalid("compile failed"),
        };
        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateService(seedPort, runPort, dispatchPort);

        var result = await service.ForkAsync(new WorkflowForkRunCommand(
            "source-run",
            "step-b"));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().BeEquivalentTo(new
        {
            Code = WorkflowForkRunStartErrorCode.InvalidWorkflowYaml,
            SourceRunId = "source-run",
            StartAtStepId = "step-b",
            Reason = "compile failed",
        });
        runPort.CreateRunBindings.Should().BeEmpty();
        dispatchPort.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task ForkAsync_WhenStartAtStepIdIsAbsent_ShouldReturnStructuredErrorWithoutCreate()
    {
        var seedPort = new RecordingSeedQueryPort
        {
            View = CreateSeedView("failed"),
        };
        var runPort = new RecordingRunProvisioningPort();
        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateService(seedPort, runPort, dispatchPort);

        var result = await service.ForkAsync(new WorkflowForkRunCommand(
            "source-run",
            "missing-step"));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().BeEquivalentTo(new
        {
            Code = WorkflowForkRunStartErrorCode.StartStepNotFound,
            SourceRunId = "source-run",
            StartAtStepId = "missing-step",
        });
        result.Error!.Reason.Should().Contain("missing-step");
        runPort.CreateRunBindings.Should().BeEmpty();
        dispatchPort.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task ForkAsync_ShouldLetVariableOverridesWinInDispatchedResumeSeed()
    {
        var seedPort = new RecordingSeedQueryPort
        {
            View = CreateSeedView(
                "failed",
                variables: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["input"] = "seed-input",
                    ["topic"] = "seed-topic",
                    ["step-a"] = "alpha",
                }),
        };
        var runPort = new RecordingRunProvisioningPort();
        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateService(seedPort, runPort, dispatchPort);

        var result = await service.ForkAsync(new WorkflowForkRunCommand(
            "source-run",
            "step-b",
            VariableOverrides: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["topic"] = "override-topic",
                ["extra"] = "override-extra",
            },
            Input: "command-input",
            CommandId: "cmd-fork",
            CorrelationId: "corr-fork"));

        result.Succeeded.Should().BeTrue();
        var request = dispatchPort.DispatchedRequest();
        request.ResumeSeed.Variables["topic"].Should().Be("override-topic");
        request.ResumeSeed.Variables["extra"].Should().Be("override-extra");
        request.ResumeSeed.Variables["step-a"].Should().Be("alpha");
        request.Prompt.Should().Be("seed-input");
    }

    [Fact]
    public async Task ForkAsync_HappyPath_ShouldCreateRunWithChosenYamlAndDispatchResumeSeed()
    {
        var sourceYaml = WorkflowYaml("source");
        var editedYaml = WorkflowYaml("edited");
        var childYaml = WorkflowYaml("child");
        var seedPort = new RecordingSeedQueryPort
        {
            View = CreateSeedView(
                "completed",
                workflowYaml: sourceYaml,
                inlineWorkflowYamls: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["source-child"] = WorkflowYaml("source-child"),
                },
                variables: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["step-a"] = "alpha",
                    ["topic"] = "seed-topic",
                }),
        };
        var runPort = new RecordingRunProvisioningPort();
        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateService(seedPort, runPort, dispatchPort);

        var result = await service.ForkAsync(new WorkflowForkRunCommand(
            SourceRunId: "source-run",
            StartAtStepId: "step-b",
            InlineYaml: editedYaml,
            InlineSubYamls: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["child"] = childYaml,
            },
            VariableOverrides: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["input"] = "override-input",
            },
            Input: "fallback-input",
            CommandId: "cmd-1857",
            CorrelationId: "corr-1857"));

        result.Succeeded.Should().BeTrue();
        result.Receipt.Should().BeEquivalentTo(new
        {
            SourceRunId = "source-run",
            NewRunActorId = "run-created",
            WorkflowName = "edited",
            Accepted = true,
            CommandId = "cmd-1857",
            CorrelationId = "corr-1857",
        });
        result.Receipt!.AckedAt.Should().NotBe(default);

        runPort.CreateRunBindings.Should().ContainSingle();
        var binding = runPort.CreateRunBindings.Single();
        binding.WorkflowName.Should().Be("edited");
        binding.WorkflowYaml.Should().Be(editedYaml);
        binding.InlineWorkflowYamls.Should().Contain("child", childYaml);

        dispatchPort.Dispatches.Should().ContainSingle();
        dispatchPort.Dispatches.Single().ActorId.Should().Be("run-created");
        var envelope = dispatchPort.Dispatches.Single().Envelope;
        envelope.Id.Should().Be("cmd-1857");
        envelope.Propagation!.CorrelationId.Should().Be("corr-1857");
        envelope.Route.GetTargetActorId().Should().Be("run-created");

        var request = envelope.Payload.Unpack<WorkflowChatRequestEvent>();
        request.Prompt.Should().Be("override-input");
        request.ResumeSeed.SourceRunId.Should().Be("source-run");
        request.ResumeSeed.StartAtStepId.Should().Be("step-b");
        request.ResumeSeed.Variables.Should().Contain("step-a", "alpha");
        request.ResumeSeed.Variables.Should().Contain("topic", "seed-topic");
        request.ResumeSeed.Variables.Should().Contain("input", "override-input");
    }

    private static WorkflowForkRunService CreateService(
        RecordingSeedQueryPort seedPort,
        RecordingRunProvisioningPort runPort,
        RecordingActorDispatchPort dispatchPort) =>
        new(
            seedPort,
            runPort,
            runPort,
            new DefaultCommandContextPolicy(),
            new WorkflowChatRequestEnvelopeFactory(),
            dispatchPort);

    private static WorkflowRunResumeSeedView CreateSeedView(
        string status,
        string? workflowYaml = null,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
        IReadOnlyDictionary<string, string>? variables = null) =>
        new(
            "source-run",
            status,
            workflowYaml ?? WorkflowYaml("source"),
            inlineWorkflowYamls ?? new Dictionary<string, string>(StringComparer.Ordinal),
            variables ?? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["input"] = "seed-input",
                ["step-a"] = "alpha",
            },
            ["step-a"],
            "step-b",
            status.Equals("failed", StringComparison.OrdinalIgnoreCase) ? "boom" : string.Empty);

    private static string WorkflowYaml(string name) =>
        $$"""
        name: {{name}}
        roles: []
        steps:
          - id: step-a
            type: transform
          - id: step-b
            type: transform
        """;

    private sealed class RecordingSeedQueryPort : IWorkflowRunSeedQueryPort
    {
        public WorkflowRunResumeSeedView? View { get; set; }
        public List<string> RequestedRunIds { get; } = [];

        public Task<WorkflowRunResumeSeedView?> GetResumeSeedAsync(
            string runId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RequestedRunIds.Add(runId);
            return Task.FromResult(View);
        }
    }

    private sealed class RecordingRunProvisioningPort : IWorkflowRunProvisioningPort, IWorkflowDefinitionParser
    {
        public WorkflowYamlParseResult? ParseResult { get; set; }
        public List<WorkflowDefinitionBinding> CreateRunBindings { get; } = [];
        public List<string> DestroyedActorIds { get; } = [];

        public Task<WorkflowRunCreationReceipt> CreateRunAsync(
            WorkflowDefinitionBinding definition,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CreateRunBindings.Add(definition);
            return Task.FromResult(new WorkflowRunCreationReceipt(
                "run-created",
                "definition-created",
                ["definition-created", "run-created"]));
        }

        public Task DestroyAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DestroyedActorIds.Add(actorId);
            return Task.CompletedTask;
        }

        public Task<WorkflowYamlParseResult> ParseWorkflowYamlAsync(
            string workflowYaml,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (ParseResult != null)
                return Task.FromResult(ParseResult);

            var nameLine = workflowYaml
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(static line => line.StartsWith("name:", StringComparison.Ordinal));
            var workflowName = nameLine == null
                ? string.Empty
                : nameLine["name:".Length..].Trim();
            return Task.FromResult(WorkflowYamlParseResult.Success(workflowName));
        }
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<RecordedDispatch> Dispatches { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Dispatches.Add(new RecordedDispatch(actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }

        public WorkflowChatRequestEvent DispatchedRequest() =>
            Dispatches.Single().Envelope.Payload.Unpack<WorkflowChatRequestEvent>();
    }

    private sealed record RecordedDispatch(string ActorId, EventEnvelope Envelope);
}
