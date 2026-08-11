using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatFixtureConvergenceTests
{
    private static readonly string FixtureDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "NyxIdChat",
        "v1");

    [Fact]
    public void VersionOneFixtures_ShouldConvergeOnCommittedPendingInputAndAttention()
    {
        using var liveFrame = ReadFixture("live-frame.json");
        using var currentState = ReadFixture("current-state.json");
        using var conversationSummary = ReadFixture("conversation-summary.json");

        var frameRoot = liveFrame.RootElement;
        frameRoot.GetProperty("type").GetString().Should().Be("CUSTOM");
        frameRoot.GetProperty("custom").GetProperty("name").GetString()
            .Should().Be("nyxid.input.request");

        var livePending = frameRoot.GetProperty("custom").GetProperty("payload");
        var stateRoot = currentState.RootElement;
        var snapshot = stateRoot.GetProperty("snapshot");
        var currentPending = snapshot.GetProperty("pendingInput");
        AssertPendingInputEquivalent(livePending, currentPending);

        var summary = conversationSummary.RootElement;
        snapshot.GetProperty("actorId").GetString().Should()
            .Be(summary.GetProperty("id").GetString());
        snapshot.GetProperty("taskStatus").GetString().Should()
            .Be(summary.GetProperty("taskStatus").GetString());
        snapshot.GetProperty("attentionKind").GetString().Should()
            .Be(summary.GetProperty("attentionKind").GetString());
        ParseInstant(snapshot.GetProperty("attentionSince")).Should()
            .Be(ParseInstant(summary.GetProperty("attentionSince")));
        snapshot.GetProperty("activeStepSummary").GetString().Should()
            .Be(summary.GetProperty("activeStepSummary").GetString())
            .And.Be(livePending.GetProperty("prompt").GetString());
        snapshot.GetProperty("stateVersion").GetInt64().Should()
            .Be(summary.GetProperty("stateVersion").GetInt64())
            .And.Be(stateRoot.GetProperty("stateVersion").GetInt64());
        frameRoot.GetProperty("sequence").GetInt64().Should()
            .Be(snapshot.GetProperty("progressSequence").GetInt64());
    }

    [Fact]
    public void VersionOneTaskPlanFixtures_ShouldUseOneShapeAcrossLiveAndCurrentState()
    {
        using var liveFrames = ReadFixture("task-plan-live-frames.json");
        using var currentState = ReadFixture("task-plan-current-state.json");
        var snapshotFrame = liveFrames.RootElement.GetProperty("snapshot");
        var changedFrame = liveFrames.RootElement.GetProperty("stepChanged");
        var liveTask = snapshotFrame.GetProperty("custom").GetProperty("payload");
        var currentTask = currentState.RootElement.GetProperty("snapshot")
            .GetProperty("activeTask");

        snapshotFrame.GetProperty("custom").GetProperty("name").GetString().Should()
            .Be("nyxid.task.snapshot");
        snapshotFrame.GetProperty("sequence").GetInt64().Should().Be(67);
        currentState.RootElement.GetProperty("stateVersion").GetInt64().Should().Be(67);
        currentState.RootElement.GetProperty("snapshot").GetProperty("progressSequence")
            .GetInt64().Should().Be(67);
        JsonNode.DeepEquals(
                JsonNode.Parse(liveTask.GetRawText()),
                JsonNode.Parse(currentTask.GetRawText()))
            .Should().BeTrue("live TaskPlan and current-state activeTask use one decoder shape");

        foreach (var propertyName in new[]
                 {
                     "schemaVersion", "actorId", "taskId", "turnId", "planId",
                     "planRevision", "title", "gate", "steps",
                 })
        {
            liveTask.TryGetProperty(propertyName, out _).Should().BeTrue(propertyName);
        }
        liveTask.GetProperty("gate").GetProperty("mode").GetString().Should().Be("confirm");
        liveTask.GetProperty("gate").GetProperty("reason").GetString().Should().NotBeEmpty();

        var steps = liveTask.GetProperty("steps").EnumerateArray().ToArray();
        steps.Select(step => step.GetProperty("source").EnumerateObject().Single().Name)
            .Should().Equal(
                "llm", "tool", "browserAction", "postcondition", "input", "approval", "web");
        var toolStep = steps.Single(step => step.GetProperty("stepId").GetString() == "step-tool");
        toolStep.GetProperty("addedBy").GetString().Should().Be("replan");
        toolStep.GetProperty("dependsOn").EnumerateArray().Select(static item => item.GetString())
            .Should().Equal("step-llm");
        toolStep.GetProperty("estimate").GetProperty("seconds").GetInt32().Should().Be(30);
        toolStep.GetProperty("substeps").GetArrayLength().Should().Be(2);
        toolStep.GetProperty("source").GetProperty("tool")
            .GetProperty("readinessCapabilityId").GetString().Should().Be("api-github");
        toolStep.GetProperty("externalEffect").GetString().Should().Be("not_applied");
        AssertAvailableActions(toolStep.GetProperty("availableActions"), true, true, false);
        steps.Single(step => step.GetProperty("stepId").GetString() == "step-postcondition")
            .GetProperty("source").GetProperty("postcondition").GetProperty("check")
            .GetString().Should().Be("service.connected");

        var changed = changedFrame.GetProperty("custom");
        changed.GetProperty("name").GetString().Should().Be("nyxid.task.step.changed");
        var changedPayload = changed.GetProperty("payload");
        changedPayload.GetProperty("taskId").GetString().Should()
            .Be(liveTask.GetProperty("taskId").GetString());
        changedPayload.GetProperty("planRevision").GetInt32().Should()
            .Be(liveTask.GetProperty("planRevision").GetInt32());
        changedPayload.GetProperty("changeKind").GetString().Should().Be("status");
        JsonNode.DeepEquals(
                JsonNode.Parse(changedPayload.GetProperty("step").GetRawText()),
                JsonNode.Parse(toolStep.GetRawText()))
            .Should().BeTrue("step.changed carries the same complete step shape");
    }

    [Fact]
    public void VersionOneUc3AndUc4Fixtures_ShouldConvergeOnTypedDomainJourneys()
    {
        using var fixture = ReadFixture("uc3-uc4-domain-journeys.json");
        var root = fixture.RootElement;
        root.GetProperty("specRevision").GetString().Should()
            .Be("f45febb057a7182dab2495d4c739d2bb8d7026f5");
        root.GetProperty("schemaVersion").GetInt32().Should().Be(6);
        var journeys = root.GetProperty("journeys").EnumerateArray().ToArray();
        journeys.Select(item => item.GetProperty("variant").GetString()).Should()
            .Equal(
                "uc3-reimbursement",
                "uc4-below-threshold",
                "uc4-above-threshold");

        foreach (var journey in journeys)
        {
            var live = journey.GetProperty("live");
            var reload = journey.GetProperty("reload");
            var liveTask = live.GetProperty("custom").GetProperty("payload");
            var reloadSnapshot = reload.GetProperty("snapshot");
            var reloadTask = reloadSnapshot.GetProperty("activeTask");

            live.GetProperty("type").GetString().Should().Be("CUSTOM");
            live.GetProperty("custom").GetProperty("name").GetString().Should()
                .Be("nyxid.task.snapshot");
            live.GetProperty("sequence").GetInt64().Should()
                .Be(reload.GetProperty("stateVersion").GetInt64())
                .And.Be(reloadSnapshot.GetProperty("progressSequence").GetInt64());
            liveTask.GetProperty("schemaVersion").GetInt32().Should().Be(6);
            liveTask.GetProperty("status").GetString().Should().Be("succeeded");
            JsonNode.DeepEquals(
                    JsonNode.Parse(liveTask.GetRawText()),
                    JsonNode.Parse(reloadTask.GetRawText()))
                .Should().BeTrue("live and reload must expose the same typed task");
        }

        var reimbursement = FindJourney(journeys, "uc3-reimbursement");
        var reimbursementTask = reimbursement.GetProperty("live")
            .GetProperty("custom").GetProperty("payload");
        var reimbursementDomain = reimbursementTask.GetProperty("domain")
            .GetProperty("reimbursement");
        reimbursementDomain.GetProperty("sourceInvoices").GetArrayLength().Should().Be(3);
        reimbursementDomain.GetProperty("retainedSourceOrdinals")
            .EnumerateArray().Select(item => item.GetInt32()).Should().Equal(1, 2);
        reimbursementDomain.GetProperty("duplicateInvoices")[0]
            .GetProperty("duplicateSourceOrdinal").GetInt32().Should().Be(3);
        reimbursementTask.GetProperty("steps").EnumerateArray()
            .Single(step => step.GetProperty("stepId").GetString() == "step-uc3-write")
            .GetProperty("operation").GetProperty("operationGeneration")
            .GetInt32().Should().Be(2);
        reimbursementTask.GetProperty("artifact").GetProperty("reimbursement")
            .GetProperty("providerInstanceId").GetString().Should()
            .Be("approval-instance-uc3");

        var belowTask = FindJourney(journeys, "uc4-below-threshold")
            .GetProperty("live").GetProperty("custom").GetProperty("payload");
        belowTask.GetProperty("domain").GetProperty("candidateScreening")
            .GetProperty("totalScore").GetInt32().Should().Be(72);
        AssertConditionAndGuard(belowTask, "false", "skipped", "not_applied");
        belowTask.TryGetProperty("artifact", out _).Should().BeFalse();
        belowTask.GetRawText().Should().NotContain("approvalObservation");

        var aboveTask = FindJourney(journeys, "uc4-above-threshold")
            .GetProperty("live").GetProperty("custom").GetProperty("payload");
        aboveTask.GetProperty("domain").GetProperty("candidateScreening")
            .GetProperty("totalScore").GetInt32().Should().Be(80);
        AssertConditionAndGuard(aboveTask, "true", "done", "confirmed");
        aboveTask.GetProperty("artifact").GetProperty("candidateTracker")
            .GetProperty("providerRecordId").GetString().Should()
            .Be("rec-candidate-uc4");
    }

    [Theory]
    [InlineData("input-request", "nyxid.input.request", "input", "pendingInput", null)]
    [InlineData("input-changed", "nyxid.input.changed", "none", null, "latestInputResolution")]
    [InlineData("approval-request", "nyxid.approval.request", "approval", "pendingApproval", null)]
    [InlineData("approval-changed", "nyxid.approval.changed", "none", null, "latestApprovalResolution")]
    public void VersionOneNeedsYouFixtures_ShouldConvergeAcrossAllCommittedShapes(
        string scenario,
        string eventName,
        string attentionKind,
        string? pendingProperty,
        string? resolutionProperty)
    {
        using var liveFrames = ReadFixture("needs-you-live-frames.json");
        using var currentStates = ReadFixture("needs-you-current-states.json");
        using var summaries = ReadFixture("needs-you-conversation-summaries.json");
        var frame = FindScenario(liveFrames.RootElement, scenario);
        var state = FindScenario(currentStates.RootElement, scenario);
        var summary = FindScenario(summaries.RootElement, scenario);
        var snapshot = state.GetProperty("snapshot");
        var payload = frame.GetProperty("custom").GetProperty("payload");

        frame.GetProperty("custom").GetProperty("name").GetString().Should().Be(eventName);
        frame.GetProperty("sequence").GetInt64().Should().Be(
            snapshot.GetProperty("progressSequence").GetInt64());
        state.GetProperty("stateVersion").GetInt64().Should().Be(
            summary.GetProperty("stateVersion").GetInt64());
        snapshot.GetProperty("attentionKind").GetString().Should().Be(attentionKind);
        summary.GetProperty("attentionKind").GetString().Should().Be(attentionKind);
        snapshot.GetProperty("taskStatus").GetString().Should().Be(
            summary.GetProperty("taskStatus").GetString());

        if (pendingProperty is not null)
        {
            var pending = snapshot.GetProperty(pendingProperty);
            var requestProperty = pendingProperty == "pendingInput"
                ? "requestId"
                : "approvalRequestId";
            payload.GetProperty(requestProperty).GetString().Should().Be(
                pending.GetProperty(requestProperty).GetString());
            var latestProperty = pendingProperty == "pendingInput"
                ? "latestInputResolution"
                : "latestApprovalResolution";
            snapshot.GetProperty(latestProperty).ValueKind.Should().Be(JsonValueKind.Null);
        }

        if (resolutionProperty is not null)
        {
            snapshot.GetProperty("pendingInput").ValueKind.Should().Be(JsonValueKind.Null);
            snapshot.GetProperty("pendingApproval").ValueKind.Should().Be(JsonValueKind.Null);
            var latest = snapshot.GetProperty(resolutionProperty);
            latest.GetProperty("requestId").GetString().Should()
                .Be(payload.GetProperty("requestId").GetString());
            latest.GetProperty("clientRequestId").GetString().Should()
                .Be(payload.GetProperty("clientRequestId").GetString());
            latest.GetProperty("outcome").GetString().Should()
                .Be(payload.GetProperty("outcome").GetString());
            ParseInstant(latest.GetProperty("committedAt")).Should()
                .Be(ParseInstant(payload.GetProperty("committedAt")));
        }
    }

    [Theory]
    [InlineData("failed", "failed", "not_applied", true, false, false)]
    [InlineData("uncertain", "uncertain", "may_have_changed", false, false, false)]
    public void VersionOneToolRecoveryFixtures_ShouldConvergeOnAuthoritativeRecoveryIdentity(
        string scenario,
        string status,
        string externalEffect,
        bool retry,
        bool skip,
        bool stop)
    {
        using var liveFrames = ReadFixture("tool-recovery-live-frames.json");
        using var currentStates = ReadFixture("tool-recovery-current-states.json");
        var frame = FindScenario(liveFrames.RootElement, scenario);
        var state = FindScenario(currentStates.RootElement, scenario);
        var liveChange = frame.GetProperty("custom").GetProperty("payload");
        var liveStep = liveChange.GetProperty("step");
        var snapshot = state.GetProperty("snapshot");
        var currentStep = snapshot.GetProperty("activeTask").GetProperty("steps")[0];

        frame.GetProperty("custom").GetProperty("name").GetString().Should()
            .Be("nyxid.task.step.changed");
        frame.GetProperty("sequence").GetInt64().Should()
            .Be(snapshot.GetProperty("progressSequence").GetInt64());
        liveChange.GetProperty("taskId").GetString().Should()
            .Be(snapshot.GetProperty("activeTask").GetProperty("taskId").GetString());
        liveChange.GetProperty("planRevision").GetInt32().Should()
            .Be(snapshot.GetProperty("activeTask").GetProperty("planRevision").GetInt32());
        liveChange.GetProperty("changeKind").GetString().Should().Be("status");
        state.GetProperty("stateVersion").GetInt64().Should()
            .Be(snapshot.GetProperty("stateVersion").GetInt64());

        liveStep.GetProperty("status").GetString().Should().Be(status);
        currentStep.GetProperty("status").GetString().Should().Be(status);
        liveStep.GetProperty("externalEffect").GetString().Should().Be(externalEffect);
        currentStep.GetProperty("externalEffect").GetString().Should().Be(externalEffect);
        AssertToolSourceEquivalent(
            liveStep.GetProperty("source").GetProperty("tool"),
            currentStep.GetProperty("source").GetProperty("tool"));
        AssertAvailableActions(
            liveStep.GetProperty("availableActions"),
            retry,
            skip,
            stop);
        AssertAvailableActions(
            currentStep.GetProperty("availableActions"),
            retry,
            skip,
            stop);

        foreach (var fixture in new[] { frame, state })
        {
            fixture.GetRawText().Should().NotContainAny(
                "credential",
                "token",
                "arguments",
                "resultJson",
                "://");
        }
    }

    private static void AssertToolSourceEquivalent(JsonElement live, JsonElement current)
    {
        foreach (var propertyName in new[]
                 {
                     "toolName", "serviceSlug", "serviceId", "readinessCapabilityId",
                 })
        {
            live.GetProperty(propertyName).GetString().Should()
                .Be(current.GetProperty(propertyName).GetString(), propertyName);
        }
    }

    private static void AssertAvailableActions(
        JsonElement actions,
        bool retry,
        bool skip,
        bool stop)
    {
        actions.GetProperty("retry").GetBoolean().Should().Be(retry);
        actions.GetProperty("skip").GetBoolean().Should().Be(skip);
        actions.GetProperty("stop").GetBoolean().Should().Be(stop);
    }

    private static void AssertConditionAndGuard(
        JsonElement task,
        string outcome,
        string writeStatus,
        string writeEffect)
    {
        var steps = task.GetProperty("steps").EnumerateArray().ToArray();
        var condition = steps.Single(step =>
            step.GetProperty("kind").GetString() == "condition");
        var conditionFacts = condition.GetProperty("source")
            .GetProperty("condition").GetProperty("condition");
        conditionFacts.GetProperty("effectiveThreshold").GetInt32().Should().Be(75);
        conditionFacts.GetProperty("thresholdOrigin").GetString().Should()
            .Be("user_override");
        conditionFacts.GetProperty("outcome").GetString().Should().Be(outcome);

        var write = steps.Single(step =>
            step.GetProperty("source").TryGetProperty("tool", out var tool) &&
            tool.GetProperty("toolName").GetString() == "bitable_record_create");
        write.GetProperty("guard").GetProperty("conditionStepId").GetString()
            .Should().Be(condition.GetProperty("stepId").GetString());
        write.GetProperty("guard").GetProperty("requiredOutcome").GetString()
            .Should().Be("true");
        write.GetProperty("status").GetString().Should().Be(writeStatus);
        write.GetProperty("externalEffect").GetString().Should().Be(writeEffect);
    }

    private static void AssertPendingInputEquivalent(JsonElement live, JsonElement current)
    {
        foreach (var propertyName in new[]
                 {
                     "requestId", "turnId", "taskId", "stepId", "prompt",
                 })
        {
            live.GetProperty(propertyName).GetString().Should()
                .Be(current.GetProperty(propertyName).GetString(), propertyName);
        }

        live.GetProperty("allowFreeText").GetBoolean().Should()
            .Be(current.GetProperty("allowFreeText").GetBoolean());
        live.GetProperty("multiSelect").GetBoolean().Should()
            .Be(current.GetProperty("multiSelect").GetBoolean());
        ParseInstant(live.GetProperty("askedAt")).Should()
            .Be(ParseInstant(current.GetProperty("askedAt")));

        var liveOptions = live.GetProperty("options").EnumerateArray().ToArray();
        var currentOptions = current.GetProperty("options").EnumerateArray().ToArray();
        liveOptions.Should().HaveSameCount(currentOptions);
        for (var index = 0; index < liveOptions.Length; index++)
        {
            liveOptions[index].GetProperty("optionId").GetString().Should()
                .Be(currentOptions[index].GetProperty("optionId").GetString());
            liveOptions[index].GetProperty("label").GetString().Should()
                .Be(currentOptions[index].GetProperty("label").GetString());
            liveOptions[index].GetProperty("description").GetString().Should()
                .Be(currentOptions[index].GetProperty("description").GetString());
        }
    }

    private static DateTimeOffset ParseInstant(JsonElement value) =>
        DateTimeOffset.Parse(
            value.GetString()!,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal);

    private static JsonDocument ReadFixture(string fileName) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, fileName)));

    private static JsonElement FindScenario(JsonElement root, string scenario) =>
        root.EnumerateArray().Single(item =>
            item.GetProperty("scenario").GetString() == scenario);

    private static JsonElement FindJourney(
        IEnumerable<JsonElement> journeys,
        string variant) =>
        journeys.Single(item => item.GetProperty("variant").GetString() == variant);
}
