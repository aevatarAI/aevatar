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
using WorkflowApplicationCallerCredential = Aevatar.Workflow.Application.Abstractions.Runs.WorkflowCallerCredential;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowForkRunCommandDispatchTests
{
    [Theory]
    [InlineData(null, WorkflowForkRunStartErrorCode.SourceRunNotFound)]
    [InlineData("running", WorkflowForkRunStartErrorCode.SourceRunNotTerminal)]
    public async Task ResolveAsync_WhenSourceRunMissingOrActive_ShouldReturnErrorWithoutCreate(
        string? status,
        WorkflowForkRunStartErrorCode expectedCode)
    {
        var seedPort = new RecordingSeedQueryPort
        {
            View = status == null ? null : CreateSeedView(status),
        };
        var runPort = new RecordingRunProvisioningPort();
        var resolver = CreateResolver(seedPort, runPort);

        var result = await resolver.ResolveAsync(new WorkflowForkRunCommand(
            " source-run ",
            "step-b"));

        result.Succeeded.Should().BeFalse();
        result.Error.Code.Should().Be(expectedCode);
        seedPort.RequestedRunIds.Should().Equal("source-run");
        seedPort.RequestedScopeIds.Should().Equal(string.Empty);
        runPort.CreateRunBindings.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_WhenYamlDoesNotCompile_ShouldReturnStructuredErrorWithoutCreate()
    {
        var seedPort = new RecordingSeedQueryPort
        {
            View = CreateSeedView("failed", workflowYaml: "broken yaml"),
        };
        var runPort = new RecordingRunProvisioningPort
        {
            ParseResult = WorkflowYamlParseResult.Invalid("compile failed"),
        };
        var resolver = CreateResolver(seedPort, runPort);

        var result = await resolver.ResolveAsync(new WorkflowForkRunCommand(
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
    }

    [Fact]
    public async Task ResolveAsync_WhenWorkflowYamlIsEmpty_ShouldReturnStructuredErrorWithoutCreate()
    {
        var seedPort = new RecordingSeedQueryPort
        {
            View = CreateSeedView("failed", workflowYaml: "   "),
        };
        var runPort = new RecordingRunProvisioningPort();
        var resolver = CreateResolver(seedPort, runPort);

        var result = await resolver.ResolveAsync(new WorkflowForkRunCommand(
            "source-run",
            "step-b"));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().BeEquivalentTo(new
        {
            Code = WorkflowForkRunStartErrorCode.InvalidWorkflowYaml,
            SourceRunId = "source-run",
            StartAtStepId = "step-b",
            Reason = "Workflow YAML is required.",
        });
        runPort.ParseRequests.Should().BeEmpty();
        runPort.CreateRunBindings.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_WhenCoreWorkflowParserRejectsYaml_ShouldReturnInvalidYamlWithoutCreate()
    {
        var seedPort = new RecordingSeedQueryPort
        {
            View = CreateSeedView("failed", workflowYaml: "name: broken\nsteps:\n  - type: transform"),
        };
        var runPort = new RecordingRunProvisioningPort
        {
            ParseResult = WorkflowYamlParseResult.Success("broken"),
        };
        var resolver = CreateResolver(seedPort, runPort);

        var result = await resolver.ResolveAsync(new WorkflowForkRunCommand(
            "source-run",
            "step-b"));

        result.Succeeded.Should().BeFalse();
        result.Error.Code.Should().Be(WorkflowForkRunStartErrorCode.InvalidWorkflowYaml);
        result.Error.Reason.Should().Contain("step");
        runPort.ParseRequests.Should().ContainSingle();
        runPort.CreateRunBindings.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_WhenStartAtStepIdIsAbsent_ShouldReturnStructuredErrorWithoutCreate()
    {
        var seedPort = new RecordingSeedQueryPort
        {
            View = CreateSeedView("failed"),
        };
        var runPort = new RecordingRunProvisioningPort();
        var resolver = CreateResolver(seedPort, runPort);

        var result = await resolver.ResolveAsync(new WorkflowForkRunCommand(
            "source-run",
            "missing-step"));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().BeEquivalentTo(new
        {
            Code = WorkflowForkRunStartErrorCode.StartStepNotFound,
            SourceRunId = "source-run",
            StartAtStepId = "missing-step",
        });
        result.Error.Reason.Should().Contain("missing-step");
        runPort.CreateRunBindings.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_WhenCallerCredentialMalformed_ShouldRejectBeforeSeedQuery()
    {
        var seedPort = new RecordingSeedQueryPort
        {
            View = CreateSeedView("failed"),
        };
        var runPort = new RecordingRunProvisioningPort();
        var resolver = CreateResolver(seedPort, runPort);

        var result = await resolver.ResolveAsync(new WorkflowForkRunCommand(
            "source-run",
            "step-b",
            CallerCredential: new WorkflowApplicationCallerCredential("Bearer token-123")));

        result.Succeeded.Should().BeFalse();
        result.Error.Code.Should().Be(WorkflowForkRunStartErrorCode.InvalidCallerCredential);
        seedPort.RequestedRunIds.Should().BeEmpty();
        runPort.CreateRunBindings.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_ShouldLetVariableOverridesWinInRequestLevelForkSeed()
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
                },
                scopeId: "scope-1"),
        };
        var runPort = new RecordingRunProvisioningPort();
        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateDispatchService(seedPort, runPort, dispatchPort);

        var result = await service.DispatchAsync(new WorkflowForkRunCommand(
            "source-run",
            "step-b",
            VariableOverrides: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["topic"] = "override-topic",
                ["extra"] = "override-extra",
            },
            Input: "command-input",
            CommandId: "cmd-fork",
            CorrelationId: "corr-fork",
            ScopeId: "scope-1"));

        result.Succeeded.Should().BeTrue();
        seedPort.RequestedScopeIds.Should().Equal("scope-1");
        var request = dispatchPort.DispatchedRequest();
        request.ForkSeed.Variables["topic"].Should().Be("override-topic");
        request.ForkSeed.Variables["extra"].Should().Be("override-extra");
        request.ForkSeed.Variables["step-a"].Should().Be("alpha");
        request.Prompt.Should().Be("seed-input");
    }

    [Fact]
    public async Task DispatchAsync_HappyPath_ShouldCreateRunWithChosenYamlAndDispatchTypedForkSeedAndScope()
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
                },
                idempotencyByStepId: new Dictionary<string, WorkflowStepIdempotencyView>(StringComparer.Ordinal)
                {
                    ["step-b"] = new("source-run", "step-b", 2, "source-run:step-b:2"),
                },
                scopeId: "scope-1"),
        };
        var runPort = new RecordingRunProvisioningPort();
        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateDispatchService(seedPort, runPort, dispatchPort);

        var result = await service.DispatchAsync(new WorkflowForkRunCommand(
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
            CorrelationId: "corr-1857",
            ScopeId: "scope-1",
            CallerCredential: new WorkflowApplicationCallerCredential("typed-token")));

        result.Succeeded.Should().BeTrue();
        seedPort.RequestedScopeIds.Should().Equal("scope-1");
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
        binding.ScopeId.Should().Be("scope-1");
        binding.InlineWorkflowYamls.Should().Contain("child", childYaml);

        dispatchPort.Dispatches.Should().ContainSingle();
        dispatchPort.Dispatches.Single().ActorId.Should().Be("run-created");
        var envelope = dispatchPort.Dispatches.Single().Envelope;
        envelope.Id.Should().Be("cmd-1857");
        envelope.Propagation!.CorrelationId.Should().Be("corr-1857");
        envelope.Route.GetTargetActorId().Should().Be("run-created");

        var request = envelope.Payload.Unpack<WorkflowChatRequestEvent>();
        request.Prompt.Should().Be("override-input");
        request.ScopeId.Should().Be("scope-1");
        request.CallerCredential.BearerToken.Should().Be("typed-token");
        request.ForkSeed.SourceRunId.Should().Be("source-run");
        request.ForkSeed.StartAtStepId.Should().Be("step-b");
        request.ForkSeed.StartStepIdempotency.LogicalRunId.Should().Be("source-run");
        request.ForkSeed.StartStepIdempotency.StepId.Should().Be("step-b");
        request.ForkSeed.StartStepIdempotency.LogicalAttempt.Should().Be(2);
        request.ForkSeed.StartStepIdempotency.IdempotencyKey.Should().Be("source-run:step-b:2");
        request.ForkSeed.Variables.Should().Contain("step-a", "alpha");
        request.ForkSeed.Variables.Should().Contain("topic", "seed-topic");
        request.ForkSeed.Variables.Should().Contain("input", "override-input");
    }

    [Fact]
    public async Task DispatchAsync_WhenSeedScopeDiffersFromTrustedCommandScope_ShouldFailClosed()
    {
        var seedPort = new RecordingSeedQueryPort
        {
            View = CreateSeedView("failed", scopeId: "source-scope-1"),
        };
        var runPort = new RecordingRunProvisioningPort();
        var dispatchPort = new RecordingActorDispatchPort();
        var service = CreateDispatchService(seedPort, runPort, dispatchPort);

        var result = await service.DispatchAsync(new WorkflowForkRunCommand(
            SourceRunId: "source-run",
            StartAtStepId: "step-b",
            ScopeId: "attacker-scope"));

        result.Succeeded.Should().BeFalse();
        result.Error.Code.Should().Be(WorkflowForkRunStartErrorCode.SourceRunNotFound);
        seedPort.RequestedScopeIds.Should().Equal("attacker-scope");
        runPort.CreateRunBindings.Should().BeEmpty();
        dispatchPort.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_WhenRunCreationFails_ShouldReturnStructuredErrorWithoutDispatchPreparation()
    {
        var seedPort = new RecordingSeedQueryPort
        {
            View = CreateSeedView("failed"),
        };
        var runPort = new RecordingRunProvisioningPort
        {
            CreateRunException = new InvalidOperationException("create boom"),
        };
        var resolver = CreateResolver(seedPort, runPort);

        var result = await resolver.ResolveAsync(new WorkflowForkRunCommand(
            "source-run",
            "step-b"));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().BeEquivalentTo(new
        {
            Code = WorkflowForkRunStartErrorCode.RunCreationFailed,
            SourceRunId = "source-run",
            StartAtStepId = "step-b",
            Reason = "create boom",
        });
        runPort.CreateRunBindings.Should().ContainSingle();
        runPort.DestroyedActorIds.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_WhenActorDispatchFails_ShouldReturnDispatchErrorAndCleanupCreatedActors()
    {
        var seedPort = new RecordingSeedQueryPort
        {
            View = CreateSeedView("failed"),
        };
        var runPort = new RecordingRunProvisioningPort();
        var dispatchPort = new RecordingActorDispatchPort
        {
            DispatchException = new InvalidOperationException("dispatch boom"),
        };
        var service = CreateDispatchService(seedPort, runPort, dispatchPort);

        var result = await service.DispatchAsync(new WorkflowForkRunCommand("source-run", "step-b"));

        result.Succeeded.Should().BeFalse();
        result.Error.Code.Should().Be(WorkflowForkRunStartErrorCode.DispatchFailed);
        result.Error.Reason.Should().Contain("dispatch boom");
        runPort.DestroyedActorIds.Should().Equal("run-created", "definition-created");
        runPort.DestroyedActorIds.Should().OnlyHaveUniqueItems();
    }

    private static WorkflowForkRunCommandTargetResolver CreateResolver(
        RecordingSeedQueryPort seedPort,
        RecordingRunProvisioningPort runPort) =>
        new(seedPort, runPort, runPort);

    private static WorkflowForkRunCommandDispatchService CreateDispatchService(
        RecordingSeedQueryPort seedPort,
        RecordingRunProvisioningPort runPort,
        RecordingActorDispatchPort dispatchPort)
    {
        var pipeline = new DefaultCommandDispatchPipeline<WorkflowForkRunCommand, WorkflowForkRunCommandTarget, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>(
            CreateResolver(seedPort, runPort),
            new DefaultCommandContextPolicy(),
            new WorkflowForkRunCommandEnvelopeFactory(new WorkflowChatRequestEnvelopeFactory()),
            new ActorCommandTargetDispatcher<WorkflowForkRunCommandTarget>(dispatchPort),
            new WorkflowForkRunAcceptedReceiptFactory());
        return new WorkflowForkRunCommandDispatchService(pipeline);
    }

    private static WorkflowRunForkSeedView CreateSeedView(
        string status,
        string? workflowYaml = null,
        IReadOnlyDictionary<string, string>? inlineWorkflowYamls = null,
        IReadOnlyDictionary<string, string>? variables = null,
        string scopeId = "",
        IReadOnlyDictionary<string, WorkflowStepIdempotencyView>? idempotencyByStepId = null) =>
        new WorkflowRunForkSeedView(
            SourceRunId: "source-run",
            Status: status,
            WorkflowYaml: workflowYaml ?? WorkflowYaml("source"),
            InlineWorkflowYamls: inlineWorkflowYamls ?? new Dictionary<string, string>(StringComparer.Ordinal),
            ExpectedExecutionMode: ExternalCapabilityExecutionMode.Interactive,
            Variables: variables ?? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["input"] = "seed-input",
                ["step-a"] = "alpha",
            },
            CompletedStepIds: ["step-a"],
            LastFailedStepId: "step-b",
            FinalError: status.Equals("failed", StringComparison.OrdinalIgnoreCase) ? "boom" : string.Empty,
            ScopeId: scopeId,
            IdempotencyByStepId: idempotencyByStepId ?? new Dictionary<string, WorkflowStepIdempotencyView>(StringComparer.Ordinal));

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

    private sealed class RecordingSeedQueryPort : IWorkflowRunForkSeedQueryPort
    {
        public WorkflowRunForkSeedView? View { get; set; }
        public List<string> RequestedScopeIds { get; } = [];
        public List<string> RequestedRunIds { get; } = [];

        public Task<WorkflowRunForkSeedView?> GetForkSeedAsync(
            string scopeId,
            string runId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RequestedScopeIds.Add(scopeId);
            RequestedRunIds.Add(runId);
            return Task.FromResult(View);
        }
    }

    private sealed class RecordingRunProvisioningPort : IWorkflowRunProvisioningPort, IWorkflowDefinitionParser
    {
        public WorkflowYamlParseResult? ParseResult { get; set; }
        public Exception? CreateRunException { get; set; }
        public List<string> ParseRequests { get; } = [];
        public List<WorkflowDefinitionBinding> CreateRunBindings { get; } = [];
        public List<string> DestroyedActorIds { get; } = [];

        public Task<WorkflowRunCreationReceipt> CreateRunAsync(
            WorkflowDefinitionBinding definition,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CreateRunBindings.Add(definition);
            if (CreateRunException != null)
                throw CreateRunException;

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
            ParseRequests.Add(workflowYaml);
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

        public async Task<WorkflowInlineYamlBundleParseResult> ParseInlineWorkflowBundleAsync(
            IReadOnlyList<WorkflowChatInlineYamlDocument> inlineWorkflowDocuments,
            CancellationToken ct = default)
        {
            if (inlineWorkflowDocuments.Count == 0)
                return WorkflowInlineYamlBundleParseResult.Invalid("workflowYamls is required.");

            var workflowYamlsByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string entryWorkflowName = string.Empty;
            string entryWorkflowYaml = string.Empty;
            for (var i = 0; i < inlineWorkflowDocuments.Count; i++)
            {
                var document = inlineWorkflowDocuments[i];
                var parseResult = await ParseWorkflowYamlAsync(document.Yaml, ct);
                if (!parseResult.Succeeded)
                    return WorkflowInlineYamlBundleParseResult.Invalid(parseResult.Error, parseResult.ExternalCapabilityReadiness);

                if (!workflowYamlsByName.TryAdd(parseResult.WorkflowName, document.Yaml))
                    return WorkflowInlineYamlBundleParseResult.Invalid($"Duplicate workflow name '{parseResult.WorkflowName}' in workflowYamls.");

                if (i == 0)
                {
                    entryWorkflowName = parseResult.WorkflowName;
                    entryWorkflowYaml = document.Yaml;
                }
            }

            return WorkflowInlineYamlBundleParseResult.Success(entryWorkflowName, entryWorkflowYaml, workflowYamlsByName);
        }
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<RecordedDispatch> Dispatches { get; } = [];
        public Exception? DispatchException { get; set; }

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (DispatchException != null)
                throw DispatchException;

            Dispatches.Add(new RecordedDispatch(actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }

        public WorkflowChatRequestEvent DispatchedRequest() =>
            Dispatches.Single().Envelope.Payload.Unpack<WorkflowChatRequestEvent>();
    }

    private sealed record RecordedDispatch(string ActorId, EventEnvelope Envelope);
}
