using System.Linq;
using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using FluentAssertions;
using Xunit;
using Aevatar.GAgents.Authoring.Lark;

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
    public void BuildDailyReportForm_PrefillsSavedGithubUsernameIntoValue_WhenProvided()
    {
        var content = AgentBuilderCardContent.BuildDailyReportForm("eanzhao");

        var githubField = content.Actions.Single(a =>
            a.Kind == ActionElementKind.TextInput && a.ActionId == "github_username");
        // Saved usernames must live in Value so LarkMessageComposer emits default_value and the
        // user sees the name as real input text they can edit, not as ghost placeholder that
        // disappears on click.
        githubField.Value.Should().Be("eanzhao");
        githubField.Placeholder.Should().Be("octocat");

        content.Cards.Single().Text.Should().Contain("Saved GitHub username: `eanzhao`");
        content.Cards.Single().Text.Should().Contain("already filled in");
    }

    [Fact]
    public void BuildDailyReportForm_LeavesValueEmpty_WhenNoSavedUsername()
    {
        var content = AgentBuilderCardContent.BuildDailyReportForm(preferredGithubUsername: null);

        var githubField = content.Actions.Single(a =>
            a.Kind == ActionElementKind.TextInput && a.ActionId == "github_username");
        githubField.Value.Should().BeEmpty();
        githubField.Placeholder.Should().Be("octocat");
    }

    [Fact]
    public void FormatDailyReportToolReply_OauthRequired_DoesNotDuplicateAuthBlockInTextAndCard()
    {
        // Regression for the duplicate "GitHub authorization required" block users were seeing
        // in Lark: BuildDailyReportCredentialsCard used to set both content.Text (intended as a
        // non-card fallback) and content.Cards[0].Text with the same auth block, which Lark's
        // form-mode composer concatenated into a single rendered message. The card body is the
        // single source of truth — content.Text must stay empty so the composer renders the
        // block exactly once.
        var toolJson = JsonSerializer.Serialize(new
        {
            status = "oauth_required",
            template = "daily_report",
            provider = "GitHub",
            provider_id = "provider-github-uuid",
            authorization_url = "https://github.com/login/oauth/authorize?client_id=abc",
            note = "Connect GitHub in NyxID, then return to Feishu and submit the daily report form again.",
        });
        using var doc = JsonDocument.Parse(toolJson);

        var content = AgentBuilderCardContent.FormatDailyReportToolReply(doc.RootElement);

        content.Text.Should().BeEmpty();
        content.Cards.Should().HaveCount(1);
        content.Cards[0].Text.Should().Contain("GitHub authorization required");
        content.Cards[0].Text.Should().Contain("provider-github-uuid");
        content.Cards[0].Text.Should().Contain("https://github.com/login/oauth/authorize?client_id=abc");
    }

    [Fact]
    public void FormatDailyReportToolReply_OauthRequired_PrefillsSubmittedGithubUsernameInForm()
    {
        // When the user typed `/daily eanzhao` and the tool returns oauth_required, the
        // re-prompt form must pre-fill `eanzhao` into the GitHub Username field — otherwise
        // users have to retype after the OAuth round-trip (which is what triggered the
        // "fix/2026-04-29_daily-card-auth-prompt" complaint).
        var toolJson = JsonSerializer.Serialize(new
        {
            status = "oauth_required",
            template = "daily_report",
            provider = "GitHub",
            provider_id = "provider-github-uuid",
            authorization_url = "https://github.com/login/oauth/authorize?client_id=abc",
            github_username = "eanzhao",
            note = "Connect GitHub in NyxID, then return to Feishu and submit the daily report form again.",
        });
        using var doc = JsonDocument.Parse(toolJson);

        var content = AgentBuilderCardContent.FormatDailyReportToolReply(doc.RootElement);

        var githubField = content.Actions.Single(a =>
            a.Kind == ActionElementKind.TextInput && a.ActionId == "github_username");
        githubField.Value.Should().Be("eanzhao");
    }

    [Fact]
    public void FormatDailyReportToolReply_CredentialsRequired_RendersCredentialsHeading()
    {
        // The credentials_required branch lacks an authorization_url and uses a "credentials"
        // heading instead of "authorization". Same single-render contract as oauth_required.
        var toolJson = JsonSerializer.Serialize(new
        {
            status = "credentials_required",
            template = "daily_report",
            provider = "GitHub",
            provider_id = "provider-github-uuid",
            documentation_url = "https://nyxid.example.com/docs/github",
            note = "GitHub in NyxID uses user-managed OAuth app credentials.",
        });
        using var doc = JsonDocument.Parse(toolJson);

        var content = AgentBuilderCardContent.FormatDailyReportToolReply(doc.RootElement);

        content.Text.Should().BeEmpty();
        content.Cards.Should().HaveCount(1);
        content.Cards[0].Text.Should().Contain("GitHub credentials required");
        content.Cards[0].Text.Should().NotContain("GitHub authorization required");
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
