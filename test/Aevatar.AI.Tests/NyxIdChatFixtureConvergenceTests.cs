using System.Text.Json;
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
}
