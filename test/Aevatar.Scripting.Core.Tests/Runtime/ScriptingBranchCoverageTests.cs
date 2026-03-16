using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Abstractions.Evolution;
using Aevatar.Scripting.Abstractions.Queries;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Application.Runtime;
using Aevatar.Scripting.Core;
using Aevatar.Scripting.Core.AI;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;
using Aevatar.Scripting.Infrastructure.Compilation;
using Aevatar.Scripting.Infrastructure.Ports;
using Aevatar.Scripting.Projection.ReadPorts;
using Aevatar.Scripting.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using SystemType = System.Type;

namespace Aevatar.Scripting.Core.Tests.Runtime;

public sealed class ScriptDefinitionBindingSpecConversionsTests
{
    [Fact]
    public void ToBindingSpec_ShouldThrow_WhenSnapshotIsNull()
    {
        Action act = () => ScriptDefinitionBindingSpecConversions.ToBindingSpec(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToBindingSpec_ShouldCloneStructuredMembers()
    {
        var package = ScriptPackageSpecExtensions.CreateSingleSource(ScriptSources.StructuredProfileBehavior);
        var semantics = new ScriptRuntimeSemanticsSpec
        {
            Messages =
            {
                new ScriptMessageSemanticsSpec
                {
                    TypeUrl = ScriptSources.StructuredProfileCommandTypeUrl,
                    Kind = ScriptMessageKind.Command,
                },
            },
        };
        var snapshot = new ScriptDefinitionSnapshot(
            "script-1",
            "rev-1",
            ScriptSources.StructuredProfileBehavior,
            ScriptSources.StructuredProfileBehaviorHash,
            package,
            ScriptSources.StructuredProfileStateTypeUrl,
            ScriptSources.StructuredProfileReadModelTypeUrl,
            "3",
            "schema-hash-1",
            ByteString.CopyFromUtf8("descriptor"),
            "Example.State",
            "Example.ReadModel",
            semantics);

        var spec = snapshot.ToBindingSpec();

        spec.ScriptId.Should().Be("script-1");
        spec.Revision.Should().Be("rev-1");
        spec.ScriptPackage.Should().NotBeSameAs(package);
        spec.RuntimeSemantics.Should().NotBeSameAs(semantics);

        package.EntryBehaviorTypeName = "Changed";
        semantics.Messages[0].TypeUrl = "changed";
        spec.ScriptPackage.EntryBehaviorTypeName.Should().NotBe("Changed");
        spec.RuntimeSemantics.Messages[0].TypeUrl.Should().Be(ScriptSources.StructuredProfileCommandTypeUrl);
    }

    [Fact]
    public void ToBindingSpec_ShouldUseEmptyDefaults_WhenOptionalMembersAreMissing()
    {
        var snapshot = new ScriptDefinitionSnapshot(
            "script-2",
            "rev-2",
            "source",
            "hash",
            null!,
            "state",
            "read-model",
            string.Empty,
            string.Empty,
            ByteString.Empty,
            string.Empty,
            string.Empty,
            null);

        var spec = snapshot.ToBindingSpec();

        spec.ScriptPackage.Should().NotBeNull();
        spec.RuntimeSemantics.Should().NotBeNull();
        spec.ScriptPackage.CsharpSources.Should().BeEmpty();
        spec.RuntimeSemantics.Messages.Should().BeEmpty();
    }

    [Fact]
    public void ToSnapshot_ShouldReturnNull_WhenSpecIsNull()
    {
        ScriptDefinitionBindingSpec? spec = null;

        spec.ToSnapshot().Should().BeNull();
    }

    [Fact]
    public void ToSnapshot_ShouldCloneStructuredMembers_AndNormalizeOptionalDefaults()
    {
        var spec = new ScriptDefinitionBindingSpec
        {
            ScriptId = "script-3",
            Revision = "rev-3",
            SourceText = ScriptSources.UppercaseBehavior,
            SourceHash = ScriptSources.UppercaseBehaviorHash,
            ScriptPackage = ScriptPackageSpecExtensions.CreateSingleSource(ScriptSources.UppercaseBehavior),
            StateTypeUrl = ScriptSources.UppercaseStateTypeUrl,
            ReadModelTypeUrl = ScriptSources.UppercaseReadModelTypeUrl,
            ReadModelSchemaVersion = "1",
            ReadModelSchemaHash = "schema-hash-3",
            ProtocolDescriptorSet = ByteString.CopyFromUtf8("proto"),
            StateDescriptorFullName = "Example.State",
            ReadModelDescriptorFullName = "Example.ReadModel",
            RuntimeSemantics = new ScriptRuntimeSemanticsSpec
            {
                Queries =
                {
                    new ScriptQuerySemanticsSpec
                    {
                        QueryTypeUrl = ScriptSources.UppercaseQueryTypeUrl,
                        ResultTypeUrl = ScriptSources.UppercaseQueryResultTypeUrl,
                    },
                },
            },
        };

        var snapshot = spec.ToSnapshot();

        snapshot.Should().NotBeNull();
        snapshot!.ScriptPackage.Should().NotBeSameAs(spec.ScriptPackage);
        snapshot.RuntimeSemantics.Should().NotBeSameAs(spec.RuntimeSemantics);

        spec.ScriptPackage.EntryBehaviorTypeName = "Changed";
        spec.RuntimeSemantics.Queries[0].ResultTypeUrl = "changed";
        snapshot.ScriptPackage.EntryBehaviorTypeName.Should().NotBe("Changed");
        snapshot.RuntimeSemantics!.Queries[0].ResultTypeUrl.Should().Be(ScriptSources.UppercaseQueryResultTypeUrl);
    }
}

public sealed class ScriptEvolutionInteractionCompletionTests
{
    [Fact]
    public void Pending_ShouldExposeStableDefaults()
    {
        var pending = ScriptEvolutionInteractionCompletion.Pending;

        pending.Accepted.Should().BeFalse();
        pending.Status.Should().Be("pending");
        pending.ValidationReport.Should().BeSameAs(ScriptEvolutionValidationReport.Empty);
        pending.DefinitionSnapshot.Should().BeNull();
    }

    [Fact]
    public void ToPromotionDecision_ShouldThrow_WhenProposalIsNull()
    {
        var completion = ScriptEvolutionInteractionCompletion.Pending;

        Action act = () => completion.ToPromotionDecision(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToPromotionDecision_ShouldNormalizeMissingFields_AndCloneSnapshot()
    {
        var completion = new ScriptEvolutionInteractionCompletion(
            Accepted: true,
            ProposalId: " ",
            Status: null!,
            FailureReason: null!,
            DefinitionActorId: null!,
            CatalogActorId: null!,
            ValidationReport: null!,
            DefinitionSnapshot: new ScriptDefinitionBindingSpec
            {
                ScriptId = "script-1",
                Revision = "rev-2",
                SourceHash = "hash-2",
            });
        var proposal = new ScriptEvolutionProposal(
            ProposalId: "proposal-1",
            ScriptId: "script-1",
            BaseRevision: "rev-1",
            CandidateRevision: "rev-2",
            CandidateSource: "source",
            CandidateSourceHash: "hash-2",
            Reason: "reason");

        var decision = completion.ToPromotionDecision(proposal);

        decision.Accepted.Should().BeTrue();
        decision.ProposalId.Should().Be("proposal-1");
        decision.ScriptId.Should().Be("script-1");
        decision.BaseRevision.Should().Be("rev-1");
        decision.CandidateRevision.Should().Be("rev-2");
        decision.Status.Should().BeEmpty();
        decision.FailureReason.Should().BeEmpty();
        decision.DefinitionActorId.Should().BeEmpty();
        decision.CatalogActorId.Should().BeEmpty();
        decision.ValidationReport.Should().BeSameAs(ScriptEvolutionValidationReport.Empty);
        decision.DefinitionSnapshot.Should().NotBeSameAs(completion.DefinitionSnapshot);
    }

    [Fact]
    public void FromDecision_ShouldThrow_WhenDecisionIsNull()
    {
        Action act = () => ScriptEvolutionInteractionCompletion.FromDecision(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FromDecision_ShouldNormalizeMissingFields_AndCloneSnapshot()
    {
        var decision = new ScriptPromotionDecision(
            Accepted: true,
            ProposalId: null!,
            ScriptId: "script-2",
            BaseRevision: "rev-1",
            CandidateRevision: "rev-2",
            Status: null!,
            FailureReason: null!,
            DefinitionActorId: null!,
            CatalogActorId: null!,
            ValidationReport: null!,
            DefinitionSnapshot: new ScriptDefinitionBindingSpec
            {
                ScriptId = "script-2",
                Revision = "rev-2",
                SourceHash = "hash-2",
            });

        var completion = ScriptEvolutionInteractionCompletion.FromDecision(decision);

        completion.Accepted.Should().BeTrue();
        completion.ProposalId.Should().BeEmpty();
        completion.Status.Should().BeEmpty();
        completion.FailureReason.Should().BeEmpty();
        completion.DefinitionActorId.Should().BeEmpty();
        completion.CatalogActorId.Should().BeEmpty();
        completion.ValidationReport.Should().BeSameAs(ScriptEvolutionValidationReport.Empty);
        completion.DefinitionSnapshot.Should().NotBeSameAs(decision.DefinitionSnapshot);
    }

    [Fact]
    public void ValueEquality_ShouldHandleEqualDifferentAndNullInstances()
    {
        var left = new ScriptEvolutionInteractionCompletion(
            Accepted: true,
            ProposalId: "proposal-1",
            Status: "promoted",
            FailureReason: string.Empty,
            DefinitionActorId: "definition-1",
            CatalogActorId: "catalog-1",
            ValidationReport: new ScriptEvolutionValidationReport(true, ["ok"]),
            DefinitionSnapshot: new ScriptDefinitionBindingSpec
            {
                ScriptId = "script-1",
                Revision = "rev-2",
            });
        var equal = (left with { })!;
        var different = left with { Status = "rejected" };

        (left == equal).Should().BeTrue();
        (left != equal).Should().BeFalse();
        left.Equals(equal).Should().BeTrue();
        left.Equals((object?)equal).Should().BeTrue();
        left.Equals((object?)different).Should().BeFalse();
        left.Equals((object?)null).Should().BeFalse();
        var equalHashCode = equal!.GetHashCode();
        var leftHashCode = left.GetHashCode();
        leftHashCode.Should().Be(equalHashCode);
        left.ToString().Should().Contain("proposal-1");
    }
}

public sealed class ScriptEvolutionCompletionPolicyTests
{
    [Fact]
    public void IncompleteCompletion_ShouldReturnPendingInstance()
    {
        var policy = new ScriptEvolutionCompletionPolicy();

        policy.IncompleteCompletion.Should().BeSameAs(ScriptEvolutionInteractionCompletion.Pending);
    }

    [Fact]
    public void TryResolve_ShouldThrow_WhenEventIsNull()
    {
        var policy = new ScriptEvolutionCompletionPolicy();

        Action act = () => policy.TryResolve(null!, out _);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void TryResolve_ShouldMapEventPayload_AndCloneDefinitionSnapshot()
    {
        var policy = new ScriptEvolutionCompletionPolicy();
        var evt = new ScriptEvolutionSessionCompletedEvent
        {
            Accepted = true,
            ProposalId = "proposal-1",
            Status = "promoted",
            FailureReason = string.Empty,
            DefinitionActorId = "definition-1",
            CatalogActorId = "catalog-1",
            Diagnostics = { "compiled", "promoted" },
            DefinitionSnapshot = new ScriptDefinitionBindingSpec
            {
                ScriptId = "script-1",
                Revision = "rev-2",
                SourceHash = "hash-2",
            },
        };

        var resolved = policy.TryResolve(evt, out var completion);

        resolved.Should().BeTrue();
        completion.Accepted.Should().BeTrue();
        completion.ProposalId.Should().Be("proposal-1");
        completion.Status.Should().Be("promoted");
        completion.ValidationReport.Diagnostics.Should().Equal("compiled", "promoted");
        completion.DefinitionSnapshot.Should().NotBeSameAs(evt.DefinitionSnapshot);
    }

    [Fact]
    public void TryResolve_ShouldNormalizeMissingStrings()
    {
        var policy = new ScriptEvolutionCompletionPolicy();
        var evt = new ScriptEvolutionSessionCompletedEvent();

        var resolved = policy.TryResolve(evt, out var completion);

        resolved.Should().BeTrue();
        completion.ProposalId.Should().BeEmpty();
        completion.Status.Should().BeEmpty();
        completion.FailureReason.Should().BeEmpty();
        completion.DefinitionActorId.Should().BeEmpty();
        completion.CatalogActorId.Should().BeEmpty();
        completion.DefinitionSnapshot.Should().BeNull();
    }
}

public sealed class ScriptingCommandStartErrorTests
{
    [Fact]
    public void InvalidArgument_ShouldNormalizeNullInputs()
    {
        var error = ScriptingCommandStartError.InvalidArgument(null!, null!);

        error.Code.Should().Be(ScriptingCommandStartErrorCode.InvalidArgument);
        error.FieldName.Should().BeEmpty();
        error.ActorId.Should().BeEmpty();
        error.Message.Should().BeEmpty();
    }

    [Fact]
    public void ActorNotFound_ShouldNormalizeNullInputs()
    {
        var error = ScriptingCommandStartError.ActorNotFound(null!, null!);

        error.Code.Should().Be(ScriptingCommandStartErrorCode.ActorNotFound);
        error.FieldName.Should().BeEmpty();
        error.ActorId.Should().BeEmpty();
        error.Message.Should().BeEmpty();
    }

    [Fact]
    public void ToException_ShouldMapInvalidArgumentError()
    {
        var error = new ScriptingCommandStartError(
            ScriptingCommandStartErrorCode.InvalidArgument,
            "field-1",
            string.Empty,
            string.Empty);

        var exception = error.ToException();

        exception.Should().BeOfType<ArgumentException>()
            .Which.ParamName.Should().Be("field-1");
    }

    [Fact]
    public void ToException_ShouldMapActorNotFoundError()
    {
        var error = new ScriptingCommandStartError(
            ScriptingCommandStartErrorCode.ActorNotFound,
            string.Empty,
            "actor-1",
            string.Empty);

        var exception = error.ToException();

        exception.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("actor-1");
    }

    [Fact]
    public void ToException_ShouldMapFallbackError()
    {
        var error = new ScriptingCommandStartError(
            ScriptingCommandStartErrorCode.None,
            string.Empty,
            string.Empty,
            string.Empty);

        var exception = error.ToException();

        exception.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("dispatch failed");
    }
}

public sealed class ScriptBehaviorRuntimeCapabilityFactoryTests
{
    [Fact]
    public async Task Create_ShouldProduceCapabilities_ThatUseProvidedContext()
    {
        var ai = new RecordingAICapability();
        var factory = new ScriptBehaviorRuntimeCapabilityFactory(
            ai,
            new RecordingActorRuntime(),
            new RecordingExecutionProjectionPort(),
            new RecordingReadModelQueryPort(),
            new RecordingDefinitionSnapshotPort(),
            new RecordingProposalPort(),
            new RecordingDefinitionCommandPort(),
            new RecordingRuntimeProvisioningPort(),
            new RecordingRuntimeCommandPort(),
            new RecordingCatalogCommandPort(),
            new RecordingAuthorityReadModelActivationPort());

        var capabilities = factory.Create(
            new ScriptBehaviorRuntimeCapabilityContext(
                "actor-1",
                "script-1",
                "rev-1",
                "definition-1",
                "run-1",
                "corr-1"),
            static (_, _, _) => Task.CompletedTask,
            static (_, _, _) => Task.CompletedTask,
            static (_, _) => Task.CompletedTask,
            static (callbackId, _, _, _) => Task.FromResult(new RuntimeCallbackLease("runtime-1", callbackId, 1, RuntimeCallbackBackend.InMemory)),
            static (_, _) => Task.CompletedTask);

        var answer = await capabilities.AskAIAsync("hello", CancellationToken.None);

        answer.Should().Be("run-1:corr-1:hello");
    }

    [Fact]
    public void Constructor_ShouldThrow_ForNullDependencies()
    {
        var cases = new (string Name, Func<ScriptBehaviorRuntimeCapabilityFactory> Create)[]
        {
            ("aiCapability", () => new ScriptBehaviorRuntimeCapabilityFactory(null!, new RecordingActorRuntime(), new RecordingExecutionProjectionPort(), new RecordingReadModelQueryPort(), new RecordingDefinitionSnapshotPort(), new RecordingProposalPort(), new RecordingDefinitionCommandPort(), new RecordingRuntimeProvisioningPort(), new RecordingRuntimeCommandPort(), new RecordingCatalogCommandPort(), new RecordingAuthorityReadModelActivationPort())),
            ("runtime", () => new ScriptBehaviorRuntimeCapabilityFactory(new RecordingAICapability(), null!, new RecordingExecutionProjectionPort(), new RecordingReadModelQueryPort(), new RecordingDefinitionSnapshotPort(), new RecordingProposalPort(), new RecordingDefinitionCommandPort(), new RecordingRuntimeProvisioningPort(), new RecordingRuntimeCommandPort(), new RecordingCatalogCommandPort(), new RecordingAuthorityReadModelActivationPort())),
            ("executionProjectionPort", () => new ScriptBehaviorRuntimeCapabilityFactory(new RecordingAICapability(), new RecordingActorRuntime(), null!, new RecordingReadModelQueryPort(), new RecordingDefinitionSnapshotPort(), new RecordingProposalPort(), new RecordingDefinitionCommandPort(), new RecordingRuntimeProvisioningPort(), new RecordingRuntimeCommandPort(), new RecordingCatalogCommandPort(), new RecordingAuthorityReadModelActivationPort())),
            ("readModelQueryPort", () => new ScriptBehaviorRuntimeCapabilityFactory(new RecordingAICapability(), new RecordingActorRuntime(), new RecordingExecutionProjectionPort(), null!, new RecordingDefinitionSnapshotPort(), new RecordingProposalPort(), new RecordingDefinitionCommandPort(), new RecordingRuntimeProvisioningPort(), new RecordingRuntimeCommandPort(), new RecordingCatalogCommandPort(), new RecordingAuthorityReadModelActivationPort())),
            ("definitionSnapshotPort", () => new ScriptBehaviorRuntimeCapabilityFactory(new RecordingAICapability(), new RecordingActorRuntime(), new RecordingExecutionProjectionPort(), new RecordingReadModelQueryPort(), null!, new RecordingProposalPort(), new RecordingDefinitionCommandPort(), new RecordingRuntimeProvisioningPort(), new RecordingRuntimeCommandPort(), new RecordingCatalogCommandPort(), new RecordingAuthorityReadModelActivationPort())),
            ("proposalPort", () => new ScriptBehaviorRuntimeCapabilityFactory(new RecordingAICapability(), new RecordingActorRuntime(), new RecordingExecutionProjectionPort(), new RecordingReadModelQueryPort(), new RecordingDefinitionSnapshotPort(), null!, new RecordingDefinitionCommandPort(), new RecordingRuntimeProvisioningPort(), new RecordingRuntimeCommandPort(), new RecordingCatalogCommandPort(), new RecordingAuthorityReadModelActivationPort())),
            ("definitionCommandPort", () => new ScriptBehaviorRuntimeCapabilityFactory(new RecordingAICapability(), new RecordingActorRuntime(), new RecordingExecutionProjectionPort(), new RecordingReadModelQueryPort(), new RecordingDefinitionSnapshotPort(), new RecordingProposalPort(), null!, new RecordingRuntimeProvisioningPort(), new RecordingRuntimeCommandPort(), new RecordingCatalogCommandPort(), new RecordingAuthorityReadModelActivationPort())),
            ("runtimeProvisioningPort", () => new ScriptBehaviorRuntimeCapabilityFactory(new RecordingAICapability(), new RecordingActorRuntime(), new RecordingExecutionProjectionPort(), new RecordingReadModelQueryPort(), new RecordingDefinitionSnapshotPort(), new RecordingProposalPort(), new RecordingDefinitionCommandPort(), null!, new RecordingRuntimeCommandPort(), new RecordingCatalogCommandPort(), new RecordingAuthorityReadModelActivationPort())),
            ("runtimeCommandPort", () => new ScriptBehaviorRuntimeCapabilityFactory(new RecordingAICapability(), new RecordingActorRuntime(), new RecordingExecutionProjectionPort(), new RecordingReadModelQueryPort(), new RecordingDefinitionSnapshotPort(), new RecordingProposalPort(), new RecordingDefinitionCommandPort(), new RecordingRuntimeProvisioningPort(), null!, new RecordingCatalogCommandPort(), new RecordingAuthorityReadModelActivationPort())),
            ("catalogCommandPort", () => new ScriptBehaviorRuntimeCapabilityFactory(new RecordingAICapability(), new RecordingActorRuntime(), new RecordingExecutionProjectionPort(), new RecordingReadModelQueryPort(), new RecordingDefinitionSnapshotPort(), new RecordingProposalPort(), new RecordingDefinitionCommandPort(), new RecordingRuntimeProvisioningPort(), new RecordingRuntimeCommandPort(), null!, new RecordingAuthorityReadModelActivationPort())),
            ("authorityReadModelActivationPort", () => new ScriptBehaviorRuntimeCapabilityFactory(new RecordingAICapability(), new RecordingActorRuntime(), new RecordingExecutionProjectionPort(), new RecordingReadModelQueryPort(), new RecordingDefinitionSnapshotPort(), new RecordingProposalPort(), new RecordingDefinitionCommandPort(), new RecordingRuntimeProvisioningPort(), new RecordingRuntimeCommandPort(), new RecordingCatalogCommandPort(), null!)),
        };

        foreach (var testCase in cases)
        {
            var act = testCase.Create;

            act.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be(testCase.Name);
        }
    }
}

public sealed class RuntimeScriptCatalogCommandServiceBranchTests
{
    [Fact]
    public async Task PromoteCatalogRevisionAsync_ShouldResolveCreatePrimeAndDispatch()
    {
        var runtime = new RecordingActorRuntime();
        var promoteDispatch = new RecordingDispatchService<PromoteScriptCatalogRevisionCommand>(
            _ => CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Success(
                new ScriptingCommandAcceptedReceipt("catalog-1", "command-1", "corr-1")));
        var activation = new RecordingAuthorityReadModelActivationPort();
        var service = new RuntimeScriptCatalogCommandService(
            promoteDispatch,
            new RecordingDispatchService<RollbackScriptCatalogRevisionCommand>(
                _ => throw new InvalidOperationException("rollback dispatch should not run")),
            new StaticAddressResolver(),
            new RuntimeScriptActorAccessor(runtime),
            activation,
            CreateCatalogQueryPort("script-1", "rev-2", "definition-2", "hash-2", "proposal-1"));

        await service.PromoteCatalogRevisionAsync(
            null,
            "script-1",
            "rev-1",
            "rev-2",
            "definition-2",
            "hash-2",
            "proposal-1",
            CancellationToken.None);

        runtime.CreatedActorIds.Should().ContainSingle("catalog-1");
        activation.ActivatedActorIds.Should().ContainSingle("catalog-1");
        promoteDispatch.CapturedCommand!.CatalogActorId.Should().Be("catalog-1");
        promoteDispatch.CapturedCommand.ScriptId.Should().Be("script-1");
        promoteDispatch.CapturedCommand.Revision.Should().Be("rev-2");
    }

    [Fact]
    public async Task RollbackCatalogRevisionAsync_ShouldUseProvidedCatalogActorId_WhenActorExists()
    {
        var runtime = new RecordingActorRuntime();
        runtime.Register(new FakeActor("catalog-custom"));
        var rollbackDispatch = new RecordingDispatchService<RollbackScriptCatalogRevisionCommand>(
            _ => CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Success(
                new ScriptingCommandAcceptedReceipt("catalog-custom", "command-1", "corr-1")));
        var activation = new RecordingAuthorityReadModelActivationPort();
        var service = new RuntimeScriptCatalogCommandService(
            new RecordingDispatchService<PromoteScriptCatalogRevisionCommand>(
                _ => throw new InvalidOperationException("promote dispatch should not run")),
            rollbackDispatch,
            new StaticAddressResolver(),
            new RuntimeScriptActorAccessor(runtime),
            activation,
            CreateCatalogQueryPort("script-1", "rev-1", string.Empty, string.Empty, "proposal-2"));

        await service.RollbackCatalogRevisionAsync(
            "catalog-custom",
            "script-1",
            "rev-1",
            "rollback",
            "proposal-2",
            "rev-2",
            CancellationToken.None);

        runtime.CreatedActorIds.Should().BeEmpty();
        activation.ActivatedActorIds.Should().ContainSingle("catalog-custom");
        rollbackDispatch.CapturedCommand!.CatalogActorId.Should().Be("catalog-custom");
        rollbackDispatch.CapturedCommand.TargetRevision.Should().Be("rev-1");
    }

    [Fact]
    public async Task PromoteCatalogRevisionAsync_ShouldThrowTypedError_WhenDispatchFails()
    {
        var service = new RuntimeScriptCatalogCommandService(
            new RecordingDispatchService<PromoteScriptCatalogRevisionCommand>(
                _ => CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Failure(
                    ScriptingCommandStartError.InvalidArgument("revision", "revision is required"))),
            new RecordingDispatchService<RollbackScriptCatalogRevisionCommand>(
                _ => throw new InvalidOperationException("rollback dispatch should not run")),
            new StaticAddressResolver(),
            new RuntimeScriptActorAccessor(new RecordingActorRuntime()),
            new RecordingAuthorityReadModelActivationPort(),
            CreateCatalogQueryPort("script-1", "rev-2", "definition-2", "hash-2", "proposal-1"));

        var act = () => service.PromoteCatalogRevisionAsync(
            null,
            "script-1",
            "rev-1",
            "rev-2",
            "definition-2",
            "hash-2",
            "proposal-1",
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("revision is required*");
    }

    [Fact]
    public async Task RollbackCatalogRevisionAsync_ShouldThrowDefaultError_WhenDispatchFailsWithoutTypedError()
    {
        var service = new RuntimeScriptCatalogCommandService(
            new RecordingDispatchService<PromoteScriptCatalogRevisionCommand>(
                _ => throw new InvalidOperationException("promote dispatch should not run")),
            new RecordingDispatchService<RollbackScriptCatalogRevisionCommand>(
                _ => new CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>
                {
                    Succeeded = false,
                    Error = null!,
                    Receipt = null,
                }),
            new StaticAddressResolver(),
            new RuntimeScriptActorAccessor(new RecordingActorRuntime()),
            new RecordingAuthorityReadModelActivationPort(),
            CreateCatalogQueryPort("script-1", "rev-1", string.Empty, string.Empty, "proposal-2"));

        var act = () => service.RollbackCatalogRevisionAsync(
            null,
            "script-1",
            "rev-1",
            "rollback",
            "proposal-2",
            "rev-2",
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Script catalog rollback dispatch failed.");
    }

    [Fact]
    public void Constructor_ShouldThrow_ForNullDependencies()
    {
        var cases = new (string Name, Func<RuntimeScriptCatalogCommandService> Create)[]
        {
            ("promoteDispatchService", () => new RuntimeScriptCatalogCommandService(null!, new RecordingDispatchService<RollbackScriptCatalogRevisionCommand>(_ => defaultSuccess()), new StaticAddressResolver(), new RuntimeScriptActorAccessor(new RecordingActorRuntime()), new RecordingAuthorityReadModelActivationPort(), CreateCatalogQueryPort("script-1", "rev-2", "definition-2", "hash-2", "proposal-1"))),
            ("rollbackDispatchService", () => new RuntimeScriptCatalogCommandService(new RecordingDispatchService<PromoteScriptCatalogRevisionCommand>(_ => defaultSuccess()), null!, new StaticAddressResolver(), new RuntimeScriptActorAccessor(new RecordingActorRuntime()), new RecordingAuthorityReadModelActivationPort(), CreateCatalogQueryPort("script-1", "rev-2", "definition-2", "hash-2", "proposal-1"))),
            ("addressResolver", () => new RuntimeScriptCatalogCommandService(new RecordingDispatchService<PromoteScriptCatalogRevisionCommand>(_ => defaultSuccess()), new RecordingDispatchService<RollbackScriptCatalogRevisionCommand>(_ => defaultSuccess()), null!, new RuntimeScriptActorAccessor(new RecordingActorRuntime()), new RecordingAuthorityReadModelActivationPort(), CreateCatalogQueryPort("script-1", "rev-2", "definition-2", "hash-2", "proposal-1"))),
            ("actorAccessor", () => new RuntimeScriptCatalogCommandService(new RecordingDispatchService<PromoteScriptCatalogRevisionCommand>(_ => defaultSuccess()), new RecordingDispatchService<RollbackScriptCatalogRevisionCommand>(_ => defaultSuccess()), new StaticAddressResolver(), null!, new RecordingAuthorityReadModelActivationPort(), CreateCatalogQueryPort("script-1", "rev-2", "definition-2", "hash-2", "proposal-1"))),
            ("authorityReadModelActivationPort", () => new RuntimeScriptCatalogCommandService(new RecordingDispatchService<PromoteScriptCatalogRevisionCommand>(_ => defaultSuccess()), new RecordingDispatchService<RollbackScriptCatalogRevisionCommand>(_ => defaultSuccess()), new StaticAddressResolver(), new RuntimeScriptActorAccessor(new RecordingActorRuntime()), null!, CreateCatalogQueryPort("script-1", "rev-2", "definition-2", "hash-2", "proposal-1"))),
            ("catalogQueryPort", () => new RuntimeScriptCatalogCommandService(new RecordingDispatchService<PromoteScriptCatalogRevisionCommand>(_ => defaultSuccess()), new RecordingDispatchService<RollbackScriptCatalogRevisionCommand>(_ => defaultSuccess()), new StaticAddressResolver(), new RuntimeScriptActorAccessor(new RecordingActorRuntime()), new RecordingAuthorityReadModelActivationPort(), null!)),
        };

        foreach (var testCase in cases)
        {
            testCase.Create.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be(testCase.Name);
        }

        static CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError> defaultSuccess() =>
            CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Success(
                new ScriptingCommandAcceptedReceipt("catalog-1", "command-1", "corr-1"));
    }

    private static ProjectionScriptCatalogQueryPort CreateCatalogQueryPort(
        string scriptId,
        string activeRevision,
        string definitionActorId,
        string sourceHash,
        string proposalId) =>
        new((_, requestedScriptId, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<ScriptCatalogEntrySnapshot?>(
                string.Equals(requestedScriptId, scriptId, StringComparison.Ordinal)
                    ? new ScriptCatalogEntrySnapshot(
                        scriptId,
                        activeRevision,
                        definitionActorId,
                        sourceHash,
                        string.Empty,
                        string.IsNullOrWhiteSpace(activeRevision) ? [] : [activeRevision],
                        proposalId)
                    : null);
        });
}

public sealed class RuntimeScriptDefinitionCommandServiceBranchTests
{
    [Fact]
    public async Task UpsertDefinitionWithSnapshotAsync_ShouldResolveActorId_CompilePrimeAndDispatch()
    {
        var dispatch = new RecordingDispatchService<UpsertScriptDefinitionCommand>(
            _ => CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Success(
                new ScriptingCommandAcceptedReceipt("script-definition:script-1", "command-1", "corr-1")));
        var activation = new RecordingAuthorityReadModelActivationPort();
        var service = new RuntimeScriptDefinitionCommandService(
            dispatch,
            new StaticAddressResolver(),
            activation,
            new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy()));

        var result = await service.UpsertDefinitionWithSnapshotAsync(
            "script-1",
            "rev-1",
            ScriptSources.StructuredProfileBehavior,
            string.Empty,
            null,
            CancellationToken.None);

        result.ActorId.Should().Be("script-definition:script-1");
        result.Snapshot.ScriptId.Should().Be("script-1");
        result.Snapshot.Revision.Should().Be("rev-1");
        result.Snapshot.SourceHash.Should().NotBeEmpty();
        result.Snapshot.ScriptPackage.CsharpSources.Should().NotBeEmpty();
        result.Snapshot.ProtocolDescriptorSet.IsEmpty.Should().BeFalse();
        result.Snapshot.RuntimeSemantics.Should().NotBeNull();
        dispatch.CapturedCommand!.DefinitionActorId.Should().Be("script-definition:script-1");
        activation.ActivatedActorIds.Should().ContainSingle("script-definition:script-1");
    }

    [Fact]
    public async Task UpsertDefinitionWithSnapshotAsync_ShouldUseProvidedActorIdAndSourceHash()
    {
        var dispatch = new RecordingDispatchService<UpsertScriptDefinitionCommand>(
            _ => CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Success(
                new ScriptingCommandAcceptedReceipt("definition-custom", "command-1", "corr-1")));
        var service = new RuntimeScriptDefinitionCommandService(
            dispatch,
            new StaticAddressResolver(),
            new RecordingAuthorityReadModelActivationPort(),
            new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy()));

        var result = await service.UpsertDefinitionWithSnapshotAsync(
            "script-2",
            "rev-2",
            ScriptSources.UppercaseBehavior,
            "hash-custom",
            "definition-custom",
            CancellationToken.None);

        result.ActorId.Should().Be("definition-custom");
        result.Snapshot.SourceHash.Should().Be("hash-custom");
        dispatch.CapturedCommand!.DefinitionActorId.Should().Be("definition-custom");
        dispatch.CapturedCommand.SourceHash.Should().Be("hash-custom");
    }

    [Fact]
    public async Task UpsertDefinitionWithSnapshotAsync_ShouldThrowTypedError_WhenDispatchFails()
    {
        var service = new RuntimeScriptDefinitionCommandService(
            new RecordingDispatchService<UpsertScriptDefinitionCommand>(
                _ => CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Failure(
                    ScriptingCommandStartError.InvalidArgument("scriptId", "script id is required"))),
            new StaticAddressResolver(),
            new RecordingAuthorityReadModelActivationPort(),
            new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy()));

        var act = () => service.UpsertDefinitionWithSnapshotAsync(
            "script-1",
            "rev-1",
            ScriptSources.UppercaseBehavior,
            ScriptSources.UppercaseBehaviorHash,
            null,
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("script id is required*");
    }

    [Fact]
    public async Task UpsertDefinitionWithSnapshotAsync_ShouldThrow_WhenCompilationFails()
    {
        var service = new RuntimeScriptDefinitionCommandService(
            new RecordingDispatchService<UpsertScriptDefinitionCommand>(
                _ => throw new InvalidOperationException("dispatch should not run")),
            new StaticAddressResolver(),
            new RecordingAuthorityReadModelActivationPort(),
            new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy()));

        var act = () => service.UpsertDefinitionWithSnapshotAsync(
            "script-1",
            "rev-1",
            "if (true {",
            ScriptSources.UppercaseBehaviorHash,
            null,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Script definition compilation failed*");
    }

    [Fact]
    public void Constructor_ShouldThrow_ForNullDependencies()
    {
        var cases = new (string Name, Func<RuntimeScriptDefinitionCommandService> Create)[]
        {
            ("dispatchService", () => new RuntimeScriptDefinitionCommandService(null!, new StaticAddressResolver(), new RecordingAuthorityReadModelActivationPort(), new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy()))),
            ("addressResolver", () => new RuntimeScriptDefinitionCommandService(new RecordingDispatchService<UpsertScriptDefinitionCommand>(_ => CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Success(new ScriptingCommandAcceptedReceipt("actor", "command", "corr"))), null!, new RecordingAuthorityReadModelActivationPort(), new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy()))),
            ("authorityReadModelActivationPort", () => new RuntimeScriptDefinitionCommandService(new RecordingDispatchService<UpsertScriptDefinitionCommand>(_ => CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Success(new ScriptingCommandAcceptedReceipt("actor", "command", "corr"))), new StaticAddressResolver(), null!, new RoslynScriptBehaviorCompiler(new ScriptSandboxPolicy()))),
            ("compiler", () => new RuntimeScriptDefinitionCommandService(new RecordingDispatchService<UpsertScriptDefinitionCommand>(_ => CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>.Success(new ScriptingCommandAcceptedReceipt("actor", "command", "corr"))), new StaticAddressResolver(), new RecordingAuthorityReadModelActivationPort(), null!)),
        };

        foreach (var testCase in cases)
        {
            testCase.Create.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be(testCase.Name);
        }
    }
}

public sealed class ScriptEvolutionCommandTargetTests
{
    [Fact]
    public void Constructor_ShouldValidateInputs()
    {
        var projectionPort = new RecordingEvolutionProjectionPort();

        Action nullActor = () => new ScriptEvolutionCommandTarget(null!, "proposal-1", projectionPort);
        Action blankProposal = () => new ScriptEvolutionCommandTarget(new FakeActor("session-1"), " ", projectionPort);
        Action nullPort = () => new ScriptEvolutionCommandTarget(new FakeActor("session-1"), "proposal-1", null!);

        nullActor.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("actor");
        blankProposal.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("proposalId");
        nullPort.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("projectionPort");
    }

    [Fact]
    public void BindLiveObservation_ShouldValidateInputs()
    {
        var target = new ScriptEvolutionCommandTarget(
            new FakeActor("session-1"),
            "proposal-1",
            new RecordingEvolutionProjectionPort());

        Action nullLease = () => target.BindLiveObservation(null!, new RecordingCompletedEventSink());
        Action nullSink = () => target.BindLiveObservation(new RecordingEvolutionProjectionLease("session-1", "proposal-1"), null!);

        nullLease.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("lease");
        nullSink.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("sink");
    }

    [Fact]
    public void RequireLiveSink_ShouldThrow_WhenNotBound()
    {
        var target = new ScriptEvolutionCommandTarget(
            new FakeActor("session-1"),
            "proposal-1",
            new RecordingEvolutionProjectionPort());

        Action act = () => target.RequireLiveSink();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Script evolution live sink is not bound.");
    }

    [Fact]
    public async Task ReleaseAsync_ShouldDetachAndDispose_WhenLeaseAndSinkAreBound()
    {
        var projectionPort = new RecordingEvolutionProjectionPort();
        var target = new ScriptEvolutionCommandTarget(
            new FakeActor("session-1"),
            "proposal-1",
            projectionPort);
        var lease = new RecordingEvolutionProjectionLease("session-1", "proposal-1");
        var sink = new RecordingCompletedEventSink();
        target.BindLiveObservation(lease, sink);

        await target.ReleaseAsync(CancellationToken.None);

        projectionPort.DetachedLeases.Should().ContainSingle().Which.Should().BeSameAs(lease);
        projectionPort.ReleasedLeases.Should().ContainSingle().Which.Should().BeSameAs(lease);
        sink.DisposeCount.Should().Be(1);
        target.ProjectionLease.Should().BeNull();
        target.LiveSink.Should().BeNull();
    }

    [Fact]
    public async Task ReleaseAsync_ShouldDisposeSink_WhenOnlySinkIsBound()
    {
        var target = new ScriptEvolutionCommandTarget(
            new FakeActor("session-1"),
            "proposal-1",
            new RecordingEvolutionProjectionPort());
        var sink = new RecordingCompletedEventSink();
        target.BindLiveObservation(new RecordingEvolutionProjectionLease("session-1", "proposal-1"), sink);
        target.BindLiveObservation(new RecordingEvolutionProjectionLease("session-1", "proposal-1"), sink);
        target.GetType().GetProperty(nameof(ScriptEvolutionCommandTarget.ProjectionLease))!.SetValue(target, null);

        await target.ReleaseAsync(CancellationToken.None);

        sink.DisposeCount.Should().Be(1);
        target.LiveSink.Should().BeNull();
    }

    [Fact]
    public async Task CleanupMethods_ShouldDelegateToReleaseAsync()
    {
        var projectionPort = new RecordingEvolutionProjectionPort();
        var target = new ScriptEvolutionCommandTarget(
            new FakeActor("session-1"),
            "proposal-1",
            projectionPort);
        target.BindLiveObservation(
            new RecordingEvolutionProjectionLease("session-1", "proposal-1"),
            new RecordingCompletedEventSink());

        await target.CleanupAfterDispatchFailureAsync(CancellationToken.None);
        await target.ReleaseAfterInteractionAsync(
            new ScriptEvolutionAcceptedReceipt("session-1", "proposal-1", "command-1", "corr-1"),
            new CommandInteractionCleanupContext<ScriptEvolutionInteractionCompletion>(
                false,
                ScriptEvolutionInteractionCompletion.Pending,
                CommandDurableCompletionObservation<ScriptEvolutionInteractionCompletion>.Incomplete),
            CancellationToken.None);

        projectionPort.DetachedLeases.Should().HaveCount(1);
    }

    [Fact]
    public async Task ReleaseAsync_ShouldRethrowFirstCleanupFailure()
    {
        var projectionPort = new RecordingEvolutionProjectionPort
        {
            DetachException = new InvalidOperationException("detach failed"),
        };
        var target = new ScriptEvolutionCommandTarget(
            new FakeActor("session-1"),
            "proposal-1",
            projectionPort);
        target.BindLiveObservation(
            new RecordingEvolutionProjectionLease("session-1", "proposal-1"),
            new RecordingCompletedEventSink());

        var act = () => target.ReleaseAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("detach failed");
        target.ProjectionLease.Should().BeNull();
        target.LiveSink.Should().BeNull();
    }
}

internal sealed class RecordingDispatchService<TCommand>(
    Func<TCommand, CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>> dispatch)
    : ICommandDispatchService<TCommand, ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>
    where TCommand : class
{
    public TCommand? CapturedCommand { get; private set; }

    public Task<CommandDispatchResult<ScriptingCommandAcceptedReceipt, ScriptingCommandStartError>> DispatchAsync(
        TCommand command,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        CapturedCommand = command;
        return Task.FromResult(dispatch(command));
    }
}

internal sealed class RecordingAuthorityReadModelActivationPort : IScriptAuthorityReadModelActivationPort
{
    public List<string> ActivatedActorIds { get; } = [];

    public Task ActivateAsync(string actorId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ActivatedActorIds.Add(actorId);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingActorRuntime : IActorRuntime
{
    private readonly Dictionary<string, IActor> _actors = new(StringComparer.Ordinal);

    public List<string> CreatedActorIds { get; } = [];

    public void Register(IActor actor)
    {
        _actors[actor.Id] = actor;
    }

    public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default) where TAgent : IAgent
    {
        ct.ThrowIfCancellationRequested();
        var resolvedId = id ?? Guid.NewGuid().ToString("N");
        CreatedActorIds.Add(resolvedId);
        var actor = new FakeActor(resolvedId);
        _actors[resolvedId] = actor;
        return Task.FromResult<IActor>(actor);
    }

    public Task<IActor> CreateAsync(SystemType agentType, string? id = null, CancellationToken ct = default)
    {
        _ = agentType;
        return CreateAsync<RecordingAgent>(id, ct);
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
}

internal sealed class RecordingAICapability : IAICapability
{
    public Task<string> AskAsync(string runId, string correlationId, string prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult($"{runId}:{correlationId}:{prompt}");
    }
}

internal sealed class RecordingExecutionProjectionPort : IScriptExecutionProjectionPort
{
    public bool ProjectionEnabled => true;

    public Task<IScriptExecutionProjectionLease?> EnsureActorProjectionAsync(string actorId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IScriptExecutionProjectionLease?>(new RecordingExecutionProjectionLease(actorId));
    }

    public Task<IScriptExecutionProjectionLease?> EnsureProjectionAsync(string actorId, string projectionName, string input, string commandId, CancellationToken ct = default)
    {
        _ = projectionName;
        _ = input;
        _ = commandId;
        return EnsureActorProjectionAsync(actorId, ct);
    }

    public Task AttachLiveSinkAsync(IScriptExecutionProjectionLease lease, IEventSink<EventEnvelope> sink, CancellationToken ct = default)
    {
        _ = lease;
        _ = sink;
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task DetachLiveSinkAsync(IScriptExecutionProjectionLease lease, IEventSink<EventEnvelope> sink, CancellationToken ct = default)
    {
        _ = lease;
        _ = sink;
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ReleaseActorProjectionAsync(IScriptExecutionProjectionLease lease, CancellationToken ct = default)
    {
        _ = lease;
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private sealed record RecordingExecutionProjectionLease(string ActorId) : IScriptExecutionProjectionLease;
}

internal sealed class RecordingReadModelQueryPort : IScriptReadModelQueryPort
{
    public Task<ScriptReadModelSnapshot?> GetSnapshotAsync(string actorId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<ScriptReadModelSnapshot?>(null);
    }

    public Task<IReadOnlyList<ScriptReadModelSnapshot>> ListSnapshotsAsync(int take = 200, CancellationToken ct = default)
    {
        _ = take;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ScriptReadModelSnapshot>>([]);
    }

    public Task<Any?> ExecuteDeclaredQueryAsync(string actorId, Any queryPayload, CancellationToken ct = default)
    {
        _ = actorId;
        _ = queryPayload;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Any?>(null);
    }
}

internal sealed class RecordingDefinitionSnapshotPort : IScriptDefinitionSnapshotPort
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

internal sealed class RecordingProposalPort : IScriptEvolutionProposalPort
{
    public Task<ScriptPromotionDecision> ProposeAsync(ScriptEvolutionProposal proposal, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ScriptPromotionDecision.Rejected(proposal, "not-used"));
    }
}

internal sealed class RecordingDefinitionCommandPort : IScriptDefinitionCommandPort
{
    public Task<ScriptDefinitionUpsertResult> UpsertDefinitionWithSnapshotAsync(
        string scriptId,
        string scriptRevision,
        string sourceText,
        string sourceHash,
        string? definitionActorId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ScriptDefinitionUpsertResult(
            definitionActorId ?? "definition-created",
            new ScriptDefinitionSnapshot(
                scriptId,
                scriptRevision,
                sourceText,
                sourceHash,
                ScriptSources.UppercaseStateTypeUrl,
                ScriptSources.UppercaseReadModelTypeUrl,
                "1",
                "schema-hash")));
    }
}

internal sealed class RecordingRuntimeProvisioningPort : IScriptRuntimeProvisioningPort
{
    public Task<string> EnsureRuntimeAsync(
        string definitionActorId,
        string scriptRevision,
        string? runtimeActorId,
        ScriptDefinitionSnapshot definitionSnapshot,
        CancellationToken ct)
    {
        _ = definitionActorId;
        _ = scriptRevision;
        _ = definitionSnapshot;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(runtimeActorId ?? "runtime-created");
    }
}

internal sealed class RecordingRuntimeCommandPort : IScriptRuntimeCommandPort
{
    public Task RunRuntimeAsync(string runtimeActorId, string runId, Any? inputPayload, string scriptRevision, string definitionActorId, string requestedEventType, CancellationToken ct)
    {
        _ = runtimeActorId;
        _ = runId;
        _ = inputPayload;
        _ = scriptRevision;
        _ = definitionActorId;
        _ = requestedEventType;
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

internal sealed class RecordingCatalogCommandPort : IScriptCatalogCommandPort
{
    public Task PromoteCatalogRevisionAsync(string? catalogActorId, string scriptId, string expectedBaseRevision, string revision, string definitionActorId, string sourceHash, string proposalId, CancellationToken ct)
    {
        _ = catalogActorId;
        _ = scriptId;
        _ = expectedBaseRevision;
        _ = revision;
        _ = definitionActorId;
        _ = sourceHash;
        _ = proposalId;
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task RollbackCatalogRevisionAsync(string? catalogActorId, string scriptId, string targetRevision, string reason, string proposalId, string expectedCurrentRevision, CancellationToken ct)
    {
        _ = catalogActorId;
        _ = scriptId;
        _ = targetRevision;
        _ = reason;
        _ = proposalId;
        _ = expectedCurrentRevision;
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

internal sealed class RecordingEvolutionProjectionPort : IScriptEvolutionProjectionPort
{
    public bool ProjectionEnabled => true;
    public List<IScriptEvolutionProjectionLease> DetachedLeases { get; } = [];
    public List<IScriptEvolutionProjectionLease> ReleasedLeases { get; } = [];
    public Exception? DetachException { get; set; }
    public Exception? ReleaseException { get; set; }

    public Task<IScriptEvolutionProjectionLease?> EnsureActorProjectionAsync(string sessionActorId, string proposalId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IScriptEvolutionProjectionLease?>(new RecordingEvolutionProjectionLease(sessionActorId, proposalId));
    }

    public async Task AttachLiveSinkAsync(IScriptEvolutionProjectionLease lease, IEventSink<ScriptEvolutionSessionCompletedEvent> sink, CancellationToken ct = default)
    {
        _ = lease;
        _ = sink;
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
    }

    public Task DetachLiveSinkAsync(IScriptEvolutionProjectionLease lease, IEventSink<ScriptEvolutionSessionCompletedEvent> sink, CancellationToken ct = default)
    {
        _ = sink;
        ct.ThrowIfCancellationRequested();
        if (DetachException != null)
            throw DetachException;
        DetachedLeases.Add(lease);
        return Task.CompletedTask;
    }

    public Task ReleaseActorProjectionAsync(IScriptEvolutionProjectionLease lease, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (ReleaseException != null)
            throw ReleaseException;
        ReleasedLeases.Add(lease);
        return Task.CompletedTask;
    }
}

internal sealed record RecordingEvolutionProjectionLease(string ActorId, string ProposalId) : IScriptEvolutionProjectionLease;

internal sealed class RecordingCompletedEventSink : IEventSink<ScriptEvolutionSessionCompletedEvent>
{
    public int DisposeCount { get; private set; }

    public void Push(ScriptEvolutionSessionCompletedEvent evt)
    {
        _ = evt;
    }

    public ValueTask PushAsync(ScriptEvolutionSessionCompletedEvent evt, CancellationToken ct = default)
    {
        _ = evt;
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public void Complete()
    {
    }

    public async IAsyncEnumerable<ScriptEvolutionSessionCompletedEvent> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
        yield break;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeActor(string id) : IActor
{
    public string Id { get; } = id;
    public IAgent Agent { get; } = new RecordingAgent(id);

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
        _ = envelope;
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
}

internal sealed class RecordingAgent(string id) : IAgent
{
    public string Id { get; } = id;

    public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        _ = envelope;
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<string> GetDescriptionAsync() => Task.FromResult("recording");

    public Task<IReadOnlyList<SystemType>> GetSubscribedEventTypesAsync() => Task.FromResult<IReadOnlyList<SystemType>>([]);

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

internal sealed class StaticAddressResolver : IScriptingActorAddressResolver
{
    public string GetEvolutionManagerActorId() => "script-evolution-manager";

    public string GetEvolutionSessionActorId(string proposalId) => $"script-evolution-session:{proposalId}";

    public string GetCatalogActorId() => "catalog-1";

    public string GetDefinitionActorId(string scriptId) => $"script-definition:{scriptId}";
}
