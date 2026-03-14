using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Core.Commands;
using Aevatar.CQRS.Core.Interactions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Tests.Messages;
using Aevatar.Scripting.Infrastructure.Compilation;
using Aevatar.Scripting.Infrastructure.Ports;
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
        runtime.RegisterActor(new TestActor("runtime-existing"));
        var service = CreateRuntimeProvisioningService(runtime);

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
        var runtime = new TestActorRuntime();
        var port = CreateDefinitionSnapshotPort(runtime, _ => throw new InvalidOperationException("should-not-handle"));

        var act = () => port.GetRequiredAsync("definition-missing", "rev-1", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*definition actor not found*definition-missing*");
    }

    [Fact]
    public async Task DefinitionSnapshotPort_ShouldThrowFailureReason_WhenSnapshotQueryReturnsNotFound()
    {
        var runtime = new TestActorRuntime();
        var port = CreateDefinitionSnapshotPort(runtime, request =>
            new ScriptDefinitionSnapshotRespondedEvent
            {
                RequestId = request.RequestId,
                Found = false,
                FailureReason = "definition-not-ready",
            });

        var act = () => port.GetRequiredAsync("definition-1", "rev-1", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*definition-not-ready*");
    }

    [Fact]
    public async Task DefinitionSnapshotPort_ShouldThrowDefaultMessage_WhenNotFoundReasonIsEmpty()
    {
        var runtime = new TestActorRuntime();
        var port = CreateDefinitionSnapshotPort(runtime, request =>
            new ScriptDefinitionSnapshotRespondedEvent
            {
                RequestId = request.RequestId,
                Found = false,
                FailureReason = string.Empty,
            });

        var act = () => port.GetRequiredAsync("definition-1", "rev-1", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*snapshot not found*definition-1*");
    }

    [Fact]
    public async Task DefinitionSnapshotPort_ShouldThrow_WhenScriptPackageIsEmpty()
    {
        var runtime = new TestActorRuntime();
        var port = CreateDefinitionSnapshotPort(runtime, request =>
            new ScriptDefinitionSnapshotRespondedEvent
            {
                RequestId = request.RequestId,
                Found = true,
                ScriptId = "script-1",
                Revision = "rev-1",
                SourceText = string.Empty,
            });

        var act = () => port.GetRequiredAsync("definition-1", "rev-1", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*script_package is empty*definition-1*");
    }

    [Fact]
    public async Task DefinitionSnapshotPort_ShouldThrow_WhenRequestedRevisionDoesNotMatchSnapshot()
    {
        var runtime = new TestActorRuntime();
        var port = CreateDefinitionSnapshotPort(runtime, request =>
            new ScriptDefinitionSnapshotRespondedEvent
            {
                RequestId = request.RequestId,
                Found = true,
                ScriptId = "script-1",
                Revision = "rev-actual",
                SourceText = "public sealed class RuntimeScript {}",
            });

        var act = () => port.GetRequiredAsync("definition-1", "rev-requested", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*rev-requested*rev-actual*");
    }

    [Fact]
    public async Task DefinitionSnapshotPort_ShouldReturnSnapshot_WhenResponseIsValid()
    {
        var runtime = new TestActorRuntime();
        var port = CreateDefinitionSnapshotPort(runtime, request =>
            new ScriptDefinitionSnapshotRespondedEvent
            {
                RequestId = request.RequestId,
                Found = true,
                ScriptId = "script-1",
                Revision = "rev-1",
                SourceText = "public sealed class RuntimeScript {}",
                ReadModelSchemaVersion = "v1",
                ReadModelSchemaHash = "hash-v1",
            });

        var snapshot = await port.GetRequiredAsync("definition-1", string.Empty, CancellationToken.None);

        snapshot.ScriptId.Should().Be("script-1");
        snapshot.Revision.Should().Be("rev-1");
        snapshot.SourceText.Should().Contain("RuntimeScript");
        snapshot.ReadModelSchemaVersion.Should().Be("v1");
        snapshot.ReadModelSchemaHash.Should().Be("hash-v1");
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
        var service = CreateCatalogQueryService(new TestActorRuntime());

        var entry = await service.GetCatalogEntryAsync(null, string.Empty, CancellationToken.None);

        entry.Should().BeNull();
    }

    [Fact]
    public async Task CatalogQueryService_GetCatalogEntryAsync_ShouldReturnNull_WhenCatalogActorMissing()
    {
        var service = CreateCatalogQueryService(new TestActorRuntime());

        var entry = await service.GetCatalogEntryAsync(null, "script-1", CancellationToken.None);

        entry.Should().BeNull();
    }

    [Fact]
    public async Task CatalogQueryService_GetCatalogEntryAsync_ShouldReturnNull_WhenQueryRespondsNotFound()
    {
        var runtime = new TestActorRuntime();
        var service = CreateCatalogQueryService(
            runtime,
            request => new ScriptCatalogEntryRespondedEvent
            {
                RequestId = request.RequestId,
                Found = false,
                ScriptId = request.ScriptId,
            });

        var entry = await service.GetCatalogEntryAsync("catalog-1", "script-1", CancellationToken.None);

        entry.Should().BeNull();
    }

    [Fact]
    public async Task CatalogQueryService_GetCatalogEntryAsync_ShouldMapSnapshot_WhenFound()
    {
        var runtime = new TestActorRuntime();
        var service = CreateCatalogQueryService(
            runtime,
            request => new ScriptCatalogEntryRespondedEvent
            {
                RequestId = request.RequestId,
                Found = true,
                ScriptId = request.ScriptId,
                ActiveRevision = "rev-2",
                ActiveDefinitionActorId = "definition-2",
                ActiveSourceHash = "hash-2",
                PreviousRevision = "rev-1",
                RevisionHistory = { "rev-1", "rev-2" },
                LastProposalId = "proposal-2",
            });

        var entry = await service.GetCatalogEntryAsync("catalog-1", "script-1", CancellationToken.None);

        entry.Should().NotBeNull();
        entry!.ScriptId.Should().Be("script-1");
        entry.ActiveRevision.Should().Be("rev-2");
        entry.RevisionHistory.Should().Contain("rev-1");
        entry.RevisionHistory.Should().Contain("rev-2");
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

    private static RuntimeScriptDefinitionSnapshotPort CreateDefinitionSnapshotPort(
        TestActorRuntime runtime,
        Func<QueryScriptDefinitionSnapshotRequestedEvent, ScriptDefinitionSnapshotRespondedEvent> responseFactory)
    {
        var streams = new InMemoryStreamProvider();
        runtime.RegisterActor(new TestActor("definition-1", async (envelope, ct) =>
        {
            var request = envelope.Payload.Unpack<QueryScriptDefinitionSnapshotRequestedEvent>();
            var response = responseFactory(request);
            if (response.Found &&
                response.ScriptPackage == null &&
                !string.IsNullOrWhiteSpace(response.SourceText))
            {
                response.ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource(response.SourceText);
            }
            await streams.GetStream(request.ReplyStreamId).ProduceAsync(response, ct);
        }));
        return new RuntimeScriptDefinitionSnapshotPort(
            new RuntimeScriptActorAccessor(runtime),
            new RuntimeScriptQueryClient(streams, new RuntimeStreamRequestReplyClient()),
            new ScriptingQueryTimeoutOptions { DefinitionSnapshotQueryTimeout = TimeSpan.FromMilliseconds(200) });
    }

    private static RuntimeScriptCatalogQueryService CreateCatalogQueryService(
        TestActorRuntime runtime,
        Func<QueryScriptCatalogEntryRequestedEvent, ScriptCatalogEntryRespondedEvent>? responseFactory = null)
    {
        var streams = new InMemoryStreamProvider();
        runtime.CreateActor = actorId => new TestActor(actorId, async (envelope, ct) =>
        {
            if (responseFactory != null && envelope.Payload.Is(QueryScriptCatalogEntryRequestedEvent.Descriptor))
            {
                var request = envelope.Payload.Unpack<QueryScriptCatalogEntryRequestedEvent>();
                var response = responseFactory(request);
                await streams.GetStream(request.ReplyStreamId).ProduceAsync(response, ct);
            }
        });

        if (responseFactory != null)
            runtime.RegisterActor(new TestActor("catalog-1", async (envelope, ct) =>
            {
                var request = envelope.Payload.Unpack<QueryScriptCatalogEntryRequestedEvent>();
                var response = responseFactory(request);
                await streams.GetStream(request.ReplyStreamId).ProduceAsync(response, ct);
            }));

        return new RuntimeScriptCatalogQueryService(
            new RuntimeScriptActorAccessor(runtime),
            new RuntimeScriptQueryClient(streams, new RuntimeStreamRequestReplyClient()),
            new StaticAddressResolver(),
            new ScriptingQueryTimeoutOptions { CatalogEntryQueryTimeout = TimeSpan.FromMilliseconds(200) });
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

    private static RuntimeScriptProvisioningService CreateRuntimeProvisioningService(TestActorRuntime runtime)
    {
        return new RuntimeScriptProvisioningService(
            new RuntimeScriptActorAccessor(runtime),
            CreateDefinitionSnapshotPort(
                runtime,
                request => new ScriptDefinitionSnapshotRespondedEvent
                {
                    RequestId = request.RequestId,
                    Found = true,
                    ScriptId = "script-1",
                    Revision = string.IsNullOrWhiteSpace(request.RequestedRevision) ? "latest" : request.RequestedRevision,
                    SourceText =
                        """
                        using System;
                        using System.Threading;
                        using System.Threading.Tasks;
                        using Aevatar.Scripting.Abstractions;
                        using Aevatar.Scripting.Abstractions.Behaviors;
                        using Aevatar.Scripting.Core.Tests.Messages;

                        public sealed class ProvisioningBehavior : ScriptBehavior<SimpleTextState, SimpleTextReadModel>
                        {
                            protected override void Configure(IScriptBehaviorBuilder<SimpleTextState, SimpleTextReadModel> builder)
                            {
                                builder.OnQuery<SimpleTextQueryRequested, SimpleTextQueryResponded>(HandleQueryAsync);
                            }

                            private static Task<SimpleTextQueryResponded?> HandleQueryAsync(
                                SimpleTextQueryRequested queryPayload,
                                ScriptQueryContext<SimpleTextReadModel> snapshot,
                                CancellationToken ct)
                            {
                                ct.ThrowIfCancellationRequested();
                                return Task.FromResult<SimpleTextQueryResponded?>(new SimpleTextQueryResponded
                                {
                                    RequestId = queryPayload.RequestId ?? string.Empty,
                                    Current = snapshot.CurrentReadModel ?? new SimpleTextReadModel(),
                                });
                            }
                        }
                        """,
                    SourceHash = "provisioning-hash",
                }),
            new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy()),
            new NoOpScriptExecutionProjectionPort());
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
                new RollbackScriptCatalogRevisionCommandEnvelopeFactory()));
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

        public Func<string, IActor>? CreateActor { get; set; }

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
