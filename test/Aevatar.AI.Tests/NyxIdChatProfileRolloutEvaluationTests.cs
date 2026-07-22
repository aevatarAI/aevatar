using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatProfileRolloutEvaluationTests
{
    private const string SkillVersion = "1.2";
    private const string PublisherId = "publisher-reviewed";
    private const string RouteToolSetName = "profile.route";

    private static readonly RolloutCase[] RolloutCases =
    [
        new(EvaluationGroup.Routing, "alias-discovery-zh", CaseBehavior.AliasRoute, ScenarioKind.Discovery,
            CaseLanguage.Chinese, ProfileMode.Shadow),
        new(EvaluationGroup.Routing, "alias-discovery-en", CaseBehavior.AliasRoute, ScenarioKind.Discovery,
            CaseLanguage.English, ProfileMode.Enforced),
        new(EvaluationGroup.Routing, "alias-connect-zh", CaseBehavior.AliasRoute, ScenarioKind.Connect,
            CaseLanguage.Chinese, ProfileMode.Shadow),
        new(EvaluationGroup.Routing, "alias-connect-en", CaseBehavior.AliasRoute, ScenarioKind.Connect,
            CaseLanguage.English, ProfileMode.Enforced),
        new(EvaluationGroup.Routing, "alias-call-zh", CaseBehavior.AliasRoute, ScenarioKind.Call,
            CaseLanguage.Chinese, ProfileMode.Shadow),
        new(EvaluationGroup.Routing, "alias-call-en", CaseBehavior.AliasRoute, ScenarioKind.Call,
            CaseLanguage.English, ProfileMode.Enforced),
        new(EvaluationGroup.Routing, "alias-maintenance-zh", CaseBehavior.AliasRoute, ScenarioKind.Maintenance,
            CaseLanguage.Chinese, ProfileMode.Shadow),
        new(EvaluationGroup.Routing, "alias-maintenance-en", CaseBehavior.AliasRoute, ScenarioKind.Maintenance,
            CaseLanguage.English, ProfileMode.Enforced),
        new(EvaluationGroup.Routing, "classifier-discovery", CaseBehavior.ClassifierRoute,
            ScenarioKind.Discovery, CaseLanguage.Chinese, ProfileMode.Enforced),
        new(EvaluationGroup.Routing, "classifier-connect", CaseBehavior.ClassifierRoute,
            ScenarioKind.Connect, CaseLanguage.English, ProfileMode.Enforced),
        new(EvaluationGroup.Routing, "classifier-call", CaseBehavior.ClassifierRoute,
            ScenarioKind.Call, CaseLanguage.Chinese, ProfileMode.Enforced),
        new(EvaluationGroup.Routing, "classifier-maintenance", CaseBehavior.ClassifierRoute,
            ScenarioKind.Maintenance, CaseLanguage.English, ProfileMode.Enforced),
        new(EvaluationGroup.Routing, "ambiguous-alias", CaseBehavior.AliasCollision,
            ScenarioKind.Discovery, CaseLanguage.Chinese, ProfileMode.Enforced),
        new(EvaluationGroup.Routing, "classifier-no-match", CaseBehavior.ClassifierNoMatch,
            ScenarioKind.Connect, CaseLanguage.English, ProfileMode.Enforced),
        new(EvaluationGroup.Routing, "classifier-timeout", CaseBehavior.ClassifierFailure,
            ScenarioKind.Call, CaseLanguage.Chinese, ProfileMode.Enforced),
        new(EvaluationGroup.Routing, "next-turn-correction", CaseBehavior.NextTurnCorrection,
            ScenarioKind.Maintenance, CaseLanguage.English, ProfileMode.Enforced),

        new(EvaluationGroup.ContinuationAuth, "shadow-connect-continuation", CaseBehavior.ShadowNoSideEffect,
            ScenarioKind.Connect, CaseLanguage.Chinese, ProfileMode.Shadow),
        new(EvaluationGroup.ContinuationAuth, "shadow-repair-continuation", CaseBehavior.ShadowNoSideEffect,
            ScenarioKind.Maintenance, CaseLanguage.English, ProfileMode.Shadow),
        new(EvaluationGroup.ContinuationAuth, "shadow-call-continuation", CaseBehavior.ShadowNoSideEffect,
            ScenarioKind.Call, CaseLanguage.Chinese, ProfileMode.Shadow),
        new(EvaluationGroup.ContinuationAuth, "shadow-discovery-continuation", CaseBehavior.ShadowNoSideEffect,
            ScenarioKind.Discovery, CaseLanguage.English, ProfileMode.Shadow),
        new(EvaluationGroup.ContinuationAuth, "authorized-connect", CaseBehavior.AuthorizedContinuation,
            ScenarioKind.Connect, CaseLanguage.Chinese, ProfileMode.Enforced),
        new(EvaluationGroup.ContinuationAuth, "authorized-repair", CaseBehavior.AuthorizedContinuation,
            ScenarioKind.Maintenance, CaseLanguage.English, ProfileMode.Enforced),
        new(EvaluationGroup.ContinuationAuth, "authorized-call", CaseBehavior.AuthorizedContinuation,
            ScenarioKind.Call, CaseLanguage.Chinese, ProfileMode.Enforced),
        new(EvaluationGroup.ContinuationAuth, "authorized-discovery", CaseBehavior.AuthorizedContinuation,
            ScenarioKind.Discovery, CaseLanguage.English, ProfileMode.Enforced),
        new(EvaluationGroup.ContinuationAuth, "missing-connect-credential", CaseBehavior.MissingCredential,
            ScenarioKind.Connect, CaseLanguage.Chinese, ProfileMode.Enforced),
        new(EvaluationGroup.ContinuationAuth, "missing-repair-credential", CaseBehavior.MissingCredential,
            ScenarioKind.Maintenance, CaseLanguage.English, ProfileMode.Enforced),
        new(EvaluationGroup.ContinuationAuth, "missing-call-credential", CaseBehavior.MissingCredential,
            ScenarioKind.Call, CaseLanguage.Chinese, ProfileMode.Enforced),
        new(EvaluationGroup.ContinuationAuth, "missing-discovery-credential", CaseBehavior.MissingCredential,
            ScenarioKind.Discovery, CaseLanguage.English, ProfileMode.Enforced),
        new(EvaluationGroup.ContinuationAuth, "unavailable-connect-target", CaseBehavior.RemoteUnavailable,
            ScenarioKind.Connect, CaseLanguage.Chinese, ProfileMode.Enforced, FetchOutcome.NotFound),
        new(EvaluationGroup.ContinuationAuth, "unavailable-repair-target", CaseBehavior.RemoteUnavailable,
            ScenarioKind.Maintenance, CaseLanguage.English, ProfileMode.Enforced, FetchOutcome.NotFound),
        new(EvaluationGroup.ContinuationAuth, "unavailable-call-target", CaseBehavior.RemoteUnavailable,
            ScenarioKind.Call, CaseLanguage.Chinese, ProfileMode.Enforced, FetchOutcome.NotFound),
        new(EvaluationGroup.ContinuationAuth, "unavailable-discovery-target", CaseBehavior.RemoteUnavailable,
            ScenarioKind.Discovery, CaseLanguage.English, ProfileMode.Enforced, FetchOutcome.NotFound),

        new(EvaluationGroup.IdentitySafety, "exact-guid-mismatch", CaseBehavior.ExactIdentityMismatch,
            ScenarioKind.Discovery, CaseLanguage.Chinese, ProfileMode.Enforced, FetchOutcome.GuidMismatch),
        new(EvaluationGroup.IdentitySafety, "exact-version-mismatch", CaseBehavior.ExactIdentityMismatch,
            ScenarioKind.Connect, CaseLanguage.English, ProfileMode.Enforced, FetchOutcome.VersionMismatch),
        new(EvaluationGroup.IdentitySafety, "exact-name-mismatch", CaseBehavior.ExactIdentityMismatch,
            ScenarioKind.Call, CaseLanguage.Chinese, ProfileMode.Enforced, FetchOutcome.NameMismatch),
        new(EvaluationGroup.IdentitySafety, "exact-publisher-mismatch", CaseBehavior.ExactIdentityMismatch,
            ScenarioKind.Maintenance, CaseLanguage.English, ProfileMode.Enforced, FetchOutcome.PublisherMismatch),
        new(EvaluationGroup.IdentitySafety, "exact-hash-missing", CaseBehavior.ExactIdentityMismatch,
            ScenarioKind.Discovery, CaseLanguage.English, ProfileMode.Enforced, FetchOutcome.MissingHash),
        new(EvaluationGroup.IdentitySafety, "oversized-skill-body", CaseBehavior.InvalidSkillBody,
            ScenarioKind.Connect, CaseLanguage.Chinese, ProfileMode.Enforced, FetchOutcome.OversizedBody),
        new(EvaluationGroup.IdentitySafety, "frontmatter-identity-mismatch", CaseBehavior.InvalidSkillBody,
            ScenarioKind.Call, CaseLanguage.English, ProfileMode.Enforced, FetchOutcome.FrontmatterMismatch),
        new(EvaluationGroup.IdentitySafety, "maximum-policy-attenuation", CaseBehavior.MaximumAttenuation,
            ScenarioKind.Discovery, CaseLanguage.Chinese, ProfileMode.Enforced),
        new(EvaluationGroup.IdentitySafety, "turn-visibility-attenuation", CaseBehavior.VisibilityAttenuation,
            ScenarioKind.Connect, CaseLanguage.English, ProfileMode.Enforced),
        new(EvaluationGroup.IdentitySafety, "tool-object-collision", CaseBehavior.ToolCollision,
            ScenarioKind.Call, CaseLanguage.Chinese, ProfileMode.Enforced),
        new(EvaluationGroup.IdentitySafety, "write-tool-approval", CaseBehavior.ApprovalRequired,
            ScenarioKind.Call, CaseLanguage.English, ProfileMode.Enforced),
        new(EvaluationGroup.IdentitySafety, "destructive-tool-approval", CaseBehavior.ApprovalRequired,
            ScenarioKind.Maintenance, CaseLanguage.Chinese, ProfileMode.Enforced),

        new(EvaluationGroup.LifecycleIsolationRegression, "pinned-snapshot-zh", CaseBehavior.PinnedSnapshot,
            ScenarioKind.Discovery, CaseLanguage.Chinese, ProfileMode.Enforced),
        new(EvaluationGroup.LifecycleIsolationRegression, "pinned-snapshot-en", CaseBehavior.PinnedSnapshot,
            ScenarioKind.Connect, CaseLanguage.English, ProfileMode.Enforced),
        new(EvaluationGroup.LifecycleIsolationRegression, "serialized-restart-zh", CaseBehavior.SerializedRestart,
            ScenarioKind.Call, CaseLanguage.Chinese, ProfileMode.Enforced),
        new(EvaluationGroup.LifecycleIsolationRegression, "serialized-restart-en", CaseBehavior.SerializedRestart,
            ScenarioKind.Maintenance, CaseLanguage.English, ProfileMode.Enforced),
        new(EvaluationGroup.LifecycleIsolationRegression, "concurrent-shadow-sessions",
            CaseBehavior.ConcurrentSessions, ScenarioKind.Discovery, CaseLanguage.Chinese, ProfileMode.Shadow),
        new(EvaluationGroup.LifecycleIsolationRegression, "concurrent-enforced-sessions",
            CaseBehavior.ConcurrentSessions, ScenarioKind.Connect, CaseLanguage.English, ProfileMode.Enforced),
        new(EvaluationGroup.LifecycleIsolationRegression, "bound-shadow-mode", CaseBehavior.BoundMode,
            ScenarioKind.Call, CaseLanguage.Chinese, ProfileMode.Shadow),
        new(EvaluationGroup.LifecycleIsolationRegression, "bound-enforced-mode", CaseBehavior.BoundMode,
            ScenarioKind.Maintenance, CaseLanguage.English, ProfileMode.Enforced),
        new(EvaluationGroup.LifecycleIsolationRegression, "authority-copy-zh", CaseBehavior.AuthorityCopy,
            ScenarioKind.Discovery, CaseLanguage.Chinese, ProfileMode.Enforced),
        new(EvaluationGroup.LifecycleIsolationRegression, "authority-copy-en", CaseBehavior.AuthorityCopy,
            ScenarioKind.Connect, CaseLanguage.English, ProfileMode.Enforced),
        new(EvaluationGroup.LifecycleIsolationRegression, "reconcile-fetch-failure",
            CaseBehavior.MonotonicReconcile, ScenarioKind.Call, CaseLanguage.Chinese, ProfileMode.Enforced,
            FetchOutcome.NotFound),
        new(EvaluationGroup.LifecycleIsolationRegression, "reconcile-identity-failure",
            CaseBehavior.MonotonicReconcile, ScenarioKind.Maintenance, CaseLanguage.English, ProfileMode.Enforced,
            FetchOutcome.NameMismatch),

        new(EvaluationGroup.PromptToolLatency, "prompt-under-bound", CaseBehavior.PromptWithinBound,
            ScenarioKind.Discovery, CaseLanguage.Chinese, ProfileMode.Enforced),
        new(EvaluationGroup.PromptToolLatency, "prompt-exact-boundary", CaseBehavior.PromptExactBoundary,
            ScenarioKind.Connect, CaseLanguage.English, ProfileMode.Enforced),
        new(EvaluationGroup.PromptToolLatency, "prompt-over-bound", CaseBehavior.InvalidSkillBody,
            ScenarioKind.Call, CaseLanguage.Chinese, ProfileMode.Enforced, FetchOutcome.OversizedBody),
        new(EvaluationGroup.PromptToolLatency, "prompt-empty-body", CaseBehavior.InvalidSkillBody,
            ScenarioKind.Maintenance, CaseLanguage.English, ProfileMode.Enforced, FetchOutcome.EmptyBody),
        new(EvaluationGroup.PromptToolLatency, "prompt-frontmatter-mismatch", CaseBehavior.InvalidSkillBody,
            ScenarioKind.Discovery, CaseLanguage.English, ProfileMode.Enforced, FetchOutcome.FrontmatterMismatch),
        new(EvaluationGroup.PromptToolLatency, "built-in-recovery-floor", CaseBehavior.BuiltInRecoveryFloor,
            ScenarioKind.Connect, CaseLanguage.Chinese, ProfileMode.Enforced),
        new(EvaluationGroup.PromptToolLatency, "task-approval-preserved", CaseBehavior.ApprovalRequired,
            ScenarioKind.Maintenance, CaseLanguage.English, ProfileMode.Enforced),
        new(EvaluationGroup.PromptToolLatency, "configured-latency-budgets", CaseBehavior.LatencyBudgets,
            ScenarioKind.Call, CaseLanguage.Chinese, ProfileMode.Shadow),
    ];

    public static IEnumerable<object[]> Cases =>
        RolloutCases.Select(static testCase => new object[] { testCase });

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Typed_matrix_case_should_execute_its_profile_invariant(RolloutCase testCase)
    {
        switch (testCase.Group)
        {
            case EvaluationGroup.Routing:
                await AssertRoutingCaseAsync(testCase);
                break;
            case EvaluationGroup.ContinuationAuth:
                await AssertContinuationAuthCaseAsync(testCase);
                break;
            case EvaluationGroup.IdentitySafety:
                await AssertIdentitySafetyCaseAsync(testCase);
                break;
            case EvaluationGroup.LifecycleIsolationRegression:
                await AssertLifecycleCaseAsync(testCase);
                break;
            case EvaluationGroup.PromptToolLatency:
                await AssertPromptToolLatencyCaseAsync(testCase);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(testCase), testCase.Group, null);
        }
    }

    [Fact]
    public void Matrix_should_have_exactly_the_required_group_distribution()
    {
        RolloutCases.Should().HaveCount(64);
        RolloutCases.Select(static testCase => testCase.Id).Should().OnlyHaveUniqueItems();
        RolloutCases.Should().Contain(testCase => testCase.Language == CaseLanguage.Chinese);
        RolloutCases.Should().Contain(testCase => testCase.Language == CaseLanguage.English);
        RolloutCases.Should().Contain(testCase => testCase.Mode == ProfileMode.Shadow);
        RolloutCases.Should().Contain(testCase => testCase.Mode == ProfileMode.Enforced);
        RolloutCases.GroupBy(static testCase => testCase.Group)
            .ToDictionary(static group => group.Key, static group => group.Count())
            .Should().BeEquivalentTo(new Dictionary<EvaluationGroup, int>
            {
                [EvaluationGroup.Routing] = 16,
                [EvaluationGroup.ContinuationAuth] = 16,
                [EvaluationGroup.IdentitySafety] = 12,
                [EvaluationGroup.LifecycleIsolationRegression] = 12,
                [EvaluationGroup.PromptToolLatency] = 8,
            });
    }

    private static async Task AssertRoutingCaseAsync(RolloutCase testCase)
    {
        var classifierResult = testCase.Behavior == CaseBehavior.ClassifierRoute
            ? AgentProfileTurnClassificationResult.Matched(ScenarioDefinition.For(testCase.Scenario).IntentId)
            : testCase.Behavior == CaseBehavior.ClassifierFailure
                ? AgentProfileTurnClassificationResult.Failed("timeout")
                : AgentProfileTurnClassificationResult.NoMatch();
        var useAlias = testCase.Behavior == CaseBehavior.AliasRoute ||
                       testCase.Behavior == CaseBehavior.AliasCollision;
        Action<AgentProfileSnapshot>? configure = testCase.Behavior == CaseBehavior.AliasCollision
            ? AddAliasCollision
            : null;
        var prepared = await PrepareCaseAsync(
            testCase,
            classifierResult,
            useAlias,
            configureProfile: configure);

        if (testCase.Behavior == CaseBehavior.NextTurnCorrection)
        {
            prepared.Preparation.Authority.CandidateRoute.Should().BeNull();
            prepared.Preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
            prepared.Classifier.CallCount.Should().Be(1);

            var corrected = await prepared.Materializer.PrepareAsync(
                prepared.Profile,
                $"session-{testCase.Id}-corrected",
                prepared.Scenario.Message(testCase.Language),
                prepared.Tools,
                ToolContext(),
                CancellationToken.None);

            corrected.Authority.CandidateRoute!.IntentId.Should().Be(prepared.Scenario.IntentId);
            corrected.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Selected);
            prepared.Classifier.CallCount.Should().Be(1);
            AssertNoToolExecution(prepared);
            return;
        }

        if (testCase.Behavior is CaseBehavior.AliasCollision or
            CaseBehavior.ClassifierNoMatch or CaseBehavior.ClassifierFailure)
        {
            prepared.Preparation.Authority.CandidateRoute.Should().BeNull();
            prepared.Preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
            prepared.Fetcher.CallCount.Should().Be(0);
            prepared.Preparation.Diagnostics.Should().Contain(diagnostic =>
                diagnostic.Code == (testCase.Behavior == CaseBehavior.ClassifierNoMatch
                    ? AgentProfileTurnDiagnosticCode.ClassifierNoMatch
                    : AgentProfileTurnDiagnosticCode.ClassifierFailed));
            AssertNoToolExecution(prepared);
            return;
        }

        prepared.Preparation.Authority.CandidateRoute!.IntentId.Should().Be(prepared.Scenario.IntentId);
        prepared.Classifier.CallCount.Should().Be(testCase.Behavior == CaseBehavior.ClassifierRoute ? 1 : 0);
        await AssertSelectedOrShadowAsync(prepared);
    }

    private static async Task AssertContinuationAuthCaseAsync(RolloutCase testCase)
    {
        var prepared = await PrepareCaseAsync(
            testCase,
            AgentProfileTurnClassificationResult.NoMatch(),
            useAlias: true);

        prepared.Profile.Members.Single().SideEffectClass.Should().Be(prepared.Scenario.SideEffectClass);
        if (testCase.Behavior == CaseBehavior.ShadowNoSideEffect)
        {
            prepared.Preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
            prepared.Preparation.Authority.CandidateRoute!.IntentId.Should().Be(prepared.Scenario.IntentId);
            prepared.Preparation.Authority.SelectedExactSkillRef.Should().BeNull();
            prepared.Fetcher.CallCount.Should().Be(0);
            AssertNoToolExecution(prepared);
            return;
        }

        prepared.Preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Selected);
        var accessToken = testCase.Behavior == CaseBehavior.MissingCredential ? null : "turn-token";
        var materialization = await prepared.Materializer.MaterializeCommittedAsync(
            prepared.Profile,
            prepared.Preparation.Authority,
            accessToken,
            prepared.Tools,
            ToolContext(),
            CancellationToken.None);

        if (testCase.Behavior == CaseBehavior.AuthorizedContinuation)
        {
            materialization.Catalog.SelectedIntentId.Should().Be(prepared.Scenario.IntentId);
            materialization.Catalog.FinalAllowedToolNames.Should()
                .BeEquivalentTo("recovery", prepared.Scenario.TaskToolName);
            prepared.Fetcher.CallCount.Should().Be(1);
        }
        else
        {
            materialization.Catalog.SelectedIntentId.Should().BeNull();
            materialization.Catalog.FinalAllowedToolNames.Should().Equal("recovery");
            materialization.ReconcileProposal.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
            materialization.Catalog.Diagnostics.Should().Contain(diagnostic =>
                diagnostic.Code == AgentProfileTurnDiagnosticCode.ExactSkillFetchFailed);
            prepared.Fetcher.CallCount.Should().Be(
                testCase.Behavior == CaseBehavior.MissingCredential ? 0 : 1);
        }

        AssertNoToolExecution(prepared);
    }

    private static async Task AssertIdentitySafetyCaseAsync(RolloutCase testCase)
    {
        if (testCase.Behavior == CaseBehavior.ToolCollision)
        {
            await AssertToolCollisionAsync(testCase);
            return;
        }

        Action<AgentProfileSnapshot>? configure = testCase.Behavior == CaseBehavior.MaximumAttenuation
            ? profile =>
            {
                profile.MaximumToolPolicy.ToolNames.Clear();
                profile.MaximumToolPolicy.ToolNames.Add("recovery");
            }
            : null;
        var approvalMode = testCase.Behavior == CaseBehavior.ApprovalRequired
            ? ToolApprovalMode.AlwaysRequire
            : ToolApprovalMode.NeverRequire;
        var visibility = testCase.Behavior == CaseBehavior.VisibilityAttenuation
            ? new[] { "recovery" }
            : null;
        var prepared = await PrepareCaseAsync(
            testCase,
            AgentProfileTurnClassificationResult.NoMatch(),
            useAlias: true,
            configureProfile: configure,
            taskApprovalMode: approvalMode,
            visibility: visibility);

        if (testCase.Behavior is CaseBehavior.MaximumAttenuation or CaseBehavior.VisibilityAttenuation)
        {
            prepared.Preparation.Authority.AuthorityCeilingToolNames.Should().Equal("recovery");
            var attenuated = await MaterializeAsync(prepared);
            attenuated.Catalog.SelectedIntentId.Should().Be(prepared.Scenario.IntentId);
            attenuated.Catalog.FinalAllowedToolNames.Should().Equal("recovery");
            AssertNoToolExecution(prepared);
            return;
        }

        var materialization = await MaterializeAsync(prepared);
        if (testCase.Behavior == CaseBehavior.ApprovalRequired)
        {
            materialization.Catalog.RouteOwnedTools[prepared.Scenario.TaskToolName].ApprovalMode
                .Should().Be(ToolApprovalMode.AlwaysRequire);
            AssertNoToolExecution(prepared);
            return;
        }

        materialization.Catalog.SelectedIntentId.Should().BeNull();
        materialization.Catalog.FinalAllowedToolNames.Should().Equal("recovery");
        materialization.Catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == (testCase.Behavior == CaseBehavior.ExactIdentityMismatch
                ? AgentProfileTurnDiagnosticCode.ExactSkillIdentityMismatch
                : AgentProfileTurnDiagnosticCode.SelectedSkillBodyInvalid));
        AssertNoToolExecution(prepared);
    }

    private static async Task AssertLifecycleCaseAsync(RolloutCase testCase)
    {
        switch (testCase.Behavior)
        {
            case CaseBehavior.PinnedSnapshot:
                await AssertPinnedSnapshotAsync(testCase);
                return;
            case CaseBehavior.SerializedRestart:
                await AssertSerializedRestartAsync(testCase);
                return;
            case CaseBehavior.ConcurrentSessions:
                await AssertConcurrentSessionsAsync(testCase);
                return;
            case CaseBehavior.BoundMode:
                await AssertBoundModeAsync(testCase);
                return;
            case CaseBehavior.AuthorityCopy:
                await AssertAuthorityCopyAsync(testCase);
                return;
            case CaseBehavior.MonotonicReconcile:
                await AssertMonotonicReconcileAsync(testCase);
                return;
            default:
                throw new InvalidOperationException($"Unsupported lifecycle behavior '{testCase.Behavior}'.");
        }
    }

    private static async Task AssertPromptToolLatencyCaseAsync(RolloutCase testCase)
    {
        if (testCase.Behavior == CaseBehavior.LatencyBudgets)
        {
            var scenario = ScenarioDefinition.For(testCase.Scenario);
            var shadow = BuildProfile(ProfileMode.Shadow, scenario);
            var enforced = BuildProfile(ProfileMode.Enforced, scenario);

            shadow.ClassifierTimeoutMs.Should().Be(600);
            enforced.ClassifierTimeoutMs.Should().Be(600);
            enforced.ExactSkillFetchTimeoutMs.Should().Be(1_500);
            (enforced.ClassifierTimeoutMs + enforced.ExactSkillFetchTimeoutMs).Should().Be(2_100);
            return;
        }

        var scenarioDefinition = ScenarioDefinition.For(testCase.Scenario);
        string? markdown = null;
        Action<AgentProfileSnapshot>? configure = null;
        if (testCase.Behavior == CaseBehavior.PromptExactBoundary)
        {
            markdown = SkillMarkdown(scenarioDefinition, "Boundary instructions.");
            var exactBytes = Encoding.UTF8.GetByteCount(markdown);
            configure = profile => profile.MaxSelectedSkillBytes = exactBytes;
        }

        var approvalMode = testCase.Behavior == CaseBehavior.ApprovalRequired
            ? ToolApprovalMode.AlwaysRequire
            : ToolApprovalMode.NeverRequire;
        var prepared = await PrepareCaseAsync(
            testCase,
            AgentProfileTurnClassificationResult.NoMatch(),
            useAlias: true,
            configureProfile: configure,
            taskApprovalMode: approvalMode,
            skillMarkdown: markdown);
        var materialization = await MaterializeAsync(prepared);

        if (testCase.Behavior == CaseBehavior.InvalidSkillBody)
        {
            materialization.Catalog.SelectedSkillPromptLayer.Should().BeNull();
            materialization.Catalog.FinalAllowedToolNames.Should().Equal("recovery");
            materialization.Catalog.Diagnostics.Should().Contain(diagnostic =>
                diagnostic.Code == AgentProfileTurnDiagnosticCode.SelectedSkillBodyInvalid);
        }
        else
        {
            materialization.Catalog.SelectedSkillPromptLayer.Should().NotBeNull();
            materialization.Catalog.FinalAllowedToolNames.Should()
                .BeEquivalentTo("recovery", prepared.Scenario.TaskToolName);
            if (testCase.Behavior == CaseBehavior.PromptExactBoundary)
            {
                materialization.Catalog.SelectedSkillPromptLayer!.Content
                    .Should().Be("Boundary instructions.");
            }
            if (testCase.Behavior == CaseBehavior.BuiltInRecoveryFloor)
                materialization.Catalog.FinalAllowedToolNames.Should().Contain("recovery");
            if (testCase.Behavior == CaseBehavior.ApprovalRequired)
            {
                materialization.Catalog.RouteOwnedTools[prepared.Scenario.TaskToolName].ApprovalMode
                    .Should().Be(ToolApprovalMode.AlwaysRequire);
            }
        }

        AssertNoToolExecution(prepared);
    }

    private static async Task AssertSelectedOrShadowAsync(PreparedCase prepared)
    {
        if (prepared.TestCase.Mode == ProfileMode.Shadow)
        {
            prepared.Preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
            prepared.Preparation.Authority.SelectedExactSkillRef.Should().BeNull();
            prepared.Preparation.Diagnostics.Should().Contain(diagnostic =>
                diagnostic.Code == AgentProfileTurnDiagnosticCode.ShadowCandidate);
            prepared.Fetcher.CallCount.Should().Be(0);
        }
        else
        {
            prepared.Preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Selected);
            var materialization = await MaterializeAsync(prepared);
            materialization.Catalog.SelectedIntentId.Should().Be(prepared.Scenario.IntentId);
            materialization.Catalog.FinalAllowedToolNames.Should()
                .BeEquivalentTo("recovery", prepared.Scenario.TaskToolName);
            prepared.Fetcher.CallCount.Should().Be(1);
        }

        AssertNoToolExecution(prepared);
    }

    private static async Task AssertToolCollisionAsync(RolloutCase testCase)
    {
        var scenario = ScenarioDefinition.For(testCase.Scenario);
        var routeRecovery = new EvaluationTool("recovery");
        var routeTask = new EvaluationTool(scenario.TaskToolName);
        var registeredRecovery = routeRecovery;
        var registeredTask = new EvaluationTool(scenario.TaskToolName);
        IAgentTool[] routeTools = [routeRecovery, routeTask];
        IAgentTool[] registeredTools = [registeredRecovery, registeredTask];
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch());
        var fetcher = new RecordingFetcher(CreateFetch(scenario, FetchOutcome.Success));
        var materializer = new AgentProfileTurnCatalogMaterializer(
            new EvaluationToolSetRegistry(RouteToolSetName, routeTools),
            classifier,
            fetcher);
        var profile = BuildProfile(testCase.Mode, scenario);

        var preparation = await materializer.PrepareAsync(
            profile,
            $"session-{testCase.Id}",
            scenario.Message(testCase.Language),
            registeredTools,
            ToolContext(),
            CancellationToken.None);

        preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
        preparation.Authority.CandidateRoute.Should().BeNull();
        preparation.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ToolNameCollision);
        classifier.CallCount.Should().Be(0);
        fetcher.CallCount.Should().Be(0);
        routeRecovery.ExecuteCount.Should().Be(0);
        routeTask.ExecuteCount.Should().Be(0);
        registeredTask.ExecuteCount.Should().Be(0);
    }

    private static async Task AssertPinnedSnapshotAsync(RolloutCase testCase)
    {
        var prepared = await PrepareCaseAsync(
            testCase,
            AgentProfileTurnClassificationResult.NoMatch(),
            useAlias: true);
        var replacement = BuildProfile(
            ProfileMode.Enforced,
            prepared.Scenario,
            profile => profile.ProfileVersion = "enforced-v2");

        var pinned = await MaterializeAsync(prepared);
        var replacementAttempt = await prepared.Materializer.MaterializeCommittedAsync(
            replacement,
            prepared.Preparation.Authority,
            "turn-token",
            prepared.Tools,
            ToolContext(),
            CancellationToken.None);

        pinned.Catalog.SelectedIntentId.Should().Be(prepared.Scenario.IntentId);
        replacementAttempt.Catalog.FinalAllowedToolNames.Should().BeEmpty();
        replacementAttempt.Catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == AgentProfileTurnDiagnosticCode.ProfileInvalid);
        prepared.Fetcher.CallCount.Should().Be(1);
        AssertNoToolExecution(prepared);
    }

    private static async Task AssertSerializedRestartAsync(RolloutCase testCase)
    {
        var prepared = await PrepareCaseAsync(
            testCase,
            AgentProfileTurnClassificationResult.NoMatch(),
            useAlias: true);
        var restoredProfile = AgentProfileSnapshot.Parser.ParseFrom(prepared.Profile.ToByteArray());
        var restoredAuthority = AgentProfileTurnAuthorityState.Parser.ParseFrom(
            prepared.Preparation.Authority.ToByteArray());
        var restartClassifier = new RecordingClassifier(
            AgentProfileTurnClassificationResult.Failed("must_not_reclassify"));
        var restartFetcher = new RecordingFetcher(CreateFetch(prepared.Scenario, FetchOutcome.Success));
        var restartMaterializer = new AgentProfileTurnCatalogMaterializer(
            new EvaluationToolSetRegistry(RouteToolSetName, prepared.Tools),
            restartClassifier,
            restartFetcher);

        var materialization = await restartMaterializer.MaterializeCommittedAsync(
            restoredProfile,
            restoredAuthority,
            "turn-token",
            prepared.Tools,
            ToolContext(),
            CancellationToken.None);

        AgentProfileSnapshotCodec.ByteEquivalent(prepared.Profile, restoredProfile).Should().BeTrue();
        materialization.Catalog.SelectedIntentId.Should().Be(prepared.Scenario.IntentId);
        restartClassifier.CallCount.Should().Be(0);
        restartFetcher.CallCount.Should().Be(1);
        AssertNoToolExecution(prepared);
    }

    private static async Task AssertConcurrentSessionsAsync(RolloutCase testCase)
    {
        var scenario = ScenarioDefinition.For(testCase.Scenario);
        var recovery = new EvaluationTool("recovery");
        var task = new EvaluationTool(scenario.TaskToolName);
        IAgentTool[] tools = [recovery, task];
        var classifier = new RecordingClassifier(AgentProfileTurnClassificationResult.NoMatch());
        var fetcher = new RecordingFetcher(CreateFetch(scenario, FetchOutcome.Success));
        var materializer = new AgentProfileTurnCatalogMaterializer(
            new EvaluationToolSetRegistry(RouteToolSetName, tools),
            classifier,
            fetcher);
        var profile = BuildProfile(testCase.Mode, scenario);

        var preparations = await Task.WhenAll(
            materializer.PrepareAsync(
                profile,
                $"session-{testCase.Id}-a",
                scenario.Message(testCase.Language),
                tools,
                ToolContext(),
                CancellationToken.None),
            materializer.PrepareAsync(
                profile,
                $"session-{testCase.Id}-b",
                scenario.Message(testCase.Language),
                tools,
                ToolContext(),
                CancellationToken.None));

        preparations.Select(static preparation => preparation.Authority.ReconciliationKey.SessionId)
            .Should().OnlyHaveUniqueItems();
        preparations.Should().OnlyContain(preparation =>
            preparation.Authority.CandidateRoute!.IntentId == scenario.IntentId);
        classifier.CallCount.Should().Be(0);
        fetcher.CallCount.Should().Be(0);
        recovery.ExecuteCount.Should().Be(0);
        task.ExecuteCount.Should().Be(0);
    }

    private static async Task AssertBoundModeAsync(RolloutCase testCase)
    {
        var scenario = ScenarioDefinition.For(testCase.Scenario);
        var profile = BuildProfile(testCase.Mode, scenario);
        var otherMode = BuildProfile(
            testCase.Mode == ProfileMode.Shadow ? ProfileMode.Enforced : ProfileMode.Shadow,
            scenario);
        var clone = profile.Clone();

        AgentProfileSnapshotCodec.ByteEquivalent(profile, clone).Should().BeTrue();
        AgentProfileSnapshotCodec.ByteEquivalent(profile, otherMode).Should().BeFalse();
        clone.ActivationMode.Should().Be(
            testCase.Mode == ProfileMode.Shadow
                ? AgentProfileActivationMode.Shadow
                : AgentProfileActivationMode.Enforced);

        var prepared = await PrepareCaseAsync(
            testCase,
            AgentProfileTurnClassificationResult.NoMatch(),
            useAlias: true);
        prepared.Preparation.Authority.AuthorityKind.Should().Be(
            testCase.Mode == ProfileMode.Shadow
                ? AgentProfileTurnAuthorityKind.Recovery
                : AgentProfileTurnAuthorityKind.Selected);
        AssertNoToolExecution(prepared);
    }

    private static async Task AssertAuthorityCopyAsync(RolloutCase testCase)
    {
        var prepared = await PrepareCaseAsync(
            testCase,
            AgentProfileTurnClassificationResult.NoMatch(),
            useAlias: true);
        var callerCopy = prepared.Preparation.Authority;
        callerCopy.AuthorityKind = AgentProfileTurnAuthorityKind.RestrictedEmpty;
        callerCopy.AuthorityCeilingToolNames.Clear();

        prepared.Preparation.Authority.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Selected);
        prepared.Preparation.Authority.AuthorityCeilingToolNames.Should()
            .BeEquivalentTo("recovery", prepared.Scenario.TaskToolName);
        AssertNoToolExecution(prepared);
    }

    private static async Task AssertMonotonicReconcileAsync(RolloutCase testCase)
    {
        var prepared = await PrepareCaseAsync(
            testCase,
            AgentProfileTurnClassificationResult.NoMatch(),
            useAlias: true);
        var failed = await MaterializeAsync(prepared);
        failed.ReconcileProposal.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
        failed.ReconcileProposal.AuthorityCeilingToolNames.Should().Equal("recovery");

        var successFetcher = new RecordingFetcher(CreateFetch(prepared.Scenario, FetchOutcome.Success));
        var retryMaterializer = new AgentProfileTurnCatalogMaterializer(
            new EvaluationToolSetRegistry(RouteToolSetName, prepared.Tools),
            new RecordingClassifier(AgentProfileTurnClassificationResult.Failed("must_not_reclassify")),
            successFetcher);
        var retry = await retryMaterializer.MaterializeCommittedAsync(
            prepared.Profile,
            failed.ReconcileProposal,
            "turn-token",
            prepared.Tools,
            ToolContext(),
            CancellationToken.None);

        retry.ReconcileProposal.AuthorityKind.Should().Be(AgentProfileTurnAuthorityKind.Recovery);
        retry.ReconcileProposal.AuthorityCeilingToolNames.Should().Equal("recovery");
        retry.Catalog.FinalAllowedToolNames.Should().Equal("recovery");
        successFetcher.CallCount.Should().Be(1);
        AssertNoToolExecution(prepared);
    }

    private static async Task<PreparedCase> PrepareCaseAsync(
        RolloutCase testCase,
        AgentProfileTurnClassificationResult classifierResult,
        bool useAlias,
        Action<AgentProfileSnapshot>? configureProfile = null,
        ToolApprovalMode taskApprovalMode = ToolApprovalMode.NeverRequire,
        IReadOnlyCollection<string>? visibility = null,
        string? skillMarkdown = null)
    {
        var scenario = ScenarioDefinition.For(testCase.Scenario);
        var recoveryTool = new EvaluationTool("recovery");
        var taskTool = new EvaluationTool(scenario.TaskToolName, taskApprovalMode);
        IAgentTool[] tools = [recoveryTool, taskTool];
        var classifier = new RecordingClassifier(classifierResult);
        var fetcher = new RecordingFetcher(CreateFetch(scenario, testCase.FetchOutcome, skillMarkdown));
        var materializer = new AgentProfileTurnCatalogMaterializer(
            new EvaluationToolSetRegistry(RouteToolSetName, tools),
            classifier,
            fetcher);
        var profile = BuildProfile(testCase.Mode, scenario, configureProfile);
        var message = useAlias
            ? scenario.Message(testCase.Language)
            : $"unaliased {testCase.Language.ToString().ToLowerInvariant()} request";
        var preparation = await materializer.PrepareAsync(
            profile,
            $"session-{testCase.Id}",
            message,
            tools,
            ToolContext(allowedToolNames: visibility),
            CancellationToken.None);

        AgentProfileSnapshotCodec.Verify(profile).Should().BeTrue();
        return new PreparedCase(
            testCase,
            scenario,
            profile,
            tools,
            recoveryTool,
            taskTool,
            classifier,
            fetcher,
            materializer,
            preparation,
            visibility);
    }

    private static Task<AgentProfileTurnCatalogMaterialization> MaterializeAsync(PreparedCase prepared) =>
        prepared.Materializer.MaterializeCommittedAsync(
            prepared.Profile,
            prepared.Preparation.Authority,
            "turn-token",
            prepared.Tools,
            ToolContext(allowedToolNames: prepared.Visibility),
            CancellationToken.None);

    private static AgentProfileSnapshot BuildProfile(
        ProfileMode mode,
        ScenarioDefinition scenario,
        Action<AgentProfileSnapshot>? configure = null)
    {
        var member = new AgentProfileSkillMember
        {
            IntentId = scenario.IntentId,
            RoutingDescription = $"Route {scenario.IntentId} requests.",
            SkillRef = scenario.SkillRef.Clone(),
            TaskToolPolicy = new AgentProfileToolPolicy(),
            SideEffectClass = scenario.SideEffectClass,
            ExpectedSkillName = scenario.SkillName,
            ReviewedPublisherId = PublisherId,
        };
        member.ExplicitTriggerAliases.Add([scenario.ChineseAlias, scenario.EnglishAlias]);
        member.TaskToolPolicy.ToolNames.Add(scenario.TaskToolName);

        var profile = new AgentProfileSnapshot
        {
            ProfileId = "nyxid-chat-evaluation",
            ProfileVersion = mode == ProfileMode.Shadow ? "shadow-v1" : "enforced-v1",
            AgentKind = "nyxid.chat",
            PolicyRevision = "evaluation-policy-v1",
            SkillsetProvenance = new ExactRemoteSkillsetRef
            {
                Guid = "10000000-0000-0000-0000-000000000000",
                LiteralVersion = SkillVersion,
            },
            RouteToolSetRef = RouteToolSetName,
            MaximumToolPolicy = new AgentProfileToolPolicy(),
            RecoveryToolPolicy = new AgentProfileToolPolicy(),
            MaxPlanSteps = 4,
            HandoffTtlSeconds = 300,
            ClassifierTimeoutMs = 600,
            ExactSkillFetchTimeoutMs = 1_500,
            MaxSelectedSkillBytes = 1_024,
            ActivationMode = mode == ProfileMode.Shadow
                ? AgentProfileActivationMode.Shadow
                : AgentProfileActivationMode.Enforced,
        };
        profile.MaximumToolPolicy.ToolNames.Add(["recovery", scenario.TaskToolName]);
        profile.RecoveryToolPolicy.ToolNames.Add("recovery");
        profile.Members.Add(member);
        configure?.Invoke(profile);
        return AgentProfileSnapshotCodec.Seal(profile);
    }

    private static void AddAliasCollision(AgentProfileSnapshot profile)
    {
        var collidingMember = profile.Members.Single().Clone();
        collidingMember.IntentId = $"{collidingMember.IntentId}-other";
        collidingMember.SkillRef.Guid = "29999999-9999-9999-9999-999999999999";
        collidingMember.ExpectedSkillName = $"{collidingMember.ExpectedSkillName}-other";
        profile.Members.Add(collidingMember);
    }

    private static ExactRemoteSkillFetchResult CreateFetch(
        ScenarioDefinition scenario,
        FetchOutcome outcome,
        string? skillMarkdown = null)
    {
        if (outcome == FetchOutcome.NotFound)
            return ExactRemoteSkillFetchResult.Failed(ExactRemoteSkillFetchFailureCode.NotFound);

        var markdown = skillMarkdown ?? outcome switch
        {
            FetchOutcome.OversizedBody =>
                SkillMarkdown(scenario, new string('x', 1_100)),
            FetchOutcome.EmptyBody =>
                $"---\nname: {scenario.SkillName}\n---\n",
            FetchOutcome.FrontmatterMismatch =>
                $"---\nname: wrong-name\n---\nReviewed instructions.",
            _ => SkillMarkdown(scenario, $"Reviewed instructions for {scenario.IntentId}."),
        };
        return ExactRemoteSkillFetchResult.Success(
            outcome == FetchOutcome.GuidMismatch
                ? "29999999-0000-0000-0000-000000000000"
                : scenario.SkillRef.Guid,
            outcome == FetchOutcome.VersionMismatch ? "1.2-other" : scenario.SkillRef.LiteralVersion,
            outcome == FetchOutcome.NameMismatch ? "wrong-name" : scenario.SkillName,
            outcome == FetchOutcome.PublisherMismatch ? "publisher-unreviewed" : PublisherId,
            outcome == FetchOutcome.MissingHash ? string.Empty : $"hash-{scenario.IntentId}",
            markdown);
    }

    private static string SkillMarkdown(ScenarioDefinition scenario, string body) =>
        $"---\nname: {scenario.SkillName}\n---\n{body}";

    private static AgentToolExecutionContext ToolContext(
        string? accessToken = "turn-token",
        IEnumerable<string>? allowedToolNames = null) =>
        AgentToolExecutionContext.Empty with
        {
            Credentials = new AgentToolCredentials(accessToken, null, null),
            ToolVisibility = allowedToolNames is null
                ? AgentToolVisibilityScope.Unrestricted
                : AgentToolVisibilityScope.FromAllowedToolNames(allowedToolNames),
        };

    private static void AssertNoToolExecution(PreparedCase prepared)
    {
        prepared.RecoveryTool.ExecuteCount.Should().Be(0);
        prepared.TaskTool.ExecuteCount.Should().Be(0);
    }

    public sealed record RolloutCase(
        EvaluationGroup Group,
        string Id,
        CaseBehavior Behavior,
        ScenarioKind Scenario,
        CaseLanguage Language,
        ProfileMode Mode,
        FetchOutcome FetchOutcome = FetchOutcome.Success);

    public enum EvaluationGroup
    {
        Routing,
        ContinuationAuth,
        IdentitySafety,
        LifecycleIsolationRegression,
        PromptToolLatency,
    }

    public enum CaseBehavior
    {
        AliasRoute,
        ClassifierRoute,
        AliasCollision,
        ClassifierNoMatch,
        ClassifierFailure,
        NextTurnCorrection,
        ShadowNoSideEffect,
        AuthorizedContinuation,
        MissingCredential,
        RemoteUnavailable,
        ExactIdentityMismatch,
        InvalidSkillBody,
        MaximumAttenuation,
        VisibilityAttenuation,
        ToolCollision,
        ApprovalRequired,
        PinnedSnapshot,
        SerializedRestart,
        ConcurrentSessions,
        BoundMode,
        AuthorityCopy,
        MonotonicReconcile,
        PromptWithinBound,
        PromptExactBoundary,
        BuiltInRecoveryFloor,
        LatencyBudgets,
    }

    public enum ScenarioKind
    {
        Discovery,
        Connect,
        Call,
        Maintenance,
    }

    public enum CaseLanguage
    {
        Chinese,
        English,
    }

    public enum ProfileMode
    {
        Shadow,
        Enforced,
    }

    public enum FetchOutcome
    {
        Success,
        NotFound,
        GuidMismatch,
        VersionMismatch,
        NameMismatch,
        PublisherMismatch,
        MissingHash,
        OversizedBody,
        EmptyBody,
        FrontmatterMismatch,
    }

    private sealed record PreparedCase(
        RolloutCase TestCase,
        ScenarioDefinition Scenario,
        AgentProfileSnapshot Profile,
        IReadOnlyList<IAgentTool> Tools,
        EvaluationTool RecoveryTool,
        EvaluationTool TaskTool,
        RecordingClassifier Classifier,
        RecordingFetcher Fetcher,
        AgentProfileTurnCatalogMaterializer Materializer,
        AgentProfileTurnAuthorityPreparation Preparation,
        IReadOnlyCollection<string>? Visibility);

    private sealed record ScenarioDefinition(
        string IntentId,
        string ChineseAlias,
        string EnglishAlias,
        string TaskToolName,
        string SkillName,
        ExactRemoteSkillRef SkillRef,
        AgentProfileSideEffectClass SideEffectClass)
    {
        public string Message(CaseLanguage language) =>
            $"{(language == CaseLanguage.Chinese ? ChineseAlias : EnglishAlias)} request";

        public static ScenarioDefinition For(ScenarioKind scenario)
        {
            var ordinal = (int)scenario + 1;
            var slug = scenario.ToString().ToLowerInvariant();
            return new ScenarioDefinition(
                $"intent-{slug}",
                scenario switch
                {
                    ScenarioKind.Discovery => "/发现服务",
                    ScenarioKind.Connect => "/连接服务",
                    ScenarioKind.Call => "/调用服务",
                    ScenarioKind.Maintenance => "/维护服务",
                    _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
                },
                $"/{slug}-en",
                $"{slug}_task",
                $"nyxid-{slug}",
                new ExactRemoteSkillRef
                {
                    Guid = $"20000000-0000-0000-0000-{ordinal:D12}",
                    LiteralVersion = SkillVersion,
                },
                scenario switch
                {
                    ScenarioKind.Connect => AgentProfileSideEffectClass.ExternalHandoff,
                    ScenarioKind.Call => AgentProfileSideEffectClass.ServiceCall,
                    ScenarioKind.Maintenance => AgentProfileSideEffectClass.Maintenance,
                    _ => AgentProfileSideEffectClass.ReadOnly,
                });
        }
    }

    private sealed class RecordingClassifier(AgentProfileTurnClassificationResult result)
        : IAgentProfileTurnClassifier
    {
        public int CallCount { get; private set; }

        public Task<AgentProfileTurnClassificationResult> ClassifyAsync(
            AgentProfileTurnClassificationRequest request,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(result);
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

    private sealed class EvaluationToolSetRegistry : IToolSetRegistry
    {
        private readonly string _name;
        private readonly IReadOnlyList<IAgentToolSource> _sources;

        public EvaluationToolSetRegistry(string name, IReadOnlyList<IAgentTool> tools)
        {
            _name = name;
            _sources = [new StaticToolSource(tools)];
        }

        public IReadOnlyList<string> GetRegisteredNames() => [_name];

        public ToolSetResolveResult Resolve(ChatRouteToolSetRef? toolSetRef) =>
            string.Equals(toolSetRef?.Name, _name, StringComparison.Ordinal)
                ? ToolSetResolveResult.Success(_name, _sources)
                : ToolSetResolveResult.Failure(new ToolSetResolveError(
                    ToolSetResolveError.UnknownNameCode,
                    toolSetRef?.Name ?? string.Empty,
                    "missing",
                    GetRegisteredNames()));
    }

    private sealed class StaticToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
            Task.FromResult(tools);
    }

    private sealed class EvaluationTool(string name, ToolApprovalMode approvalMode = ToolApprovalMode.NeverRequire)
        : IAgentTool
    {
        public string Name => name;
        public string Description => name;
        public string ParametersSchema => "{}";
        public ToolApprovalMode ApprovalMode => approvalMode;
        public int ExecuteCount { get; private set; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            ExecuteCount++;
            return Task.FromResult("{}");
        }
    }
}
