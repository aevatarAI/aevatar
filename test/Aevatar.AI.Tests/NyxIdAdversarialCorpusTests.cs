using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Tests;

/// <summary>
/// Executes the checked-in adversarial corpus against production code.
///
/// The corpus previously carried its payloads and expected outcomes as data that nothing ever
/// ran: the guard and the manifest test asserted only its shape — categories present, effect
/// receipts forbidden, identity strings distinct — so a claim like "an injected instruction is
/// ignored" was asserted by the corpus about itself. Here every fixture is bound to a real
/// code path, and a fixture without a binding fails the suite rather than passing silently.
///
/// Each executor asserts the enforcement that actually exists. Where the repository enforces an
/// outcome structurally rather than by inspecting content — an injected instruction cannot
/// approve anything because no model-reachable path decides approvals at all — the executor
/// asserts that structure, and says so.
/// </summary>
public sealed class NyxIdAdversarialCorpusTests
{
    private static readonly string ContractRoot = Path.Combine(
        FindRepositoryRoot(), "docs", "contracts", "nyxid-assistant-conformance", "v1");

    private static readonly Timestamp AskedAt = Timestamp.FromDateTimeOffset(
        DateTimeOffset.Parse("2026-08-01T12:00:00Z"));
    private static readonly Timestamp ResolvedAt = Timestamp.FromDateTimeOffset(
        DateTimeOffset.Parse("2026-08-01T12:01:00Z"));

    private static readonly IReadOnlyDictionary<string, Action<JsonElement>> Executors =
        new Dictionary<string, Action<JsonElement>>(StringComparer.Ordinal)
        {
            ["github-indirect-injection"] = RunUntrustedContentCannotDecide,
            ["lark-indirect-injection"] = RunUntrustedContentCannotDecide,
            ["mcp-result-indirect-injection"] = RunAdmissionSurvivesResultAuthoredOverride,
            ["external-page-indirect-injection"] = RunClaimedCompletionNeedsTypedEvidence,
            ["studio-identity-confusion"] = RunCrossResourceIdentityIsRejected,
            ["duplicate-command"] = RunDuplicateDecisionIsIdempotent,
            ["stale-conflicting-reordered-decisions"] = RunStaleAndConflictingDecisionsAreRejected,
            ["credential-and-content-leakage"] = RunForbiddenFieldsStayOutOfCommittedState,
            ["unknown-wire-members"] = RunUnknownWireMembersAreToleratedOrFailClosed,
            ["false-execution-verification-consistency"] = RunEffectIsNeverClaimedWithoutEvidence,
        };

    public static TheoryData<string> FixtureIds()
    {
        var data = new TheoryData<string>();
        foreach (var fixture in Fixtures())
            data.Add(fixture.GetProperty("id").GetString()!);
        return data;
    }

    [Fact]
    public void EveryFixture_ShouldBeBoundToAnExecutor()
    {
        var ids = Fixtures().Select(fixture => fixture.GetProperty("id").GetString()!).ToList();

        ids.Should().OnlyHaveUniqueItems();
        ids.Should().BeSubsetOf(
            Executors.Keys,
            "a corpus fixture without an executor asserts nothing about the system");
        Executors.Keys.Should().BeSubsetOf(
            ids,
            "an executor whose fixture was removed no longer describes the corpus");
    }

    [Theory]
    [MemberData(nameof(FixtureIds))]
    public void Fixture_ShouldEnforceItsExpectedOutcome(string fixtureId)
    {
        var fixture = Fixtures().Single(item => item.GetProperty("id").GetString() == fixtureId);

        // Every fixture in this corpus forbids an effect receipt. The executors below each end
        // in a state where no external effect was authorized, so the corpus-wide claim is
        // enforced by the same run that enforces the individual outcome.
        fixture.GetProperty("effect_receipt_allowed").GetBoolean().Should().BeFalse();

        Executors[fixtureId].Invoke(fixture);
    }

    // Indirect prompt injection, whatever its source. Aevatar does not defend this by reading
    // the payload: an approval advances only through the typed client command carrying the
    // exact pending request identity and the observed state version. Untrusted text — a GitHub
    // issue body, a Lark message, a tool result — has no path to that command, and text that
    // reaches the actor as a decision reason is never committed.
    private static void RunUntrustedContentCannotDecide(JsonElement fixture)
    {
        var payload = fixture.GetProperty("payload").GetString()!;
        var state = ApprovalState();

        // The injected instruction, arriving as content rather than as the pending identity,
        // cannot select the pending approval.
        var impersonating = ApprovalCommand(requestId: payload, approved: true, expectedVersion: 52);
        var ignored = NyxIdChatNeedsYouDecisions.ResolveApproval(state, impersonating, 52, ResolvedAt);

        ignored.ShouldCommit.Should().BeFalse();
        ignored.State.PendingApproval.Should().NotBeNull();
        ignored.NextCommand.Should().BeNull();

        // Carried as the decision reason on an otherwise valid command it is accepted as a
        // decision but never retained, so it cannot resurface as an instruction later.
        var decided = ApprovalCommand("approval-alpha", approved: false, expectedVersion: 52);
        decided.Reason = payload;
        var accepted = NyxIdChatNeedsYouDecisions.ResolveApproval(state, decided, 52, ResolvedAt);

        accepted.ShouldCommit.Should().BeTrue();
        accepted.State.LatestApprovalResolution.Approved.Should().BeFalse();
        accepted.State.ToString().Should().NotContain(payload);
    }

    // A tool result that asks for a different endpoint cannot reach the wire: the admitted
    // request is built only from declared value slots, and route identity and execution policy
    // are not slots.
    private static void RunAdmissionSurvivesResultAuthoredOverride(JsonElement fixture)
    {
        foreach (var authored in new[]
                 {
                     """{"endpoint_id":"endpoint-override"}""",
                     """{"path":"/override"}""",
                     """{"method":"DELETE"}""",
                     """{"execution_policy":{"risk":"read_only"}}""",
                 })
        {
            var result = NyxIdAdmittedRequestBuilder.Build(AuthoredAdmission(), authored);

            result.Succeeded.Should().BeFalse(
                "a result-authored override of {0} must not reach the wire", authored);
            result.Failure!.Code.Should().Be("NYXID_OPERATION_ARGUMENT_NOT_SUPPORTED");
        }
    }

    // Text claiming an effect already happened does not make a step done: an effect-capable
    // step reaches a terminal only through its typed postcondition, and an unverifiable one
    // stays uncertain.
    private static void RunClaimedCompletionNeedsTypedEvidence(JsonElement fixture)
    {
        var state = ApprovalState();
        var step = state.ActiveTask.Steps.Single();
        step.Status = NyxIdChatStepStatus.Running;
        step.ExternalEffect = NyxIdChatEffectEvidence.NotStarted;

        // The page text is not a receipt: nothing in the committed state advances because a
        // caller asserted success in content.
        var claimed = ApprovalCommand(
            requestId: fixture.GetProperty("payload").GetString()!,
            approved: true,
            expectedVersion: 52);
        var result = NyxIdChatNeedsYouDecisions.ResolveApproval(state, claimed, 52, ResolvedAt);

        result.ShouldCommit.Should().BeFalse();
        result.State.ActiveTask.Steps.Single().Status.Should().Be(NyxIdChatStepStatus.Running);
        result.State.ActiveTask.Steps.Single().ExternalEffect
            .Should().Be(NyxIdChatEffectEvidence.NotStarted);
    }

    // One identity kind may never stand in for another. The fixture names Studio provisioning
    // identities; the equivalent rail in this subsystem is that a pending approval is selected
    // only by its own approval request id, never by the task, turn, step, or conversation id
    // that travels beside it.
    private static void RunCrossResourceIdentityIsRejected(JsonElement fixture)
    {
        var state = ApprovalState();
        var neighbouringIdentities = new[]
        {
            state.ActiveTask.TaskId,
            state.ActiveTurn.TurnId,
            state.ActiveTask.ActiveStepId,
            state.ConversationActorId,
            fixture.GetProperty("member_id").GetString()!,
            fixture.GetProperty("workflow_id").GetString()!,
            fixture.GetProperty("published_service_id").GetString()!,
        };

        foreach (var identity in neighbouringIdentities)
        {
            var command = ApprovalCommand(identity, approved: true, expectedVersion: 52);
            var result = NyxIdChatNeedsYouDecisions.ResolveApproval(state, command, 52, ResolvedAt);

            result.ShouldCommit.Should().BeFalse(
                "{0} is not the pending approval identity", identity);
            result.State.PendingApproval.Should().NotBeNull();
        }
    }

    // The same decision delivered twice is an idempotent replay, not a second decision.
    private static void RunDuplicateDecisionIsIdempotent(JsonElement fixture)
    {
        var version = FixtureVersion(fixture, "command:v7");
        var state = ApprovalState();

        var first = NyxIdChatNeedsYouDecisions.ResolveApproval(
            state, ApprovalCommand("approval-alpha", true, version), version, ResolvedAt);
        first.ShouldCommit.Should().BeTrue();

        var replay = NyxIdChatNeedsYouDecisions.ResolveApproval(
            first.State, ApprovalCommand("approval-alpha", true, version), version, ResolvedAt);

        replay.ShouldCommit.Should().BeFalse();
        replay.IsExactReplay.Should().BeTrue();
        replay.State.ToByteArray().Should().Equal(
            first.State.ToByteArray(),
            "an exact replay must not advance committed state");
    }

    // approve@v9 wins; deny@v7 is stale; deny@v9 conflicts with the decision already committed.
    // None of the later commands may overwrite the first.
    private static void RunStaleAndConflictingDecisionsAreRejected(JsonElement fixture)
    {
        var sequence = fixture.GetProperty("sequence").EnumerateArray()
            .Select(item => item.GetString()!)
            .ToList();
        sequence.Should().HaveCount(3);

        var current = DecisionVersion(sequence[0]);
        var state = ApprovalState();

        var accepted = NyxIdChatNeedsYouDecisions.ResolveApproval(
            state,
            ApprovalCommand("approval-alpha", DecisionApproved(sequence[0]), current),
            current,
            ResolvedAt);
        accepted.ShouldCommit.Should().BeTrue();
        accepted.State.LatestApprovalResolution.Approved.Should().Be(DecisionApproved(sequence[0]));

        foreach (var later in sequence.Skip(1))
        {
            var result = NyxIdChatNeedsYouDecisions.ResolveApproval(
                accepted.State,
                ApprovalCommand("approval-alpha", DecisionApproved(later), DecisionVersion(later)),
                current,
                ResolvedAt);

            result.ShouldCommit.Should().BeFalse("{0} must not overwrite the first decision", later);
            result.State.ToByteArray().Should().Equal(accepted.State.ToByteArray());
        }
    }

    // Credentials, decision reasons, raw arguments, raw results, and user content are evidence
    // inputs, never committed facts.
    private static void RunForbiddenFieldsStayOutOfCommittedState(JsonElement fixture)
    {
        var forbidden = fixture.GetProperty("forbidden_fields").EnumerateArray()
            .Select(item => item.GetString()!)
            .ToList();
        forbidden.Should().NotBeEmpty();

        // A secret-shaped action parameter never becomes an action at all.
        var secretParams = () => NyxIdActionSecretPolicy.ValidateParamsJson(
            """{"serviceSlug":"github","credential":"must-not-pass"}""");
        secretParams.Should().Throw<NyxIdActionSecretPolicyException>()
            .Which.Code.Should().Be("NYXID_ACTION_SECRET_FIELD_FORBIDDEN");

        // A decision reason is consumed to derive the decision digest and then dropped.
        var sentinel = $"forbidden-{string.Join('-', forbidden)}";
        var command = ApprovalCommand("approval-alpha", approved: false, expectedVersion: 52);
        command.Reason = sentinel;
        var accepted = NyxIdChatNeedsYouDecisions.ResolveApproval(
            ApprovalState(), command, 52, ResolvedAt);

        accepted.ShouldCommit.Should().BeTrue();
        accepted.State.LatestApprovalResolution.DecisionSha256.Should().NotBeEmpty();
        accepted.State.ToString().Should().NotContain(sentinel);
        accepted.State.ToByteArray().Should().NotBeEmpty();
    }

    // Unknown additive members are ignored; unknown closed-vocabulary members fail closed.
    private static void RunUnknownWireMembersAreToleratedOrFailClosed(JsonElement fixture)
    {
        var mutations = fixture.GetProperty("wire_mutations").EnumerateArray()
            .Select(item => item.GetString()!)
            .ToList();
        mutations.Should().Contain(["unknown_enum", "unknown_field"]);

        // Mutate the pinned registry the assistant actually loads, so the tolerance being
        // proven is the shipped reader's, not a hand-authored payload's.
        var pinned = File.ReadAllText(Path.Combine(ContractRoot, "registry-v4.json"));

        // unknown_field: additive members at registry and descriptor level are ignored, and
        // the known verb still resolves.
        var extended = JsonNode.Parse(pinned)!.AsObject();
        extended["unknownRegistryMember"] = true;
        extended["actions"]!.AsArray()[0]!.AsObject()["unknownActionMember"] =
            JsonNode.Parse("""{"nested":1}""");
        var registry = NyxIdAssistantActionRegistry.Load(extended.ToJsonString());
        registry.TryGetDefinition("service.connect", out _).Should().BeTrue();

        // unknown_enum: an undeclared closed-vocabulary value is never silently coerced into
        // a weaker risk — the descriptor fails closed on its own and stays unavailable.
        var unknownEnum = JsonNode.Parse(pinned)!.AsObject();
        unknownEnum["actions"]!.AsArray()[0]!.AsObject()["risk"] = "unknown-risk";
        var degraded = NyxIdAssistantActionRegistry.Load(unknownEnum.ToJsonString());
        degraded.TryGetDefinition("service.connect", out _).Should().BeFalse();
        degraded.SkippedActions.Should().ContainSingle(skip =>
            skip.WireAction == "service.connect" &&
            skip.Code == "NYXID_ACTION_REGISTRY_INVALID");
    }

    // No path may report an effect that no receipt or postcondition backs.
    private static void RunEffectIsNeverClaimedWithoutEvidence(JsonElement fixture)
    {
        var claims = fixture.GetProperty("forbidden_claims").EnumerateArray()
            .Select(item => item.GetString()!)
            .ToList();
        claims.Should().Contain("executed_without_receipt");

        // A denial does not end the operation by assertion: the tool re-enters so it can emit
        // a typed denied receipt. What must hold is that no effect is claimed from the
        // decision itself, and that re-entry is bound to the decided approval at a fresh
        // generation rather than running as a new unauthorized execution.
        var state = ApprovalState();
        var consumedGeneration = state.ActiveTask.Steps.Single().Operation.Key.OperationGeneration;
        var denied = NyxIdChatNeedsYouDecisions.ResolveApproval(
            state,
            ApprovalCommand("approval-alpha", approved: false, expectedVersion: 52),
            52,
            ResolvedAt);

        denied.ShouldCommit.Should().BeTrue();
        denied.State.LatestApprovalResolution.Approved.Should().BeFalse();
        denied.State.ActiveTask.Steps.Single().ExternalEffect
            .Should().NotBe(
                NyxIdChatEffectEvidence.Confirmed,
                "a decision is not evidence that an external effect happened");
        denied.NextCommand!.InputCase.Should().Be(
            NyxIdChatOperationDispatchCommand.InputOneofCase.ToolApprovalContinuation);
        denied.NextCommand.ToolApprovalContinuation.ApprovalRequestId
            .Should().Be("approval-alpha");
        denied.NextCommand.Key.OperationGeneration.Should().BeGreaterThan(
            consumedGeneration,
            "a consumed approval must never authorize the same generation twice");
    }

    // A published write operation whose route identity and execution policy are fixed at
    // admission time; only the declared value slots may be authored per call.
    private static AgentToolOperationAdmission AuthoredAdmission() =>
        new(
            "us-calendar-alpha",
            "calendar-alpha",
            new AgentToolOperationIdentity.AuthoredRequest(
                "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            AgentToolOperationAuthorizationBasis.ExplicitRequest,
            "POST",
            "/events/{event_id}",
            "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            [
                new AgentToolOperationParameter(
                    "event_id",
                    AgentToolOperationParameterLocation.Path,
                    true,
                    AgentToolOperationValueSchema.Text),
            ],
            null,
            AgentToolOperationResponsePolicy.TextOnly,
            new AgentToolOperationExecutionPolicy(
                AgentToolOperationRisk.Write,
                AgentToolOperationApproval.Required,
                AgentToolOperationEnforcementOwner.Aevatar,
                [AgentToolOperationExecutionMode.Interactive]));

    private static NyxIdChatApprovalResolveCommand ApprovalCommand(
        string requestId,
        bool approved,
        long expectedVersion) =>
        new()
        {
            ScopeId = "scope-alpha",
            ConversationActorId = "conversation-alpha",
            RequestId = requestId,
            ClientRequestId = "client-approval-alpha",
            Approved = approved,
            ExpectedStateVersion = expectedVersion,
        };

    private static long FixtureVersion(JsonElement fixture, string entry)
    {
        var sequence = fixture.GetProperty("sequence").EnumerateArray()
            .Select(item => item.GetString()!)
            .ToList();
        sequence.Should().OnlyContain(item => item == entry);
        return DecisionVersion(entry);
    }

    private static long DecisionVersion(string entry) =>
        long.Parse(entry.Split(':')[1].TrimStart('v'));

    private static bool DecisionApproved(string entry) =>
        entry.Split(':').Length > 2 && entry.Split(':')[2] == "approve";

    private static IReadOnlyList<JsonElement> Fixtures()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(ContractRoot, "adversarial-fixtures.json")));
        return document.RootElement.GetProperty("fixtures").EnumerateArray()
            .Select(fixture => fixture.Clone())
            .ToList();
    }

    private static NyxIdChatConversationGAgentState ApprovalState()
    {
        var state = new NyxIdChatConversationGAgentState
        {
            ScopeId = "scope-alpha",
            ConversationActorId = "conversation-alpha",
            ProgressSequence = 7,
            ActiveTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTurnStatus.Active,
            },
            ActiveTask = new NyxIdChatTaskState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                Status = NyxIdChatTaskStatus.Active,
                ActiveStepId = "step-alpha",
            },
        };
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = "step-alpha",
            Kind = NyxIdChatStepKind.Tool,
            Status = NyxIdChatStepStatus.Waiting,
            Description = "Delete the repository.",
            ApprovalRequestId = "approval-alpha",
            MayChangeExternalState = true,
            Source = new NyxIdChatStepSource
            {
                Tool = new NyxIdChatToolStepSource { ToolName = "repository_delete" },
            },
            Operation = new NyxIdChatOperationState
            {
                Key = new NyxIdChatOperationKey
                {
                    ConversationActorId = "conversation-alpha",
                    TurnId = "turn-alpha",
                    TaskId = "task-alpha",
                    StepId = "step-alpha",
                    OperationId = "operation-approval-alpha",
                    OperationGeneration = 1,
                },
                Kind = NyxIdChatStepKind.Tool,
                Phase = NyxIdChatOperationPhase.Succeeded,
                MayChangeExternalState = true,
            },
        });
        state.PendingApproval = new NyxIdChatPendingApprovalState
        {
            ApprovalRequestId = "approval-alpha",
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = "step-alpha",
            ToolCallId = "call-approval-alpha",
            ToolName = "repository_delete",
            AskedAt = AskedAt.Clone(),
            Presentation = new NyxIdChatApprovalPresentation
            {
                Action = "delete",
                Target = "repository:repo-alpha",
                ActorLabel = "Aevatar Assistant",
                Reversibility = NyxIdChatApprovalReversibility.Irreversible,
                GrantBoundary = "within_grant",
            },
        };
        return state;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Aevatar repository root.");
    }
}
