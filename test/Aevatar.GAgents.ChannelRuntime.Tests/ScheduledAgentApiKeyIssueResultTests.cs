using System.Text.Json;
using Aevatar.GAgents.Scheduled;
using FluentAssertions;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ScheduledAgentApiKeyIssueResultTests
{
    [Fact]
    public void Succeeded_ShouldNotExposeFullKeyThroughJsonOrToString()
    {
        var result = ScheduledAgentApiKeyIssueResult.Succeeded(
            "key-id",
            "full-secret-key",
            DateTimeOffset.Parse("2026-07-18T00:00:00+00:00").ToUnixTimeMilliseconds());

        JsonSerializer.Serialize(result).Should().NotContain("full-secret-key");
        result.ToString().Should().NotContain("full-secret-key");
        typeof(ScheduledAgentApiKeyIssueResult).GetProperties()
            .Should().NotContain(property => property.Name == "Secret");
    }
}
