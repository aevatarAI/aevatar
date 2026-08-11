using System.Text.Json;
using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions.Prompting;
using Aevatar.AI.Core.Prompting;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatUc1JourneyFixtureTests
{
    private const string GitHubUserServiceId = "usvc-github-alpha";
    private const string LarkUserServiceId = "usvc-lark-alpha";
    private const string LarkMessageId = "lark-message-alpha";
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "NyxIdChat",
        "v1",
        "uc1-github-lark-journeys.json");

    [Fact]
    public void Fixture_ShouldScopeOnceAndConnectOnlyTheDisconnectedVariant()
    {
        using var fixture = ReadFixture();
        var root = fixture.RootElement;
        root.GetProperty("specRevision").GetString().Should()
            .Be("f45febb057a7182dab2495d4c739d2bb8d7026f5");

        var disconnected = FindJourney(root, "uc1a-disconnected");
        var ready = FindJourney(root, "uc1b-ready");
        foreach (var journey in new[] { disconnected, ready })
        {
            var scope = journey.GetProperty("scope");
            scope.GetProperty("options").GetArrayLength().Should().Be(0);
            scope.GetProperty("allowFreeText").GetBoolean().Should().BeTrue();
            scope.GetProperty("resolved").GetBoolean().Should().BeTrue();
            scope.GetProperty("prompt").GetString().Should().Contain("repo-alpha")
                .And.Contain("repo-beta")
                .And.Contain("merged-PR window")
                .And.Contain("#eng-updates")
                .And.Contain("audience")
                .And.Contain("tone");
        }

        var actions = disconnected.GetProperty("connectActions").EnumerateArray().ToArray();
        actions.Should().HaveCount(2);
        actions.Select(action => action.GetProperty("serviceSlug").GetString()).Should()
            .Equal("api-github", "api-lark");
        actions.Select(action => action.GetProperty("resource").GetProperty("userService")
                .GetProperty("userServiceId").GetString()).Should()
            .Equal(GitHubUserServiceId, LarkUserServiceId);
        actions.Should().OnlyContain(action =>
            action.GetProperty("effect").GetString() == "confirmed" &&
            action.GetProperty("postcondition").GetString() == "service.connected");
        actions.Max(action => action.GetProperty("order").GetInt32()).Should().BeLessThan(
            FindProviderReadSteps(disconnected).Min(step => step.GetProperty("order").GetInt32()));
        disconnected.GetProperty("turnIds").GetArrayLength().Should().Be(4);
        actions.Select(action => action.GetProperty("continuationTurnId").GetString())
            .Should().OnlyHaveUniqueItems();
        disconnected.GetProperty("taskId").GetString().Should().Be(
            disconnected.GetProperty("plan").GetProperty("taskId").GetString());

        ready.GetProperty("connectActions").GetArrayLength().Should().Be(0);
        ready.GetProperty("services").EnumerateArray().Should().OnlyContain(service =>
            service.GetProperty("initialStatus").GetString() == "ready" &&
            service.GetProperty("finalStatus").GetString() == "ready");
    }

    [Fact]
    public void Fixture_ShouldUseExactIdentityBoundExecutorsForTwoReadsAndOnePublish()
    {
        using var fixture = ReadFixture();
        foreach (var journey in fixture.RootElement.GetProperty("journeys").EnumerateArray())
        {
            var plan = journey.GetProperty("plan");
            plan.GetProperty("gate").GetProperty("mode").GetString().Should().Be("confirm");
            var steps = plan.GetProperty("steps").EnumerateArray().ToArray();
            steps.Should().OnlyContain(step =>
                !string.IsNullOrWhiteSpace(step.GetProperty("description").GetString()) &&
                step.GetProperty("description").GetString()!.Contains('—'));

            var reads = FindProviderReadSteps(journey);
            reads.Should().HaveCount(2);
            reads.Select(step => step.GetProperty("source").GetProperty("tool")
                    .GetProperty("providerResourceId").GetString()).Should()
                .Equal("repo-alpha", "repo-beta");
            foreach (var read in reads)
            {
                var tool = read.GetProperty("source").GetProperty("tool");
                tool.GetProperty("serviceSlug").GetString().Should().Be("api-github");
                tool.GetProperty("serviceId").GetString().Should().Be(GitHubUserServiceId);
                read.GetProperty("externalEffect").GetString().Should().Be("not_applied");
                read.GetProperty("mayChangeExternalState").GetBoolean().Should().BeFalse();
            }

            var draft = steps.Single(step =>
                step.GetProperty("source").TryGetProperty("llm", out _) &&
                step.GetProperty("description").GetString()!.Contains("draft", StringComparison.Ordinal));
            draft.GetProperty("dependsOn").EnumerateArray().Select(item => item.GetString())
                .Should().BeEquivalentTo(reads.Select(step => step.GetProperty("stepId").GetString()));

            var publish = FindPublishStep(journey);
            var publishTool = publish.GetProperty("source").GetProperty("tool");
            publishTool.GetProperty("serviceSlug").GetString().Should().Be("api-lark");
            publishTool.GetProperty("serviceId").GetString().Should().Be(LarkUserServiceId);
            publishTool.GetProperty("providerResourceId").GetString().Should().Be(LarkMessageId);
            publish.GetProperty("mayChangeExternalState").GetBoolean().Should().BeTrue();
            publish.GetProperty("externalEffect").GetString().Should().Be("confirmed");
            publish.GetProperty("approvalObservation").GetProperty("decisionMode").GetString()
                .Should().Be("per_request");
            publish.GetProperty("approvalObservation").GetProperty("receiptStatus").GetString()
                .Should().Be("success");
            publish.GetProperty("approvalObservation").GetProperty("subjectKind").GetString()
                .Should().Be("nyxid.user-service");
            publish.GetProperty("approvalObservation").TryGetProperty("terminalOutcome", out _)
                .Should().BeFalse("a successful receipt has no denial terminal outcome");

            GitHubUserServiceId.Should().NotBe(LarkUserServiceId)
                .And.NotBe("api-github")
                .And.NotBe("repo-alpha")
                .And.NotBe("repo-beta");
            LarkUserServiceId.Should().NotBe("api-lark").And.NotBe(LarkMessageId);
            journey.GetProperty("conversationActorId").GetString().Should()
                .NotBe(journey.GetProperty("taskId").GetString())
                .And.NotBe(journey.GetProperty("planId").GetString());
        }
    }

    [Fact]
    public void Fixture_ShouldVerifyExactMessageAndFenceDuplicateReadsAndWrites()
    {
        using var fixture = ReadFixture();
        foreach (var journey in fixture.RootElement.GetProperty("journeys").EnumerateArray())
        {
            var ledger = journey.GetProperty("operationLedger").EnumerateArray().ToArray();
            ledger.Should().HaveCount(4);
            ledger.Select(entry => entry.GetProperty("operationId").GetString()).Should()
                .OnlyHaveUniqueItems();
            ledger.Should().OnlyContain(entry =>
                entry.GetProperty("dispatchCount").GetInt32() == 1);
            ledger.Count(entry => entry.GetProperty("kind").GetString() == "provider_read")
                .Should().Be(2);
            ledger.Count(entry => entry.GetProperty("kind").GetString() == "provider_write")
                .Should().Be(1);
            ledger.Count(entry => entry.GetProperty("kind").GetString() == "provider_read_back")
                .Should().Be(1);

            var publish = FindPublishStep(journey);
            var verification = journey.GetProperty("plan").GetProperty("steps")
                .EnumerateArray().Single(step =>
                    step.GetProperty("kind").GetString() == "postcondition" &&
                    step.GetProperty("source").GetProperty("postcondition")
                        .GetProperty("check").GetString() ==
                            "lark_provider_message_visible_in_chat");
            var verificationSource = verification.GetProperty("source")
                .GetProperty("postcondition");
            verification.GetProperty("dependsOn").EnumerateArray().Select(item => item.GetString())
                .Should().Equal(publish.GetProperty("stepId").GetString());
            verificationSource.GetProperty("providerResourceId").GetString().Should()
                .Be(LarkMessageId);
            verificationSource.GetProperty("check").GetString().Should()
                .Be("lark_provider_message_visible_in_chat");
            verification.GetProperty("externalEffect").GetString().Should().Be("confirmed");

            var artifact = journey.GetProperty("artifact");
            artifact.GetProperty("providerResourceId").GetString().Should().Be(LarkMessageId);
            artifact.GetProperty("verificationOperationId").GetString().Should()
                .Be(verification.GetProperty("operation").GetProperty("operationId").GetString());
            artifact.GetProperty("externalEffect").GetString().Should().Be("confirmed");
        }
    }

    [Fact]
    public void Fixture_ShouldReloadIdenticallyAtEveryCommittedBoundary()
    {
        using var fixture = ReadFixture();
        foreach (var journey in fixture.RootElement.GetProperty("journeys").EnumerateArray())
        {
            var boundaries = journey.GetProperty("boundaries").EnumerateArray().ToArray();
            boundaries.Select(boundary => boundary.GetProperty("name").GetString()).Should()
                .Equal("blocked", "running", "terminal");
            foreach (var boundary in boundaries)
            {
                var live = boundary.GetProperty("live");
                var reload = boundary.GetProperty("reload");
                JsonNode.DeepEquals(
                        JsonNode.Parse(live.GetRawText()),
                        JsonNode.Parse(reload.GetRawText()))
                    .Should().BeTrue(boundary.GetProperty("name").GetString());
                live.GetProperty("stateVersion").GetInt64().Should()
                    .Be(live.GetProperty("progressSequence").GetInt64());
                live.GetProperty("taskId").GetString().Should()
                    .Be(journey.GetProperty("taskId").GetString());
            }
            boundaries[^1].GetProperty("live").GetProperty("taskStatus").GetString()
                .Should().Be("succeeded");
            boundaries[^1].GetProperty("live").GetProperty("externalEffect").GetString()
                .Should().Be("confirmed");
        }
    }

    [Fact]
    public void ComposedPrompt_ShouldPreserveOneExactCrossServiceTaskThroughVerification()
    {
        var floor = new BuiltInPromptFloorProvider().GetFloor();
        var prompt = SystemPromptLayerComposer.Compose(
            NyxIdChatSystemPrompt.Value,
            floor,
            global: null,
            profile: null,
            selectedSkill: null,
            runtimeFacts: null,
            conversation: null).Prompt;

        prompt.Should().Contain("Cross-service read, draft, and publish journeys");
        prompt.Should().Contain("single composite `ask_user` request");
        prompt.Should().Contain("before the first business read");
        prompt.Should().Contain("A continuation resumes the existing task");
        prompt.Should().Contain("Read every requested source resource separately");
        prompt.Should().Contain("Name the executor in every communicated plan step");
        prompt.Should().Contain("Publish exactly once");
        prompt.Should().Contain("server-sealed provider read-back finds the exact returned `provider_resource_id`");
        prompt.Should().Contain("must not duplicate provider reads or writes");
    }

    private static JsonDocument ReadFixture() =>
        JsonDocument.Parse(File.ReadAllText(FixturePath));

    private static JsonElement FindJourney(JsonElement root, string variant) =>
        root.GetProperty("journeys").EnumerateArray().Single(journey =>
            journey.GetProperty("variant").GetString() == variant);

    private static JsonElement[] FindProviderReadSteps(JsonElement journey) =>
        journey.GetProperty("plan").GetProperty("steps").EnumerateArray()
            .Where(step =>
                step.GetProperty("kind").GetString() == "tool" &&
                step.GetProperty("source").GetProperty("tool").GetProperty("serviceSlug")
                    .GetString() == "api-github")
            .ToArray();

    private static JsonElement FindPublishStep(JsonElement journey) =>
        journey.GetProperty("plan").GetProperty("steps").EnumerateArray().Single(step =>
            step.GetProperty("kind").GetString() == "tool" &&
            step.GetProperty("source").GetProperty("tool").GetProperty("serviceSlug")
                .GetString() == "api-lark");
}
