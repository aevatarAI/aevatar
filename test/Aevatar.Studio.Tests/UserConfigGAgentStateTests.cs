using System.Reflection;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Studio.Tests;

public sealed class UserConfigGAgentStateTests
{
    [Fact]
    public void UpdateCommand_ShouldReserveLegacyDefaultModelMutation()
    {
        UpdateUserConfigCommand.Descriptor.FindFieldByName("default_model").Should().BeNull();
        UpdateUserConfigCommand.Descriptor.FindFieldByNumber(1).Should().BeNull();
    }

    [Fact]
    public void EventEndpoints_ShouldExposeOnlyConfigDelta()
    {
        var endpointNames = typeof(UserConfigGAgent)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.GetCustomAttribute<EventHandlerAttribute>())
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.EndpointName);

        endpointNames.Should().BeEquivalentTo(["updateConfigDelta"]);
    }

    [Fact]
    public void ResourceKeys_WithSimilarOpaqueValues_ShouldRemainDistinct()
    {
        var owner = UserConfigResourceKey.ForOwnerScope("binding-alpha");
        var binding = UserConfigResourceKey.ForChannelBinding("alpha");

        owner.Kind.Should().Be(UserConfigResourceKind.OwnerScope);
        owner.Value.Should().Be("binding-alpha");
        binding.Kind.Should().Be(UserConfigResourceKind.ChannelBinding);
        binding.Value.Should().Be("alpha");
        owner.Should().NotBe(binding);
    }

    [Fact]
    public void LLMSelection_NyxIdUserService_ShouldRoundTripThroughProtobuf()
    {
        var selection = UserServiceSelection("gpt-5.5");

        var roundTrip = LLMSelection.Parser.ParseFrom(selection.ToByteArray());
        roundTrip.Should().BeEquivalentTo(selection);
    }

    [Fact]
    public void UpdateUserConfigCommand_OptionalScalars_ShouldPreservePresenceThroughProtobuf()
    {
        var command = new UpdateUserConfigCommand
        {
            MaxToolRounds = 0,
            GithubUsername = string.Empty,
        };

        var roundTrip = UpdateUserConfigCommand.Parser.ParseFrom(command.ToByteArray());

        roundTrip.HasRuntimeMode.Should().BeFalse();
        roundTrip.HasLocalRuntimeBaseUrl.Should().BeFalse();
        roundTrip.HasRemoteRuntimeBaseUrl.Should().BeFalse();
        roundTrip.HasMaxToolRounds.Should().BeTrue();
        roundTrip.HasGithubUsername.Should().BeTrue();
        roundTrip.LlmSelection.Should().BeNull();
    }

    [Fact]
    public void BuildUpdatedEvent_WithExplicitSelection_ShouldDeriveBothCompatibilityFields()
    {
        var selection = UserServiceSelection("gpt-5.5");

        var committed = UserConfigGAgent.BuildUpdatedEvent(
            new UserConfigGAgentState(),
            new UpdateUserConfigCommand { LlmSelection = selection });

        committed.LlmSelection.Should().BeEquivalentTo(selection);
        committed.DefaultModel.Should().Be("gpt-5.5");
        committed.PreferredLlmRoute.Should().Be("/api/v1/proxy/s/chrono-llm-public");
    }

    [Fact]
    public void BuildUpdatedEvent_WithNonLlmDelta_ShouldPreserveLegacyFieldsByteForByte()
    {
        var state = new UserConfigGAgentState
        {
            DefaultModel = " legacy-model ",
            PreferredLlmRoute = " legacy-route ",
            RuntimeMode = "remote",
            LocalRuntimeBaseUrl = "http://127.0.0.1:5080",
            RemoteRuntimeBaseUrl = "https://runtime.example.com",
            MaxToolRounds = 12,
            GithubUsername = "octocat",
        };

        var committed = UserConfigGAgent.BuildUpdatedEvent(
            state,
            new UpdateUserConfigCommand { GithubUsername = "updated" });

        committed.DefaultModel.Should().Be(" legacy-model ");
        committed.PreferredLlmRoute.Should().Be(" legacy-route ");
        committed.RuntimeMode.Should().Be("remote");
        committed.LocalRuntimeBaseUrl.Should().Be("http://127.0.0.1:5080");
        committed.RemoteRuntimeBaseUrl.Should().Be("https://runtime.example.com");
        committed.MaxToolRounds.Should().Be(12);
        committed.GithubUsername.Should().Be("updated");
        committed.LlmSelection.Should().BeNull();
    }

    [Fact]
    public void BuildUpdatedEvent_WithReset_ShouldCommitCompleteUnspecifiedSelection()
    {
        var reset = LLMSelectionPolicy.SystemDefaultSelection();

        var committed = UserConfigGAgent.BuildUpdatedEvent(
            new UserConfigGAgentState
            {
                DefaultModel = "legacy-model",
                PreferredLlmRoute = "legacy-route",
            },
            new UpdateUserConfigCommand { LlmSelection = reset });

        committed.LlmSelection.Should().BeEquivalentTo(reset);
        committed.LlmSelection.ModelSelection.Should().NotBeNull();
        committed.LlmSelection.ModelSelection.Kind.Should().Be(LLMModelSelectionKind.Unspecified);
        committed.DefaultModel.Should().BeEmpty();
        committed.PreferredLlmRoute.Should().BeEmpty();
    }

    [Fact]
    public void HistoricalFourFieldSelection_ShouldRemainIncompleteAfterRoundTrip()
    {
        var historical = new LLMSelection
        {
            RouteKind = LLMRouteKind.NyxIdUserService,
            RouteValue = "/api/v1/proxy/s/chrono-llm-public",
            NyxIdUserServiceId = "us-alpha",
            ServiceSlugSnapshot = "chrono-llm-public",
        };

        var copy = LLMSelection.Parser.ParseFrom(historical.ToByteArray());

        copy.ModelSelection.Should().BeNull();
        FluentActions.Invoking(() => LLMSelectionPolicy.ValidateSelection(copy))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void BuildUpdatedEvent_WithPartialSelection_ShouldReject()
    {
        var act = () => UserConfigGAgent.BuildUpdatedEvent(
            new UserConfigGAgentState(),
            new UpdateUserConfigCommand
            {
                LlmSelection = new LLMSelection
                {
                    RouteKind = LLMRouteKind.Gateway,
                    RouteValue = LLMSelectionPolicy.GatewayRoute,
                },
            });

        act.Should().Throw<InvalidOperationException>();
    }

    private static LLMSelection UserServiceSelection(string modelId) => new()
    {
        RouteKind = LLMRouteKind.NyxIdUserService,
        RouteValue = "/api/v1/proxy/s/chrono-llm-public",
        NyxIdUserServiceId = "us-alpha",
        ServiceSlugSnapshot = "chrono-llm-public",
        ModelSelection = new LLMModelSelection
        {
            Kind = LLMModelSelectionKind.ExplicitModel,
            ModelId = modelId,
        },
    };
}
