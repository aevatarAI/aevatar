using Aevatar.Mainnet.Host.Api.AgentProfiles;
using FluentAssertions;

namespace Aevatar.Capabilities.Tests;

public sealed class NyxIdChatAgentProfileOptionsTests
{
    private readonly NyxIdChatAgentProfileOptionsValidator _validator = new();

    [Fact]
    public void Disabled_without_release_spec_should_be_valid()
    {
        var result = _validator.Validate(null, new NyxIdChatAgentProfileOptions());

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Enabled_without_release_spec_should_fail_closed()
    {
        var result = _validator.Validate(null, new NyxIdChatAgentProfileOptions
        {
            Enabled = true,
        });

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().Contain("ReleaseSpecPath");
    }

    [Fact]
    public void Disabled_with_dormant_release_spec_should_fail_closed()
    {
        var result = _validator.Validate(null, new NyxIdChatAgentProfileOptions
        {
            ReleaseSpecPath = "release.json",
        });

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().Contain("cannot configure");
    }

    [Fact]
    public void Enabled_with_release_spec_path_should_be_valid()
    {
        var result = _validator.Validate(null, new NyxIdChatAgentProfileOptions
        {
            Enabled = true,
            ReleaseSpecPath = "Profiles/nyxid-chat/release.json",
        });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Options_should_express_only_rollout_enablement_and_release_spec_location()
    {
        typeof(NyxIdChatAgentProfileOptions)
            .GetProperties()
            .Select(static property => property.Name)
            .Should()
            .BeEquivalentTo(
                nameof(NyxIdChatAgentProfileOptions.Enabled),
                nameof(NyxIdChatAgentProfileOptions.ReleaseSpecPath));
    }

    [Fact]
    public void Production_schema_scanner_should_accept_pin_only_contract_roots()
    {
        AgentProfileProductionSchemaScanner.FindForbiddenNames().Should().BeEmpty();
    }

    [Theory]
    [InlineData("api_token", true)]
    [InlineData("SkillPayload", true)]
    [InlineData("PublishedRevision", false)]
    public void Production_schema_scanner_should_detect_forbidden_identifier_tokens(
        string identifier,
        bool expectedForbidden)
    {
        AgentProfileProductionSchemaScanner.IsForbiddenIdentifier(identifier)
            .Should()
            .Be(expectedForbidden);
    }
}
