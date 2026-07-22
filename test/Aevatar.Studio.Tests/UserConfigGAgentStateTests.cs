using Aevatar.GAgents.UserConfig;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Google.Protobuf;

namespace Aevatar.Studio.Tests;

public sealed class UserConfigGAgentStateTests
{
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
    public void UserLlmSelection_NyxIdUserService_ShouldRoundTripThroughProtobuf()
    {
        var selection = new Aevatar.GAgents.UserConfig.UserLlmSelection
        {
            RouteKind = UserLlmRouteKind.NyxIdUserService,
            RouteValue = "/api/v1/proxy/s/chrono-llm-public",
            NyxIdUserServiceId = "us-alpha",
            ServiceSlugSnapshot = "chrono-llm-public",
        };

        var roundTrip = Aevatar.GAgents.UserConfig.UserLlmSelection.Parser.ParseFrom(selection.ToByteArray());
        roundTrip.Should().BeEquivalentTo(selection);
    }

    [Fact]
    public void UpdateUserConfigCommand_OptionalScalars_ShouldPreservePresenceThroughProtobuf()
    {
        var command = new UpdateUserConfigCommand
        {
            DefaultModel = string.Empty,
            MaxToolRounds = 0,
            GithubUsername = string.Empty,
        };

        var roundTrip = UpdateUserConfigCommand.Parser.ParseFrom(command.ToByteArray());

        roundTrip.HasDefaultModel.Should().BeTrue();
        roundTrip.HasRuntimeMode.Should().BeFalse();
        roundTrip.HasLocalRuntimeBaseUrl.Should().BeFalse();
        roundTrip.HasRemoteRuntimeBaseUrl.Should().BeFalse();
        roundTrip.HasMaxToolRounds.Should().BeTrue();
        roundTrip.HasGithubUsername.Should().BeTrue();
        roundTrip.LlmSelection.Should().BeNull();
    }

    [Fact]
    public void BuildUpdatedEvent_ShouldPreserveOmittedFieldsAndReturnFullStateEvent()
    {
        var state = new UserConfigGAgentState
        {
            DefaultModel = "gpt-5.4",
            RuntimeMode = "remote",
            LocalRuntimeBaseUrl = "http://127.0.0.1:5080",
            RemoteRuntimeBaseUrl = "https://runtime.example.com",
            MaxToolRounds = 12,
            GithubUsername = "octocat",
            LlmSelection = GatewaySelection(),
            PreferredLlmRoute = UserConfigLlmRouteDefaults.Gateway,
        };

        var committed = UserConfigGAgent.BuildUpdatedEvent(state, new UpdateUserConfigCommand
        {
            DefaultModel = "gpt-5.5",
        });

        committed.DefaultModel.Should().Be("gpt-5.5");
        committed.RuntimeMode.Should().Be("remote");
        committed.LocalRuntimeBaseUrl.Should().Be("http://127.0.0.1:5080");
        committed.RemoteRuntimeBaseUrl.Should().Be("https://runtime.example.com");
        committed.MaxToolRounds.Should().Be(12);
        committed.GithubUsername.Should().Be("octocat");
        committed.LlmSelection.Should().BeEquivalentTo(GatewaySelection());
        committed.PreferredLlmRoute.Should().Be(UserConfigLlmRouteDefaults.Gateway);
    }

    [Theory]
    [MemberData(nameof(InvalidSelections))]
    public void BuildUpdatedEvent_WithInvalidSelection_ShouldReject(
        string _,
        Aevatar.GAgents.UserConfig.UserLlmSelection selection,
        string expectedError)
    {
        var act = () => UserConfigGAgent.BuildUpdatedEvent(
            new UserConfigGAgentState(),
            new UpdateUserConfigCommand { LlmSelection = selection });

        act.Should().Throw<InvalidOperationException>().WithMessage(expectedError);
    }

    public static TheoryData<string, Aevatar.GAgents.UserConfig.UserLlmSelection, string> InvalidSelections =>
        new()
        {
            {
                "unspecified kind",
                new Aevatar.GAgents.UserConfig.UserLlmSelection
                {
                    RouteValue = UserConfigLlmRouteDefaults.Gateway,
                },
                "user_llm_selection_invalid"
            },
            {
                "noncanonical Gateway route",
                new Aevatar.GAgents.UserConfig.UserLlmSelection
                {
                    RouteKind = UserLlmRouteKind.Gateway,
                    RouteValue = "/api/v1/llm/gateway/v2",
                },
                "user_llm_selection_invalid"
            },
            {
                "Gateway with ID",
                new Aevatar.GAgents.UserConfig.UserLlmSelection
                {
                    RouteKind = UserLlmRouteKind.Gateway,
                    RouteValue = UserConfigLlmRouteDefaults.Gateway,
                    NyxIdUserServiceId = "us-alpha",
                },
                "user_llm_selection_invalid"
            },
            {
                "service selection missing route",
                new Aevatar.GAgents.UserConfig.UserLlmSelection
                {
                    RouteKind = UserLlmRouteKind.NyxIdUserService,
                    NyxIdUserServiceId = "us-alpha",
                    ServiceSlugSnapshot = "chrono-llm-public",
                },
                "user_llm_selection_invalid"
            },
            {
                "service selection missing ID",
                new Aevatar.GAgents.UserConfig.UserLlmSelection
                {
                    RouteKind = UserLlmRouteKind.NyxIdUserService,
                    RouteValue = "/api/v1/proxy/s/chrono-llm-public",
                    ServiceSlugSnapshot = "chrono-llm-public",
                },
                "user_llm_selection_invalid"
            },
            {
                "service selection missing slug",
                new Aevatar.GAgents.UserConfig.UserLlmSelection
                {
                    RouteKind = UserLlmRouteKind.NyxIdUserService,
                    RouteValue = "/api/v1/proxy/s/chrono-llm-public",
                    NyxIdUserServiceId = "us-alpha",
                },
                "user_llm_selection_invalid"
            },
            {
                "route and slug mismatch",
                new Aevatar.GAgents.UserConfig.UserLlmSelection
                {
                    RouteKind = UserLlmRouteKind.NyxIdUserService,
                    RouteValue = "/api/v1/proxy/s/other-llm-public",
                    NyxIdUserServiceId = "us-alpha",
                    ServiceSlugSnapshot = "chrono-llm-public",
                },
                "user_llm_selection_invalid"
            },
            {
                "whitespace around route ID and slug",
                new Aevatar.GAgents.UserConfig.UserLlmSelection
                {
                    RouteKind = UserLlmRouteKind.NyxIdUserService,
                    RouteValue = " /api/v1/proxy/s/chrono-llm-public ",
                    NyxIdUserServiceId = " us-alpha ",
                    ServiceSlugSnapshot = " chrono-llm-public ",
                },
                "user_llm_selection_not_canonical"
            },
        };

    private static Aevatar.GAgents.UserConfig.UserLlmSelection GatewaySelection() => new()
    {
        RouteKind = UserLlmRouteKind.Gateway,
        RouteValue = UserConfigLlmRouteDefaults.Gateway,
    };
}
