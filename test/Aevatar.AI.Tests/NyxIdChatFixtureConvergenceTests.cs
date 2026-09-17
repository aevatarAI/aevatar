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
    public void Uc2ResearchJourneyFixture_ShouldConvergeAcrossScopeSteerStopReloadAndRestart()
    {
        using var fixture = ReadFixture("uc2-research-journey.json");
        var root = fixture.RootElement;
        root.GetProperty("specRevision").GetString().Should()
            .Be("f45febb057a7182dab2495d4c739d2bb8d7026f5");

        var initial = root.GetProperty("initialInput").GetProperty("snapshot");
        var initialTask = initial.GetProperty("activeTask");
        var pending = initial.GetProperty("pendingInput");
        initialTask.GetProperty("turnId").GetString().Should().Be("turn-uc2-1");
        initialTask.GetProperty("taskId").GetString().Should().Be("task-uc2");
        initialTask.GetProperty("planRevision").GetInt32().Should().Be(1);
        initialTask.GetProperty("steps").EnumerateArray().Should().ContainSingle(step =>
            step.GetProperty("kind").GetString() == "input");
        pending.GetProperty("options").GetArrayLength().Should().Be(0);
        pending.GetProperty("allowFreeText").GetBoolean().Should().BeTrue();
        pending.GetProperty("prompt").GetString().Should().Contain("party size")
            .And.Contain("dietary restrictions")
            .And.Contain("budget cap")
            .And.Contain("research-only shortlist");

        var research = root.GetProperty("researchRunning").GetProperty("snapshot")
            .GetProperty("activeTask");
        research.GetProperty("planRevision").GetInt32().Should().Be(2);
        research.GetProperty("gate").GetProperty("mode").GetString().Should().Be("auto");
        var runningSearch = FindStep(research, "step-uc2-search");
        runningSearch.GetProperty("source").GetProperty("tool").GetProperty("toolName")
            .GetString().Should().Be("web_search");
        runningSearch.GetProperty("externalEffect").GetString().Should().Be("not_started");
        var runningOperation = runningSearch.GetProperty("operation");
        runningOperation.GetProperty("phase").GetString().Should().Be("running");
        runningOperation.GetProperty("key").GetProperty("operationId").GetString()
            .Should().Be("operation-uc2-search");
        runningOperation.GetProperty("idempotent").GetBoolean().Should().BeTrue();
        runningSearch.GetProperty("substeps").EnumerateArray()
            .Select(substep => (
                substep.GetProperty("substepId").GetString(),
                substep.GetProperty("title").GetString(),
                substep.GetProperty("status").GetString()))
            .Should().Equal(
                ("prepare-operation", "Build search query", "done"),
                ("execute-operation", "Search current web results", "running"));

        var steeredSnapshot = root.GetProperty("steered").GetProperty("snapshot");
        var steered = steeredSnapshot.GetProperty("activeTask");
        steeredSnapshot.GetProperty("activeTurn").GetProperty("turnId").GetString()
            .Should().Be("turn-uc2-2");
        steered.GetProperty("taskId").GetString().Should().Be("task-uc2");
        steered.GetProperty("planRevision").GetInt32().Should().Be(3);
        var completedSearch = FindStep(steered, "step-uc2-search");
        completedSearch.GetProperty("status").GetString().Should().Be("done");
        completedSearch.GetProperty("externalEffect").GetString().Should().Be("not_applied");
        completedSearch.GetProperty("operation").GetProperty("phase").GetString()
            .Should().Be("succeeded");
        completedSearch.GetProperty("substeps").EnumerateArray().Should().OnlyContain(substep =>
            substep.GetProperty("status").GetString() == "done");
        FindStep(steered, "step-uc2-compare").GetProperty("status").GetString()
            .Should().Be("cancelled");
        var replacement = FindStep(steered, "step-uc2-refine");
        replacement.GetProperty("status").GetString().Should().Be("running");
        replacement.GetProperty("addedBy").GetString().Should().Be("steering");
        replacement.GetProperty("operation").GetProperty("phase").GetString()
            .Should().Be("running");

        var stoppedSnapshot = root.GetProperty("stopped").GetProperty("snapshot");
        var stopped = stoppedSnapshot.GetProperty("activeTask");
        stopped.GetProperty("status").GetString().Should().Be("stopped");
        var fence = stoppedSnapshot.GetProperty("controlFence");
        fence.GetProperty("requestId").GetString().Should().Be("stop-uc2-1");
        fence.GetProperty("outcome").GetString().Should().Be("uncancellable");
        fence.GetProperty("safeMessage").GetString().Should().Contain("Partial-work receipt")
            .And.Contain("Retained: Answer logistics and agree to research-only scope")
            .And.Contain("Fenced: Refine for 7 pm and a private room")
            .And.Contain("No external effect was applied")
            .And.Contain("Late evidence cannot advance this stopped task");
        var stoppedSearch = FindStep(stopped, "step-uc2-search");
        JsonNode.DeepEquals(
                JsonNode.Parse(completedSearch.GetProperty("source").GetRawText()),
                JsonNode.Parse(stoppedSearch.GetProperty("source").GetRawText()))
            .Should().BeTrue("reload must preserve the exact Aevatar search executor identity");
        JsonNode.DeepEquals(
                JsonNode.Parse(completedSearch.GetProperty("substeps").GetRawText()),
                JsonNode.Parse(stoppedSearch.GetProperty("substeps").GetRawText()))
            .Should().BeTrue("reload must preserve completed search progress evidence");
        JsonNode.DeepEquals(
                JsonNode.Parse(completedSearch.GetProperty("operation").GetRawText()),
                JsonNode.Parse(stoppedSearch.GetProperty("operation").GetRawText()))
            .Should().BeTrue("reload must preserve the durable search operation reservation");
        stopped.GetProperty("steps").EnumerateArray().Should().OnlyContain(step =>
            step.GetProperty("externalEffect").GetString() == "not_started" ||
            step.GetProperty("externalEffect").GetString() == "not_applied");

        var restart = root.GetProperty("restart");
        var restartedSnapshot = restart.GetProperty("snapshot");
        var restarted = restartedSnapshot.GetProperty("activeTask");
        restartedSnapshot.GetProperty("activeTurn").GetProperty("turnId").GetString()
            .Should().Be("turn-uc2b-1");
        restarted.GetProperty("taskId").GetString().Should().Be("task-uc2b")
            .And.NotBe(stopped.GetProperty("taskId").GetString());
        restarted.GetProperty("steps").EnumerateArray()
            .Select(step => step.GetProperty("stepId").GetString())
            .Intersect(stopped.GetProperty("steps").EnumerateArray()
                .Select(step => step.GetProperty("stepId").GetString()))
            .Should().BeEmpty();
        var restartedSearch = FindStep(restarted, "step-uc2b-search");
        restartedSearch.GetProperty("operation").GetProperty("key").GetProperty("taskId")
            .GetString().Should().Be("task-uc2b");
        restartedSearch.GetProperty("substeps").GetArrayLength().Should().Be(2);
        var message = restart.GetProperty("assistantMessage");
        message.GetProperty("turnId").GetString().Should().Be("turn-uc2b-1");
        message.GetProperty("taskId").GetString().Should().Be("task-uc2b");
        message.GetProperty("content").GetString().Should().Contain("Verified facts:")
            .And.Contain("Cannot check right now:")
            .And.EndWith("Research artifact only: no reservation was made.");

        root.GetRawText().Should().NotContainAny("credential", "accessToken", "bearer");
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
    private static JsonElement FindStep(JsonElement task, string stepId) =>
        task.GetProperty("steps").EnumerateArray().Single(step =>
            step.GetProperty("stepId").GetString() == stepId);
}
