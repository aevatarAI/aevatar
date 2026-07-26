using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Prompting;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Prompting;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using FluentAssertions;
using ProfileValidationLimits =
    Aevatar.GAgentService.Abstractions.AgentProfiles.AgentProfileValidationLimits;

namespace Aevatar.AI.Tests;

public sealed class AgentProfileTurnCatalogMaterializerTests
{
    private const string RouteToolSet = "nyxid.profile.route";
    private const string SkillGuid = "11111111-1111-1111-1111-111111111111";
    private const string SkillVersion = "1.2";

    [Fact]
    public void Constructor_ShouldHaveNoRuntimeRemoteSkillDependency()
    {
        typeof(AgentProfileTurnCatalogMaterializer).GetConstructors()
            .SelectMany(static constructor => constructor.GetParameters())
            .Select(static parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name)
            .Should().NotContain(typeName =>
                typeName.Contains("RemoteSkillFetcher", StringComparison.Ordinal) ||
                typeName.Contains("Ornn", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareAsync_EnforcedAlias_ShouldFreezeBindingProvenanceAndStrictIntersection()
    {
        var tools = NewTools("recovery-tool", "task-tool", "maximum-only", "route-only");
        var binding = BuildBinding(AgentProfileActivationMode.Enforced);
        binding.EffectiveMaximumToolPolicy.ToolNames.Add(["maximum-only", "route-only"]);
        binding.Members[0].TaskToolPolicy.ToolNames.Add("route-only");
        var sealedBinding = Seal(binding);
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch());

        var preparation = await NewMaterializer(RegistryWithRoute(tools), classifier)
            .PrepareAsync(
                sealedBinding,
                "session-a",
                "alpha run",
                tools,
                ToolContext("recovery-tool", "task-tool", "maximum-only"));

        preparation.Authority.CandidateRoute.Should().BeEquivalentTo(
            new AgentProfileTurnCandidateRouteIdentity
            {
                SourceProfileId = "profile-alpha",
                SourceStateVersion = 17,
                PublishedRevision = 5,
                PublishedSnapshotSha256 = sealedBinding.Source.PublishedSnapshotSha256,
                ExecutionBindingSha256 = sealedBinding.DeterministicBindingSha256,
                IntentId = "intent-alpha",
            });
        preparation.Authority.SelectedExactSkillRef.Should().BeEquivalentTo(
            new ExactRemoteSkillRef { Guid = SkillGuid, LiteralVersion = SkillVersion });
        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Selected);
        preparation.Authority.AuthorityCeilingToolNames.Should().Equal("recovery-tool", "task-tool");
        classifier.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeCommittedAsync_Enforced_ShouldUseSealedBodyAndProfileInstructionsWithoutRemoteRead()
    {
        var tools = NewTools("recovery-tool", "task-tool");
        var binding = Seal(BuildBinding(AgentProfileActivationMode.Enforced));
        var preparation = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()))
            .PrepareAsync(binding, "session-a", "alpha run", tools, ToolContext());
        var classifier = new RecordingClassifier(new InvalidOperationException("must not reclassify"));

        var materialization = await NewMaterializer(RegistryWithRoute(tools), classifier)
            .MaterializeCommittedAsync(
                binding,
                preparation.Authority,
                tools,
                ToolContext());

        materialization.Catalog.ProfilePromptLayer!.Content
            .Should().Contain("Follow the published profile instructions.");
        materialization.Catalog.SelectedSkillPromptLayer!.Content
            .Should().Be("Use the exact sealed procedure.");
        materialization.Catalog.SelectedSkillPromptLayer.Provenance.Source
            .Should().Be($"sealed-agent-profile:{SkillGuid}@{SkillVersion}");
        materialization.Catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery-tool", "task-tool");
        classifier.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ValidBinding_ProfilePrompt_ShouldContainOnlyAuthoredInstructions()
    {
        const string authoredInstructions = "Follow only the Profile Actor-authored instructions.";
        var tools = NewTools("recovery-tool", "task-tool");
        var binding = BuildBinding(AgentProfileActivationMode.Enforced);
        binding.ProfileInstructions = authoredInstructions;
        var sealedBinding = Seal(binding);
        var materializer = NewMaterializer(
            RegistryWithRoute(tools),
            new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()));
        var preparation = await materializer.PrepareAsync(
            sealedBinding,
            "session-hostile-admission",
            "alpha run",
            tools,
            ToolContext());

        var materialization = await materializer.MaterializeCommittedAsync(
            sealedBinding,
            preparation.Authority,
            tools,
            ToolContext());
        var composition = SystemPromptLayerComposer.Compose(
            new KernelPromptLayer("kernel", new KernelPromptProvenance("test")),
            new BuiltInPromptFloorLayer("floor", new BuiltInPromptFloorProvenance("test")),
            global: null,
            materialization.Catalog.ProfilePromptLayer,
            materialization.Catalog.SelectedSkillPromptLayer,
            runtimeFacts: null,
            conversation: null);

        materialization.Catalog.ProfilePromptLayer!.Content.Should().Be(authoredInstructions);
        composition.Profile.Included.Should().BeTrue();
        composition.Prompt.Should().Contain(authoredInstructions);
        composition.Prompt.Should().NotContain(sealedBinding.Source.ProfileId);
        composition.Prompt.Should().NotContain(sealedBinding.Admission.RolloutRelease);
        composition.Prompt.Should().NotContain(sealedBinding.Admission.RolloutStage);
        composition.Prompt.Should().NotContain(sealedBinding.Admission.RouteToolSetRef);
        composition.Prompt.Should().NotContain(preparation.Authority.CandidateRoute!.IntentId);
        composition.Prompt.Should().NotContain("Agent profile:");
        composition.Prompt.Should().NotContain("Source state version:");
        composition.Prompt.Should().NotContain("Published revision:");
        composition.Prompt.Should().NotContain("Candidate intent:");
        composition.Prompt.Should().NotContain("Selected intent:");
    }

    [Fact]
    public async Task InvalidBinding_ShouldRestrictEmptyWithoutAnyPromptOrIntent()
    {
        const string tamperedInstructions = "Tampered instructions must never reach the prompt.";
        var tools = NewTools("recovery-tool", "task-tool");
        var binding = Seal(BuildBinding(AgentProfileActivationMode.Enforced));
        var materializer = NewMaterializer(
            RegistryWithRoute(tools),
            new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()));
        var preparation = await materializer.PrepareAsync(
            binding,
            "session-invalid-materialization",
            "alpha run",
            tools,
            ToolContext());
        var tamperedBinding = binding.Clone();
        tamperedBinding.ProfileInstructions = tamperedInstructions;
        var materializationRegistry = RegistryWithRoute(tools);

        var materialization = await NewMaterializer(
                materializationRegistry,
                new RecordingClassifier(new InvalidOperationException("must not classify")))
            .MaterializeCommittedAsync(
                tamperedBinding,
                preparation.Authority,
                tools,
                ToolContext());
        var composition = SystemPromptLayerComposer.Compose(
            new KernelPromptLayer("kernel", new KernelPromptProvenance("test")),
            new BuiltInPromptFloorLayer("floor", new BuiltInPromptFloorProvenance("test")),
            global: null,
            materialization.Catalog.ProfilePromptLayer,
            materialization.Catalog.SelectedSkillPromptLayer,
            runtimeFacts: null,
            conversation: null);

        AgentProfileExecutionBindingCodec.Verify(tamperedBinding).Should().BeFalse();
        materialization.ReconcileProposal.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.RestrictedEmpty);
        materialization.ReconcileProposal.CandidateRoute.Should().BeNull();
        materialization.ReconcileProposal.SelectedExactSkillRef.Should().BeNull();
        materialization.Catalog.FinalAllowedToolNames.Should().BeEmpty();
        materialization.Catalog.ProfilePromptLayer.Should().BeNull();
        materialization.Catalog.SelectedSkillPromptLayer.Should().BeNull();
        materialization.Catalog.SelectedIntentId.Should().BeNull();
        materialization.Catalog.CandidateIntentId.Should().BeNull();
        materialization.Catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ProfileInvalid);
        composition.Profile.Included.Should().BeFalse();
        composition.Prompt.Should().NotContain(tamperedInstructions);
        materializationRegistry.ResolveCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ProfileInstructionsAtAuthoritativeLimit_ShouldSurviveMaterializationAndComposition()
    {
        var authoredInstructions = new string(
            'a',
            ProfileValidationLimits.ProfileInstructionsMaxUtf8Bytes);
        var tools = NewTools("recovery-tool", "task-tool");
        var binding = BuildBinding(AgentProfileActivationMode.Enforced);
        binding.ProfileInstructions = authoredInstructions;
        var materializer = NewMaterializer(
            RegistryWithRoute(tools),
            new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()));

        var result = await materializer.MaterializeWithPreparationAsync(
            Seal(binding),
            "alpha run",
            tools,
            ToolContext());
        var composition = SystemPromptLayerComposer.Compose(
            new KernelPromptLayer("kernel", new KernelPromptProvenance("test")),
            new BuiltInPromptFloorLayer("floor", new BuiltInPromptFloorProvenance("test")),
            global: null,
            result.Catalog.ProfilePromptLayer,
            result.Catalog.SelectedSkillPromptLayer,
            runtimeFacts: null,
            conversation: null);

        result.Catalog.ProfilePromptLayer!.Content.Should().Be(authoredInstructions);
        result.Catalog.ProfilePromptLayer.ActualUtf8Bytes.Should()
            .Be(ProfileValidationLimits.ProfileInstructionsMaxUtf8Bytes);
        result.Catalog.ProfilePromptLayer.Bounds.MaxUtf8Bytes.Should()
            .Be(AgentProfileExecutionBindingLimits.MaterializedProfileLayerMaxUtf8Bytes);
        composition.Profile.Included.Should().BeTrue();
        composition.Prompt.Should().Contain(authoredInstructions);
    }

    [Fact]
    public async Task Shadow_ShouldClassifyButNeverInjectSelectedBodyOrTaskAuthority()
    {
        var tools = NewTools("recovery-tool", "task-tool");
        var binding = Seal(BuildBinding(AgentProfileActivationMode.Shadow));
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha"));
        var materializer = NewMaterializer(RegistryWithRoute(tools), classifier);

        var preparation = await materializer.PrepareAsync(
            binding,
            "session-shadow",
            "classify this",
            tools,
            ToolContext());
        var materialization = await materializer.MaterializeCommittedAsync(
            binding,
            preparation.Authority,
            tools,
            ToolContext());

        classifier.CallCount.Should().Be(1);
        preparation.Authority.CandidateRoute!.IntentId.Should().Be("intent-alpha");
        preparation.Authority.SelectedExactSkillRef.Should().BeNull();
        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
        preparation.Authority.AuthorityCeilingToolNames.Should().Equal("recovery-tool");
        preparation.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ShadowCandidate);
        materialization.ReconcileProposal.CandidateRoute.Should()
            .BeEquivalentTo(preparation.Authority.CandidateRoute);
        materialization.ReconcileProposal.SelectedExactSkillRef.Should().BeNull();
        materialization.Catalog.CandidateIntentId.Should().Be("intent-alpha");
        materialization.Catalog.SelectedSkillPromptLayer.Should().BeNull();
        materialization.Catalog.FinalAllowedToolNames.Should().Equal("recovery-tool");
    }

    [Fact]
    public async Task ClassifierNoMatch_ShouldUseRecoveryOnly()
    {
        var tools = NewTools("recovery-tool", "task-tool");
        var binding = BuildBinding(AgentProfileActivationMode.Enforced);
        binding.Members[0].ExplicitTriggerAliases.Clear();
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch());

        var result = await NewMaterializer(RegistryWithRoute(tools), classifier)
            .MaterializeWithPreparationAsync(
                Seal(binding),
                "unmatched",
                tools,
                ToolContext());

        classifier.CallCount.Should().Be(1);
        result.Preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
        result.Catalog.FinalAllowedToolNames.Should().Equal("recovery-tool");
        result.Catalog.SelectedSkillPromptLayer.Should().BeNull();
    }

    [Fact]
    public async Task RoutedAlias_ShouldWinBeforeDefaultFallback()
    {
        var tools = NewTools("recovery-tool", "task-tool");
        var binding = BuildBinding(AgentProfileActivationMode.Enforced);
        AddDefaultMember(binding, "fallback");
        var classifier = new RecordingClassifier(
            new InvalidOperationException("routed alias must bypass classification"));

        var result = await NewMaterializer(RegistryWithRoute(tools), classifier)
            .MaterializeWithPreparationAsync(
                Seal(binding),
                "alpha run",
                tools,
                ToolContext());

        classifier.CallCount.Should().Be(0);
        result.Preparation.Authority.CandidateRoute!.IntentId.Should().Be("intent-alpha");
        result.Preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Selected);
        result.Catalog.SelectedIntentId.Should().Be("intent-alpha");
    }

    [Fact]
    public async Task DefaultAlias_ShouldNotPreemptRoutedClassifierMatch()
    {
        var tools = NewTools("recovery-tool", "task-tool");
        var binding = BuildBinding(AgentProfileActivationMode.Enforced);
        binding.Members[0].ExplicitTriggerAliases.Clear();
        AddDefaultMember(binding, "fallback");
        var classifier = new RecordingClassifier(
            AgentProfileTurnClassificationResult.Matched("intent-alpha"));

        var result = await NewMaterializer(RegistryWithRoute(tools), classifier)
            .MaterializeWithPreparationAsync(
                Seal(binding),
                "fallback request",
                tools,
                ToolContext());

        classifier.CallCount.Should().Be(1);
        classifier.Requests.Should().ContainSingle();
        classifier.Requests[0].Candidates.Select(static candidate => candidate.IntentId)
            .Should().Equal("intent-alpha");
        result.Preparation.Authority.CandidateRoute!.IntentId.Should().Be("intent-alpha");
        result.Catalog.SelectedIntentId.Should().Be("intent-alpha");
    }

    [Theory]
    [InlineData(AgentProfileActivationMode.Shadow)]
    [InlineData(AgentProfileActivationMode.Enforced)]
    public async Task AlwaysMembers_ShouldJoinEveryProfilePromptWithoutRoutingOrToolAuthority(
        AgentProfileActivationMode activationMode)
    {
        var tools = NewTools("recovery-tool", "task-tool");
        var binding = BuildBinding(activationMode);
        binding.Members[0].ExplicitTriggerAliases.Clear();
        AddAlwaysMember(
            binding,
            "Always procedure one.",
            "22222222-2222-2222-2222-222222222222");
        AddAlwaysMember(
            binding,
            "Always procedure two.",
            "33333333-3333-3333-3333-333333333333");
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch());

        var result = await NewMaterializer(RegistryWithRoute(tools), classifier)
            .MaterializeWithPreparationAsync(
                Seal(binding),
                "genuinely unmatched",
                tools,
                ToolContext());
        var composition = SystemPromptLayerComposer.Compose(
            new KernelPromptLayer("kernel", new KernelPromptProvenance("test")),
            new BuiltInPromptFloorLayer("floor", new BuiltInPromptFloorProvenance("test")),
            global: null,
            result.Catalog.ProfilePromptLayer,
            result.Catalog.SelectedSkillPromptLayer,
            runtimeFacts: null,
            conversation: null);

        classifier.CallCount.Should().Be(1);
        classifier.Requests.Should().ContainSingle();
        classifier.Requests[0].Candidates.Select(static candidate => candidate.IntentId)
            .Should().Equal("intent-alpha");
        result.Preparation.Authority.CandidateRoute.Should().BeNull();
        result.Preparation.Authority.SelectedExactSkillRef.Should().BeNull();
        result.Preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
        result.Catalog.FinalAllowedToolNames.Should().Equal("recovery-tool");
        result.Catalog.SelectedIntentId.Should().BeNull();
        result.Catalog.CandidateIntentId.Should().BeNull();
        result.Catalog.SelectedSkillPromptLayer.Should().BeNull();
        result.Catalog.ProfilePromptLayer!.Content.Should().Be(binding.ProfileInstructions);
        result.Catalog.ProfilePromptLayer.Bounds.MaxUtf8Bytes.Should()
            .Be(AgentProfileExecutionBindingLimits.MaterializedProfileLayerMaxUtf8Bytes);
        composition.Profile.Included.Should().BeTrue();
        composition.Prompt.Should().Contain(
            "<always-skill-procedure>\nAlways procedure one.\n</always-skill-procedure>");
        composition.Prompt.Should().Contain(
            "<always-skill-procedure>\nAlways procedure two.\n</always-skill-procedure>");
        composition.Prompt.IndexOf("Always procedure one.", StringComparison.Ordinal).Should()
            .BeLessThan(composition.Prompt.IndexOf("Always procedure two.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClassifierNoMatch_ShouldSelectDefaultForUnmatchedTurn()
    {
        var tools = NewTools("recovery-tool", "task-tool");
        var binding = BuildBinding(AgentProfileActivationMode.Enforced);
        binding.Members[0].ExplicitTriggerAliases.Clear();
        var defaultMember = AddDefaultMember(binding, "fallback");
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch());

        var result = await NewMaterializer(RegistryWithRoute(tools), classifier)
            .MaterializeWithPreparationAsync(
                Seal(binding),
                "genuinely unmatched",
                tools,
                ToolContext());

        classifier.CallCount.Should().Be(1);
        result.Preparation.Authority.CandidateRoute!.IntentId.Should().Be("intent-default");
        result.Preparation.Authority.SelectedExactSkillRef.Should()
            .BeEquivalentTo(defaultMember.SkillProvenance.ExactSkillRef);
        result.Preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Selected);
        result.Catalog.SelectedIntentId.Should().Be("intent-default");
        result.Catalog.SelectedSkillPromptLayer!.Content.Should()
            .Be("Use the default sealed procedure.");
    }

    [Fact]
    public async Task DefaultOnlyCatalog_ShouldSelectDefaultWithoutCallingClassifier()
    {
        var tools = NewTools("recovery-tool", "task-tool");
        var binding = BuildBinding(AgentProfileActivationMode.Enforced);
        binding.Members[0].ActivationMode =
            AgentProfileExecutionMemberActivationMode.DefaultForUnmatchedTurn;
        var classifier = new RecordingClassifier(
            new InvalidOperationException("default-only catalog must not classify"));

        var result = await NewMaterializer(RegistryWithRoute(tools), classifier)
            .MaterializeWithPreparationAsync(
                Seal(binding),
                "genuinely unmatched",
                tools,
                ToolContext());

        classifier.CallCount.Should().Be(0);
        result.Preparation.Authority.CandidateRoute!.IntentId.Should().Be("intent-alpha");
        result.Preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Selected);
        result.Preparation.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierNoMatch &&
            diagnostic.Detail == "no_routed_candidates");
        result.Catalog.SelectedIntentId.Should().Be("intent-alpha");
        result.Catalog.SelectedSkillPromptLayer!.Content.Should()
            .Be("Use the exact sealed procedure.");
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("exception")]
    [InlineData("unknown-intent")]
    public async Task ClassifierFailure_ShouldNotSelectDefault(string failureKind)
    {
        var tools = NewTools("recovery-tool", "task-tool");
        var binding = BuildBinding(AgentProfileActivationMode.Enforced);
        binding.Members[0].ExplicitTriggerAliases.Clear();
        AddDefaultMember(binding, "fallback");
        var classifier = failureKind switch
        {
            "failed" => new RecordingClassifier(AgentProfileTurnClassificationResult.Failed("failed")),
            "exception" => new RecordingClassifier(new InvalidOperationException("classifier failed")),
            "unknown-intent" => new RecordingClassifier(
                AgentProfileTurnClassificationResult.Matched("intent-default")),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind), failureKind, null),
        };

        var result = await NewMaterializer(RegistryWithRoute(tools), classifier)
            .MaterializeWithPreparationAsync(
                Seal(binding),
                "classify this",
                tools,
                ToolContext());

        classifier.CallCount.Should().Be(1);
        result.Preparation.Authority.CandidateRoute.Should().BeNull();
        result.Preparation.Authority.SelectedExactSkillRef.Should().BeNull();
        result.Preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
        result.Preparation.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierFailed);
        result.Catalog.SelectedSkillPromptLayer.Should().BeNull();
    }

    [Fact]
    public async Task RoutedAliasCollision_ShouldNotSelectDefault()
    {
        var tools = NewTools("recovery-tool", "task-tool");
        var binding = BuildBinding(AgentProfileActivationMode.Enforced);
        AddRoutedMember(binding, "intent-beta", "alpha beta");
        AddDefaultMember(binding, "fallback");
        var classifier = new RecordingClassifier(
            new InvalidOperationException("alias collision must not classify"));

        var result = await NewMaterializer(RegistryWithRoute(tools), classifier)
            .MaterializeWithPreparationAsync(
                Seal(binding),
                "alpha beta request",
                tools,
                ToolContext());

        classifier.CallCount.Should().Be(0);
        result.Preparation.Authority.CandidateRoute.Should().BeNull();
        result.Preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
        result.Preparation.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierFailed &&
            diagnostic.Detail == "alias_collision");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task PrepareAsync_BlankSessionId_ShouldRejectBeforeToolRegistryIo(string sessionId)
    {
        var registry = RegistryWithRoute(NewTools("recovery-tool", "task-tool"));

        var act = () => NewMaterializer(
                registry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()))
            .PrepareAsync(
                Seal(BuildBinding(AgentProfileActivationMode.Enforced)),
                sessionId,
                "alpha run",
                [],
                ToolContext());

        await act.Should().ThrowAsync<ArgumentException>();
        registry.ResolveCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task AliasPrefixWithoutWhitespaceBoundary_ShouldUseClassifierRecovery()
    {
        var tools = NewTools("recovery-tool", "task-tool");
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch());

        var result = await NewMaterializer(RegistryWithRoute(tools), classifier)
            .MaterializeWithPreparationAsync(
                Seal(BuildBinding(AgentProfileActivationMode.Enforced)),
                "alphabet request",
                tools,
                ToolContext());

        classifier.CallCount.Should().Be(1);
        result.Preparation.Authority.CandidateRoute.Should().BeNull();
        result.Preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
        result.Catalog.FinalAllowedToolNames.Should().Equal("recovery-tool");
    }

    [Theory]
    [InlineData("exception", 1)]
    [InlineData("unknown-intent", 1)]
    [InlineData("empty-catalog", 0)]
    public async Task ClassifierFailure_ShouldUseRecoveryWithoutSelectedBody(
        string failureKind,
        int expectedClassifierCalls)
    {
        var tools = NewTools("recovery-tool", "task-tool");
        var binding = BuildBinding(AgentProfileActivationMode.Enforced);
        binding.Members[0].ExplicitTriggerAliases.Clear();
        if (failureKind == "empty-catalog")
            binding.Members.Clear();
        var classifier = failureKind switch
        {
            "exception" => new RecordingClassifier(new InvalidOperationException("classifier failed")),
            "unknown-intent" => new RecordingClassifier(
                AgentProfileTurnClassificationResult.Matched("intent-unknown")),
            "empty-catalog" => new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
            _ => throw new InvalidOperationException("Unknown classifier failure kind."),
        };

        var result = await NewMaterializer(RegistryWithRoute(tools), classifier)
            .MaterializeWithPreparationAsync(
                Seal(binding),
                "classify this",
                tools,
                ToolContext());

        classifier.CallCount.Should().Be(expectedClassifierCalls);
        result.Preparation.Authority.CandidateRoute.Should().BeNull();
        result.Preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
        result.Preparation.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierFailed);
        result.Catalog.SelectedSkillPromptLayer.Should().BeNull();
        result.Catalog.FinalAllowedToolNames.Should().Equal("recovery-tool");
    }

    [Fact]
    public async Task NoMatchThenAliasOnNextTurn_ShouldSelectWithoutLeakingPriorRecovery()
    {
        var tools = NewTools("recovery-tool", "task-tool");
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch());
        var materializer = NewMaterializer(RegistryWithRoute(tools), classifier);
        var binding = Seal(BuildBinding(AgentProfileActivationMode.Enforced));

        var first = await materializer.PrepareAsync(
            binding,
            "session-first",
            "unmatched",
            tools,
            ToolContext());
        var corrected = await materializer.PrepareAsync(
            binding,
            "session-corrected",
            "alpha run",
            tools,
            ToolContext());

        first.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
        first.Authority.CandidateRoute.Should().BeNull();
        corrected.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Selected);
        corrected.Authority.CandidateRoute!.IntentId.Should().Be("intent-alpha");
        classifier.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task TamperedAdmission_ShouldFailClosedBeforeClassification()
    {
        var tools = NewTools("recovery-tool", "task-tool");
        var binding = Seal(BuildBinding(AgentProfileActivationMode.Enforced));
        binding.Admission.RolloutStage = "tampered";
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha"));
        var registry = RegistryWithRoute(tools);

        var preparation = await NewMaterializer(registry, classifier)
            .PrepareAsync(binding, "session-tampered", "alpha run", tools, ToolContext());

        AgentProfileExecutionBindingCodec.Verify(binding).Should().BeFalse();
        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.RestrictedEmpty);
        preparation.Authority.AuthorityCeilingToolNames.Should().BeEmpty();
        preparation.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ProfileInvalid);
        classifier.CallCount.Should().Be(0);
        registry.ResolveCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task RouteAndRegisteredSameNameDifferentReference_ShouldFailClosedWithoutGrantingCollision()
    {
        var routeTools = NewTools("recovery-tool", "task-tool");
        var registered = NewTools("recovery-tool", "task-tool");
        var binding = Seal(BuildBinding(AgentProfileActivationMode.Enforced));

        var preparation = await NewMaterializer(
                RegistryWithRoute(routeTools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()))
            .PrepareAsync(binding, "session-collision", "alpha run", registered, ToolContext());

        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.RestrictedEmpty);
        preparation.Authority.AuthorityCeilingToolNames.Should().BeEmpty();
        preparation.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ToolNameCollision);
    }

    [Theory]
    [InlineData("resolve", AgentProfileTurnDiagnosticCode.RouteToolSetUnavailable)]
    [InlineData("discovery", AgentProfileTurnDiagnosticCode.ToolDiscoveryFailed)]
    public async Task RouteToolFailures_ShouldRestrictEmptyBeforeClassification(
        string failureKind,
        AgentProfileTurnDiagnosticCode expectedDiagnostic)
    {
        IToolSetRegistry registry;
        if (failureKind == "resolve")
        {
            registry = new ThrowingToolSetRegistry();
        }
        else
        {
            var recordingRegistry = new RecordingToolSetRegistry();
            recordingRegistry.Add(RouteToolSet, new ThrowingToolSource());
            registry = recordingRegistry;
        }
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch());

        var preparation = await NewMaterializer(registry, classifier)
            .PrepareAsync(
                Seal(BuildBinding(AgentProfileActivationMode.Enforced)),
                "session-route-failure",
                "alpha run",
                [],
                ToolContext());

        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.RestrictedEmpty);
        preparation.Authority.AuthorityCeilingToolNames.Should().BeEmpty();
        preparation.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == expectedDiagnostic);
        classifier.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("collision", AgentProfileTurnDiagnosticCode.ToolNameCollision)]
    [InlineData("capability", AgentProfileTurnDiagnosticCode.ToolCapabilityRejected)]
    public async Task RouteToolRejection_ShouldRestrictEmpty(
        string rejectionKind,
        AgentProfileTurnDiagnosticCode expectedDiagnostic)
    {
        var recovery = new TestTool("recovery-tool");
        IReadOnlyList<IAgentTool> routeTools = rejectionKind == "collision"
            ? [recovery, new TestTool("task-tool"), new TestTool("task-tool")]
            : [recovery, new CapabilityTool(
                "task-tool",
                [AgentToolCapabilities.ExcludeFromDirectChannelChat])];

        var preparation = await NewMaterializer(
                RegistryWithRoute(routeTools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()))
            .PrepareAsync(
                Seal(BuildBinding(AgentProfileActivationMode.Enforced)),
                "session-route-rejection",
                "alpha run",
                [],
                ToolContext());

        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.RestrictedEmpty);
        preparation.Authority.AuthorityCeilingToolNames.Should().BeEmpty();
        preparation.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == expectedDiagnostic);
    }

    [Fact]
    public async Task PolicyToolSetRefs_ShouldOnlyAttenuateFinalAuthority()
    {
        var routeTools = NewTools("recovery-tool", "task-tool", "outside-route");
        var registry = RegistryWithRoute(routeTools);
        registry.Add("profile.maximum", new StaticToolSource(NewTools("task-tool", "outside-route")));
        registry.Add("profile.recovery", new StaticToolSource(NewTools("recovery-tool", "outside-route")));
        registry.Add("member.task", new StaticToolSource(NewTools("task-tool", "outside-route")));
        var binding = BuildBinding(AgentProfileActivationMode.Enforced);
        binding.EffectiveMaximumToolPolicy.ToolSetRefs.Add(
            ["profile.maximum", "profile.recovery", "member.task"]);
        binding.EffectiveRecoveryToolPolicy.ToolSetRefs.Add("profile.recovery");
        binding.Members[0].TaskToolPolicy.ToolSetRefs.Add("member.task");

        var result = await NewMaterializer(
                registry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()))
            .MaterializeWithPreparationAsync(
                Seal(binding),
                "alpha run",
                routeTools,
                ToolContext("recovery-tool", "task-tool"));

        result.Catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery-tool", "task-tool");
        result.Catalog.FinalAllowedToolNames.Should().NotContain("outside-route");
    }

    [Fact]
    public async Task TaskPolicyResolutionFailure_ShouldCommitRecoveryWithoutSelectedIdentityOrPrompt()
    {
        const string taskPolicyToolSet = "member.task";
        var tools = NewTools("recovery-tool", "task-tool");
        var registry = RegistryWithRoute(tools);
        registry.Add(taskPolicyToolSet, new FailOnSecondDiscoveryToolSource(NewTools("task-tool")));
        var binding = BuildBinding(AgentProfileActivationMode.Enforced);
        binding.EffectiveMaximumToolPolicy.ToolSetRefs.Add(taskPolicyToolSet);
        binding.Members[0].TaskToolPolicy.ToolSetRefs.Add(taskPolicyToolSet);
        var sealedBinding = Seal(binding);
        var materializer = NewMaterializer(
            registry,
            new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()));

        var preparation = await materializer.PrepareAsync(
            sealedBinding,
            "session-task-policy-failure",
            "alpha run",
            tools,
            ToolContext());
        var materialization = await materializer.MaterializeCommittedAsync(
            sealedBinding,
            preparation.Authority,
            tools,
            ToolContext());

        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
        preparation.Authority.AuthorityCeilingToolNames.Should().Equal("recovery-tool");
        preparation.Authority.CandidateRoute.Should().BeNull();
        preparation.Authority.SelectedExactSkillRef.Should().BeNull();
        materialization.ReconcileProposal.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
        materialization.ReconcileProposal.CandidateRoute.Should().BeNull();
        materialization.ReconcileProposal.SelectedExactSkillRef.Should().BeNull();
        materialization.Catalog.SelectedIntentId.Should().BeNull();
        materialization.Catalog.SelectedSkillPromptLayer.Should().BeNull();
        materialization.Catalog.FinalAllowedToolNames.Should().Equal("recovery-tool");
    }

    [Fact]
    public async Task MaterializationRecoveryDegradation_ShouldClearCommittedSelectedIdentity()
    {
        const string maximumToolSet = "profile.maximum";
        var tools = NewTools("recovery-tool", "task-tool");
        var registry = RegistryWithRoute(tools);
        registry.Add(maximumToolSet, new FailOnSecondDiscoveryToolSource(NewTools("task-tool")));
        var binding = BuildBinding(AgentProfileActivationMode.Enforced);
        binding.EffectiveMaximumToolPolicy.ToolSetRefs.Add(maximumToolSet);
        var sealedBinding = Seal(binding);
        var materializer = NewMaterializer(
            registry,
            new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()));
        var preparation = await materializer.PrepareAsync(
            sealedBinding,
            "session-materialization-degradation",
            "alpha run",
            tools,
            ToolContext());

        var materialization = await materializer.MaterializeCommittedAsync(
            sealedBinding,
            preparation.Authority,
            tools,
            ToolContext());

        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Selected);
        materialization.ReconcileProposal.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
        materialization.ReconcileProposal.CandidateRoute.Should().BeNull();
        materialization.ReconcileProposal.SelectedExactSkillRef.Should().BeNull();
        materialization.Catalog.SelectedIntentId.Should().BeNull();
        materialization.Catalog.SelectedSkillPromptLayer.Should().BeNull();
        materialization.Catalog.FinalAllowedToolNames.Should().Equal("recovery-tool");
    }

    [Fact]
    public async Task TamperedBinding_ShouldRestrictEmptyBeforeToolRegistryIo()
    {
        var binding = Seal(BuildBinding(AgentProfileActivationMode.Enforced));
        binding.Members[0].InstructionBody = "tampered";
        var registry = RegistryWithRoute(NewTools("recovery-tool", "task-tool"));

        var preparation = await NewMaterializer(
                registry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()))
            .PrepareAsync(binding, "session-tampered", "alpha run", [], ToolContext());

        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.RestrictedEmpty);
        preparation.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ProfileInvalid);
        registry.ResolveCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task DifferentCommittedBindingProvenance_ShouldRestrictEmptyWithoutPromptOrToolRegistryIo()
    {
        const string replacementInstructions = "Replacement instructions must never reach the prompt.";
        var tools = NewTools("recovery-tool", "task-tool");
        var original = Seal(BuildBinding(AgentProfileActivationMode.Enforced));
        var preparation = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()))
            .PrepareAsync(original, "session-a", "alpha run", tools, ToolContext());
        var replacement = BuildBinding(AgentProfileActivationMode.Enforced);
        replacement.Source.StateVersion++;
        replacement.ProfileInstructions = replacementInstructions;
        var replacementRegistry = RegistryWithRoute(tools);

        var materialization = await NewMaterializer(
                replacementRegistry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()))
            .MaterializeCommittedAsync(Seal(replacement), preparation.Authority, tools, ToolContext());
        var composition = SystemPromptLayerComposer.Compose(
            new KernelPromptLayer("kernel", new KernelPromptProvenance("test")),
            new BuiltInPromptFloorLayer("floor", new BuiltInPromptFloorProvenance("test")),
            global: null,
            materialization.Catalog.ProfilePromptLayer,
            materialization.Catalog.SelectedSkillPromptLayer,
            runtimeFacts: null,
            conversation: null);

        materialization.ReconcileProposal.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.RestrictedEmpty);
        materialization.ReconcileProposal.CandidateRoute.Should().BeNull();
        materialization.ReconcileProposal.SelectedExactSkillRef.Should().BeNull();
        materialization.Catalog.FinalAllowedToolNames.Should().BeEmpty();
        materialization.Catalog.ProfilePromptLayer.Should().BeNull();
        materialization.Catalog.SelectedSkillPromptLayer.Should().BeNull();
        materialization.Catalog.SelectedIntentId.Should().BeNull();
        materialization.Catalog.CandidateIntentId.Should().BeNull();
        composition.Profile.Included.Should().BeFalse();
        composition.Prompt.Should().NotContain(replacementInstructions);
        replacementRegistry.ResolveCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task DifferentCommittedExactReference_ShouldRestrictEmptyWithoutPromptInjection()
    {
        var tools = NewTools("recovery-tool", "task-tool");
        var binding = Seal(BuildBinding(AgentProfileActivationMode.Enforced));
        var preparation = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()))
            .PrepareAsync(binding, "session-a", "alpha run", tools, ToolContext());
        var committedAuthority = preparation.Authority;
        committedAuthority.SelectedExactSkillRef.Guid = "22222222-2222-2222-2222-222222222222";

        var materialization = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(new InvalidOperationException("must not classify")))
            .MaterializeCommittedAsync(binding, committedAuthority, tools, ToolContext());

        materialization.ReconcileProposal.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.RestrictedEmpty);
        materialization.Catalog.FinalAllowedToolNames.Should().BeEmpty();
        materialization.Catalog.SelectedSkillPromptLayer.Should().BeNull();
        materialization.Catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ExactSkillIdentityMismatch);
    }

    [Fact]
    public async Task MaterializationFailureThenRetry_ShouldNeverWidenCommittedAuthority()
    {
        var tools = NewTools("recovery-tool", "task-tool");
        var binding = Seal(BuildBinding(AgentProfileActivationMode.Enforced));
        var preparation = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()))
            .PrepareAsync(binding, "session-a", "alpha run", tools, ToolContext());
        var failed = await NewMaterializer(
                new RecordingToolSetRegistry(),
                new RecordingClassifier(new InvalidOperationException("must not classify")))
            .MaterializeCommittedAsync(binding, preparation.Authority, tools, ToolContext());

        var retry = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(new InvalidOperationException("must not classify")))
            .MaterializeCommittedAsync(binding, failed.ReconcileProposal, tools, ToolContext());

        failed.ReconcileProposal.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.RestrictedEmpty);
        failed.ReconcileProposal.AuthorityCeilingToolNames.Should().BeEmpty();
        retry.ReconcileProposal.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.RestrictedEmpty);
        retry.ReconcileProposal.AuthorityCeilingToolNames.Should().BeEmpty();
        retry.Catalog.FinalAllowedToolNames.Should().BeEmpty();
    }

    [Fact]
    public async Task CallerCancellationDuringClassification_ShouldPropagate()
    {
        var tools = NewTools("recovery-tool", "task-tool");
        var binding = BuildBinding(AgentProfileActivationMode.Enforced);
        binding.Members[0].ExplicitTriggerAliases.Clear();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => NewMaterializer(RegistryWithRoute(tools), new CancellationAwareClassifier())
            .PrepareAsync(Seal(binding), "session-cancel", "classify", tools, ToolContext(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CallerCancellationDuringToolDiscovery_ShouldPropagate()
    {
        var registry = new RecordingToolSetRegistry();
        registry.Add(RouteToolSet, new CancellationAwareToolSource());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => NewMaterializer(
                registry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()))
            .PrepareAsync(
                Seal(BuildBinding(AgentProfileActivationMode.Enforced)),
                "session-cancel",
                "alpha run",
                [],
                ToolContext(),
                cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("token", true)]
    public async Task HumanSessionTool_ShouldRespectCallerCredentialCapability(
        string? accessToken,
        bool expectedAllowed)
    {
        var humanTool = new CapabilityTool(
            "task-tool",
            [AgentToolCapabilities.RequiresHumanSession]);
        var recoveryTool = new TestTool("recovery-tool");
        var tools = new IAgentTool[] { recoveryTool, humanTool };

        var result = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()))
            .MaterializeWithPreparationAsync(
                Seal(BuildBinding(AgentProfileActivationMode.Enforced)),
                "alpha run",
                tools,
                ToolContextWithAccessToken(accessToken));

        if (expectedAllowed)
            result.Catalog.FinalAllowedToolNames.Should().Contain("task-tool");
        else
            result.Catalog.FinalAllowedToolNames.Should().NotContain("task-tool");
    }

    private static AgentProfileTurnCatalogMaterializer NewMaterializer(
        IToolSetRegistry registry,
        IAgentProfileTurnClassifier classifier) =>
        new(registry, classifier);

    private static AgentProfileExecutionBinding BuildBinding(AgentProfileActivationMode activationMode)
    {
        var binding = AgentProfileExecutionBindingCodecTests.BuildExecutionBinding(activationMode);
        binding.Admission.RouteToolSetRef = RouteToolSet;
        binding.Members[0].SkillProvenance.ExactSkillRef.Guid = SkillGuid;
        binding.Members[0].SkillProvenance.ExactSkillRef.LiteralVersion = SkillVersion;
        return binding;
    }

    private static AgentProfileExecutionMember AddDefaultMember(
        AgentProfileExecutionBinding binding,
        string alias)
    {
        var member = AddMember(
            binding,
            "intent-default",
            alias,
            AgentProfileExecutionMemberActivationMode.DefaultForUnmatchedTurn);
        member.InstructionBody = "Use the default sealed procedure.";
        member.InstructionBodySha256 = Google.Protobuf.ByteString.CopyFrom(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(member.InstructionBody)));
        member.SkillProvenance.ExactSkillRef.Guid =
            "22222222-2222-2222-2222-222222222222";
        member.SkillProvenance.ExactSkillRef.LiteralVersion = "2.0";
        return member;
    }

    private static AgentProfileExecutionMember AddAlwaysMember(
        AgentProfileExecutionBinding binding,
        string instructionBody,
        string skillGuid)
    {
        var member = binding.Members[0].Clone();
        AgentProfileExecutionBindingCodecTests.ConfigureAlwaysMember(member, instructionBody);
        member.SkillProvenance.ExactSkillRef.Guid = skillGuid;
        binding.Members.Add(member);
        return member;
    }

    private static AgentProfileExecutionMember AddRoutedMember(
        AgentProfileExecutionBinding binding,
        string intentId,
        string alias) =>
        AddMember(
            binding,
            intentId,
            alias,
            AgentProfileExecutionMemberActivationMode.Routed);

    private static AgentProfileExecutionMember AddMember(
        AgentProfileExecutionBinding binding,
        string intentId,
        string alias,
        AgentProfileExecutionMemberActivationMode activationMode)
    {
        var member = binding.Members[0].Clone();
        member.IntentId = intentId;
        member.RoutingDescription = $"Handle {intentId} requests.";
        member.ActivationMode = activationMode;
        member.ExplicitTriggerAliases.Clear();
        member.ExplicitTriggerAliases.Add(alias);
        binding.Members.Add(member);
        return member;
    }

    private static AgentProfileExecutionBinding Seal(AgentProfileExecutionBinding binding) =>
        AgentProfileExecutionBindingCodec.Seal(binding);

    private static AgentToolExecutionContext ToolContext(
        params string[] visibleToolNames) =>
        ToolContextWithAccessToken("token", visibleToolNames);

    private static AgentToolExecutionContext ToolContextWithAccessToken(
        string? accessToken,
        params string[] visibleToolNames) =>
        AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(accessToken, null, null),
            ToolVisibility = visibleToolNames.Length == 0
                ? AgentToolVisibilityScope.Unrestricted
                : new AgentToolVisibilityScope(
                    visibleToolNames.ToHashSet(StringComparer.OrdinalIgnoreCase)),
        };

    private static IReadOnlyList<IAgentTool> NewTools(params string[] names) =>
        names.Select(static name => (IAgentTool)new TestTool(name)).ToArray();

    private static RecordingToolSetRegistry RegistryWithRoute(IReadOnlyList<IAgentTool> tools)
    {
        var registry = new RecordingToolSetRegistry();
        registry.Add(RouteToolSet, new StaticToolSource(tools));
        return registry;
    }

    private sealed class RecordingClassifier : IAgentProfileTurnClassifier
    {
        private readonly AgentProfileTurnClassificationResult? _result;
        private readonly Exception? _exception;

        public RecordingClassifier(AgentProfileTurnClassificationResult result) => _result = result;
        public RecordingClassifier(Exception exception) => _exception = exception;

        public int CallCount { get; private set; }
        public List<AgentProfileTurnClassificationRequest> Requests { get; } = [];

        public Task<AgentProfileTurnClassificationResult> ClassifyAsync(
            AgentProfileTurnClassificationRequest request,
            CancellationToken ct = default)
        {
            CallCount++;
            Requests.Add(request);
            return _exception is null
                ? Task.FromResult(_result!)
                : Task.FromException<AgentProfileTurnClassificationResult>(_exception);
        }
    }

    private sealed class CancellationAwareClassifier : IAgentProfileTurnClassifier
    {
        public Task<AgentProfileTurnClassificationResult> ClassifyAsync(
            AgentProfileTurnClassificationRequest request,
            CancellationToken ct = default) =>
            Task.FromCanceled<AgentProfileTurnClassificationResult>(ct);
    }

    private sealed class RecordingToolSetRegistry : IToolSetRegistry
    {
        private readonly Dictionary<string, IReadOnlyList<IAgentToolSource>> _sources =
            new(StringComparer.Ordinal);

        public List<string> ResolveCalls { get; } = [];

        public void Add(string name, params IAgentToolSource[] sources) => _sources.Add(name, sources);

        public IReadOnlyList<string> GetRegisteredNames() => _sources.Keys.ToArray();

        public ToolSetResolveResult Resolve(ChatRouteToolSetRef? toolSetRef)
        {
            var name = toolSetRef?.Name ?? string.Empty;
            ResolveCalls.Add(name);
            return _sources.TryGetValue(name, out var sources)
                ? ToolSetResolveResult.Success(name, sources)
                : ToolSetResolveResult.Failure(new ToolSetResolveError(
                    ToolSetResolveError.UnknownNameCode,
                    name,
                    "missing",
                    GetRegisteredNames()));
        }
    }

    private sealed class ThrowingToolSetRegistry : IToolSetRegistry
    {
        public IReadOnlyList<string> GetRegisteredNames() => [RouteToolSet];

        public ToolSetResolveResult Resolve(ChatRouteToolSetRef? toolSetRef) =>
            throw new InvalidOperationException("registry unavailable");
    }

    private sealed class StaticToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult(tools);
    }

    private sealed class FailOnSecondDiscoveryToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        private int _callCount;

        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            _callCount++;
            return _callCount == 2
                ? Task.FromException<IReadOnlyList<IAgentTool>>(
                    new InvalidOperationException("second discovery failed"))
                : Task.FromResult(tools);
        }
    }

    private sealed class CancellationAwareToolSource : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromCanceled<IReadOnlyList<IAgentTool>>(ct);
    }

    private sealed class ThrowingToolSource : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<IAgentTool>>(new InvalidOperationException("discovery failed"));
    }

    private class TestTool(string name) : IAgentTool
    {
        public string Name => name;
        public string Description => name;
        public string ParametersSchema => "{}";
        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            Task.FromResult("{}");
    }

    private sealed class CapabilityTool(string name, IReadOnlyCollection<string> capabilities)
        : TestTool(name), IAgentToolCapabilityDescriptor
    {
        public IReadOnlyCollection<string> Capabilities { get; } = capabilities;
    }
}

internal static class AgentProfileTurnCatalogMaterializerTestExtensions
{
    public static async Task<(
        AgentProfileTurnCatalog Catalog,
        AgentProfileTurnAuthorityPreparation Preparation)> MaterializeWithPreparationAsync(
        this AgentProfileTurnCatalogMaterializer materializer,
        AgentProfileExecutionBinding binding,
        string userMessage,
        IReadOnlyList<IAgentTool> registeredTools,
        AgentToolExecutionContext toolContext,
        CancellationToken ct = default)
    {
        var preparation = await materializer.PrepareAsync(
            binding,
            "materializer-test-session",
            userMessage,
            registeredTools,
            toolContext,
            ct);
        var materialization = await materializer.MaterializeCommittedAsync(
            binding,
            preparation.Authority,
            registeredTools,
            toolContext,
            ct);
        return (materialization.Catalog, preparation);
    }
}
