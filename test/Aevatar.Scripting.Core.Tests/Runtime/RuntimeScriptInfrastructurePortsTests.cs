using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Compilation;
using Aevatar.Scripting.Infrastructure.Ports;
using Aevatar.Scripting.Projection.ReadPorts;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public class RuntimeScriptInfrastructurePortsTests
{
    [Fact]
    public async Task SpawnRuntimeAsync_ShouldCreateActorWithLatestRevision_WhenRevisionIsEmpty()
    {
        var runtime = new TestActorRuntime();
        var service = CreateRuntimeProvisioningService(runtime);

        var actorId = await service.EnsureRuntimeAsync(
            definitionActorId: "definition-1",
            scriptRevision: string.Empty,
            runtimeActorId: null,
            ct: CancellationToken.None);

        actorId.Should().StartWith("script-runtime:definition-1:latest:");
        runtime.CreateRequests.Should().ContainSingle(x => x == actorId);
    }

    [Fact]
    public async Task SpawnRuntimeAsync_ShouldReturnExistingActorId_WhenRuntimeAlreadyExists()
    {
        var runtime = new TestActorRuntime();
        var service = CreateRuntimeProvisioningService(runtime, "runtime-existing");

        var actorId = await service.EnsureRuntimeAsync(
            definitionActorId: "definition-1",
            scriptRevision: "rev-1",
            runtimeActorId: "runtime-existing",
            ct: CancellationToken.None);

        actorId.Should().Be("runtime-existing");
        runtime.CreateRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task RunRuntimeAsync_ShouldThrow_WhenRuntimeActorIdMissing()
    {
        var runtime = new TestActorRuntime();
        var service = CreateRuntimeCommandService(runtime);

        var act = () => service.RunRuntimeAsync(
            runtimeActorId: string.Empty,
            runId: "run-1",
            inputPayload: Any.Pack(new SimpleTextCommand
            {
                CommandId = "command-1",
                Value = "input",
            }),
            scriptRevision: "rev-1",
            definitionActorId: "definition-1",
            requestedEventType: "chat.requested",
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RunRuntimeAsync_ShouldThrow_WhenRunIdMissing()
    {
        var runtime = new TestActorRuntime();
        var service = CreateRuntimeCommandService(runtime);

        var act = () => service.RunRuntimeAsync(
            runtimeActorId: "runtime-1",
            runId: string.Empty,
            inputPayload: Any.Pack(new SimpleTextCommand
            {
                CommandId = "command-1",
                Value = "input",
            }),
            scriptRevision: "rev-1",
            definitionActorId: "definition-1",
            requestedEventType: "chat.requested",
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RunRuntimeAsync_ShouldThrow_WhenRuntimeActorNotFound()
    {
        var runtime = new TestActorRuntime();
        var service = CreateRuntimeCommandService(runtime);

        var act = () => service.RunRuntimeAsync(
            runtimeActorId: "runtime-missing",
            runId: "run-1",
            inputPayload: Any.Pack(new SimpleTextCommand
            {
                CommandId = "command-1",
                Value = "input",
            }),
            scriptRevision: "rev-1",
            definitionActorId: "definition-1",
            requestedEventType: "chat.requested",
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*runtime actor not found*runtime-missing*");
    }

    [Fact]
    public async Task RunRuntimeAsync_ShouldDispatchRunScriptRequestedEnvelope_WhenRuntimeActorExists()
    {
        RunScriptRequestedEvent? captured = null;
        var runtime = new TestActorRuntime();
        runtime.RegisterActor(new TestActor("runtime-1", (envelope, ct) =>
        {
            captured = envelope.Payload.Unpack<RunScriptRequestedEvent>();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }));
        var service = CreateRuntimeCommandService(runtime);

        await service.RunRuntimeAsync(
            runtimeActorId: "runtime-1",
            runId: "run-1",
            inputPayload: Any.Pack(new SimpleTextCommand
            {
                CommandId = "command-1",
                Value = "input",
            }),
            scriptRevision: "rev-1",
            definitionActorId: "definition-1",
            requestedEventType: "chat.requested",
            ct: CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.RunId.Should().Be("run-1");
        captured.DefinitionActorId.Should().Be("definition-1");
        captured.ScriptRevision.Should().Be("rev-1");
        captured.RequestedEventType.Should().Be("chat.requested");
        captured.InputPayload.Should().NotBeNull();
    }

    [Fact]
    public async Task DefinitionSnapshotPort_ShouldThrow_WhenDefinitionActorMissing()
    {
        var eventStore = new TestEventStore();
        var port = CreateDefinitionSnapshotPort(eventStore);

        var act = () => port.GetRequiredAsync("definition-missing", "rev-1", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*snapshot not found*definition-missing*");
    }

    [Fact]
    public async Task DefinitionSnapshotPort_ShouldThrow_WhenCommittedDefinitionIsMissing()
    {
        var eventStore = new TestEventStore();
        eventStore.Seed(
            "definition-1",
            new ScriptReadModelSchemaDeclaredEvent
            {
                ScriptId = "script-1",
                ScriptRevision = "rev-1",
                ReadModelSchemaVersion = "v1",
            });
        var port = CreateDefinitionSnapshotPort(eventStore);

        var act = () => port.GetRequiredAsync("definition-1", "rev-1", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*script_package is empty*definition-1*");
    }

    [Fact]
    public async Task DefinitionSnapshotPort_ShouldThrow_WhenScriptPackageIsEmpty()
    {
        var port = new ProjectionScriptDefinitionSnapshotPort((definitionActorId, requestedRevision, ct) =>
        {
            _ = definitionActorId;
            _ = requestedRevision;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<ScriptDefinitionSnapshot?>(new ScriptDefinitionSnapshot(
                "script-1",
                "rev-1",
                string.Empty,
                string.Empty,
                new ScriptPackageSpec(),
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                ByteString.Empty,
                string.Empty,
                string.Empty,
                new ScriptRuntimeSemanticsSpec()));
        });

        var act = () => port.GetRequiredAsync("definition-1", "rev-1", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*script_package is empty*definition-1*");
    }

    [Fact]
    public async Task DefinitionSnapshotPort_ShouldThrow_WhenRequestedRevisionDoesNotMatchSnapshot()
    {
        var eventStore = new TestEventStore();
        eventStore.Seed(
            "definition-1",
            new ScriptDefinitionUpsertedEvent
            {
                ScriptId = "script-1",
                ScriptRevision = "rev-actual",
                SourceText = "public sealed class RuntimeScript {}",
            });
        var port = CreateDefinitionSnapshotPort(eventStore);

        var act = () => port.GetRequiredAsync("definition-1", "rev-requested", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*snapshot not found*definition-1*rev-requested*");
    }

    [Fact]
    public async Task DefinitionSnapshotPort_ShouldReturnSnapshot_WhenResponseIsValid()
    {
        var eventStore = new TestEventStore();
        eventStore.Seed(
            "definition-1",
            new ScriptDefinitionUpsertedEvent
            {
                ScriptId = "script-1",
                ScriptRevision = "rev-1",
                SourceText = "public sealed class RuntimeScript {}",
                SourceHash = "hash-1",
                ReadModelSchemaVersion = "v1",
                ReadModelSchemaHash = "hash-v1",
                ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource("public sealed class RuntimeScript {}"),
            });
        var port = CreateDefinitionSnapshotPort(eventStore);

        var snapshot = await port.GetRequiredAsync("definition-1", string.Empty, CancellationToken.None);

        snapshot.ScriptId.Should().Be("script-1");
        snapshot.Revision.Should().Be("rev-1");
        snapshot.SourceText.Should().Contain("RuntimeScript");
        snapshot.ReadModelSchemaVersion.Should().Be("v1");
        snapshot.ReadModelSchemaHash.Should().Be("hash-v1");
    }

    [Fact]
    public async Task DefinitionSnapshotPort_ShouldUseLatestCommittedDefinitionSnapshot()
    {
        var eventStore = new TestEventStore();
        eventStore.Seed(
            "definition-1",
            new ScriptDefinitionUpsertedEvent
            {
                ScriptId = "script-1",
                ScriptRevision = "rev-1",
                SourceText = "old-source",
                SourceHash = "hash-old",
                ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource("old-source"),
            },
            new ScriptDefinitionUpsertedEvent
            {
                ScriptId = "script-1",
                ScriptRevision = "rev-2",
                SourceText = "new-source",
                SourceHash = "hash-new",
                ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource("new-source"),
            });
        var port = CreateDefinitionSnapshotPort(eventStore);

        var snapshot = await port.GetRequiredAsync("definition-1", "rev-2", CancellationToken.None);

        snapshot.ScriptId.Should().Be("script-1");
        snapshot.Revision.Should().Be("rev-2");
        snapshot.SourceHash.Should().Be("hash-new");
    }

    [Fact]
    public async Task CatalogCommandService_ShouldDispatchPromoteRequest_WithResolvedCatalogActorId()
    {
        PromoteScriptRevisionRequestedEvent? captured = null;
        var runtime = new TestActorRuntime
        {
            CreateActor = actorId => new TestActor(actorId, (envelope, ct) =>
            {
                captured = envelope.Payload.Unpack<PromoteScriptRevisionRequestedEvent>();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }),
        };
        var service = CreateCatalogCommandService(runtime);

        await service.PromoteCatalogRevisionAsync(
            catalogActorId: null,
            scriptId: "script-1",
            expectedBaseRevision: "rev-1",
            revision: "rev-2",
            definitionActorId: "definition-2",
            sourceHash: "hash-2",
            proposalId: "proposal-1",
            ct: CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.ScriptId.Should().Be("script-1");
        captured.ExpectedBaseRevision.Should().Be("rev-1");
        captured.Revision.Should().Be("rev-2");
    }

    [Fact]
    public async Task CatalogCommandService_ShouldDispatchRollbackRequest_WithProvidedCatalogActorId()
    {
        RollbackScriptRevisionRequestedEvent? captured = null;
        var runtime = new TestActorRuntime
        {
            CreateActor = actorId => new TestActor(actorId, (envelope, ct) =>
            {
                captured = envelope.Payload.Unpack<RollbackScriptRevisionRequestedEvent>();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }),
        };
        var service = CreateCatalogCommandService(runtime);

        await service.RollbackCatalogRevisionAsync(
            catalogActorId: "catalog-custom",
            scriptId: "script-1",
            targetRevision: "rev-1",
            reason: "rollback",
            proposalId: "proposal-rollback",
            expectedCurrentRevision: "rev-2",
            ct: CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.ScriptId.Should().Be("script-1");
        captured.TargetRevision.Should().Be("rev-1");
        captured.ExpectedCurrentRevision.Should().Be("rev-2");
    }

    [Fact]
    public async Task CatalogQueryService_GetCatalogEntryAsync_ShouldReturnNull_WhenScriptIdMissing()
    {
        var service = CreateCatalogQueryService(new TestEventStore());

        var entry = await service.GetCatalogEntryAsync(null, string.Empty, CancellationToken.None);

        entry.Should().BeNull();
    }

    [Fact]
    public async Task CatalogQueryService_GetCatalogEntryAsync_ShouldReturnNull_WhenCatalogActorMissing()
    {
        var service = CreateCatalogQueryService(new TestEventStore());

        var entry = await service.GetCatalogEntryAsync(null, "script-1", CancellationToken.None);

        entry.Should().BeNull();
    }

    [Fact]
    public async Task CatalogQueryService_GetCatalogEntryAsync_ShouldReturnNull_WhenCatalogFactsDoNotContainScript()
    {
        var eventStore = new TestEventStore();
        eventStore.Seed(
            "catalog-1",
            new ScriptCatalogRevisionPromotedEvent
            {
                ScriptId = "script-other",
                Revision = "rev-1",
            });
        var service = CreateCatalogQueryService(eventStore);

        var entry = await service.GetCatalogEntryAsync("catalog-1", "script-1", CancellationToken.None);

        entry.Should().BeNull();
    }

    [Fact]
    public async Task CatalogQueryService_GetCatalogEntryAsync_ShouldMapSnapshot_WhenFound()
    {
        var eventStore = new TestEventStore();
        eventStore.Seed(
            "catalog-1",
            new ScriptCatalogRevisionPromotedEvent
            {
                ScriptId = "script-1",
                Revision = "rev-1",
                DefinitionActorId = "definition-1",
                SourceHash = "hash-1",
                ProposalId = "proposal-1",
            },
            new ScriptCatalogRevisionPromotedEvent
            {
                ScriptId = "script-1",
                Revision = "rev-2",
                DefinitionActorId = "definition-2",
                SourceHash = "hash-2",
                ProposalId = "proposal-2",
            });
        var service = CreateCatalogQueryService(eventStore);

        var entry = await service.GetCatalogEntryAsync("catalog-1", "script-1", CancellationToken.None);

        entry.Should().NotBeNull();
        entry!.ScriptId.Should().Be("script-1");
        entry.ActiveRevision.Should().Be("rev-2");
        entry.RevisionHistory.Should().Contain("rev-1");
        entry.RevisionHistory.Should().Contain("rev-2");
    }

    [Fact]
    public async Task CatalogQueryService_GetCatalogEntryAsync_ShouldReplayRollbackFacts()
    {
        var eventStore = new TestEventStore();
        eventStore.Seed(
            "catalog-1",
            new ScriptCatalogRevisionPromotedEvent
            {
                ScriptId = "script-1",
                Revision = "rev-1",
                DefinitionActorId = "definition-1",
                SourceHash = "hash-1",
                ProposalId = "proposal-1",
            },
            new ScriptCatalogRevisionPromotedEvent
            {
                ScriptId = "script-1",
                Revision = "rev-2",
                DefinitionActorId = "definition-2",
                SourceHash = "hash-2",
                ProposalId = "proposal-2",
            },
            new ScriptCatalogRolledBackEvent
            {
                ScriptId = "script-1",
                TargetRevision = "rev-1",
                PreviousRevision = "rev-2",
                ProposalId = "proposal-rollback",
            });
        var service = CreateCatalogQueryService(eventStore);

        var entry = await service.GetCatalogEntryAsync("catalog-1", "script-1", CancellationToken.None);

        entry.Should().NotBeNull();
        entry!.ActiveRevision.Should().Be("rev-1");
        entry.PreviousRevision.Should().Be("rev-2");
        entry.ActiveDefinitionActorId.Should().BeEmpty();
        entry.ActiveSourceHash.Should().BeEmpty();
        entry.LastProposalId.Should().Be("proposal-rollback");
        entry.RevisionHistory.Should().Equal("rev-1", "rev-2");
    }

    [Fact]
    public async Task EvolutionInteractionService_ShouldReturnDecision_WhenSessionCompletes()
    {
        var projectionPort = new TestProjectionPort();
        var decisionReadPort = new TestDecisionReadPort();
        var runtime = CreateEvolutionRuntime(projectionPort, (_, start) =>
        {
            projectionPort.Publish("script-evolution-session:proposal-1", new ScriptEvolutionSessionCompletedEvent
            {
                ProposalId = "another-proposal",
                Accepted = false,
                Status = "ignored",
            });
            projectionPort.Publish("script-evolution-session:proposal-1", new ScriptEvolutionSessionCompletedEvent
            {
                ProposalId = start.ProposalId,
                Accepted = true,
                Status = "promoted",
                DefinitionActorId = "definition-1",
                CatalogActorId = "catalog-1",
                Diagnostics = { "compile-ok" },
            });
        });
        var service = CreateEvolutionInteractionService(runtime, projectionPort, decisionReadPort);

        var decision = await service.ProposeAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-1",
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "source-rev-2",
                CandidateSourceHash: "hash-rev-2",
                Reason: "rollout"),
            CancellationToken.None);

        decision.Accepted.Should().BeTrue();
        decision.Status.Should().Be("promoted");
        decision.ValidationReport.Diagnostics.Should().ContainSingle(x => x == "compile-ok");
        projectionPort.DetachCount.Should().Be(1);
        projectionPort.ReleaseCount.Should().Be(1);
        decisionReadPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task EvolutionInteractionService_ShouldGenerateProposalId_WhenRequestProposalIdMissing()
    {
        var projectionPort = new TestProjectionPort();
        var decisionReadPort = new TestDecisionReadPort();
        StartScriptEvolutionSessionRequestedEvent? capturedStart = null;
        var runtime = CreateEvolutionRuntime(projectionPort, (_, start) =>
        {
            capturedStart = start;
            projectionPort.Publish("script-evolution-session:" + start.ProposalId, new ScriptEvolutionSessionCompletedEvent
            {
                ProposalId = start.ProposalId,
                Accepted = true,
                Status = "promoted",
            });
        });
        var service = CreateEvolutionInteractionService(runtime, projectionPort, decisionReadPort);

        var decision = await service.ProposeAsync(
            new ScriptEvolutionProposal(
                ProposalId: string.Empty,
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "source-rev-2",
                CandidateSourceHash: "hash-rev-2",
                Reason: "rollout"),
            CancellationToken.None);

        capturedStart.Should().NotBeNull();
        capturedStart!.ProposalId.Should().NotBeNullOrWhiteSpace();
        decision.ProposalId.Should().Be(capturedStart.ProposalId);
    }

    [Fact]
    public async Task EvolutionInteractionService_ShouldUseFallbackDecision_WhenSessionTimesOut()
    {
        var projectionPort = new TestProjectionPort();
        var decisionReadPort = new TestDecisionReadPort
        {
            NextResult = new ScriptPromotionDecision(
                Accepted: false,
                ProposalId: "proposal-timeout",
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                Status: "rejected",
                FailureReason: "fallback-decision",
                DefinitionActorId: "definition-fallback",
                CatalogActorId: "catalog-fallback",
                ValidationReport: ScriptEvolutionValidationReport.Empty),
        };
        var runtime = CreateEvolutionRuntime(projectionPort, (_, _) => { });
        var service = CreateEvolutionInteractionService(
            runtime,
            projectionPort,
            decisionReadPort,
            new ScriptingInteractionTimeoutOptions { EvolutionCompletionTimeout = TimeSpan.FromMilliseconds(50) });

        var decision = await service.ProposeAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-timeout",
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "source-rev-2",
                CandidateSourceHash: "hash-rev-2",
                Reason: "rollout"),
            CancellationToken.None);

        decision.FailureReason.Should().Be("fallback-decision");
        decisionReadPort.Calls.Should().ContainSingle();
        projectionPort.DetachCount.Should().Be(1);
        projectionPort.ReleaseCount.Should().Be(1);
    }

    [Fact]
    public async Task EvolutionInteractionService_ShouldThrowTimeout_WhenSessionTimesOutWithoutFallback()
    {
        var projectionPort = new TestProjectionPort();
        var decisionReadPort = new TestDecisionReadPort { NextResult = null };
        var runtime = CreateEvolutionRuntime(projectionPort, (_, _) => { });
        var service = CreateEvolutionInteractionService(
            runtime,
            projectionPort,
            decisionReadPort,
            new ScriptingInteractionTimeoutOptions { EvolutionCompletionTimeout = TimeSpan.FromMilliseconds(50) });

        var act = () => service.ProposeAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-timeout-no-fallback",
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "source-rev-2",
                CandidateSourceHash: "hash-rev-2",
                Reason: "rollout"),
            CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*proposal-timeout-no-fallback*");
        decisionReadPort.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task EvolutionInteractionService_ShouldThrow_WhenProjectionLeaseIsUnavailable()
    {
        var projectionPort = new TestProjectionPort { ReturnNullLease = true };
        var decisionReadPort = new TestDecisionReadPort();
        var runtime = CreateEvolutionRuntime(projectionPort, (_, _) => { });
        var service = CreateEvolutionInteractionService(runtime, projectionPort, decisionReadPort);

        var act = () => service.ProposeAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-no-projection",
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "source-rev-2",
                CandidateSourceHash: "hash-rev-2",
                Reason: "rollout"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*projection is disabled*");
        projectionPort.DetachCount.Should().Be(0);
        projectionPort.ReleaseCount.Should().Be(0);
    }

    private static ProjectionScriptDefinitionSnapshotPort CreateDefinitionSnapshotPort(
        TestEventStore eventStore)
    {
        return new ProjectionScriptDefinitionSnapshotPort((definitionActorId, requestedRevision, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                eventStore.BuildDefinitionSnapshotResponse(definitionActorId, requestedRevision));
        });
    }

    private static ProjectionScriptCatalogQueryPort CreateCatalogQueryService(
        TestEventStore eventStore)
    {
        return new ProjectionScriptCatalogQueryPort((catalogActorId, scriptId, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                eventStore.BuildCatalogEntryResponse(catalogActorId, scriptId));
        });
    }

    private static TestActorRuntime CreateEvolutionRuntime(
        TestProjectionPort projectionPort,
        Action<string, StartScriptEvolutionSessionRequestedEvent> onStartSession)
    {
        var runtime = new TestActorRuntime();
        runtime.CreateActor = actorId =>
        {
            if (string.Equals(actorId, "script-evolution-manager", StringComparison.Ordinal))
                return new TestActor(actorId);

            if (actorId.StartsWith("script-evolution-session:", StringComparison.Ordinal))
            {
                return new TestActor(actorId, (envelope, ct) =>
                {
                    var start = envelope.Payload.Unpack<StartScriptEvolutionSessionRequestedEvent>();
                    onStartSession(actorId, start);
                    ct.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                });
            }

            return new TestActor(actorId);
        };
        return runtime;
    }

    private static RuntimeScriptEvolutionInteractionService CreateEvolutionInteractionService(
        TestActorRuntime runtime,
        TestProjectionPort projectionPort,
        TestDecisionReadPort decisionReadPort,
        ScriptingInteractionTimeoutOptions? interactionTimeoutOptions = null)
    {
        var resolvedInteractionTimeoutOptions = interactionTimeoutOptions
            ?? new ScriptingInteractionTimeoutOptions { EvolutionCompletionTimeout = TimeSpan.FromMilliseconds(200) };
        var actorAccessor = new RuntimeScriptActorAccessor(runtime);
        var addressResolver = new StaticAddressResolver();
        var targetResolver = new ScriptEvolutionCommandTargetResolver(
            actorAccessor,
            addressResolver,
            projectionPort);
        var dispatchPipeline = new DefaultCommandDispatchPipeline<ScriptEvolutionProposal, ScriptEvolutionCommandTarget, ScriptEvolutionAcceptedReceipt, ScriptEvolutionStartError>(
            targetResolver,
            new DefaultCommandContextPolicy(),
            new ScriptEvolutionCommandTargetBinder(projectionPort),
            new ScriptEvolutionEnvelopeFactory(),
            new ActorCommandTargetDispatcher<ScriptEvolutionCommandTarget>(runtime),
            new ScriptEvolutionAcceptedReceiptFactory());
        var interactionService = new DefaultCommandInteractionService<ScriptEvolutionProposal, ScriptEvolutionCommandTarget, ScriptEvolutionAcceptedReceipt, ScriptEvolutionStartError, ScriptEvolutionSessionCompletedEvent, ScriptEvolutionSessionCompletedEvent, ScriptEvolutionInteractionCompletion>(
            dispatchPipeline,
            new ScriptEvolutionTimedEventOutputStream(resolvedInteractionTimeoutOptions),
            new ScriptEvolutionCompletionPolicy(),
            new NoOpCommandFinalizeEmitter<ScriptEvolutionAcceptedReceipt, ScriptEvolutionInteractionCompletion, ScriptEvolutionSessionCompletedEvent>(),
            new ScriptEvolutionDurableCompletionResolver(decisionReadPort));

        return new RuntimeScriptEvolutionInteractionService(interactionService);
    }

    private static RuntimeScriptProvisioningService CreateRuntimeProvisioningService(
        TestActorRuntime runtime,
        params string[] existingActorIds)
    {
        TestActor CreateBindingAwareActor(string actorId) => new(actorId, async (envelope, ct) =>
        {
            _ = envelope;
            ct.ThrowIfCancellationRequested();
        });

        foreach (var existingActorId in existingActorIds.Where(static x => !string.IsNullOrWhiteSpace(x)))
            runtime.RegisterActor(CreateBindingAwareActor(existingActorId));

        runtime.CreateActor = CreateBindingAwareActor;

        return new RuntimeScriptProvisioningService(
            CreateDispatchService(
                runtime,
                new ProvisionScriptRuntimeCommandTargetResolver(new RuntimeScriptActorAccessor(runtime)),
                new ProvisionScriptRuntimeCommandEnvelopeFactory()),
            new ProjectionScriptDefinitionSnapshotPort((definitionActorId, requestedRevision, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<ScriptDefinitionSnapshot?>(new ScriptDefinitionSnapshot(
                    "script-" + definitionActorId,
                    string.IsNullOrWhiteSpace(requestedRevision) ? "latest" : requestedRevision,
                    ScriptSources.UppercaseBehavior,
                    ScriptSources.UppercaseBehaviorHash,
                    ScriptSources.UppercaseStateTypeUrl,
                    ScriptSources.UppercaseReadModelTypeUrl,
                    "1",
                    "schema-hash"));
            }));
    }

    private static RuntimeScriptCommandService CreateRuntimeCommandService(TestActorRuntime runtime)
    {
        return new RuntimeScriptCommandService(
            CreateDispatchService(
                runtime,
                new RunScriptRuntimeCommandTargetResolver(new RuntimeScriptActorAccessor(runtime)),
                new RunScriptRuntimeCommandEnvelopeFactory()));
    }

    private static RuntimeScriptCatalogCommandService CreateCatalogCommandService(TestActorRuntime runtime)
    {
        return new RuntimeScriptCatalogCommandService(
            CreateDispatchService(
                runtime,
                new PromoteScriptCatalogRevisionCommandTargetResolver(
                    new RuntimeScriptActorAccessor(runtime),
                    new StaticAddressResolver()),
                new PromoteScriptCatalogRevisionCommandEnvelopeFactory()),
            CreateDispatchService(
                runtime,
                new RollbackScriptCatalogRevisionCommandTargetResolver(
                    new RuntimeScriptActorAccessor(runtime),
                    new StaticAddressResolver()),
                new RollbackScriptCatalogRevisionCommandEnvelopeFactory()),
            new StaticAddressResolver(),
            new RuntimeScriptActorAccessor(runtime),
            new NoOpAuthorityProjectionPrimingPort());
    }

    private static ICommandDispatchService<TCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> CreateDispatchService<TCommand>(
        TestActorRuntime runtime,
        ICommandTargetResolver<TCommand, ScriptingActorCommandTarget, ScriptingCommandStartError> resolver,
        ICommandEnvelopeFactory<TCommand> envelopeFactory)
        where TCommand : class
    {
        return new DefaultCommandDispatchService<TCommand, ScriptingActorCommandTarget, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>(
            new DefaultCommandDispatchPipeline<TCommand, ScriptingActorCommandTarget, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>(
                resolver,
                new DefaultCommandContextPolicy(),
                new NoOpCommandTargetBinder<TCommand, ScriptingActorCommandTarget, ScriptingCommandStartError>(),
                envelopeFactory,
                new ActorCommandTargetDispatcher<ScriptingActorCommandTarget>(runtime),
                new ScriptingCommandAcceptedReceiptFactory()));
    }

    private sealed class TestActorRuntime : IActorRuntime, IActorDispatchPort
    {
        private readonly Dictionary<string, IActor> _actors = new(StringComparer.Ordinal);

        public List<string> CreateRequests { get; } = [];
        public List<string> DispatchRequests { get; } = [];

        public Func<string, IActor>? CreateActor { get; set; }
        public Func<string, EventEnvelope, CancellationToken, Task>? DispatchOverride { get; set; }

        public void RegisterActor(IActor actor)
        {
            _actors[actor.Id] = actor;
        }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent
        {
            ct.ThrowIfCancellationRequested();
            var resolvedId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
            CreateRequests.Add(resolvedId);
            if (!_actors.TryGetValue(resolvedId, out var actor))
            {
                actor = CreateActor?.Invoke(resolvedId) ?? new TestActor(resolvedId);
                _actors[resolvedId] = actor;
            }

            return Task.FromResult(actor);
        }

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            _ = agentType;
            return CreateAsync<NoopAgent>(id, ct);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _actors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id)
        {
            _actors.TryGetValue(id, out var actor);
            return Task.FromResult(actor);
        }

        public async Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DispatchRequests.Add(actorId);
            if (DispatchOverride != null)
            {
                await DispatchOverride(actorId, envelope, ct);
                return;
            }

            var actor = await GetAsync(actorId) ?? throw new InvalidOperationException($"Actor {actorId} not found.");
            await actor.HandleEventAsync(envelope, ct);
        }

        public Task<bool> ExistsAsync(string id) => Task.FromResult(_actors.ContainsKey(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
        {
            _ = parentId;
            _ = childId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task UnlinkAsync(string childId, CancellationToken ct = default)
        {
            _ = childId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task RestoreAllAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpAuthorityProjectionPrimingPort : IScriptAuthorityProjectionPrimingPort
    {
        public Task PrimeAsync(string actorId, CancellationToken ct)
        {
            _ = actorId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class TestEventStore
    {
        private readonly Dictionary<string, List<IMessage>> _streams = new(StringComparer.Ordinal);

        public void Seed(string agentId, params IMessage[] events)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

            if (!_streams.TryGetValue(agentId, out var stream))
            {
                stream = [];
                _streams[agentId] = stream;
            }

            stream.AddRange(events);
        }

        public ScriptDefinitionSnapshot? BuildDefinitionSnapshotResponse(
            string definitionActorId,
            string requestedRevision)
        {
            if (!_streams.TryGetValue(definitionActorId, out var stream))
                return null;

            var state = new DefinitionSnapshotState();
            foreach (var evt in stream)
            {
                switch (evt)
                {
                    case ScriptDefinitionUpsertedEvent upserted:
                        state.ScriptId = upserted.ScriptId ?? string.Empty;
                        state.Revision = upserted.ScriptRevision ?? string.Empty;
                        state.SourceText = upserted.SourceText ?? string.Empty;
                        state.SourceHash = upserted.SourceHash ?? string.Empty;
                        state.ReadModelSchemaVersion = upserted.ReadModelSchemaVersion ?? string.Empty;
                        state.ReadModelSchemaHash = upserted.ReadModelSchemaHash ?? string.Empty;
                        state.StateTypeUrl = upserted.StateTypeUrl ?? string.Empty;
                        state.ReadModelTypeUrl = upserted.ReadModelTypeUrl ?? string.Empty;
                        state.ScriptPackage = upserted.ScriptPackage?.Clone() ?? new ScriptPackageSpec();
                        state.ProtocolDescriptorSet = upserted.ProtocolDescriptorSet;
                        state.StateDescriptorFullName = upserted.StateDescriptorFullName ?? string.Empty;
                        state.ReadModelDescriptorFullName = upserted.ReadModelDescriptorFullName ?? string.Empty;
                        state.RuntimeSemantics = upserted.RuntimeSemantics?.Clone() ?? new ScriptRuntimeSemanticsSpec();
                        break;
                    case ScriptReadModelSchemaDeclaredEvent schemaDeclared:
                        state.ScriptId = schemaDeclared.ScriptId ?? state.ScriptId;
                        state.Revision = schemaDeclared.ScriptRevision ?? state.Revision;
                        state.ReadModelSchemaVersion = schemaDeclared.ReadModelSchemaVersion ?? string.Empty;
                        state.ReadModelSchemaHash = schemaDeclared.ReadModelSchemaHash ?? string.Empty;
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(state.Revision))
                return null;

            if (!string.IsNullOrWhiteSpace(requestedRevision) &&
                !string.Equals(requestedRevision, state.Revision, StringComparison.Ordinal))
                return null;

            return new ScriptDefinitionSnapshot(
                state.ScriptId,
                state.Revision,
                state.SourceText,
                state.SourceHash,
                state.ScriptPackage.Clone(),
                state.StateTypeUrl,
                state.ReadModelTypeUrl,
                state.ReadModelSchemaVersion,
                state.ReadModelSchemaHash,
                state.ProtocolDescriptorSet,
                state.StateDescriptorFullName,
                state.ReadModelDescriptorFullName,
                state.RuntimeSemantics.Clone());
        }

        public ScriptCatalogEntrySnapshot? BuildCatalogEntryResponse(
            string? catalogActorId,
            string scriptId)
        {
            var resolvedCatalogActorId = string.IsNullOrWhiteSpace(catalogActorId)
                ? "script-catalog"
                : catalogActorId;
            if (string.IsNullOrWhiteSpace(scriptId))
                return null;

            if (!_streams.TryGetValue(resolvedCatalogActorId, out var stream))
                return null;

            var entries = new Dictionary<string, CatalogEntryState>(StringComparer.Ordinal);
            foreach (var evt in stream)
            {
                switch (evt)
                {
                    case ScriptCatalogRevisionPromotedEvent promoted:
                        ApplyPromoted(entries, promoted);
                        break;
                    case ScriptCatalogRolledBackEvent rolledBack:
                        ApplyRolledBack(entries, rolledBack);
                        break;
                }
            }

            if (!entries.TryGetValue(scriptId, out var entry))
                return null;

            return new ScriptCatalogEntrySnapshot(
                entry.ScriptId,
                entry.ActiveRevision,
                entry.ActiveDefinitionActorId,
                entry.ActiveSourceHash,
                entry.PreviousRevision,
                entry.RevisionHistory.ToArray(),
                entry.LastProposalId);
        }

        private static void ApplyPromoted(
            Dictionary<string, CatalogEntryState> entries,
            ScriptCatalogRevisionPromotedEvent evt)
        {
            var scriptId = evt.ScriptId ?? string.Empty;
            if (!entries.TryGetValue(scriptId, out var entry))
            {
                entry = new CatalogEntryState { ScriptId = scriptId };
                entries[scriptId] = entry;
            }

            entry.PreviousRevision = entry.ActiveRevision;
            entry.ActiveRevision = evt.Revision ?? string.Empty;
            entry.ActiveDefinitionActorId = evt.DefinitionActorId ?? string.Empty;
            entry.ActiveSourceHash = evt.SourceHash ?? string.Empty;
            entry.LastProposalId = evt.ProposalId ?? string.Empty;
            if (!entry.RevisionHistory.Contains(entry.ActiveRevision, StringComparer.Ordinal))
                entry.RevisionHistory.Add(entry.ActiveRevision);
        }

        private static void ApplyRolledBack(
            Dictionary<string, CatalogEntryState> entries,
            ScriptCatalogRolledBackEvent evt)
        {
            var scriptId = evt.ScriptId ?? string.Empty;
            if (!entries.TryGetValue(scriptId, out var entry))
            {
                entry = new CatalogEntryState { ScriptId = scriptId };
                entries[scriptId] = entry;
            }

            var targetRevision = evt.TargetRevision ?? string.Empty;
            var previouslyActiveRevision = entry.ActiveRevision;
            var previouslyActiveDefinitionActorId = entry.ActiveDefinitionActorId;
            var previouslyActiveSourceHash = entry.ActiveSourceHash;

            entry.PreviousRevision = string.IsNullOrWhiteSpace(evt.PreviousRevision)
                ? previouslyActiveRevision
                : evt.PreviousRevision;
            entry.ActiveRevision = targetRevision;
            if (string.Equals(targetRevision, previouslyActiveRevision, StringComparison.Ordinal))
            {
                entry.ActiveDefinitionActorId = previouslyActiveDefinitionActorId;
                entry.ActiveSourceHash = previouslyActiveSourceHash;
            }
            else
            {
                entry.ActiveDefinitionActorId = string.Empty;
                entry.ActiveSourceHash = string.Empty;
            }

            entry.LastProposalId = evt.ProposalId ?? string.Empty;
            if (!entry.RevisionHistory.Contains(targetRevision, StringComparer.Ordinal))
                entry.RevisionHistory.Add(targetRevision);
        }

        private sealed class DefinitionSnapshotState
        {
            public string ScriptId { get; set; } = string.Empty;
            public string Revision { get; set; } = string.Empty;
            public string SourceText { get; set; } = string.Empty;
            public string SourceHash { get; set; } = string.Empty;
            public string ReadModelSchemaVersion { get; set; } = string.Empty;
            public string ReadModelSchemaHash { get; set; } = string.Empty;
            public string StateTypeUrl { get; set; } = string.Empty;
            public string ReadModelTypeUrl { get; set; } = string.Empty;
            public ScriptPackageSpec ScriptPackage { get; set; } = new();
            public ByteString ProtocolDescriptorSet { get; set; } = ByteString.Empty;
            public string StateDescriptorFullName { get; set; } = string.Empty;
            public string ReadModelDescriptorFullName { get; set; } = string.Empty;
            public ScriptRuntimeSemanticsSpec RuntimeSemantics { get; set; } = new();
        }

        private sealed class CatalogEntryState
        {
            public string ScriptId { get; set; } = string.Empty;
            public string ActiveRevision { get; set; } = string.Empty;
            public string ActiveDefinitionActorId { get; set; } = string.Empty;
            public string ActiveSourceHash { get; set; } = string.Empty;
            public string PreviousRevision { get; set; } = string.Empty;
            public List<string> RevisionHistory { get; } = [];
            public string LastProposalId { get; set; } = string.Empty;
        }
    }

    private sealed class TestActor : IActor
    {
        private readonly Func<EventEnvelope, CancellationToken, Task> _onHandle;

        public TestActor(
            string id,
            Func<EventEnvelope, CancellationToken, Task>? onHandle = null)
        {
            Id = id;
            _onHandle = onHandle ?? ((_, _) => Task.CompletedTask);
            Agent = new NoopAgent(id);
        }

        public string Id { get; }
        public IAgent Agent { get; }

        public Task ActivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DeactivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            return _onHandle(envelope, ct);
        }

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class NoopAgent(string id) : IAgent
    {
        public string Id { get; } = id;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = envelope;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<string> GetDescriptionAsync() => Task.FromResult("noop");

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>(Array.Empty<System.Type>());

        public Task ActivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DeactivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class StaticAddressResolver : IScriptingActorAddressResolver
    {
        public string GetEvolutionManagerActorId() => "script-evolution-manager";

        public string GetEvolutionSessionActorId(string proposalId) => $"script-evolution-session:{proposalId}";

        public string GetCatalogActorId() => "catalog-1";

        public string GetDefinitionActorId(string scriptId) => $"script-definition:{scriptId}";
    }

    private sealed class TestDecisionReadPort : IScriptEvolutionDecisionReadPort
    {
        public ScriptPromotionDecision? NextResult { get; set; }

        public List<string> Calls { get; } = [];

        public Task<ScriptPromotionDecision?> TryGetAsync(
            string proposalId,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(proposalId);
            return Task.FromResult(NextResult);
        }
    }

    private sealed class TestProjectionLease(string actorId, string proposalId) : IScriptEvolutionProjectionLease
    {
        public string ActorId { get; } = actorId;

        public string ProposalId { get; } = proposalId;
    }

    private sealed class TestProjectionPort : IScriptEvolutionProjectionPort
    {
        private readonly Dictionary<string, IEventSink<ScriptEvolutionSessionCompletedEvent>> _sinks =
            new(StringComparer.Ordinal);

        public bool ProjectionEnabled => true;

        public bool ReturnNullLease { get; set; }

        public int DetachCount { get; private set; }

        public int ReleaseCount { get; private set; }

        public Task<IScriptEvolutionProjectionLease?> EnsureActorProjectionAsync(
            string sessionActorId,
            string proposalId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (ReturnNullLease)
                return Task.FromResult<IScriptEvolutionProjectionLease?>(null);
            return Task.FromResult<IScriptEvolutionProjectionLease?>(
                new TestProjectionLease(sessionActorId, proposalId));
        }

        public Task AttachLiveSinkAsync(
            IScriptEvolutionProjectionLease lease,
            IEventSink<ScriptEvolutionSessionCompletedEvent> sink,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _sinks[lease.ActorId] = sink;
            return Task.CompletedTask;
        }

        public Task DetachLiveSinkAsync(
            IScriptEvolutionProjectionLease lease,
            IEventSink<ScriptEvolutionSessionCompletedEvent> sink,
            CancellationToken ct = default)
        {
            _ = sink;
            ct.ThrowIfCancellationRequested();
            DetachCount++;
            _sinks.Remove(lease.ActorId);
            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(
            IScriptEvolutionProjectionLease lease,
            CancellationToken ct = default)
        {
            _ = lease;
            ct.ThrowIfCancellationRequested();
            ReleaseCount++;
            return Task.CompletedTask;
        }

        public void Publish(string sessionActorId, ScriptEvolutionSessionCompletedEvent evt)
        {
            if (_sinks.TryGetValue(sessionActorId, out var sink))
                sink.Push(evt);
        }
    }

    private sealed class NoOpScriptExecutionProjectionPort : IScriptExecutionProjectionPort
    {
        public bool ProjectionEnabled => true;

        public Task<IScriptExecutionProjectionLease?> EnsureActorProjectionAsync(
            string actorId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IScriptExecutionProjectionLease?>(new NoOpScriptExecutionProjectionLease(actorId));
        }

        public Task AttachLiveSinkAsync(
            IScriptExecutionProjectionLease lease,
            IEventSink<EventEnvelope> sink,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task DetachLiveSinkAsync(
            IScriptExecutionProjectionLease lease,
            IEventSink<EventEnvelope> sink,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task ReleaseActorProjectionAsync(
            IScriptExecutionProjectionLease lease,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed record NoOpScriptExecutionProjectionLease(string ActorId) : IScriptExecutionProjectionLease;
}
