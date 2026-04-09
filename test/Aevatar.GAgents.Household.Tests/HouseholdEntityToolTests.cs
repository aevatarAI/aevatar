using System.Text.Json;
using Aevatar.AI.Abstractions.ToolProviders;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.Household.Tests;

public class HouseholdEntityToolMetadataTests
{
    [Fact]
    public void Name_returns_household()
    {
        var tool = CreateTool();
        tool.Name.Should().Be("household");
    }

    [Fact]
    public void Description_mentions_home_automation()
    {
        var tool = CreateTool();
        tool.Description.Should().Contain("home automation");
    }

    [Fact]
    public void ParametersSchema_is_valid_json()
    {
        var tool = CreateTool();
        var action = () => JsonDocument.Parse(tool.ParametersSchema);
        action.Should().NotThrow();
    }

    [Fact]
    public void ParametersSchema_requires_message()
    {
        var tool = CreateTool();
        using var doc = JsonDocument.Parse(tool.ParametersSchema);
        var required = doc.RootElement.GetProperty("required");
        required.EnumerateArray().Should().Contain(e => e.GetString() == "message");
    }

    [Fact]
    public void ParametersSchema_has_household_id_optional()
    {
        var tool = CreateTool();
        using var doc = JsonDocument.Parse(tool.ParametersSchema);
        var props = doc.RootElement.GetProperty("properties");
        props.TryGetProperty("household_id", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_returns_error_when_message_missing()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("""{"household_id":"test"}""");
        result.Should().Contain("error");
        result.Should().Contain("message");
    }

    [Fact]
    public async Task ExecuteAsync_returns_error_for_invalid_json()
    {
        var tool = CreateTool();
        var result = await tool.ExecuteAsync("not json");
        result.Should().Contain("error");
    }

    // Helper — creates tool with null runtime (will fail on dispatch but metadata tests pass)
    private static HouseholdEntityTool CreateTool() =>
        new(null!, new HouseholdEntityToolOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
}

public class HouseholdEntityToolSourceTests
{
    [Fact]
    public async Task DiscoverToolsAsync_returns_household_tool()
    {
        var source = new HouseholdEntityToolSource(
            null!, // runtime not needed for discovery
            new HouseholdEntityToolOptions());

        var tools = await source.DiscoverToolsAsync();

        tools.Should().HaveCount(1);
        tools[0].Should().BeOfType<HouseholdEntityTool>();
        tools[0].Name.Should().Be("household");
    }
}

public class HouseholdEntityToolOptionsTests
{
    [Fact]
    public void Default_prefix_is_household()
    {
        var options = new HouseholdEntityToolOptions();
        options.ActorIdPrefix.Should().Be("household");
    }

    [Fact]
    public void Prefix_can_be_customized()
    {
        var options = new HouseholdEntityToolOptions { ActorIdPrefix = "home" };
        options.ActorIdPrefix.Should().Be("home");
    }
}
