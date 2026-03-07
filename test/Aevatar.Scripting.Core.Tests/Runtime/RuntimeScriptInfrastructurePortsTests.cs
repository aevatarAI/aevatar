using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Streaming;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.Ports;
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
        var accessor = new RuntimeScriptActorAccessor(runtime);
        var service = new RuntimeScriptExecutionLifecycleService(accessor);

        var actorId = await service.SpawnRuntimeAsync(
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
        var accessor = new RuntimeScriptActorAccessor(runtime);
        var service = new RuntimeScriptExecutionLifecycleService(accessor);

        var actorId = await service.SpawnRuntimeAsync(
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
        var accessor = new RuntimeScriptActorAccessor(runtime);
        var service = new RuntimeScriptExecutionLifecycleService(accessor);

        var act = () => service.RunRuntimeAsync(
            runtimeActorId: string.Empty,
            runId: "run-1",
            inputPayload: Any.Pack(new Struct()),
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
        var accessor = new RuntimeScriptActorAccessor(runtime);
        var service = new RuntimeScriptExecutionLifecycleService(accessor);

        var act = () => service.RunRuntimeAsync(
            runtimeActorId: "runtime-1",
            runId: string.Empty,
            inputPayload: Any.Pack(new Struct()),
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
        var accessor = new RuntimeScriptActorAccessor(runtime);
        var service = new RuntimeScriptExecutionLifecycleService(accessor);

        var act = () => service.RunRuntimeAsync(
            runtimeActorId: "runtime-missing",
            runId: "run-1",
            inputPayload: Any.Pack(new Struct()),
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
        var accessor = new RuntimeScriptActorAccessor(runtime);
        var service = new RuntimeScriptExecutionLifecycleService(accessor);

        await service.RunRuntimeAsync(
            runtimeActorId: "runtime-1",
            runId: "run-1",
            inputPayload: Any.Pack(new StringValue { Value = "input" }),
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
    public async Task DefinitionSnapshotPort_ShouldThrow_WhenSourceTextIsEmpty()
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
            .WithMessage("*source_text is empty*definition-1*");
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
    public async Task DefinitionSnapshotPort_ShouldExposeEventDrivenModeFromOptions()
    {
        var runtime = new TestActorRuntime();
        var streams = new InMemoryStreamProvider();
        var queryClient = new RuntimeScriptQueryClient(streams, new RuntimeStreamRequestReplyClient());
        var port = new RuntimeScriptDefinitionSnapshotPort(
            new RuntimeScriptActorAccessor(runtime),
            queryClient,
            new FixedQueryModes(useEventDrivenDefinitionQuery: true),
            new FixedTimeouts());

        port.UseEventDrivenDefinitionQuery.Should().BeTrue();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task DefinitionLifecycleService_ShouldDispatchUpsertRequest_AndWaitForCommandAck()
    {
        UpsertScriptDefinitionRequestedEvent? captured = null;
        var streams = new InMemoryStreamProvider();
        var runtime = new TestActorRuntime
        {
            CreateActor = actorId => new TestActor(actorId, async (envelope, ct) =>
            {
                if (!envelope.Payload.Is(UpsertScriptDefinitionRequestedEvent.Descriptor))
                    return;

                captured = envelope.Payload.Unpack<UpsertScriptDefinitionRequestedEvent>();
                await streams.GetStream(captured.ReplyStreamId).ProduceAsync(new ScriptDefinitionCommandRespondedEvent
                {
                    RequestId = captured.RequestId,
                    Succeeded = true,
                    ScriptId = captured.ScriptId,
                    Revision = captured.ScriptRevision,
                }, ct);
            }),
        };
        var service = new RuntimeScriptDefinitionLifecycleService(
            new RuntimeScriptActorAccessor(runtime),
            new RuntimeScriptQueryClient(streams, new RuntimeStreamRequestReplyClient()),
            new StaticAddressResolver(),
            new FixedTimeouts());

        var actorId = await service.UpsertDefinitionAsync(
            scriptId: "script-1",
            scriptRevision: "rev-1",
            sourceText: "public sealed class RuntimeScript {}",
            sourceHash: "hash-1",
            definitionActorId: null,
            ct: CancellationToken.None);

        actorId.Should().Be("script-definition:script-1");
        captured.Should().NotBeNull();
        captured!.ScriptId.Should().Be("script-1");
        captured.ScriptRevision.Should().Be("rev-1");
        captured.RequestId.Should().NotBeNullOrWhiteSpace();
        captured.ReplyStreamId.Should().StartWith(ScriptingQueryChannels.DefinitionReplyStreamPrefix + ":");
    }

    [Fact]
    public async Task CatalogLifecycleService_ShouldDispatchPromoteRequest_WithResolvedCatalogActorId()
    {
        PromoteScriptRevisionRequestedEvent? captured = null;
        var streams = new InMemoryStreamProvider();
        var runtime = new TestActorRuntime
        {
            CreateActor = actorId => new TestActor(actorId, async (envelope, ct) =>
            {
                if (envelope.Payload.Is(PromoteScriptRevisionRequestedEvent.Descriptor))
                {
                    captured = envelope.Payload.Unpack<PromoteScriptRevisionRequestedEvent>();
                    await streams.GetStream(captured.ReplyStreamId).ProduceAsync(new ScriptCatalogCommandRespondedEvent
                    {
                        RequestId = captured.RequestId,
                        Succeeded = true,
                        ScriptId = "script-1",
                        ActiveRevision = "rev-2",
                        ActiveDefinitionActorId = "definition-2",
                    }, ct);
                    return;
                }
            }),
        };
        var service = new RuntimeScriptCatalogLifecycleService(
            new RuntimeScriptActorAccessor(runtime),
            new RuntimeScriptQueryClient(streams, new RuntimeStreamRequestReplyClient()),
            new StaticAddressResolver(),
            new FixedTimeouts());

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
    public async Task CatalogLifecycleService_ShouldDispatchRollbackRequest_WithProvidedCatalogActorId()
    {
        RollbackScriptRevisionRequestedEvent? captured = null;
        var streams = new InMemoryStreamProvider();
        var runtime = new TestActorRuntime
        {
            CreateActor = actorId => new TestActor(actorId, async (envelope, ct) =>
            {
                if (envelope.Payload.Is(RollbackScriptRevisionRequestedEvent.Descriptor))
                {
                    captured = envelope.Payload.Unpack<RollbackScriptRevisionRequestedEvent>();
                    await streams.GetStream(captured.ReplyStreamId).ProduceAsync(new ScriptCatalogCommandRespondedEvent
                    {
                        RequestId = captured.RequestId,
                        Succeeded = true,
                        ScriptId = "script-1",
                        ActiveRevision = "rev-1",
                    }, ct);
                    return;
                }
            }),
        };
        var service = new RuntimeScriptCatalogLifecycleService(
            new RuntimeScriptActorAccessor(runtime),
            new RuntimeScriptQueryClient(streams, new RuntimeStreamRequestReplyClient()),
            new StaticAddressResolver(),
            new FixedTimeouts());

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
    public async Task CatalogLifecycleService_GetCatalogEntryAsync_ShouldReturnNull_WhenScriptIdMissing()
    {
        var service = CreateCatalogLifecycleService(new TestActorRuntime());

        var entry = await service.GetCatalogEntryAsync(null, string.Empty, CancellationToken.None);

        entry.Should().BeNull();
    }

    [Fact]
    public async Task CatalogLifecycleService_GetCatalogEntryAsync_ShouldReturnNull_WhenCatalogActorMissing()
    {
        var service = CreateCatalogLifecycleService(new TestActorRuntime());

        var entry = await service.GetCatalogEntryAsync(null, "script-1", CancellationToken.None);

        entry.Should().BeNull();
    }

    [Fact]
    public async Task CatalogLifecycleService_GetCatalogEntryAsync_ShouldReturnNull_WhenQueryRespondsNotFound()
    {
        var runtime = new TestActorRuntime();
        var service = CreateCatalogLifecycleService(
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
    public async Task CatalogLifecycleService_GetCatalogEntryAsync_ShouldMapSnapshot_WhenFound()
    {
        var runtime = new TestActorRuntime();
        var service = CreateCatalogLifecycleService(
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
    public async Task EvolutionLifecycleService_ShouldReturnAcceptedCommand_WhenSessionAcknowledged()
    {
        var streams = new InMemoryStreamProvider();
        var runtime = CreateEvolutionRuntime(streams, (_, start, session) =>
        {
            session.AcceptedResponse = new ScriptEvolutionCommandAcceptedEvent
            {
                ProposalId = start.ProposalId ?? string.Empty,
                Accepted = true,
                ScriptId = start.ScriptId ?? string.Empty,
                SessionActorId = "script-evolution-session:proposal-1",
            };
        });
        var service = CreateEvolutionLifecycleService(runtime, streams);

        var accepted = await service.ProposeAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-1",
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "source-rev-2",
                CandidateSourceHash: "hash-rev-2",
                Reason: "rollout"),
            CancellationToken.None);

        accepted.ProposalId.Should().Be("proposal-1");
        accepted.ScriptId.Should().Be("script-1");
        accepted.SessionActorId.Should().Be("script-evolution-session:proposal-1");
    }

    [Fact]
    public async Task EvolutionLifecycleService_ShouldGenerateProposalId_WhenRequestProposalIdMissing()
    {
        var streams = new InMemoryStreamProvider();
        StartScriptEvolutionSessionRequestedEvent? capturedStart = null;
        var runtime = CreateEvolutionRuntime(streams, (_, start, session) =>
        {
            capturedStart = start;
            session.AcceptedResponse = new ScriptEvolutionCommandAcceptedEvent
            {
                ProposalId = start.ProposalId ?? string.Empty,
                Accepted = true,
                ScriptId = start.ScriptId ?? string.Empty,
                SessionActorId = $"script-evolution-session:{start.ProposalId}",
            };
        });
        var service = CreateEvolutionLifecycleService(runtime, streams);

        var accepted = await service.ProposeAsync(
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
        accepted.ProposalId.Should().Be(capturedStart.ProposalId);
    }

    [Fact]
    public async Task EvolutionLifecycleService_ShouldThrowTimeout_WhenSessionTimesOut()
    {
        var streams = new InMemoryStreamProvider();
        var runtime = CreateEvolutionRuntime(streams, (_, _, session) => session.SuppressStartResponse = true);
        var service = CreateEvolutionLifecycleService(
            runtime,
            streams,
            new FixedTimeouts { EvolutionCommandAckTimeout = TimeSpan.FromMilliseconds(50) });

        var act = () => service.ProposeAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-timeout",
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "source-rev-2",
                CandidateSourceHash: "hash-rev-2",
                Reason: "rollout"),
            CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*script evolution command ack response*");
    }

    [Fact]
    public async Task EvolutionLifecycleService_ShouldThrow_WhenSessionCommandRejected()
    {
        var streams = new InMemoryStreamProvider();
        var runtime = CreateEvolutionRuntime(streams, (_, start, session) =>
        {
            session.AcceptedResponse = new ScriptEvolutionCommandAcceptedEvent
            {
                ProposalId = start.ProposalId ?? string.Empty,
                ScriptId = start.ScriptId ?? string.Empty,
                Accepted = false,
                SessionActorId = "script-evolution-session:proposal-no-decision",
                FailureReason = "policy rejected",
            };
        });
        var service = CreateEvolutionLifecycleService(runtime, streams);

        var act = () => service.ProposeAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-no-decision",
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "source-rev-2",
                CandidateSourceHash: "hash-rev-2",
                Reason: "rollout"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*policy rejected*");
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
            await streams.GetStream(request.ReplyStreamId).ProduceAsync(response, ct);
        }));
        return new RuntimeScriptDefinitionSnapshotPort(
            new RuntimeScriptActorAccessor(runtime),
            new RuntimeScriptQueryClient(streams, new RuntimeStreamRequestReplyClient()),
            new FixedQueryModes(useEventDrivenDefinitionQuery: false),
            new FixedTimeouts { DefinitionSnapshotQueryTimeout = TimeSpan.FromMilliseconds(200) });
    }

    private static RuntimeScriptCatalogLifecycleService CreateCatalogLifecycleService(
        TestActorRuntime runtime,
        Func<QueryScriptCatalogEntryRequestedEvent, ScriptCatalogEntryRespondedEvent>? responseFactory = null,
        Func<PromoteScriptRevisionRequestedEvent, ScriptCatalogCommandRespondedEvent>? promoteResponseFactory = null,
        Func<RollbackScriptRevisionRequestedEvent, ScriptCatalogCommandRespondedEvent>? rollbackResponseFactory = null)
    {
        var streams = new InMemoryStreamProvider();
        runtime.CreateActor = actorId => new TestActor(actorId, async (envelope, ct) =>
        {
            if (promoteResponseFactory != null && envelope.Payload.Is(PromoteScriptRevisionRequestedEvent.Descriptor))
            {
                var request = envelope.Payload.Unpack<PromoteScriptRevisionRequestedEvent>();
                var response = promoteResponseFactory(request);
                await streams.GetStream(request.ReplyStreamId).ProduceAsync(response, ct);
                return;
            }

            if (rollbackResponseFactory != null && envelope.Payload.Is(RollbackScriptRevisionRequestedEvent.Descriptor))
            {
                var request = envelope.Payload.Unpack<RollbackScriptRevisionRequestedEvent>();
                var response = rollbackResponseFactory(request);
                await streams.GetStream(request.ReplyStreamId).ProduceAsync(response, ct);
                return;
            }

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

        return new RuntimeScriptCatalogLifecycleService(
            new RuntimeScriptActorAccessor(runtime),
            new RuntimeScriptQueryClient(streams, new RuntimeStreamRequestReplyClient()),
            new StaticAddressResolver(),
            new FixedTimeouts { CatalogEntryQueryTimeout = TimeSpan.FromMilliseconds(200) });
    }

    private static TestActorRuntime CreateEvolutionRuntime(
        InMemoryStreamProvider streams,
        Action<string, StartScriptEvolutionSessionRequestedEvent, TestEvolutionSessionState> onStartSession)
    {
        var runtime = new TestActorRuntime();
        var sessions = new Dictionary<string, TestEvolutionSessionState>(StringComparer.Ordinal);
        runtime.CreateActor = actorId =>
        {
            if (actorId.StartsWith("script-evolution-session:", StringComparison.Ordinal))
            {
                var session = new TestEvolutionSessionState();
                sessions[actorId] = session;
                return new TestActor(actorId, async (envelope, ct) =>
                {
                    if (envelope.Payload.Is(StartScriptEvolutionSessionRequestedEvent.Descriptor))
                    {
                        var start = envelope.Payload.Unpack<StartScriptEvolutionSessionRequestedEvent>();
                        onStartSession(actorId, start, session);
                        if (!session.SuppressStartResponse &&
                            !string.IsNullOrWhiteSpace(start.RequestId) &&
                            !string.IsNullOrWhiteSpace(start.ReplyStreamId))
                        {
                            var response = session.AcceptedResponse?.Clone()
                                ?? new ScriptEvolutionCommandAcceptedEvent
                                {
                                    ProposalId = start.ProposalId ?? string.Empty,
                                    ScriptId = start.ScriptId ?? string.Empty,
                                    Accepted = true,
                                    SessionActorId = actorId,
                                };
                            response.RequestId = start.RequestId;
                            await streams.GetStream(start.ReplyStreamId).ProduceAsync(response, ct);
                        }
                        return;
                    }

                    ct.ThrowIfCancellationRequested();
                });
            }

            return new TestActor(actorId);
        };
        return runtime;
    }

    private static RuntimeScriptEvolutionLifecycleService CreateEvolutionLifecycleService(
        TestActorRuntime runtime,
        InMemoryStreamProvider streams,
        IScriptingPortTimeouts? timeouts = null)
    {
        return new RuntimeScriptEvolutionLifecycleService(
            new RuntimeScriptActorAccessor(runtime),
            new RuntimeScriptQueryClient(streams, new RuntimeStreamRequestReplyClient()),
            new StaticAddressResolver(),
            timeouts ?? new FixedTimeouts { EvolutionCommandAckTimeout = TimeSpan.FromMilliseconds(200) });
    }

    private sealed class TestActorRuntime : IActorRuntime
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

    private sealed class FixedTimeouts : IScriptingPortTimeouts
    {
        public TimeSpan DefinitionSnapshotQueryTimeout { get; init; } = TimeSpan.FromMilliseconds(200);

        public TimeSpan DefinitionMutationTimeout { get; init; } = TimeSpan.FromMilliseconds(200);

        public TimeSpan CatalogEntryQueryTimeout { get; init; } = TimeSpan.FromMilliseconds(200);

        public TimeSpan CatalogMutationTimeout { get; init; } = TimeSpan.FromMilliseconds(200);

        public TimeSpan EvolutionCommandAckTimeout { get; init; } = TimeSpan.FromMilliseconds(200);

        public TimeSpan EvolutionSnapshotQueryTimeout { get; init; } = TimeSpan.FromMilliseconds(200);

        public TimeSpan RuntimeSnapshotQueryTimeout { get; init; } = TimeSpan.FromMilliseconds(200);
    }

    private sealed class FixedQueryModes(bool useEventDrivenDefinitionQuery) : IScriptingRuntimeQueryModes
    {
        public bool UseEventDrivenDefinitionQuery { get; } = useEventDrivenDefinitionQuery;
    }

    private sealed class StaticAddressResolver : IScriptingActorAddressResolver
    {
        public string GetEvolutionSessionActorId(string proposalId) => $"script-evolution-session:{proposalId}";

        public string GetCatalogActorId() => "catalog-1";

        public string GetDefinitionActorId(string scriptId) => $"script-definition:{scriptId}";
    }

    private sealed class TestEvolutionSessionState
    {
        public ScriptEvolutionCommandAcceptedEvent? AcceptedResponse { get; set; }

        public bool SuppressStartResponse { get; set; }
    }
}
