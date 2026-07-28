using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Application.Runtime;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Tests.Messages;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using CoreScriptDefinitionUpsertResult = Aevatar.Scripting.Core.Ports.ScriptDefinitionUpsertResult;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public sealed class ScriptAgentLifecycleCapabilitiesTests
{
    [Fact]
    public async Task MessagingAndCallbackApis_ShouldDelegateToInjectedHandlers()
    {
        var published = new List<(IMessage Payload, TopologyAudience Audience)>();
        var sent = new List<(string TargetActorId, IMessage Payload)>();
        var selfPublished = new List<IMessage>();
        var scheduled = new List<(string CallbackId, TimeSpan DueTime, IMessage Payload)>();
        var canceled = new List<RuntimeCallbackLease>();
        var capabilities = CreateCapabilities(
            publishAsync: (payload, audience, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                published.Add((payload, audience));
                return Task.CompletedTask;
            },
            sendToAsync: (targetActorId, payload, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                sent.Add((targetActorId, payload));
                return Task.CompletedTask;
            },
            publishToSelfAsync: (payload, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                selfPublished.Add(payload);
                return Task.CompletedTask;
            },
            scheduleSelfSignalAsync: (callbackId, dueTime, payload, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                scheduled.Add((callbackId, dueTime, payload));
                return Task.FromResult(new RuntimeCallbackLease("runtime-1", callbackId, 3, RuntimeCallbackBackend.InMemory));
            },
            cancelCallbackAsync: (lease, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                canceled.Add(lease);
                return Task.CompletedTask;
            });

        await capabilities.PublishAsync(new SimpleTextSignal { Value = "published" }, TopologyAudience.Parent, CancellationToken.None);
        await capabilities.SendToAsync("target-1", new SimpleTextSignal { Value = "sent" }, CancellationToken.None);
        await capabilities.PublishToSelfAsync(new SimpleTextSignal { Value = "self" }, CancellationToken.None);
        var lease = await capabilities.ScheduleSelfDurableSignalAsync(
            "cb-1",
            TimeSpan.FromSeconds(5),
            new SimpleTextSignal { Value = "scheduled" },
            CancellationToken.None);
        await capabilities.CancelDurableCallbackAsync(lease, CancellationToken.None);

        published.Should().ContainSingle(x => x.Audience == TopologyAudience.Parent);
        sent.Should().ContainSingle(x => x.TargetActorId == "target-1");
        selfPublished.Should().ContainSingle();
        scheduled.Should().ContainSingle(x => x.CallbackId == "cb-1" && x.DueTime == TimeSpan.FromSeconds(5));
        canceled.Should().ContainSingle(x => x.CallbackId == "cb-1");
    }

    [Fact]
    public async Task EvolutionAndProvisioningApis_ShouldDelegateToPorts()
    {
        var proposalPort = new RecordingProposalPort();
        var definitionPort = new RecordingDefinitionCommandPort();
        var provisioningPort = new RecordingRuntimeProvisioningPort();
        var runtimeCommandPort = new RecordingRuntimeCommandPort();
        var catalogCommandPort = new RecordingCatalogCommandPort();
        var aiCapability = new RecordingAICapability();
        var capabilities = CreateCapabilities(
            aiCapability: aiCapability,
            proposalPort: proposalPort,
            definitionCommandPort: definitionPort,
            runtimeProvisioningPort: provisioningPort,
            runtimeCommandPort: runtimeCommandPort,
            catalogCommandPort: catalogCommandPort);

        var aiResponse = await capabilities.AskAIAsync("hello", CancellationToken.None);
        var decision = await capabilities.ProposeScriptEvolutionAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-1",
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: "source",
                CandidateSourceHash: "hash",
                Reason: "reason"),
            CancellationToken.None);
        var definitionUpsert = await capabilities.UpsertScriptDefinitionAsync(
            "script-1",
            "rev-2",
            "source",
            "hash",
            "definition-1",
            CancellationToken.None);
        var runtimeActorId = await capabilities.SpawnScriptRuntimeAsync(
            "definition-1",
            "rev-2",
            "runtime-1",
            definitionUpsert.DefinitionSnapshot,
            CancellationToken.None);
        await capabilities.RunScriptInstanceAsync(
            "runtime-1",
            "run-1",
            Any.Pack(new SimpleTextCommand
            {
                CommandId = "command-1",
                Value = "input",
            }),
            "rev-2",
            "definition-1",
            "integration.requested",
            CancellationToken.None);
        await capabilities.PromoteRevisionAsync(
            "catalog-1",
            "script-1",
            "rev-2",
            "definition-1",
            "hash",
            "proposal-1",
            CancellationToken.None);
        await capabilities.RollbackRevisionAsync(
            "catalog-1",
            "script-1",
            "rev-1",
            "rollback",
            "proposal-2",
            CancellationToken.None);

        aiResponse.Should().Be("ok:hello");
        decision.Accepted.Should().BeTrue();
        definitionUpsert.ActorId.Should().Be("definition-1");
        runtimeActorId.Should().Be("runtime-1");
        proposalPort.LastProposal.Should().NotBeNull();
        proposalPort.LastProposal!.CandidateRevision.Should().Be("rev-2");
        definitionPort.Upserts.Should().ContainSingle();
        provisioningPort.EnsureCalls.Should().ContainSingle();
        provisioningPort.EnsureCalls[0].DefinitionSnapshot.Should().NotBeNull();
        provisioningPort.EnsureCalls[0].DefinitionSnapshot!.Revision.Should().Be("rev-2");
        runtimeCommandPort.RunCalls.Should().ContainSingle(x => x.RunId == "run-1");
        catalogCommandPort.PromoteCalls.Should().ContainSingle(x => x.Revision == "rev-2");
        catalogCommandPort.RollbackCalls.Should().ContainSingle(x => x.TargetRevision == "rev-1");
    }

    [Fact]
    public async Task SpawnScriptRuntimeAsync_ShouldUseCommandOwnedDefinitionSnapshot()
    {
        var provisioningPort = new RecordingRuntimeProvisioningPort();
        var proposalPort = new StaticProposalPort(new ScriptPromotionDecision(
            Accepted: true,
            ProposalId: "proposal-1",
            ScriptId: "script-1",
            BaseRevision: "rev-1",
            CandidateRevision: "rev-2",
            Status: "promoted",
            FailureReason: string.Empty,
            DefinitionActorId: "definition-1",
            CatalogActorId: "catalog-1",
            ValidationReport: new ScriptEvolutionValidationReport(true, []),
            DefinitionSnapshot: new ScriptDefinitionBindingSpec
            {
                ScriptId = "script-1",
                Revision = "rev-2",
                SourceHash = ScriptSources.UppercaseBehaviorHash,
                ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource(ScriptSources.UppercaseBehavior),
                StateTypeUrl = ScriptSources.UppercaseStateTypeUrl,
                ReadModelTypeUrl = ScriptSources.UppercaseReadModelTypeUrl,
            }));
        var capabilities = CreateCapabilities(
            proposalPort: proposalPort,
            runtimeProvisioningPort: provisioningPort);

        var decision = await capabilities.ProposeScriptEvolutionAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-1",
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: ScriptSources.UppercaseBehavior,
                CandidateSourceHash: ScriptSources.UppercaseBehaviorHash,
                Reason: "reason"),
            CancellationToken.None);
        var runtimeActorId = await capabilities.SpawnScriptRuntimeAsync(
            "definition-1",
            "rev-2",
            "runtime-1",
            decision.DefinitionSnapshot!,
            CancellationToken.None);

        decision.Accepted.Should().BeTrue();
        runtimeActorId.Should().Be("runtime-1");
        provisioningPort.EnsureCalls.Should().ContainSingle();
        provisioningPort.EnsureCalls[0].DefinitionSnapshot.Should().NotBeNull();
        provisioningPort.EnsureCalls[0].DefinitionSnapshot!.Revision.Should().Be("rev-2");
    }

    [Fact]
    public async Task SpawnScriptRuntimeAsync_ShouldNotReadSnapshot_WhenDecisionHasNoSnapshot()
    {
        var provisioningPort = new RecordingRuntimeProvisioningPort();
        var proposalPort = new StaticProposalPort(new ScriptPromotionDecision(
            Accepted: false,
            ProposalId: "proposal-1",
            ScriptId: "script-1",
            BaseRevision: "rev-1",
            CandidateRevision: "rev-2",
            Status: "rejected",
            FailureReason: "denied",
            DefinitionActorId: "definition-1",
            CatalogActorId: "catalog-1",
            ValidationReport: new ScriptEvolutionValidationReport(false, ["denied"])));
        var capabilities = CreateCapabilities(
            proposalPort: proposalPort,
            runtimeProvisioningPort: provisioningPort);

        await capabilities.ProposeScriptEvolutionAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-1",
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: ScriptSources.UppercaseBehavior,
                CandidateSourceHash: ScriptSources.UppercaseBehaviorHash,
                Reason: "reason"),
            CancellationToken.None);
        var runtimeActorId = await capabilities.SpawnScriptRuntimeAsync(
            "definition-1",
            "rev-2",
            "runtime-1",
            CreateDefinitionBindingSpec("rev-2"),
            CancellationToken.None);

        runtimeActorId.Should().Be("runtime-1");
        provisioningPort.EnsureCalls.Should().ContainSingle();
        provisioningPort.EnsureCalls[0].DefinitionSnapshot.Revision.Should().Be("rev-2");
    }

    [Fact]
    public async Task SpawnScriptRuntimeAsync_ShouldTrustProvidedSnapshot_AcrossCapabilityInstances()
    {
        var firstProvisioningPort = new RecordingRuntimeProvisioningPort();
        var secondProvisioningPort = new RecordingRuntimeProvisioningPort();
        var cachedSnapshot = new ScriptDefinitionSnapshot(
            "script-1",
            "rev-2",
            ScriptSources.UppercaseBehavior,
            ScriptSources.UppercaseBehaviorHash,
            ScriptSources.UppercaseStateTypeUrl,
            ScriptSources.UppercaseReadModelTypeUrl,
            "1",
            "schema-hash");

        var firstCapabilities = CreateCapabilities(
            definitionCommandPort: new StaticDefinitionCommandPort(
                new CoreScriptDefinitionUpsertResult(
                    "definition-1",
                    cachedSnapshot,
                    new ScriptingCommandAcceptedReceipt("definition-1", "command-1", "corr-1"))),
            runtimeProvisioningPort: firstProvisioningPort);

        await firstCapabilities.UpsertScriptDefinitionAsync(
            "script-1",
            "rev-2",
            ScriptSources.UppercaseBehavior,
            ScriptSources.UppercaseBehaviorHash,
            "definition-1",
            CancellationToken.None);
        await firstCapabilities.SpawnScriptRuntimeAsync(
            "definition-1",
            "rev-2",
            "runtime-1",
            cachedSnapshot.ToBindingSpec(),
            CancellationToken.None);

        firstProvisioningPort.EnsureCalls.Should().ContainSingle();
        firstProvisioningPort.EnsureCalls[0].DefinitionSnapshot.Revision.Should().Be("rev-2");

        var secondCapabilities = CreateCapabilities(
            runtimeProvisioningPort: secondProvisioningPort);

        await secondCapabilities.SpawnScriptRuntimeAsync(
            "definition-1",
            "rev-2",
            "runtime-2",
            cachedSnapshot.ToBindingSpec(),
            CancellationToken.None);

        secondProvisioningPort.EnsureCalls.Should().ContainSingle();
        secondProvisioningPort.EnsureCalls[0].DefinitionSnapshot.Revision.Should().Be("rev-2");
    }

    [Fact]
    public async Task SpawnScriptRuntimeAsync_ShouldUseProvidedSnapshot_WhenRevisionIsBlank()
    {
        var provisioningPort = new RecordingRuntimeProvisioningPort();
        var definitionPort = new StaticDefinitionCommandPort(
            new CoreScriptDefinitionUpsertResult(
                "definition-1",
                new ScriptDefinitionSnapshot(
                    "script-1",
                    string.Empty,
                    ScriptSources.UppercaseBehavior,
                    ScriptSources.UppercaseBehaviorHash,
                    ScriptSources.UppercaseStateTypeUrl,
                    ScriptSources.UppercaseReadModelTypeUrl,
                    "1",
                    "schema-hash"),
                new ScriptingCommandAcceptedReceipt("definition-1", "definition-command-1", "definition-correlation-1")));
        var capabilities = CreateCapabilities(
            definitionCommandPort: definitionPort,
            runtimeProvisioningPort: provisioningPort);

        await capabilities.UpsertScriptDefinitionAsync(
            "script-1",
            string.Empty,
            ScriptSources.UppercaseBehavior,
            ScriptSources.UppercaseBehaviorHash,
            "definition-1",
            CancellationToken.None);
        await capabilities.SpawnScriptRuntimeAsync(
            "definition-1",
            string.Empty,
            "runtime-1",
            definitionPort.Result.Snapshot.ToBindingSpec(),
            CancellationToken.None);

        provisioningPort.EnsureCalls.Should().ContainSingle();
        provisioningPort.EnsureCalls[0].DefinitionSnapshot.Should().NotBeNull();
        provisioningPort.EnsureCalls[0].DefinitionSnapshot!.Revision.Should().BeEmpty();
    }

    [Fact]
    public async Task SpawnScriptRuntimeAsync_ShouldUseProvidedProposalSnapshot_WhenDefinitionActorIdIsBlank()
    {
        var provisioningPort = new RecordingRuntimeProvisioningPort();
        var definitionPort = new StaticDefinitionCommandPort(
            new CoreScriptDefinitionUpsertResult(
                string.Empty,
                new ScriptDefinitionSnapshot(
                    "script-1",
                    "rev-1",
                    ScriptSources.UppercaseBehavior,
                    ScriptSources.UppercaseBehaviorHash,
                    ScriptSources.UppercaseStateTypeUrl,
                    ScriptSources.UppercaseReadModelTypeUrl,
                    "1",
                    "schema-hash"),
                new ScriptingCommandAcceptedReceipt("definition-1", "definition-command-1", "definition-correlation-1")));
        var capabilities = CreateCapabilities(
            definitionCommandPort: definitionPort,
            runtimeProvisioningPort: provisioningPort);

        await capabilities.UpsertScriptDefinitionAsync(
            "script-1",
            "rev-1",
            ScriptSources.UppercaseBehavior,
            ScriptSources.UppercaseBehaviorHash,
            null,
            CancellationToken.None);
        var runtimeActorId = await capabilities.SpawnScriptRuntimeAsync(
            "definition-1",
            "rev-1",
            "runtime-1",
            definitionPort.Result.Snapshot.ToBindingSpec(),
            CancellationToken.None);

        runtimeActorId.Should().Be("runtime-1");
        provisioningPort.EnsureCalls.Should().ContainSingle();
        provisioningPort.EnsureCalls[0].DefinitionSnapshot.Revision.Should().Be("rev-1");
    }

    [Fact]
    public async Task SpawnScriptRuntimeAsync_ShouldUseProvidedSnapshot_WhenDefinitionActorIdIsBlank()
    {
        var provisioningPort = new RecordingRuntimeProvisioningPort();
        var proposalPort = new StaticProposalPort(new ScriptPromotionDecision(
            Accepted: true,
            ProposalId: "proposal-1",
            ScriptId: "script-1",
            BaseRevision: "rev-1",
            CandidateRevision: "rev-2",
            Status: "promoted",
            FailureReason: string.Empty,
            DefinitionActorId: string.Empty,
            CatalogActorId: "catalog-1",
            ValidationReport: new ScriptEvolutionValidationReport(true, []),
            DefinitionSnapshot: new ScriptDefinitionBindingSpec
            {
                ScriptId = "script-1",
                Revision = "rev-2",
                SourceHash = ScriptSources.UppercaseBehaviorHash,
            }));
        var capabilities = CreateCapabilities(
            proposalPort: proposalPort,
            runtimeProvisioningPort: provisioningPort);

        await capabilities.ProposeScriptEvolutionAsync(
            new ScriptEvolutionProposal(
                ProposalId: "proposal-1",
                ScriptId: "script-1",
                BaseRevision: "rev-1",
                CandidateRevision: "rev-2",
                CandidateSource: ScriptSources.UppercaseBehavior,
                CandidateSourceHash: ScriptSources.UppercaseBehaviorHash,
                Reason: "reason"),
            CancellationToken.None);
        var runtimeActorId = await capabilities.SpawnScriptRuntimeAsync(
            "definition-1",
            "rev-2",
            "runtime-1",
            CreateDefinitionBindingSpec("rev-2"),
            CancellationToken.None);

        runtimeActorId.Should().Be("runtime-1");
        provisioningPort.EnsureCalls.Should().ContainSingle();
        provisioningPort.EnsureCalls[0].DefinitionSnapshot.Revision.Should().Be("rev-2");
    }

    [Fact]
    public void ScriptBehaviorRuntimeCapabilities_must_not_reintroduce_snapshot_side_read_or_cache()
    {
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(
            repoRoot,
            "src",
            "Aevatar.Scripting.Application",
            "Runtime",
            "ScriptBehaviorRuntimeCapabilities.cs");
        File.Exists(path).Should().BeTrue("source regression test needs the runtime capabilities source file: {0}", path);

        var source = File.ReadAllText(path);
        source.Should().NotContain(
            "IScriptDefinitionSnapshotPort",
            "deleted per iter113/cluster-2 - command directly carries typed ScriptDefinitionSnapshot");
        source.Should().NotContain(
            "ResolveDefinitionSnapshotAsync",
            "side-read deleted; runtime trusts command-supplied snapshot");
        source.Should().NotContain(
            "IScriptDefinitionSnapshotFactory",
            "factory injection deleted per Phase 9 r2 consensus");
    }

    [Fact]
    public async Task PromoteAndRollback_ShouldNormalizeBlankCatalogActorId()
    {
        var catalogCommandPort = new RecordingCatalogCommandPort();
        var capabilities = CreateCapabilities(catalogCommandPort: catalogCommandPort);

        await capabilities.PromoteRevisionAsync(
            " ",
            "script-1",
            "rev-2",
            "definition-1",
            "hash-1",
            "proposal-1",
            CancellationToken.None);
        await capabilities.RollbackRevisionAsync(
            " ",
            "script-1",
            "rev-1",
            "rollback",
            "proposal-2",
            CancellationToken.None);

        catalogCommandPort.PromoteCalls.Should().ContainSingle(x => x.CatalogActorId == null);
        catalogCommandPort.RollbackCalls.Should().ContainSingle(x => x.CatalogActorId == null);
    }

    [Fact]
    public void Constructor_ShouldThrow_ForNullDependencies()
    {
        var cases = new (string Name, Func<ScriptBehaviorRuntimeCapabilities> Create)[]
        {
            ("publishAsync", () => new ScriptBehaviorRuntimeCapabilities("run-1", "corr-1", null!, static (_, _, _) => Task.CompletedTask, static (_, _) => Task.CompletedTask, static (callbackId, _, _, _) => Task.FromResult(new RuntimeCallbackLease("runtime-1", callbackId, 1, RuntimeCallbackBackend.InMemory)), static (_, _) => Task.CompletedTask, new RecordingAICapability(), new RecordingProposalPort(), new RecordingDefinitionCommandPort(), new RecordingRuntimeProvisioningPort(), new RecordingRuntimeCommandPort(), new RecordingCatalogCommandPort())),
            ("sendToAsync", () => new ScriptBehaviorRuntimeCapabilities("run-1", "corr-1", static (_, _, _) => Task.CompletedTask, null!, static (_, _) => Task.CompletedTask, static (callbackId, _, _, _) => Task.FromResult(new RuntimeCallbackLease("runtime-1", callbackId, 1, RuntimeCallbackBackend.InMemory)), static (_, _) => Task.CompletedTask, new RecordingAICapability(), new RecordingProposalPort(), new RecordingDefinitionCommandPort(), new RecordingRuntimeProvisioningPort(), new RecordingRuntimeCommandPort(), new RecordingCatalogCommandPort())),
            ("publishToSelfAsync", () => new ScriptBehaviorRuntimeCapabilities("run-1", "corr-1", static (_, _, _) => Task.CompletedTask, static (_, _, _) => Task.CompletedTask, null!, static (callbackId, _, _, _) => Task.FromResult(new RuntimeCallbackLease("runtime-1", callbackId, 1, RuntimeCallbackBackend.InMemory)), static (_, _) => Task.CompletedTask, new RecordingAICapability(), new RecordingProposalPort(), new RecordingDefinitionCommandPort(), new RecordingRuntimeProvisioningPort(), new RecordingRuntimeCommandPort(), new RecordingCatalogCommandPort())),
            ("scheduleSelfSignalAsync", () => new ScriptBehaviorRuntimeCapabilities("run-1", "corr-1", static (_, _, _) => Task.CompletedTask, static (_, _, _) => Task.CompletedTask, static (_, _) => Task.CompletedTask, null!, static (_, _) => Task.CompletedTask, new RecordingAICapability(), new RecordingProposalPort(), new RecordingDefinitionCommandPort(), new RecordingRuntimeProvisioningPort(), new RecordingRuntimeCommandPort(), new RecordingCatalogCommandPort())),
            ("cancelCallbackAsync", () => new ScriptBehaviorRuntimeCapabilities("run-1", "corr-1", static (_, _, _) => Task.CompletedTask, static (_, _, _) => Task.CompletedTask, static (_, _) => Task.CompletedTask, static (callbackId, _, _, _) => Task.FromResult(new RuntimeCallbackLease("runtime-1", callbackId, 1, RuntimeCallbackBackend.InMemory)), null!, new RecordingAICapability(), new RecordingProposalPort(), new RecordingDefinitionCommandPort(), new RecordingRuntimeProvisioningPort(), new RecordingRuntimeCommandPort(), new RecordingCatalogCommandPort())),
            ("aiCapability", () => new ScriptBehaviorRuntimeCapabilities("run-1", "corr-1", static (_, _, _) => Task.CompletedTask, static (_, _, _) => Task.CompletedTask, static (_, _) => Task.CompletedTask, static (callbackId, _, _, _) => Task.FromResult(new RuntimeCallbackLease("runtime-1", callbackId, 1, RuntimeCallbackBackend.InMemory)), static (_, _) => Task.CompletedTask, null!, new RecordingProposalPort(), new RecordingDefinitionCommandPort(), new RecordingRuntimeProvisioningPort(), new RecordingRuntimeCommandPort(), new RecordingCatalogCommandPort())),
            ("proposalPort", () => new ScriptBehaviorRuntimeCapabilities("run-1", "corr-1", static (_, _, _) => Task.CompletedTask, static (_, _, _) => Task.CompletedTask, static (_, _) => Task.CompletedTask, static (callbackId, _, _, _) => Task.FromResult(new RuntimeCallbackLease("runtime-1", callbackId, 1, RuntimeCallbackBackend.InMemory)), static (_, _) => Task.CompletedTask, new RecordingAICapability(), null!, new RecordingDefinitionCommandPort(), new RecordingRuntimeProvisioningPort(), new RecordingRuntimeCommandPort(), new RecordingCatalogCommandPort())),
            ("definitionCommandPort", () => new ScriptBehaviorRuntimeCapabilities("run-1", "corr-1", static (_, _, _) => Task.CompletedTask, static (_, _, _) => Task.CompletedTask, static (_, _) => Task.CompletedTask, static (callbackId, _, _, _) => Task.FromResult(new RuntimeCallbackLease("runtime-1", callbackId, 1, RuntimeCallbackBackend.InMemory)), static (_, _) => Task.CompletedTask, new RecordingAICapability(), new RecordingProposalPort(), null!, new RecordingRuntimeProvisioningPort(), new RecordingRuntimeCommandPort(), new RecordingCatalogCommandPort())),
            ("runtimeProvisioningPort", () => new ScriptBehaviorRuntimeCapabilities("run-1", "corr-1", static (_, _, _) => Task.CompletedTask, static (_, _, _) => Task.CompletedTask, static (_, _) => Task.CompletedTask, static (callbackId, _, _, _) => Task.FromResult(new RuntimeCallbackLease("runtime-1", callbackId, 1, RuntimeCallbackBackend.InMemory)), static (_, _) => Task.CompletedTask, new RecordingAICapability(), new RecordingProposalPort(), new RecordingDefinitionCommandPort(), null!, new RecordingRuntimeCommandPort(), new RecordingCatalogCommandPort())),
            ("runtimeCommandPort", () => new ScriptBehaviorRuntimeCapabilities("run-1", "corr-1", static (_, _, _) => Task.CompletedTask, static (_, _, _) => Task.CompletedTask, static (_, _) => Task.CompletedTask, static (callbackId, _, _, _) => Task.FromResult(new RuntimeCallbackLease("runtime-1", callbackId, 1, RuntimeCallbackBackend.InMemory)), static (_, _) => Task.CompletedTask, new RecordingAICapability(), new RecordingProposalPort(), new RecordingDefinitionCommandPort(), new RecordingRuntimeProvisioningPort(), null!, new RecordingCatalogCommandPort())),
            ("catalogCommandPort", () => new ScriptBehaviorRuntimeCapabilities("run-1", "corr-1", static (_, _, _) => Task.CompletedTask, static (_, _, _) => Task.CompletedTask, static (_, _) => Task.CompletedTask, static (callbackId, _, _, _) => Task.FromResult(new RuntimeCallbackLease("runtime-1", callbackId, 1, RuntimeCallbackBackend.InMemory)), static (_, _) => Task.CompletedTask, new RecordingAICapability(), new RecordingProposalPort(), new RecordingDefinitionCommandPort(), new RecordingRuntimeProvisioningPort(), new RecordingRuntimeCommandPort(), null!)),
        };

        foreach (var testCase in cases)
        {
            testCase.Create.Should().Throw<ArgumentNullException>()
                .Which.ParamName.Should().Be(testCase.Name);
        }
    }

    private static ScriptBehaviorRuntimeCapabilities CreateCapabilities(
        IAICapability? aiCapability = null,
        IScriptEvolutionProposalPort? proposalPort = null,
        IScriptDefinitionCommandPort? definitionCommandPort = null,
        IScriptRuntimeProvisioningPort? runtimeProvisioningPort = null,
        IScriptRuntimeCommandPort? runtimeCommandPort = null,
        IScriptCatalogCommandPort? catalogCommandPort = null,
        Func<IMessage, TopologyAudience, CancellationToken, Task>? publishAsync = null,
        Func<string, IMessage, CancellationToken, Task>? sendToAsync = null,
        Func<IMessage, CancellationToken, Task>? publishToSelfAsync = null,
        Func<string, TimeSpan, IMessage, CancellationToken, Task<RuntimeCallbackLease>>? scheduleSelfSignalAsync = null,
        Func<RuntimeCallbackLease, CancellationToken, Task>? cancelCallbackAsync = null)
    {
        return new ScriptBehaviorRuntimeCapabilities(
            runId: "run-1",
            correlationId: "corr-1",
            publishAsync: publishAsync ?? ((_, _, _) => Task.CompletedTask),
            sendToAsync: sendToAsync ?? ((_, _, _) => Task.CompletedTask),
            publishToSelfAsync: publishToSelfAsync ?? ((_, _) => Task.CompletedTask),
            scheduleSelfSignalAsync: scheduleSelfSignalAsync ?? ((callbackId, _, _, _) =>
                Task.FromResult(new RuntimeCallbackLease("runtime-1", callbackId, 1, RuntimeCallbackBackend.InMemory))),
            cancelCallbackAsync: cancelCallbackAsync ?? ((_, _) => Task.CompletedTask),
            aiCapability: aiCapability ?? new RecordingAICapability(),
            proposalPort: proposalPort ?? new RecordingProposalPort(),
            definitionCommandPort: definitionCommandPort ?? new RecordingDefinitionCommandPort(),
            runtimeProvisioningPort: runtimeProvisioningPort ?? new RecordingRuntimeProvisioningPort(),
            runtimeCommandPort: runtimeCommandPort ?? new RecordingRuntimeCommandPort(),
            catalogCommandPort: catalogCommandPort ?? new RecordingCatalogCommandPort());
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var marker = Path.Combine(dir.FullName, "aevatar.slnx");
            if (File.Exists(marker))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Cannot locate repository root from test base directory.");
    }

    private static ScriptDefinitionBindingSpec CreateDefinitionBindingSpec(string revision) =>
        new()
        {
            ScriptId = "script-1",
            Revision = revision,
            SourceHash = ScriptSources.UppercaseBehaviorHash,
            ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource(ScriptSources.UppercaseBehavior),
            StateTypeUrl = ScriptSources.UppercaseStateTypeUrl,
            ReadModelTypeUrl = ScriptSources.UppercaseReadModelTypeUrl,
            ReadModelSchemaVersion = "1",
            ReadModelSchemaHash = "schema-hash",
        };

    private sealed class RecordingAICapability : IAICapability
    {
        public Task<string> AskAsync(string runId, string correlationId, string prompt, CancellationToken ct)
        {
            runId.Should().Be("run-1");
            correlationId.Should().Be("corr-1");
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("ok:" + prompt);
        }
    }

    private sealed class RecordingExecutionProjectionPort
        : IScriptExecutionProjectionPort
    {
        public bool ProjectionEnabled => true;
        public List<string> EnsureCalls { get; } = [];

        public Task<IScriptExecutionProjectionLease?> EnsureActorProjectionAsync(
            string actorId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            EnsureCalls.Add(actorId);
            return Task.FromResult<IScriptExecutionProjectionLease?>(new RecordingLease(actorId));
        }

        public Task<IScriptExecutionProjectionLease?> EnsureProjectionAsync(
            string actorId,
            string projectionName,
            string input,
            string commandId,
            CancellationToken ct = default)
        {
            _ = projectionName;
            _ = input;
            _ = commandId;
            return EnsureActorProjectionAsync(actorId, ct);
        }

        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            IScriptExecutionProjectionLease lease,
            IEventSink<EventEnvelope> sink,
            CancellationToken ct = default)
        {
            _ = lease;
            _ = sink;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IAsyncDisposable?>(null);
        }
    public Task DetachLiveSinkAsync(
        IAsyncDisposable? liveSinkLease,
        CancellationToken ct = default) =>
        Task.CompletedTask;

        public Task ReleaseActorProjectionAsync(
            IScriptExecutionProjectionLease lease,
            CancellationToken ct = default)
        {
            _ = lease;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        private sealed record RecordingLease(string ActorId) : IScriptExecutionProjectionLease;
    }

    private sealed class RecordingReadModelQueryPort : IScriptReadModelQueryPort
    {
        public string? LastActorId { get; private set; }

        public Task<ScriptReadModelSnapshot?> GetSnapshotAsync(string actorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastActorId = actorId;
            return Task.FromResult<ScriptReadModelSnapshot?>(new ScriptReadModelSnapshot(
                ActorId: actorId,
                ScriptId: "script-1",
                DefinitionActorId: "definition-1",
                Revision: "rev-1",
                ReadModelTypeUrl: Any.Pack(new SimpleTextReadModel()).TypeUrl,
                ReadModelPayload: Any.Pack(new SimpleTextReadModel
                {
                    HasValue = true,
                    Value = "snapshot",
                }),
                StateVersion: 1,
                LastEventId: "evt-1",
                UpdatedAt: DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<ScriptReadModelSnapshot>> ListSnapshotsAsync(int take = 200, CancellationToken ct = default)
        {
            _ = take;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ScriptReadModelSnapshot>>([]);
        }

        public Task<Any?> ExecuteDeclaredQueryAsync(string actorId, Any queryPayload, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastActorId = actorId;
            queryPayload.Is(SimpleTextQueryRequested.Descriptor).Should().BeTrue();
            var requested = queryPayload.Unpack<SimpleTextQueryRequested>();
            return Task.FromResult<Any?>(Any.Pack(new SimpleTextQueryResponded
            {
                RequestId = requested.RequestId ?? string.Empty,
                Current = new SimpleTextReadModel
                {
                    HasValue = true,
                    Value = "query-result",
                },
            }));
        }
    }

    private sealed class RecordingProposalPort : IScriptEvolutionProposalPort
    {
        public ScriptEvolutionProposal? LastProposal { get; private set; }

        public Task<ScriptPromotionDecision> ProposeAsync(ScriptEvolutionProposal proposal, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            LastProposal = proposal;
            return Task.FromResult(new ScriptPromotionDecision(
                Accepted: true,
                ProposalId: proposal.ProposalId,
                ScriptId: proposal.ScriptId,
                BaseRevision: proposal.BaseRevision,
                CandidateRevision: proposal.CandidateRevision,
                Status: "promoted",
                FailureReason: string.Empty,
                DefinitionActorId: "definition-1",
                CatalogActorId: "catalog-1",
                ValidationReport: new ScriptEvolutionValidationReport(true, [])));
        }
    }

    private sealed class RecordingDefinitionSnapshotPort : IScriptDefinitionSnapshotPort
    {
        public Task<ScriptDefinitionSnapshot> GetRequiredAsync(
            string definitionActorId,
            string requestedRevision,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ScriptDefinitionSnapshot(
                "script-" + definitionActorId,
                requestedRevision,
                ScriptSources.UppercaseBehavior,
                ScriptSources.UppercaseBehaviorHash,
                ScriptSources.UppercaseStateTypeUrl,
                ScriptSources.UppercaseReadModelTypeUrl,
                "1",
                "schema-hash"));
        }
    }

    private sealed class CountingDefinitionSnapshotPort : IScriptDefinitionSnapshotPort
    {
        public int RequestCount { get; private set; }

        public Task<ScriptDefinitionSnapshot> GetRequiredAsync(
            string definitionActorId,
            string requestedRevision,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(new ScriptDefinitionSnapshot(
                "script-" + definitionActorId,
                requestedRevision,
                ScriptSources.UppercaseBehavior,
                ScriptSources.UppercaseBehaviorHash,
                ScriptSources.UppercaseStateTypeUrl,
                ScriptSources.UppercaseReadModelTypeUrl,
                "1",
                "schema-hash"));
        }
    }

    private sealed class StaticProposalPort(ScriptPromotionDecision decision) : IScriptEvolutionProposalPort
    {
        public Task<ScriptPromotionDecision> ProposeAsync(ScriptEvolutionProposal proposal, CancellationToken ct)
        {
            _ = proposal;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(decision);
        }
    }

    private sealed class RecordingDefinitionCommandPort : IScriptDefinitionCommandPort
    {
        public List<(string ScriptId, string Revision, string? DefinitionActorId)> Upserts { get; } = [];

        public Task<ScriptDefinitionUpsertResult> UpsertDefinitionWithSnapshotAsync(
            string scriptId,
            string scriptRevision,
            ScriptPackageSpec scriptPackage,
            string? definitionActorId,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add((scriptId, scriptRevision, definitionActorId));
            var sourceHash = ScriptPackageModel.ComputePackageHash(scriptPackage);
            var actorId = definitionActorId ?? "definition-created";
            return Task.FromResult(new CoreScriptDefinitionUpsertResult(
                actorId,
                new ScriptDefinitionSnapshot(
                    scriptId,
                    scriptRevision,
                    sourceHash,
                    scriptPackage,
                    "type.googleapis.com/example.State",
                    "type.googleapis.com/example.ReadModel",
                    "1",
                    "schema-hash-1"),
                new ScriptingCommandAcceptedReceipt(actorId, "definition-command-1", "definition-correlation-1")));
        }
    }

    private sealed class StaticDefinitionCommandPort(CoreScriptDefinitionUpsertResult result) : IScriptDefinitionCommandPort
    {
        public CoreScriptDefinitionUpsertResult Result => result;

        public Task<CoreScriptDefinitionUpsertResult> UpsertDefinitionWithSnapshotAsync(
            string scriptId,
            string scriptRevision,
            ScriptPackageSpec scriptPackage,
            string? definitionActorId,
            CancellationToken ct)
        {
            _ = scriptId;
            _ = scriptRevision;
            _ = scriptPackage;
            _ = definitionActorId;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingRuntimeProvisioningPort : IScriptRuntimeProvisioningPort
    {
        public List<(string DefinitionActorId, string Revision, string? RuntimeActorId, ScriptDefinitionSnapshot DefinitionSnapshot)> EnsureCalls { get; } = [];

        public Task<string> EnsureRuntimeAsync(
            string definitionActorId,
            string scriptRevision,
            string? runtimeActorId,
            ScriptDefinitionSnapshot definitionSnapshot,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            EnsureCalls.Add((definitionActorId, scriptRevision, runtimeActorId, definitionSnapshot));
            return Task.FromResult(runtimeActorId ?? "runtime-created");
        }
    }

    private sealed class RecordingRuntimeCommandPort : IScriptRuntimeCommandPort
    {
        public List<(string RuntimeActorId, string RunId, string Revision, string DefinitionActorId, string RequestedEventType, string? CompletionNotificationDeliveryId, long CompletionNotificationExpiresAtUnixMs)> RunCalls { get; } = [];

        public Task RunRuntimeAsync(
            string runtimeActorId,
            string runId,
            string commandId,
            string correlationId,
            Any? inputPayload,
            string scriptRevision,
            string definitionActorId,
            string requestedEventType,
            string? scopeId,
            string? completionNotificationActorId,
            string? completionNotificationDeliveryId,
            long completionNotificationExpiresAtUnixMs,
            CancellationToken ct)
        {
            _ = inputPayload;
            _ = commandId;
            _ = correlationId;
            _ = scopeId;
            _ = completionNotificationActorId;
            ct.ThrowIfCancellationRequested();
            RunCalls.Add((
                runtimeActorId,
                runId,
                scriptRevision,
                definitionActorId,
                requestedEventType,
                completionNotificationDeliveryId,
                completionNotificationExpiresAtUnixMs));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCatalogCommandPort : IScriptCatalogCommandPort
    {
        public List<(string? CatalogActorId, string ScriptId, string Revision, string ProposalId)> PromoteCalls { get; } = [];
        public List<(string? CatalogActorId, string ScriptId, string TargetRevision, string ProposalId)> RollbackCalls { get; } = [];

        public Task<ScriptingCommandAcceptedReceipt> PromoteCatalogRevisionAsync(
            string? catalogActorId,
            string scriptId,
            string expectedBaseRevision,
            string revision,
            string definitionActorId,
            string sourceHash,
            string proposalId,
            CancellationToken ct)
        {
            _ = expectedBaseRevision;
            _ = definitionActorId;
            _ = sourceHash;
            ct.ThrowIfCancellationRequested();
            PromoteCalls.Add((catalogActorId, scriptId, revision, proposalId));
            return Task.FromResult(new ScriptingCommandAcceptedReceipt(
                catalogActorId ?? "catalog-created",
                "catalog-command-1",
                proposalId));
        }

        public Task<ScriptingCommandAcceptedReceipt> RollbackCatalogRevisionAsync(
            string? catalogActorId,
            string scriptId,
            string targetRevision,
            string reason,
            string proposalId,
            string expectedCurrentRevision,
            CancellationToken ct)
        {
            _ = reason;
            _ = expectedCurrentRevision;
            ct.ThrowIfCancellationRequested();
            RollbackCalls.Add((catalogActorId, scriptId, targetRevision, proposalId));
            return Task.FromResult(new ScriptingCommandAcceptedReceipt(
                catalogActorId ?? "catalog-created",
                "catalog-rollback-command-1",
                proposalId));
        }
    }

}
