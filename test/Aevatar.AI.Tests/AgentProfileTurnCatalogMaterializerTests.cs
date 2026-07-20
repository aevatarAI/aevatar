using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class AgentProfileTurnCatalogMaterializerTests
{
    private const string SkillGuid = "11111111-1111-1111-1111-111111111111";
    private const string SkillVersion = "1.2";
    private const string SkillName = "skill-alpha";
    private const string PublisherId = "publisher-alpha";
    private const string SkillMarkdown = "---\nname: skill-alpha\n---\nSelected instructions.";

    [Fact]
    public async Task MaterializeAsync_EnforcedAlias_ShouldSelectBodyAndAttenuatedPolicy()
    {
        var tools = NewTools("recovery", "task", "extra");
        var registry = RegistryWithRoute(tools);
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch());
        var fetcher = new RecordingFetcher(SuccessfulFetch());
        var profile = SealProfile(BuildProfile(withAlias: true));

        var catalog = await NewMaterializer(registry, classifier, fetcher)
            .MaterializeAsync(profile, "/alpha now", "token", tools, ToolContext(), CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery", "task");
        catalog.SelectedIntentId.Should().Be("intent-alpha");
        catalog.CandidateIntentId.Should().Be("intent-alpha");
        catalog.SelectedSkillPromptLayer!.Content.Should().Be("Selected instructions.");
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.AliasMatched);
        classifier.CallCount.Should().Be(0);
        fetcher.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task MaterializeAsync_PolicyToolSetRefs_ShouldOnlyAttenuateFinalAuthority()
    {
        var routeTools = NewTools(
            "recovery-from-set",
            "task-from-set",
            "route-only",
            "visibility-blocked");
        var registeredTools = NewTools(
            "recovery-from-set",
            "task-from-set",
            "route-only",
            "visibility-blocked",
            "registered-only");
        var registry = new RecordingToolSetRegistry();
        registry.Add("profile.route", new StaticToolSource(routeTools));
        registry.Add("maximum.policy", new StaticToolSource(NewTools(
            "recovery-from-set",
            "task-from-set",
            "maximum-only")));
        registry.Add("recovery.policy", new StaticToolSource(NewTools(
            "recovery-from-set",
            "recovery-outside-maximum")));
        registry.Add("task.policy", new StaticToolSource(NewTools(
            "task-from-set",
            "task-outside-maximum")));
        var profile = BuildProfile(withAlias: true);
        profile.MaximumToolPolicy.ToolNames.Clear();
        profile.MaximumToolPolicy.ToolSetRefs.Add("maximum.policy");
        profile.RecoveryToolPolicy.ToolNames.Clear();
        profile.RecoveryToolPolicy.ToolSetRefs.Add("recovery.policy");
        profile.Members[0].TaskToolPolicy.ToolNames.Clear();
        profile.Members[0].TaskToolPolicy.ToolSetRefs.Add("task.policy");
        var toolContext = ToolContext() with
        {
            ToolVisibility = AgentToolVisibilityScope.FromAllowedToolNames(
                ["recovery-from-set", "task-from-set", "route-only"]),
        };

        var catalog = await NewMaterializer(
                registry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                registeredTools,
                toolContext,
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery-from-set", "task-from-set");
        catalog.FinalAllowedToolNames.Should().NotContain([
            "route-only",
            "visibility-blocked",
            "registered-only",
            "maximum-only",
            "recovery-outside-maximum",
            "task-outside-maximum",
        ]);
        registry.ResolveCalls.Should().Equal(
            "profile.route",
            "maximum.policy",
            "recovery.policy",
            "task.policy");
    }

    [Fact]
    public async Task MaterializeAsync_DuplicateAlias_ShouldUseRecoveryWithoutFetching()
    {
        var tools = NewTools("recovery", "task", "extra");
        var profile = BuildProfile(withAlias: true);
        var collidingMember = profile.Members[0].Clone();
        collidingMember.IntentId = "intent-beta";
        collidingMember.RoutingDescription = "Route beta requests.";
        profile.Members.Add(collidingMember);
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch());
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(RegistryWithRoute(tools), classifier, fetcher)
            .MaterializeAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.SelectedIntentId.Should().BeNull();
        catalog.CandidateIntentId.Should().BeNull();
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierFailed &&
            diagnostic.Detail == "alias_collision");
        classifier.CallCount.Should().Be(0);
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_AliasPrefixWithoutBoundary_ShouldUseClassifierAndRecovery()
    {
        var tools = NewTools("recovery", "task", "extra");
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch());
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(RegistryWithRoute(tools), classifier, fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile(withAlias: true)),
                "/alphabet",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.SelectedIntentId.Should().BeNull();
        catalog.CandidateIntentId.Should().BeNull();
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierNoMatch);
        classifier.CallCount.Should().Be(1);
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_ClassifierMatch_ShouldSelectExactMember()
    {
        var tools = NewTools("recovery", "task", "extra");
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha"));
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(RegistryWithRoute(tools), classifier, fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "classify me",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.SelectedIntentId.Should().Be("intent-alpha");
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierMatched);
        classifier.CallCount.Should().Be(1);
        classifier.LastRequest!.Candidates.Should().ContainSingle(candidate =>
            candidate.IntentId == "intent-alpha" && candidate.RoutingDescription == "Route alpha requests.");
    }

    [Fact]
    public async Task MaterializeAsync_ClassifierNoMatch_ShouldUseRecoveryOnly()
    {
        var tools = NewTools("recovery", "task", "extra");
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "no route",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.SelectedIntentId.Should().BeNull();
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierNoMatch);
        fetcher.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData(true, 600)]
    [InlineData(false, 0)]
    public async Task MaterializeAsync_ClassifierNotConfigured_ShouldUseRecoveryWithoutClassifierOrFetch(
        bool removeMembers,
        int classifierTimeoutMs)
    {
        var tools = NewTools("recovery", "task", "extra");
        var profile = BuildProfile();
        profile.ClassifierTimeoutMs = classifierTimeoutMs;
        if (removeMembers)
            profile.Members.Clear();
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha"));
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(RegistryWithRoute(tools), classifier, fetcher)
            .MaterializeAsync(
                SealProfile(profile),
                "classify",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.SelectedIntentId.Should().BeNull();
        catalog.CandidateIntentId.Should().BeNull();
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierFailed &&
            diagnostic.Detail == "classifier_not_configured");
        classifier.CallCount.Should().Be(0);
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_CallerCancellationDuringClassification_ShouldPropagate()
    {
        var tools = NewTools("recovery", "task", "extra");
        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();
        var classifier = new CancellationAwareClassifier();

        var act = async () => await NewMaterializer(
                RegistryWithRoute(tools),
                classifier,
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "classify",
                "token",
                tools,
                ToolContext(),
                callerCts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        classifier.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task MaterializeAsync_CallerCancellationDuringToolDiscovery_ShouldPropagate()
    {
        var tools = NewTools("recovery", "task", "extra");
        var registry = new RecordingToolSetRegistry();
        registry.Add("profile.route", new CancellationAwareToolSource());
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch());
        var fetcher = new RecordingFetcher(SuccessfulFetch());
        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();

        var act = async () => await NewMaterializer(registry, classifier, fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "classify",
                "token",
                tools,
                ToolContext(),
                callerCts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        classifier.CallCount.Should().Be(0);
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_ClassifierException_ShouldFailClosedToRecovery()
    {
        var tools = NewTools("recovery", "task", "extra");
        var classifier = new RecordingClassifier(new InvalidOperationException("classifier failed"));

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                classifier,
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "classify",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierFailed &&
            diagnostic.Detail == "classifier_exception");
    }

    [Fact]
    public async Task MaterializeAsync_ClassifierReturnsUnknownIntent_ShouldUseRecoveryWithoutFetching()
    {
        var tools = NewTools("recovery", "task", "extra");
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-outside-profile")),
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "classify",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.FinalAllowedToolNames.Should().NotContain("task");
        catalog.SelectedIntentId.Should().BeNull();
        catalog.CandidateIntentId.Should().BeNull();
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ClassifierFailed &&
            diagnostic.Detail == "unknown_intent");
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_Shadow_ShouldKeepCandidateDiagnosticWithoutFetchingOrResolvingTaskPolicy()
    {
        var tools = NewTools("recovery", "task", "extra");
        var registry = RegistryWithRoute(tools);
        var profile = BuildProfile(withAlias: true);
        profile.ActivationMode = AgentProfileActivationMode.Shadow;
        profile.Members[0].TaskToolPolicy.ToolSetRefs.Add("candidate-only");
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(
                registry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                fetcher)
            .MaterializeAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.CandidateIntentId.Should().Be("intent-alpha");
        catalog.SelectedIntentId.Should().BeNull();
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ShadowCandidate);
        fetcher.CallCount.Should().Be(0);
        registry.ResolveCalls.Should().NotContain("candidate-only");
    }

    [Fact]
    public async Task MaterializeAsync_ExactFetchIdentityOrBodyFailure_ShouldUseRecoveryOnly()
    {
        var tools = NewTools("recovery", "task", "extra");
        var failures = new[]
        {
            ExactRemoteSkillFetchResult.Failed(ExactRemoteSkillFetchFailureCode.NotFound),
            ExactRemoteSkillFetchResult.Success(
                SkillGuid, SkillVersion, "wrong-name", PublisherId, "hash", SkillMarkdown),
            ExactRemoteSkillFetchResult.Success(
                SkillGuid, SkillVersion, SkillName, PublisherId, "hash", new string('x', 300)),
        };

        foreach (var failure in failures)
        {
            var catalog = await NewMaterializer(
                    RegistryWithRoute(tools),
                    new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha")),
                    new RecordingFetcher(failure))
                .MaterializeAsync(
                    SealProfile(BuildProfile()),
                    "select",
                    "token",
                    tools,
                    ToolContext(),
                    CancellationToken.None);

            catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
            catalog.SelectedSkillPromptLayer.Should().BeNull();
            catalog.Diagnostics.Should().Contain(diagnostic =>
                diagnostic.Code == AgentProfileTurnDiagnosticCode.ExactSkillFetchFailed ||
                diagnostic.Code == AgentProfileTurnDiagnosticCode.ExactSkillIdentityMismatch ||
                diagnostic.Code == AgentProfileTurnDiagnosticCode.SelectedSkillBodyInvalid);
        }
    }

    [Theory]
    [InlineData("22222222-2222-2222-2222-222222222222", SkillVersion, PublisherId, "hash-alpha")]
    [InlineData(SkillGuid, "9.9", PublisherId, "hash-alpha")]
    [InlineData(SkillGuid, SkillVersion, "publisher-beta", "hash-alpha")]
    [InlineData(SkillGuid, SkillVersion, PublisherId, " ")]
    public async Task MaterializeAsync_ExactFetchIdentityMismatch_ShouldUseRecoveryOnly(
        string fetchedGuid,
        string fetchedVersion,
        string fetchedPublisherId,
        string fetchedSkillHash)
    {
        var tools = NewTools("recovery", "task", "extra");
        var fetcher = new RecordingFetcher(ExactRemoteSkillFetchResult.Success(
            fetchedGuid,
            fetchedVersion,
            SkillName,
            fetchedPublisherId,
            fetchedSkillHash,
            SkillMarkdown));

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha")),
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "select",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.FinalAllowedToolNames.Should().NotContain("task");
        catalog.SelectedIntentId.Should().BeNull();
        catalog.CandidateIntentId.Should().Be("intent-alpha");
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ExactSkillIdentityMismatch);
        fetcher.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task MaterializeAsync_EmptySelectedSkillBody_ShouldUseRecoveryWithoutPromptInjection()
    {
        var tools = NewTools("recovery", "task", "extra");
        var fetcher = new RecordingFetcher(SuccessfulFetch("---\nname: skill-alpha\n---\n   "));

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile(withAlias: true)),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.SelectedIntentId.Should().BeNull();
        catalog.CandidateIntentId.Should().Be("intent-alpha");
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.SelectedSkillBodyInvalid &&
            diagnostic.Detail == "frontmatter_identity_invalid");
        fetcher.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task MaterializeAsync_SelectedSkillFrontmatterNameMismatch_ShouldUseRecoveryWithoutPromptInjection()
    {
        var tools = NewTools("recovery", "task", "extra");
        var fetcher = new RecordingFetcher(SuccessfulFetch(
            "---\nname: skill-beta\n---\nSelected instructions."));

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile(withAlias: true)),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.SelectedIntentId.Should().BeNull();
        catalog.CandidateIntentId.Should().Be("intent-alpha");
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.SelectedSkillBodyInvalid &&
            diagnostic.Detail == "frontmatter_identity_invalid");
        fetcher.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task MaterializeAsync_ExactFetcherUnavailable_ShouldUseRecoveryOnly()
    {
        var tools = NewTools("recovery", "task", "extra");

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                fetcher: null)
            .MaterializeAsync(
                SealProfile(BuildProfile(withAlias: true)),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ExactSkillFetchFailed &&
            diagnostic.Detail == "exact_fetch_unavailable");
    }

    [Fact]
    public async Task MaterializeAsync_ExactFetchTimeout_ShouldUseRecoveryOnly()
    {
        var tools = NewTools("recovery", "task", "extra");
        var profile = BuildProfile(withAlias: true);
        profile.ExactSkillFetchTimeoutMs = 1_000;
        var timeProvider = new ManualDeadlineTimeProvider();
        var fetcher = new CancellationBlockingFetcher();

        var materialization = NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                fetcher,
                timeProvider)
            .MaterializeAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);
        await fetcher.Started;

        timeProvider.Advance(TimeSpan.FromMilliseconds(profile.ExactSkillFetchTimeoutMs));
        var catalog = await materialization;

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ExactSkillFetchFailed &&
            diagnostic.Detail == "timeout");
        fetcher.CancellationObserved.Should().BeTrue();
    }

    [Fact]
    public async Task MaterializeAsync_ExactFetcherException_ShouldUseRecoveryOnly()
    {
        var tools = NewTools("recovery", "task", "extra");

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new ThrowingFetcher(new InvalidOperationException("fetch failed")))
            .MaterializeAsync(
                SealProfile(BuildProfile(withAlias: true)),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ExactSkillFetchFailed &&
            diagnostic.Detail == "fetch_exception");
    }

    [Fact]
    public async Task MaterializeAsync_CallerCancellationDuringExactFetch_ShouldPropagate()
    {
        var tools = NewTools("recovery", "task", "extra");
        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();

        var act = async () => await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new CancellationBlockingFetcher())
            .MaterializeAsync(
                SealProfile(BuildProfile(withAlias: true)),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                callerCts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task MaterializeAsync_InvalidSnapshot_ShouldReturnRestrictedEmpty()
    {
        var tools = NewTools("recovery", "task", "extra");
        var profile = SealProfile(BuildProfile());
        profile.PolicyRevision = "tampered";

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha")),
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(profile, "select", "token", tools, ToolContext(), CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.ToolVisibility.IsRestricted.Should().BeTrue();
        catalog.Diagnostics.Should().ContainSingle(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ProfileInvalid);
    }

    [Fact]
    public async Task MaterializeAsync_UnknownRouteToolSet_ShouldReturnRestrictedEmpty()
    {
        var tools = NewTools("recovery", "task", "extra");
        var registry = new RecordingToolSetRegistry();

        var catalog = await NewMaterializer(
                registry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "no route",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.RouteToolSetUnavailable);
    }

    [Fact]
    public async Task MaterializeAsync_RouteToolSetResolveThrows_ShouldReturnRestrictedEmptyWithoutFetching()
    {
        var tools = NewTools("recovery", "task", "extra");
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha"));
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(
                new ThrowingToolSetRegistry(),
                classifier,
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "classify",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.ToolVisibility.IsRestricted.Should().BeTrue();
        catalog.SelectedIntentId.Should().BeNull();
        catalog.CandidateIntentId.Should().BeNull();
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.RouteToolSetUnavailable &&
            diagnostic.Detail == "profile.route");
        classifier.CallCount.Should().Be(0);
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_RouteDiscoveryFailure_ShouldReturnRestrictedEmpty()
    {
        var tools = NewTools("recovery", "task", "extra");
        var registry = new RecordingToolSetRegistry();
        registry.Add("profile.route", new ThrowingToolSource());
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(
                registry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha")),
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "no route",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ToolDiscoveryFailed);
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_CollisionAndCapabilityRejection_ShouldNotGrantThoseNames()
    {
        var duplicateA = new TestTool("recovery");
        var duplicateB = new TestTool("recovery");
        var blocked = new CapabilityTool(
            "task",
            [AgentToolCapabilities.ExcludeFromDirectChannelChat]);
        var routeTools = new IAgentTool[] { duplicateA, duplicateB, blocked };
        var registry = RegistryWithRoute(routeTools);
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(
                registry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.Matched("intent-alpha")),
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile()),
                "no route",
                "token",
                routeTools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ToolNameCollision &&
            diagnostic.Detail == "recovery");
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ToolCapabilityRejected &&
            diagnostic.Detail == "task");
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_HumanSessionToolWithoutToken_ShouldBeRejected()
    {
        var humanSessionTool = new CapabilityTool(
            "task",
            [AgentToolCapabilities.RequiresHumanSession]);
        var tools = new IAgentTool[] { humanSessionTool };
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile(withAlias: true)),
                "/alpha",
                accessToken: null,
                tools,
                ToolContext(accessToken: null),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEmpty();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ToolCapabilityRejected &&
            diagnostic.Detail == "task");
        fetcher.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaterializeAsync_HumanSessionToolWithToken_ShouldBeAdmittedWhenPoliciesAllow()
    {
        var humanSessionTool = new CapabilityTool(
            "task",
            [AgentToolCapabilities.RequiresHumanSession]);
        var tools = new IAgentTool[] { humanSessionTool };
        var fetcher = new RecordingFetcher(SuccessfulFetch());

        var catalog = await NewMaterializer(
                RegistryWithRoute(tools),
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                fetcher)
            .MaterializeAsync(
                SealProfile(BuildProfile(withAlias: true)),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("task");
        catalog.SelectedIntentId.Should().Be("intent-alpha");
        catalog.Diagnostics.Should().NotContain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ToolCapabilityRejected);
        fetcher.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task MaterializeAsync_TaskToolSetFailure_ShouldDiscardSelectionAndUseRecovery()
    {
        var tools = NewTools("recovery", "task", "extra");
        var registry = RegistryWithRoute(tools);
        var profile = BuildProfile(withAlias: true);
        profile.Members[0].TaskToolPolicy.ToolSetRefs.Add("missing.task");

        var catalog = await NewMaterializer(
                registry,
                new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch()),
                new RecordingFetcher(SuccessfulFetch()))
            .MaterializeAsync(
                SealProfile(profile),
                "/alpha",
                "token",
                tools,
                ToolContext(),
                CancellationToken.None);

        catalog.FinalAllowedToolNames.Should().BeEquivalentTo("recovery");
        catalog.SelectedIntentId.Should().BeNull();
        catalog.CandidateIntentId.Should().Be("intent-alpha");
        catalog.SelectedSkillPromptLayer.Should().BeNull();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ToolSetUnavailable);
    }

    private static AgentProfileTurnCatalogMaterializer NewMaterializer(
        IToolSetRegistry registry,
        IAgentProfileTurnClassifier classifier,
        IExactRemoteSkillFetcher? fetcher,
        TimeProvider? timeProvider = null) =>
        new(registry, classifier, fetcher, timeProvider: timeProvider);

    private static AgentProfileSnapshot BuildProfile(bool withAlias = false)
    {
        var member = new AgentProfileSkillMember
        {
            IntentId = "intent-alpha",
            RoutingDescription = "Route alpha requests.",
            SkillRef = new ExactRemoteSkillRef { Guid = SkillGuid, LiteralVersion = SkillVersion },
            TaskToolPolicy = new AgentProfileToolPolicy(),
            SideEffectClass = AgentProfileSideEffectClass.ReadOnly,
            ExpectedSkillName = SkillName,
            ReviewedPublisherId = PublisherId,
        };
        member.TaskToolPolicy.ToolNames.Add("task");
        if (withAlias)
            member.ExplicitTriggerAliases.Add("/alpha");

        var profile = new AgentProfileSnapshot
        {
            ProfileId = "profile-alpha",
            ProfileVersion = "profile-v1",
            AgentKind = "nyxid.chat",
            PolicyRevision = "policy-v1",
            RouteToolSetRef = "profile.route",
            MaximumToolPolicy = new AgentProfileToolPolicy(),
            RecoveryToolPolicy = new AgentProfileToolPolicy(),
            ClassifierTimeoutMs = 600,
            ExactSkillFetchTimeoutMs = 1_500,
            MaxSelectedSkillBytes = 256,
            ActivationMode = AgentProfileActivationMode.Enforced,
        };
        profile.MaximumToolPolicy.ToolNames.Add(["recovery", "task", "extra"]);
        profile.RecoveryToolPolicy.ToolNames.Add("recovery");
        profile.Members.Add(member);
        return profile;
    }

    private static AgentProfileSnapshot SealProfile(AgentProfileSnapshot profile) =>
        AgentProfileSnapshotCodec.Seal(profile);

    private static ExactRemoteSkillFetchResult SuccessfulFetch(string skillMarkdown = SkillMarkdown) =>
        ExactRemoteSkillFetchResult.Success(
            SkillGuid,
            SkillVersion,
            SkillName,
            PublisherId,
            "hash-alpha",
            skillMarkdown);

    private static AgentToolExecutionContext ToolContext(string? accessToken = "token") =>
        AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(accessToken, null, null),
        };

    private static IReadOnlyList<IAgentTool> NewTools(params string[] names) =>
        names.Select(static name => (IAgentTool)new TestTool(name)).ToArray();

    private static RecordingToolSetRegistry RegistryWithRoute(IReadOnlyList<IAgentTool> tools)
    {
        var registry = new RecordingToolSetRegistry();
        registry.Add("profile.route", new StaticToolSource(tools));
        return registry;
    }

    private sealed class RecordingClassifier : IAgentProfileTurnClassifier
    {
        private readonly AgentProfileTurnClassificationResult? _result;
        private readonly Exception? _exception;

        public RecordingClassifier(AgentProfileTurnClassificationResult result) => _result = result;
        public RecordingClassifier(Exception exception) => _exception = exception;

        public int CallCount { get; private set; }
        public AgentProfileTurnClassificationRequest? LastRequest { get; private set; }

        public Task<AgentProfileTurnClassificationResult> ClassifyAsync(
            AgentProfileTurnClassificationRequest request,
            CancellationToken ct = default)
        {
            CallCount++;
            LastRequest = request;
            return _exception is not null
                ? Task.FromException<AgentProfileTurnClassificationResult>(_exception)
                : Task.FromResult(_result!);
        }
    }

    private sealed class CancellationAwareClassifier : IAgentProfileTurnClassifier
    {
        public int CallCount { get; private set; }

        public Task<AgentProfileTurnClassificationResult> ClassifyAsync(
            AgentProfileTurnClassificationRequest request,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromCanceled<AgentProfileTurnClassificationResult>(ct);
        }
    }

    private sealed class RecordingFetcher(ExactRemoteSkillFetchResult result) : IExactRemoteSkillFetcher
    {
        public int CallCount { get; private set; }

        public Task<ExactRemoteSkillFetchResult> FetchAsync(
            string accessToken,
            ExactRemoteSkillRef skillRef,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class CancellationBlockingFetcher : IExactRemoteSkillFetcher
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;
        public bool CancellationObserved { get; private set; }

        public async Task<ExactRemoteSkillFetchResult> FetchAsync(
            string accessToken,
            ExactRemoteSkillRef skillRef,
            CancellationToken ct = default)
        {
            _started.TrySetResult();
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
                canceled);
            try
            {
                await canceled.Task;
                return SuccessfulFetch();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }

    private sealed class ThrowingFetcher(Exception exception) : IExactRemoteSkillFetcher
    {
        public Task<ExactRemoteSkillFetchResult> FetchAsync(
            string accessToken,
            ExactRemoteSkillRef skillRef,
            CancellationToken ct = default) =>
            Task.FromException<ExactRemoteSkillFetchResult>(exception);
    }

    private sealed class RecordingToolSetRegistry : IToolSetRegistry
    {
        private readonly Dictionary<string, IReadOnlyList<IAgentToolSource>> _sources =
            new(StringComparer.Ordinal);

        public List<string> ResolveCalls { get; } = [];

        public void Add(string name, params IAgentToolSource[] sources) =>
            _sources.Add(name, sources);

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
        public IReadOnlyList<string> GetRegisteredNames() => [];

        public ToolSetResolveResult Resolve(ChatRouteToolSetRef? toolSetRef) =>
            throw new InvalidOperationException("resolve failed");
    }

    private sealed class StaticToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult(tools);
    }

    private sealed class ThrowingToolSource : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<IAgentTool>>(new InvalidOperationException("discovery failed"));
    }

    private sealed class CancellationAwareToolSource : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromCanceled<IReadOnlyList<IAgentTool>>(ct);
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
