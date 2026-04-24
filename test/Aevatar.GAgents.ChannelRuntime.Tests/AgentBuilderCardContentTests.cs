using System.Linq;
using Aevatar.GAgents.Channel.Abstractions;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class AgentBuilderCardContentTests
{
    [Fact]
    public void BuildDailyReportForm_EmitsTextInputsAndSubmitButton()
    {
        var content = AgentBuilderCardContent.BuildDailyReportForm(preferredGithubUsername: null);

        content.Actions.Should().HaveCount(5);
        content.Actions.Where(a => a.Kind == ActionElementKind.TextInput)
            .Select(a => a.ActionId)
            .Should().BeEquivalentTo(new[]
            {
                "github_username",
                "repositories",
                "schedule_time",
                "schedule_timezone",
            });

        var submit = content.Actions.Single(a => a.Kind == ActionElementKind.FormSubmit);
        submit.ActionId.Should().Be("submit_daily_report");
        submit.IsPrimary.Should().BeTrue();
        submit.Arguments["agent_builder_action"].Should().Be("create_daily_report");
        submit.Arguments["run_immediately"].Should().Be("true");

        content.Cards.Should().HaveCount(1);
        content.Cards[0].Title.Should().Be("Create Daily Report Agent");
    }

    [Fact]
    public void BuildDailyReportForm_PrefillsGithubUsernamePlaceholder_WhenProvided()
    {
        var content = AgentBuilderCardContent.BuildDailyReportForm("eanzhao");

        var githubField = content.Actions.Single(a =>
            a.Kind == ActionElementKind.TextInput && a.ActionId == "github_username");
        githubField.Placeholder.Should().Be("eanzhao");

        content.Cards.Single().Text.Should().Contain("Saved GitHub username: `eanzhao`");
    }

    [Fact]
    public void BuildSocialMediaForm_EmitsFormInputsAndSubmitButton()
    {
        var content = AgentBuilderCardContent.BuildSocialMediaForm();

        content.Actions.Where(a => a.Kind == ActionElementKind.TextInput)
            .Select(a => a.ActionId)
            .Should().BeEquivalentTo(new[]
            {
                "topic",
                "audience",
                "style",
                "schedule_time",
                "schedule_timezone",
            });

        var submit = content.Actions.Single(a => a.Kind == ActionElementKind.FormSubmit);
        submit.Arguments["agent_builder_action"].Should().Be("create_social_media");
    }
}
