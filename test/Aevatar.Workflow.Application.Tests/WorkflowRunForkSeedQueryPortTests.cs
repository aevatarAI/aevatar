using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Projection.ReadModels;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Tests;

public sealed class WorkflowRunForkSeedQueryPortTests
{
    // Serialized by the schema-v0 WorkflowRunState descriptor. Keep this as a
    // fixed wire fixture so current generated code cannot rewrite the input.
    private const string LegacyWorkflowRunStateAnyBase64 =
        "CjV0eXBlLmdvb2dsZWFwaXMuY29tL2FldmF0YXIud29ya2Zsb3cuV29ya2Zsb3dSdW5TdGF0ZRL/AgoR" +
        "ZGVmaW5pdGlvbi1sZWdhY3kSFm5hbWU6IGxlZ2FjeQpzdGVwczogW10aBmxlZ2FjeSABOgpsZWdhY3kt" +
        "cnVuQgZmYWlsZWRKDGxlZ2FjeS1pbnB1dFoObGVnYWN5IGZhaWx1cmVihgIKGXdvcmtmbG93X2V4ZWN1" +
        "dGlvbl9rZXJuZWwS6AEKQXR5cGUuZ29vZ2xlYXBpcy5jb20vYWV2YXRhci53b3JrZmxvdy5Xb3JrZmxv" +
        "d0V4ZWN1dGlvbktlcm5lbFN0YXRlEqIBGgZzdGVwLWIiFGxlZ2FjeS1jdXJyZW50LWlucHV0KhUKBWlu" +
        "cHV0EgxsZWdhY3ktaW5wdXQqDwoGc3RlcC1hEgVhbHBoYTIKCgZzdGVwLWIQAVoXCgZzdGVwLWISDWxl" +
        "Z2FjeS1leGVjLWJ6NQoGc3RlcC1iEisKCmxlZ2FjeS1ydW4SBnN0ZXAtYhgCIhNsZWdhY3ktcnVuOnN0" +
        "ZXAtYjoykgEMc2NvcGUtbGVnYWN5";

    [Fact]
    public void ForkSeedReadModelMapper_ShouldBuildLegacySeedFromFixedSchemaV0WireFixture()
    {
        var packed = Any.Parser.ParseFrom(Convert.FromBase64String(LegacyWorkflowRunStateAnyBase64));
        packed.Is(WorkflowRunState.Descriptor).Should().BeTrue();
        var state = packed.Unpack<WorkflowRunState>();
        var mapper = new WorkflowRunForkSeedReadModelMapper();

        var snapshot = mapper.ToProjectionSnapshot(state);
        var view = mapper.ToSeedView(BuildDocument(state, snapshot));

        snapshot.NormalizedValues.Should().BeNull();
        snapshot.Variables.Should().Contain("input", "legacy-input");
        snapshot.Variables.Should().Contain("step-a", "alpha");
        snapshot.CompletedStepIds.Should().Equal("step-a");
        snapshot.LastFailedStepId.Should().Be("step-b");
        snapshot.IdempotencyByStepId["step-b"].IdempotencyKey
            .Should().Be("legacy-run:step-b:2");
        view.SourceRunId.Should().Be("legacy-run");
        view.Status.Should().Be("failed");
        view.Variables.Should().Contain("step-a", "alpha");
        view.NormalizedValues.Should().BeNull();
    }

    [Fact]
    public void ForkSeedReadModelMapper_ShouldMapCompletedRunForkSeed()
    {
        var state = BuildWorkflowRunState("run-completed", "completed", finalError: string.Empty);
        state.ExecutionStates["kernel-state"] = Any.Pack(new WorkflowExecutionKernelState
        {
            CurrentStepId = string.Empty,
            Variables =
            {
                ["input"] = "latest-input",
                ["step-a"] = "alpha",
                ["step-b"] = "bravo",
                ["workflow.usage.total_tokens"] = "12",
                ["steps.step-a.output"] = "alpha",
                ["steps.step-a.success"] = "true",
                ["workflow_call.invocation_id"] = "call-1",
            },
            IdempotencyByStepId =
            {
                ["step-b"] = new WorkflowStepIdempotencyState
                {
                    LogicalRunId = "run-completed",
                    StepId = "step-b",
                    LogicalAttempt = 1,
                    IdempotencyKey = "run-completed:step-b:1",
                },
            },
        });

        var mapper = new WorkflowRunForkSeedReadModelMapper();
        var snapshot = mapper.ToProjectionSnapshot(state);
        var document = BuildDocument(state, snapshot);
        var view = mapper.ToSeedView(document);

        view.SourceRunId.Should().Be("run-completed");
        view.Status.Should().Be("completed");
        view.WorkflowYaml.Should().Be("name: demo\nsteps: []");
        view.InlineWorkflowYamls.Should().Contain("child", "name: child");
        view.Variables.Should().Contain("step-a", "alpha");
        view.Variables.Should().Contain("workflow.usage.total_tokens", "12");
        view.CompletedStepIds.Should().Equal("step-a", "step-b");
        view.LastFailedStepId.Should().BeEmpty();
        view.FinalError.Should().BeEmpty();
        view.ScopeId.Should().Be("scope-1");
        view.WorkflowId.Should().Be("wf-alpha");
        view.RevisionId.Should().Be("rev-alpha");
        view.IdempotencyByStepId.Should().ContainKey("step-b");
        view.IdempotencyByStepId!["step-b"].IdempotencyKey.Should().Be("run-completed:step-b:1");
    }

    [Fact]
    public void ForkSeedReadModelMapper_ShouldUseKernelAsOnlyForEachChildOutputSource()
    {
        var state = BuildWorkflowRunState("run-foreach", "failed", "foreach failed");
        state.ExecutionStates["kernel-state"] = Any.Pack(new WorkflowExecutionKernelState
        {
            CurrentStepId = "foreach-parent",
            Variables =
            {
                ["input"] = "source-input",
                ["foreach-parent_item_0"] = "kernel-wins",
                ["foreach-parent_item_1"] = "kernel-output-1",
                ["foreach-parent_item_2"] = "kernel-output-2",
            },
        });
        var forEachState = new ForEachModuleState
        {
            CompletedChildOutputsRunId = "run-foreach",
            CompletedChildOutputs =
            {
                ["foreach-parent_item_0"] = "completed-must-not-overwrite-kernel",
                ["foreach-parent_item_1"] = "completed-output",
                ["foreach-parent_item_2"] = "older-completed-output",
            },
        };
        forEachState.Parents["run-foreach:foreach-parent:execution:active"] = new ForEachParentState
        {
            ParentRunId = "run-foreach",
            Collected =
            {
                new ForEachItemResult
                {
                    StepId = "foreach-parent_item_2",
                    Index = 2,
                    Success = true,
                    Output = "active-output",
                },
                new ForEachItemResult
                {
                    Index = 3,
                    Success = true,
                    Output = "legacy-result-without-step-id",
                },
            },
        };
        forEachState.Parents["stale-run:foreach-parent:execution:stale"] = new ForEachParentState
        {
            ParentRunId = "stale-run",
            Collected =
            {
                new ForEachItemResult
                {
                    StepId = "stale-child",
                    Index = 0,
                    Success = true,
                    Output = "stale-output",
                },
            },
        };
        state.ExecutionStates["foreach-state"] = Any.Pack(forEachState);

        var snapshot = new WorkflowRunForkSeedReadModelMapper().ToProjectionSnapshot(state);

        snapshot.Variables.Should().Contain("foreach-parent_item_0", "kernel-wins");
        snapshot.Variables.Should().Contain("foreach-parent_item_1", "kernel-output-1");
        snapshot.Variables.Should().Contain("foreach-parent_item_2", "kernel-output-2");
        snapshot.Variables.Should().NotContainValue("legacy-result-without-step-id");
        snapshot.Variables.Should().NotContainKey("stale-child");
        snapshot.CompletedStepIds.Should().Equal(
            "foreach-parent_item_0",
            "foreach-parent_item_1",
            "foreach-parent_item_2");
    }

    [Fact]
    public void ForkSeedReadModelMapper_ShouldIgnoreCompletedForEachOutputsFromAnotherRun()
    {
        var state = BuildWorkflowRunState("current-run", "failed", "failed");
        state.ExecutionStates["foreach-state"] = Any.Pack(new ForEachModuleState
        {
            CompletedChildOutputsRunId = "previous-run",
            CompletedChildOutputs =
            {
                ["previous-child"] = "previous-output",
            },
        });

        var snapshot = new WorkflowRunForkSeedReadModelMapper().ToProjectionSnapshot(state);

        snapshot.Variables.Should().BeEmpty();
        snapshot.CompletedStepIds.Should().BeEmpty();
    }

    [Fact]
    public void ForkSeedReadModelMapper_ShouldCarryOriginalRunIdFromLineage()
    {
        var mapper = new WorkflowRunForkSeedReadModelMapper();

        var view = mapper.ToSeedView(new WorkflowExecutionCurrentStateDocument
        {
            RunId = "run-source-gamma",
            Status = "failed",
            ScopeId = "scope-alpha",
            Lineage = new WorkflowRunLineage
            {
                Availability = WorkflowRunLineageAvailability.Available,
                RetryFork = new WorkflowRunRetryForkLineage
                {
                    Availability = WorkflowRunLineageAvailability.Available,
                    SourceRunId = "run-source-gamma",
                    OriginalRunId = "run-original-alpha",
                },
            },
        });

        view.SourceRunId.Should().Be("run-source-gamma");
        view.OriginalRunId.Should().Be("run-original-alpha");
    }

    [Fact]
    public async Task GetForkSeedAsync_ShouldReadFailedRunForkSeedThroughCurrentStateReadModel()
    {
        var state = BuildWorkflowRunState("run-failed", "failed", "step boom");
        state.ExecutionStates["kernel-state"] = Any.Pack(new WorkflowExecutionKernelState
        {
            CurrentStepId = "step-failed",
            Variables =
            {
                ["input"] = "failed-input",
                ["step-a"] = "alpha",
                ["step-b"] = "bravo",
                ["steps.step-b.error"] = string.Empty,
            },
        });

        var mapper = new WorkflowRunForkSeedReadModelMapper();
        var currentStateReader = new RecordingDocumentReader<WorkflowExecutionCurrentStateDocument>
        {
            Item = BuildDocument(state, mapper.ToProjectionSnapshot(state)),
        };
        var bindingReader = new FakeWorkflowRunBindingReader(
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "actor-run-failed",
                "definition-1",
                "run-failed",
                "demo",
                "name: demo\nsteps: []",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Interactive,
                "scope-1"));
        var port = new WorkflowRunForkSeedQueryPort(
            currentStateReader,
            bindingReader,
            mapper);

        var view = await port.GetForkSeedAsync("scope-1", "run-failed", CancellationToken.None);

        view.Should().NotBeNull();
        view!.Status.Should().Be("failed");
        view.WorkflowYaml.Should().Be("name: demo\nsteps: []");
        view.Variables.Should().Contain("step-a", "alpha");
        view.Variables.Should().Contain("step-b", "bravo");
        view.CompletedStepIds.Should().Equal("step-a", "step-b");
        view.LastFailedStepId.Should().Be("step-failed");
        view.FinalError.Should().Be("step boom");
        view.ScopeId.Should().Be("scope-1");
        bindingReader.LastQuery.Should().NotBeNull();
        bindingReader.LastQuery!.ScopeId.Should().Be("scope-1");
        bindingReader.LastQuery.RunIds.Should().Equal("run-failed");
        currentStateReader.GetKeys.Should().ContainSingle().Which.Should().Be("actor-run-failed");
    }

    [Fact]
    public async Task GetForkSeedAsync_WhenBindingBelongsToVictimScope_ShouldReturnNullWithoutReadingCurrentState()
    {
        var currentStateReader = new RecordingDocumentReader<WorkflowExecutionCurrentStateDocument>();
        var bindingReader = new FakeWorkflowRunBindingReader(
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "actor-victim-run",
                "wf-alpha",
                "victim-run",
                "demo",
                "name: demo\nsteps: []",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Interactive,
                "victim-scope"));
        var port = new WorkflowRunForkSeedQueryPort(
            currentStateReader,
            bindingReader,
            new WorkflowRunForkSeedReadModelMapper());

        var view = await port.GetForkSeedAsync("attacker-scope", "victim-run", CancellationToken.None);

        view.Should().BeNull();
        bindingReader.LastQuery.Should().NotBeNull();
        bindingReader.LastQuery!.ScopeId.Should().Be("attacker-scope");
        bindingReader.LastQuery.RunIds.Should().Equal("victim-run");
        currentStateReader.GetKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task GetForkSeedAsync_WhenCurrentStateBelongsToVictimScope_ShouldReturnNull()
    {
        var victimState = BuildWorkflowRunState("victim-run", "failed", "step boom");
        victimState.ScopeId = "victim-scope";
        var mapper = new WorkflowRunForkSeedReadModelMapper();
        var currentStateReader = new RecordingDocumentReader<WorkflowExecutionCurrentStateDocument>
        {
            Item = BuildDocument(victimState, mapper.ToProjectionSnapshot(victimState)),
        };
        var bindingReader = new FakeWorkflowRunBindingReader(
            new WorkflowActorBinding(
                WorkflowActorKind.Run,
                "actor-victim-run",
                "wf-alpha",
                "victim-run",
                "demo",
                "name: demo\nsteps: []",
                new Dictionary<string, string>(StringComparer.Ordinal),
                ExternalCapabilityExecutionMode.Interactive,
                "attacker-scope"));
        var port = new WorkflowRunForkSeedQueryPort(currentStateReader, bindingReader, mapper);

        var view = await port.GetForkSeedAsync("attacker-scope", "victim-run", CancellationToken.None);

        view.Should().BeNull();
        currentStateReader.GetKeys.Should().ContainSingle().Which.Should().Be("actor-victim-run");
    }

    private static WorkflowRunState BuildWorkflowRunState(
        string runId,
        string status,
        string finalError) =>
        new()
        {
            RunId = runId,
            Status = status,
            WorkflowName = "demo",
            WorkflowYaml = "name: demo\nsteps: []",
            FinalError = finalError,
            ScopeId = "scope-1",
            WorkflowId = "wf-alpha",
            RevisionId = "rev-alpha",
            InlineWorkflowYamls = { ["child"] = "name: child" },
        };

    private static WorkflowExecutionCurrentStateDocument BuildDocument(
        WorkflowRunState state,
        WorkflowRunForkSeedProjectionSnapshot seedSnapshot) =>
        new()
        {
            Id = $"actor-{state.RunId}",
            RootActorId = $"actor-{state.RunId}",
            RunId = state.RunId,
            WorkflowName = state.WorkflowName,
            Status = state.Status,
            ScopeId = seedSnapshot.ScopeId,
            WorkflowId = state.WorkflowId,
            RevisionId = state.RevisionId,
            FinalError = state.FinalError,
            WorkflowYaml = seedSnapshot.WorkflowYaml,
            InlineWorkflowYamls = seedSnapshot.InlineWorkflowYamls.ToDictionary(
                x => x.Key,
                x => x.Value,
                StringComparer.Ordinal),
            ForkSeedVariables = seedSnapshot.Variables.ToDictionary(
                x => x.Key,
                x => x.Value,
                StringComparer.Ordinal),
            ForkSeedCompletedStepIds = seedSnapshot.CompletedStepIds.ToList(),
            ForkSeedLastFailedStepId = seedSnapshot.LastFailedStepId,
            ForkSeedIdempotencies = seedSnapshot.IdempotencyByStepId.ToDictionary(
                x => x.Key,
                x => new WorkflowStepIdempotencyReadModel
                {
                    LogicalRunId = x.Value.LogicalRunId,
                    StepId = x.Value.StepId,
                    LogicalAttempt = x.Value.LogicalAttempt,
                    IdempotencyKey = x.Value.IdempotencyKey,
                },
                StringComparer.Ordinal),
        };

    private sealed class FakeWorkflowRunBindingReader(params WorkflowActorBinding[] bindings)
        : IWorkflowRunBindingReader
    {
        private readonly IReadOnlyList<WorkflowActorBinding> _bindings = bindings;

        public WorkflowRunBindingQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<WorkflowActorBinding>> ListByRunIdAsync(
            string runId,
            int take = 20,
            CancellationToken ct = default)
        {
            throw new InvalidOperationException("Fork seed lookup must not use the global run-id query.");
        }

        public Task<IReadOnlyList<WorkflowActorBinding>> QueryAsync(
            WorkflowRunBindingQuery query,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastQuery = query;
            return Task.FromResult(_bindings);
        }
    }

    private sealed class RecordingDocumentReader<TReadModel> : IProjectionDocumentReader<TReadModel, string>
        where TReadModel : class, IProjectionReadModel
    {
        public TReadModel? Item { get; init; }
        public List<string> GetKeys { get; } = [];

        public Task<TReadModel?> GetAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            GetKeys.Add(key);
            return Task.FromResult(Item);
        }

        public Task<ProjectionDocumentQueryResult<TReadModel>> QueryAsync(
            ProjectionDocumentQuery query,
            CancellationToken ct = default)
        {
            _ = query;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ProjectionDocumentQueryResult<TReadModel>.Empty);
        }
    }
}
