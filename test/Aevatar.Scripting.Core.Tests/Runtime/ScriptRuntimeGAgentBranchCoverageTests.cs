using System.Reflection;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Foundation.Runtime.Persistence;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class ScriptRuntimeGAgentBranchCoverageTests
{
    [Fact]
    public void Ctor_ShouldThrow_WhenDependenciesAreNull()
    {
        Action actWithNullOrchestrator = () => new ScriptRuntimeGAgent(null!, new DirectSnapshotPort());
        Action actWithNullSnapshotPort = () => new ScriptRuntimeGAgent(new NoopOrchestrator(), null!);

        actWithNullOrchestrator.Should().Throw<ArgumentNullException>();
        actWithNullSnapshotPort.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task SnapshotResponse_ShouldIgnore_WhenRequestIdMissing_OrPendingRunEventMissing()
    {
        var agent = CreateAgent();
        var beforeVersion = agent.State.LastAppliedEventVersion;

        await agent.HandleScriptDefinitionSnapshotResponded(new ScriptDefinitionSnapshotRespondedEvent
        {
            RequestId = string.Empty,
            Found = true,
            ScriptId = "script-1",
            Revision = "rev-1",
            SourceText = "class A {}",
        });

        agent.State.LastAppliedEventVersion.Should().Be(beforeVersion);

        agent.State.PendingDefinitionQueries["request-with-null-run-event"] = new PendingScriptDefinitionQueryState
        {
            RunEvent = null,
            QueuedAtUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        await agent.HandleScriptDefinitionSnapshotResponded(new ScriptDefinitionSnapshotRespondedEvent
        {
            RequestId = "request-with-null-run-event",
            Found = true,
            ScriptId = "script-1",
            Revision = "rev-1",
            SourceText = "class A {}",
        });

        agent.State.LastAppliedEventVersion.Should().Be(beforeVersion);
    }

    [Fact]
    public void BuildRunFailureCommittedEvent_ShouldFallbackToStateRevision_AndDefaultReason()
    {
        var agent = CreateAgent();
        agent.State.Revision = "state-rev";
        agent.State.LastAppliedSchemaVersion = "schema-v1";
        agent.State.LastSchemaHash = "schema-hash";
        agent.State.StatePayloads["state"] = Any.Pack(new StringValue { Value = "state-value" });
        agent.State.ReadModelPayloads["view"] = Any.Pack(new StringValue { Value = "view-value" });

        var runEvent = new RunScriptRequestedEvent
        {
            RunId = string.Empty,
            ScriptRevision = string.Empty,
            DefinitionActorId = string.Empty,
        };

        var committed = InvokePrivateInstance<ScriptRunDomainEventCommitted>(
            agent,
            "BuildRunFailureCommittedEvent",
            runEvent,
            string.Empty);

        committed.EventType.Should().Be("script.run.failed");
        committed.RunId.Should().BeEmpty();
        committed.ScriptRevision.Should().Be("state-rev");
        committed.DefinitionActorId.Should().BeEmpty();
        committed.ReadModelSchemaVersion.Should().Be("schema-v1");
        committed.ReadModelSchemaHash.Should().Be("schema-hash");
        committed.Payload.Unpack<StringValue>().Value.Should().Be("Script run failed.");
        committed.StatePayloads.Should().ContainKey("state");
        committed.ReadModelPayloads.Should().ContainKey("view");
    }

    [Fact]
    public void ApplyCommitted_ShouldHandleFailureAndSuccessBranches_WithNullFields()
    {
        var state = new ScriptRuntimeState
        {
            Revision = "rev-old",
            DefinitionActorId = "definition-old",
            LastAppliedEventVersion = 9,
        };
        state.StatePayloads["state"] = Any.Pack(new StringValue { Value = "state-old" });
        state.ReadModelPayloads["view"] = Any.Pack(new StringValue { Value = "view-old" });

        var failedCommitted = new ScriptRunDomainEventCommitted
        {
            EventType = "script.run.failed",
            RunId = string.Empty,
            ScriptRevision = string.Empty,
            DefinitionActorId = string.Empty,
            ReadModelSchemaVersion = string.Empty,
            ReadModelSchemaHash = string.Empty,
        };
        failedCommitted.StatePayloads[""] = Any.Pack(new StringValue { Value = "ignored-empty-key" });
        failedCommitted.StatePayloads["state"] = Any.Pack(new StringValue { Value = "state-failed" });

        var failedNext = InvokePrivateStatic<ScriptRuntimeState>(
            typeof(ScriptRuntimeGAgent),
            "ApplyCommitted",
            state,
            failedCommitted);

        failedNext.DefinitionActorId.Should().Be("definition-old");
        failedNext.Revision.Should().Be("rev-old");
        failedNext.LastRunId.Should().BeEmpty();
        failedNext.LastAppliedSchemaVersion.Should().BeEmpty();
        failedNext.LastSchemaHash.Should().BeEmpty();
        failedNext.LastEventId.Should().BeEmpty();
        failedNext.StatePayloads.Should().ContainKey("state");
        failedNext.StatePayloads.Should().NotContainKey(string.Empty);

        var succeededCommitted = new ScriptRunDomainEventCommitted
        {
            EventType = "script.executed",
            RunId = string.Empty,
            ScriptRevision = string.Empty,
            DefinitionActorId = string.Empty,
            ReadModelSchemaVersion = string.Empty,
            ReadModelSchemaHash = string.Empty,
        };

        var successNext = InvokePrivateStatic<ScriptRuntimeState>(
            typeof(ScriptRuntimeGAgent),
            "ApplyCommitted",
            state,
            succeededCommitted);

        successNext.DefinitionActorId.Should().BeEmpty();
        successNext.Revision.Should().BeEmpty();
    }

    [Fact]
    public void ApplyDefinitionQueryQueued_ShouldCoverInvalidAndValidBranches()
    {
        var state = new ScriptRuntimeState();

        var withNullRequestId = new ScriptDefinitionQueryQueuedEvent
        {
            RequestId = string.Empty,
            RunEvent = new RunScriptRequestedEvent { RunId = "run-1" },
            QueuedAtUnixTimeMs = 1,
        };
        var nextNullRequest = InvokePrivateStatic<ScriptRuntimeState>(
            typeof(ScriptRuntimeGAgent),
            "ApplyDefinitionQueryQueued",
            state,
            withNullRequestId);

        nextNullRequest.PendingDefinitionQueries.Should().BeEmpty();
        nextNullRequest.LastEventId.Should().Be("definition-query::queued");

        var withMissingRunEvent = new ScriptDefinitionQueryQueuedEvent
        {
            RequestId = "request-missing-run-event",
            RunEvent = null,
            QueuedAtUnixTimeMs = 2,
        };
        var nextMissingRun = InvokePrivateStatic<ScriptRuntimeState>(
            typeof(ScriptRuntimeGAgent),
            "ApplyDefinitionQueryQueued",
            state,
            withMissingRunEvent);

        nextMissingRun.PendingDefinitionQueries.Should().NotContainKey("request-missing-run-event");

        var valid = new ScriptDefinitionQueryQueuedEvent
        {
            RequestId = "request-valid",
            RunEvent = new RunScriptRequestedEvent
            {
                RunId = "run-valid",
                ScriptRevision = "rev-valid",
                DefinitionActorId = "definition-valid",
            },
            QueuedAtUnixTimeMs = 3,
        };
        var nextValid = InvokePrivateStatic<ScriptRuntimeState>(
            typeof(ScriptRuntimeGAgent),
            "ApplyDefinitionQueryQueued",
            state,
            valid);

        nextValid.PendingDefinitionQueries.Should().ContainKey("request-valid");
    }

    [Fact]
    public void ApplyDefinitionQueryCleared_ShouldHandleNullAndValidRequestId()
    {
        var state = new ScriptRuntimeState();
        state.PendingDefinitionQueries["request-1"] = new PendingScriptDefinitionQueryState
        {
            RunEvent = new RunScriptRequestedEvent { RunId = "run-1" },
            QueuedAtUnixTimeMs = 1,
        };

        var withNullRequestId = new ScriptDefinitionQueryClearedEvent
        {
            RequestId = string.Empty,
        };
        var nextWithNull = InvokePrivateStatic<ScriptRuntimeState>(
            typeof(ScriptRuntimeGAgent),
            "ApplyDefinitionQueryCleared",
            state,
            withNullRequestId);

        nextWithNull.PendingDefinitionQueries.Should().ContainKey("request-1");
        nextWithNull.LastEventId.Should().Be("definition-query::cleared");

        var withValidRequestId = new ScriptDefinitionQueryClearedEvent
        {
            RequestId = "request-1",
        };
        var nextWithValid = InvokePrivateStatic<ScriptRuntimeState>(
            typeof(ScriptRuntimeGAgent),
            "ApplyDefinitionQueryCleared",
            nextWithNull,
            withValidRequestId);

        nextWithValid.PendingDefinitionQueries.Should().NotContainKey("request-1");
    }

    [Fact]
    public void CloneAndCopyPayloads_ShouldSkipEmptyKeys()
    {
        var source = new MapField<string, Any>
        {
            [""] = Any.Pack(new StringValue { Value = "ignored" }),
            ["state"] = Any.Pack(new StringValue { Value = "state-v1" }),
        };

        var cloned = InvokePrivateStatic<IReadOnlyDictionary<string, Any>>(
            typeof(ScriptRuntimeGAgent),
            "ClonePayloads",
            source);

        cloned.Should().ContainKey("state");
        cloned.Should().NotContainKey(string.Empty);

        var target = new MapField<string, Any>
        {
            ["legacy"] = Any.Pack(new StringValue { Value = "legacy-v1" }),
        };

        InvokePrivateStatic<object?>(
            typeof(ScriptRuntimeGAgent),
            "CopyPayloads",
            source,
            target);

        target.Should().ContainKey("state");
        target.Should().NotContainKey("legacy");
        target.Should().NotContainKey(string.Empty);
    }

    private static ScriptRuntimeGAgent CreateAgent()
    {
        return new ScriptRuntimeGAgent(new NoopOrchestrator(), new DirectSnapshotPort())
        {
            EventSourcingBehaviorFactory = new DefaultEventSourcingBehaviorFactory<ScriptRuntimeState>(
                new InMemoryEventStore()),
        };
    }

    private static TResult InvokePrivateStatic<TResult>(System.Type type, string methodName, params object?[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull($"private static method `{methodName}` must exist for branch tests");
        var result = method!.Invoke(null, args);
        return (TResult)result!;
    }

    private static TResult InvokePrivateInstance<TResult>(object instance, string methodName, params object?[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull($"private instance method `{methodName}` must exist for branch tests");
        var result = method!.Invoke(instance, args);
        return (TResult)result!;
    }

    private sealed class NoopOrchestrator : IScriptRuntimeExecutionOrchestrator
    {
        public Task<IReadOnlyList<IMessage>> ExecuteRunAsync(
            ScriptRuntimeExecutionRequest request,
            CancellationToken ct)
        {
            _ = request;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<IMessage>>([]);
        }
    }

    private sealed class DirectSnapshotPort : IScriptDefinitionSnapshotPort
    {
        public bool UseEventDrivenDefinitionQuery => false;

        public Task<ScriptDefinitionSnapshot> GetRequiredAsync(
            string definitionActorId,
            string requestedRevision,
            CancellationToken ct)
        {
            _ = definitionActorId;
            _ = requestedRevision;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ScriptDefinitionSnapshot(
                ScriptId: "script-1",
                Revision: "rev-1",
                SourceText: "class Runtime {}",
                ReadModelSchemaVersion: "v1",
                ReadModelSchemaHash: "hash-v1"));
        }
    }
}
