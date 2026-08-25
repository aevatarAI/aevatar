using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Commands;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Core.Ports;
using Aevatar.GAgentService.Infrastructure.Orchestration;
using Aevatar.Scripting.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Tests.Infrastructure;

public sealed class ScriptingServiceRevisionRepublishHookTests
{
    [Fact]
    public async Task BeforePublishAsync_ShouldRepublishEveryServingCandidate()
    {
        var commandPort = new RecordingServiceCommandPort();
        var hook = new ScriptingServiceRevisionRepublishHook(
            new FakeCandidateQueryReader
            {
                Result =
                [
                    new ServiceScriptingRepublishCandidateSnapshot(
                        GAgentService.Tests.TestSupport.GAgentServiceTestKit.CreateIdentity("svc-a"),
                        "rev-old-a",
                        "dep-a",
                        new ServiceRevisionScriptingSnapshot("script-a", "script-rev-1", "def-old-a", "hash-old-a"),
                        null),
                    new ServiceScriptingRepublishCandidateSnapshot(
                        GAgentService.Tests.TestSupport.GAgentServiceTestKit.CreateIdentity("svc-b"),
                        "rev-old-b",
                        "dep-b",
                        new ServiceRevisionScriptingSnapshot("script-a", "script-rev-1", "def-old-b", "hash-old-b"),
                        null),
                ],
            },
            commandPort,
            [new FakeScriptingImplementationAdapter()]);

        await hook.BeforePublishAsync(CreateContext(new ScriptCatalogRevisionPromotedEvent
        {
            ScopeId = "tenant",
            ScriptId = "script-a",
            Revision = "script-rev-2",
            DefinitionActorId = "def-new",
            SourceHash = "hash-new",
        }), CancellationToken.None);

        commandPort.Calls.Select(x => x.Method).Should().Equal(
            "CreateRevisionAsync",
            "PrepareRevisionAsync",
            "PublishRevisionAsync",
            "ActivateServiceRevisionAsync",
            "CreateRevisionAsync",
            "PrepareRevisionAsync",
            "PublishRevisionAsync",
            "ActivateServiceRevisionAsync");

        var createA = (CreateServiceRevisionCommand)commandPort.Calls[0].Command;
        createA.Spec.Identity.Should().BeEquivalentTo(GAgentService.Tests.TestSupport.GAgentServiceTestKit.CreateIdentity("svc-a"));
        createA.Spec.ScriptingSpec.Should().BeEquivalentTo(new ScriptingServiceRevisionSpec
        {
            ScriptId = "script-a",
            Revision = "script-rev-2",
            DefinitionActorId = "def-new",
            SourceHash = "hash-new",
        });
        createA.Spec.RevisionId.Should().StartWith("rev-old-a-script-script-rev-2-");
        createA.Spec.RevisionId.Length.Should().BeGreaterThan("rev-old-a-script-script-rev-2-".Length);
        var prepareA = (PrepareServiceRevisionCommand)commandPort.Calls[1].Command;
        var publishA = (PublishServiceRevisionCommand)commandPort.Calls[2].Command;
        prepareA.PreparationSpec.Should().BeEquivalentTo(createA.Spec);
        publishA.PublicationSpec.Should().BeEquivalentTo(createA.Spec);
        var activateA = (ActivateServiceRevisionCommand)commandPort.Calls[3].Command;
        activateA.ExpectedArtifactHash.Should().NotBeNullOrWhiteSpace();

        var activateB = (ActivateServiceRevisionCommand)commandPort.Calls[7].Command;
        activateB.Identity.Should().BeEquivalentTo(GAgentService.Tests.TestSupport.GAgentServiceTestKit.CreateIdentity("svc-b"));
        activateB.RevisionId.Should().StartWith("rev-old-b-script-script-rev-2-");
        activateB.RevisionId.Length.Should().BeGreaterThan("rev-old-b-script-script-rev-2-".Length);
        activateB.ExpectedArtifactHash.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldSkipWhenServingRevisionAlreadyMatchesPromotedScript()
    {
        var commandPort = new RecordingServiceCommandPort();
        var hook = new ScriptingServiceRevisionRepublishHook(
            new FakeCandidateQueryReader
            {
                Result =
                [
                    new ServiceScriptingRepublishCandidateSnapshot(
                        GAgentService.Tests.TestSupport.GAgentServiceTestKit.CreateIdentity(),
                        "rev-live",
                        "dep-live",
                        new ServiceRevisionScriptingSnapshot("script-a", "script-rev-2", "def-new", "hash-new"),
                        null),
                ],
            },
            commandPort,
            [new FakeScriptingImplementationAdapter()]);

        await hook.BeforePublishAsync(CreateContext(new ScriptCatalogRevisionPromotedEvent
        {
            ScopeId = "tenant",
            ScriptId = "script-a",
            Revision = "script-rev-2",
            DefinitionActorId = "def-new",
            SourceHash = "hash-new",
        }), CancellationToken.None);

        commandPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldIgnoreUnrelatedCommittedEvents()
    {
        var commandPort = new RecordingServiceCommandPort();
        var reader = new FakeCandidateQueryReader();
        var hook = new ScriptingServiceRevisionRepublishHook(
            reader,
            commandPort,
            [new FakeScriptingImplementationAdapter()]);

        await hook.BeforePublishAsync(
            CreateContext(new ScriptCatalogRollbackRequestedEvent
            {
                ScriptId = "script-a",
                ScopeId = "tenant",
            }),
            CancellationToken.None);

        reader.QueryCount.Should().Be(0);
        commandPort.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(" ", "script-a")]
    [InlineData("tenant", " ")]
    public async Task BeforePublishAsync_ShouldIgnorePromotedEventsWithBlankLookupIdentity(
        string scopeId,
        string scriptId)
    {
        var commandPort = new RecordingServiceCommandPort();
        var reader = new FakeCandidateQueryReader();
        var hook = new ScriptingServiceRevisionRepublishHook(
            reader,
            commandPort,
            [new FakeScriptingImplementationAdapter()]);

        await hook.BeforePublishAsync(CreateContext(new ScriptCatalogRevisionPromotedEvent
        {
            ScopeId = scopeId,
            ScriptId = scriptId,
            Revision = "script-rev-2",
        }), CancellationToken.None);

        reader.QueryCount.Should().Be(0);
        commandPort.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task BeforePublishAsync_WhenCreateRevisionAlreadyExists_ShouldContinueLifecycleCommands()
    {
        var commandPort = new RecordingServiceCommandPort
        {
            CreateRevisionException = new InvalidOperationException("revision already exists"),
        };
        var hook = new ScriptingServiceRevisionRepublishHook(
            new FakeCandidateQueryReader
            {
                Result =
                [
                    new ServiceScriptingRepublishCandidateSnapshot(
                        GAgentService.Tests.TestSupport.GAgentServiceTestKit.CreateIdentity("svc-existing"),
                        "rev-existing",
                        "dep-existing",
                        new ServiceRevisionScriptingSnapshot("script-a", "script-rev-1", "def-old", "hash-old"),
                        null),
                ],
            },
            commandPort,
            [new FakeScriptingImplementationAdapter()]);

        await hook.BeforePublishAsync(CreateContext(new ScriptCatalogRevisionPromotedEvent
        {
            ScopeId = "tenant",
            ScriptId = "script-a",
            Revision = "script-rev-2",
            DefinitionActorId = "def-new",
            SourceHash = "hash-new",
        }), CancellationToken.None);

        commandPort.Calls.Select(x => x.Method).Should().Equal(
            "CreateRevisionAsync",
            "PrepareRevisionAsync",
            "PublishRevisionAsync",
            "ActivateServiceRevisionAsync");
    }

    [Fact]
    public async Task BeforePublishAsync_WhenLifecycleCommandFails_ShouldSwallowAndContinuePublication()
    {
        var commandPort = new RecordingServiceCommandPort
        {
            PrepareRevisionException = new InvalidOperationException("prepare failed"),
        };
        var hook = new ScriptingServiceRevisionRepublishHook(
            new FakeCandidateQueryReader
            {
                Result =
                [
                    new ServiceScriptingRepublishCandidateSnapshot(
                        GAgentService.Tests.TestSupport.GAgentServiceTestKit.CreateIdentity("svc-failing"),
                        "rev-live",
                        "dep-live",
                        new ServiceRevisionScriptingSnapshot("script-a", "script-rev-1", "def-old", "hash-old"),
                        null),
                ],
            },
            commandPort,
            [new FakeScriptingImplementationAdapter()]);

        var act = async () => await hook.BeforePublishAsync(CreateContext(new ScriptCatalogRevisionPromotedEvent
        {
            ScopeId = "tenant",
            ScriptId = "script-a",
            Revision = "script-rev-2",
            DefinitionActorId = "def-new",
            SourceHash = "hash-new",
        }), CancellationToken.None);

        await act.Should().NotThrowAsync();
        commandPort.Calls.Select(x => x.Method).Should().Equal(
            "CreateRevisionAsync",
            "PrepareRevisionAsync");
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldUseStableFallbackSegmentsForBlankRevisionInputs()
    {
        var commandPort = new RecordingServiceCommandPort();
        var hook = new ScriptingServiceRevisionRepublishHook(
            new FakeCandidateQueryReader
            {
                Result =
                [
                    new ServiceScriptingRepublishCandidateSnapshot(
                        GAgentService.Tests.TestSupport.GAgentServiceTestKit.CreateIdentity("svc-fallback"),
                        " ",
                        "dep-live",
                        new ServiceRevisionScriptingSnapshot("script-a", "script-rev-1", "def-old", "hash-old"),
                        null),
                ],
            },
            commandPort,
            [new FakeScriptingImplementationAdapter()]);

        await hook.BeforePublishAsync(CreateContext(new ScriptCatalogRevisionPromotedEvent
        {
            ScopeId = "tenant",
            ScriptId = "script-a",
            Revision = " --Alpha--Beta-- ",
            DefinitionActorId = "def-new",
            SourceHash = "hash-new",
        }), CancellationToken.None);

        var create = (CreateServiceRevisionCommand)commandPort.Calls[0].Command;
        create.Spec.RevisionId.Should().StartWith("rev-script-alpha-beta-");
        create.Spec.RevisionId.Should().NotContain("--");
    }

    [Fact]
    public async Task BeforePublishAsync_ShouldUseScriptSegmentWhenPromotedRevisionIsBlank()
    {
        var commandPort = new RecordingServiceCommandPort();
        var hook = new ScriptingServiceRevisionRepublishHook(
            new FakeCandidateQueryReader
            {
                Result =
                [
                    new ServiceScriptingRepublishCandidateSnapshot(
                        GAgentService.Tests.TestSupport.GAgentServiceTestKit.CreateIdentity("svc-blank-revision"),
                        "rev-live",
                        "dep-live",
                        new ServiceRevisionScriptingSnapshot("script-a", "script-rev-1", "def-old", "hash-old"),
                        null),
                ],
            },
            commandPort,
            [new FakeScriptingImplementationAdapter()]);

        await hook.BeforePublishAsync(CreateContext(new ScriptCatalogRevisionPromotedEvent
        {
            ScopeId = "tenant",
            ScriptId = "script-a",
            Revision = " ",
            DefinitionActorId = "def-new",
            SourceHash = "hash-new",
        }), CancellationToken.None);

        var create = (CreateServiceRevisionCommand)commandPort.Calls[0].Command;
        create.Spec.RevisionId.Should().StartWith("rev-live-script-script-");
    }

    private static CommittedStatePublicationContext CreateContext<TEvent>(TEvent evt)
        where TEvent : class, Google.Protobuf.IMessage<TEvent>
    {
        return new CommittedStatePublicationContext
        {
            ActorId = "catalog-actor",
            ActorType = typeof(object),
            Published = new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Version = 1,
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    EventData = Any.Pack(evt),
                },
            },
        };
    }

    private sealed class FakeCandidateQueryReader : IServiceScriptingRepublishCandidateQueryReader
    {
        public IReadOnlyList<ServiceScriptingRepublishCandidateSnapshot> Result { get; init; } = [];

        public int QueryCount { get; private set; }

        public Task<IReadOnlyList<ServiceScriptingRepublishCandidateSnapshot>> QueryServingByScopeScriptAsync(
            string scopeId,
            string scriptId,
            CancellationToken ct = default)
        {
            QueryCount++;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeScriptingImplementationAdapter : IServiceImplementationAdapter
    {
        public ServiceImplementationKind ImplementationKind => ServiceImplementationKind.Scripting;

        public Task<PreparedServiceRevisionArtifact> PrepareRevisionAsync(
            PrepareServiceRevisionRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var spec = request.Spec.ScriptingSpec;
            return Task.FromResult(new PreparedServiceRevisionArtifact
            {
                Identity = request.Spec.Identity.Clone(),
                RevisionId = request.Spec.RevisionId,
                ImplementationKind = ServiceImplementationKind.Scripting,
                Endpoints =
                {
                    new ServiceEndpointDescriptor
                    {
                        EndpointId = "script.command",
                        DisplayName = "script.command",
                        Kind = ServiceEndpointKind.Command,
                        RequestTypeUrl = "type.googleapis.com/test.ScriptCommand",
                    },
                },
                DeploymentPlan = new ServiceDeploymentPlan
                {
                    ScriptingPlan = new ScriptingServiceDeploymentPlan
                    {
                        ScriptId = spec.ScriptId,
                        Revision = spec.Revision,
                        DefinitionActorId = spec.DefinitionActorId,
                        SourceHash = spec.SourceHash,
                    },
                },
            });
        }
    }

    private sealed class RecordingServiceCommandPort : IServiceCommandPort
    {
        private static readonly ServiceCommandAcceptedReceipt Receipt = new("actor", "cmd", "corr");

        public List<(string Method, object Command)> Calls { get; } = [];

        public Exception? CreateRevisionException { get; init; }

        public Exception? PrepareRevisionException { get; init; }

        public Task<ServiceCommandAcceptedReceipt> CreateServiceAsync(CreateServiceDefinitionCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);

        public Task<ServiceCommandAcceptedReceipt> UpdateServiceAsync(UpdateServiceDefinitionCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);

        public Task<ServiceCommandAcceptedReceipt> CreateRevisionAsync(CreateServiceRevisionCommand command, CancellationToken ct = default)
        {
            Calls.Add((nameof(CreateRevisionAsync), command));
            if (CreateRevisionException != null)
                throw CreateRevisionException;

            return Task.FromResult(Receipt);
        }

        public Task<ServiceCommandAcceptedReceipt> PrepareRevisionAsync(PrepareServiceRevisionCommand command, CancellationToken ct = default)
        {
            Calls.Add((nameof(PrepareRevisionAsync), command));
            if (PrepareRevisionException != null)
                throw PrepareRevisionException;

            return Task.FromResult(Receipt);
        }

        public Task<ServiceCommandAcceptedReceipt> PublishRevisionAsync(PublishServiceRevisionCommand command, CancellationToken ct = default)
        {
            Calls.Add((nameof(PublishRevisionAsync), command));
            return Task.FromResult(Receipt);
        }

        public Task<ServiceCommandAcceptedReceipt> RetireRevisionAsync(RetireServiceRevisionCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);

        public Task<ServiceCommandAcceptedReceipt> ActivateServiceRevisionAsync(ActivateServiceRevisionCommand command, CancellationToken ct = default)
        {
            Calls.Add((nameof(ActivateServiceRevisionAsync), command));
            return Task.FromResult(Receipt);
        }

        public Task<ServiceCommandAcceptedReceipt> DeactivateServiceDeploymentAsync(DeactivateServiceDeploymentCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);

        public Task<ServiceCommandAcceptedReceipt> ReplaceServiceServingTargetsAsync(ReplaceServiceServingTargetsCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);

        public Task<ServiceCommandAcceptedReceipt> StartServiceRolloutAsync(StartServiceRolloutCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);

        public Task<ServiceCommandAcceptedReceipt> AdvanceServiceRolloutAsync(AdvanceServiceRolloutCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);

        public Task<ServiceCommandAcceptedReceipt> PauseServiceRolloutAsync(PauseServiceRolloutCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);

        public Task<ServiceCommandAcceptedReceipt> ResumeServiceRolloutAsync(ResumeServiceRolloutCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);

        public Task<ServiceCommandAcceptedReceipt> RollbackServiceRolloutAsync(RollbackServiceRolloutCommand command, CancellationToken ct = default) =>
            Task.FromResult(Receipt);
    }
}
