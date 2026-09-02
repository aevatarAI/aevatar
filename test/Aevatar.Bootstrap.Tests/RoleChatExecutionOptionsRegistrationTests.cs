using Aevatar.AI.Core;
using Aevatar.Bootstrap.Extensions.AI;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Bootstrap.Tests;

public sealed class RoleChatExecutionOptionsRegistrationTests
{
    [Fact]
    public void AddAevatarAIFeatures_ShouldRegisterConfiguredHostTurnDeadline()
    {
        var services = new ServiceCollection();
        var config = BuildConfiguration(
            maxTurnDeadlineMs: "45000",
            postCommitConfigRefreshTimeoutMs: "6000",
            postTurnProcessingTimeoutMs: "7000");

        services.AddAevatarAIFeatures(config, options => options.EnableMEAIProviders = false);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<RoleChatExecutionOptions>();
        options.MaxTurnDeadlineMs.Should().Be(45_000);
        options.PostCommitConfigRefreshTimeoutMs.Should().Be(6_000);
        options.PostTurnProcessingTimeoutMs.Should().Be(7_000);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void AddAevatarAIFeatures_WhenHostTurnDeadlineIsNotPositive_ShouldFailComposition(
        string configuredValue)
    {
        var services = new ServiceCollection();
        var config = BuildConfiguration(configuredValue);

        var act = () => services.AddAevatarAIFeatures(
            config,
            options => options.EnableMEAIProviders = false);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AddAevatarAIFeatures_WhenHostTurnDeadlineIsNotAnInteger_ShouldFailComposition()
    {
        var services = new ServiceCollection();
        var config = BuildConfiguration("not-an-integer");

        var act = () => services.AddAevatarAIFeatures(
            config,
            options => options.EnableMEAIProviders = false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MaxTurnDeadlineMs*positive integer*");
    }

    [Theory]
    [InlineData("Aevatar:AI:PostCommitConfigRefreshTimeoutMs", "0")]
    [InlineData("Aevatar:AI:PostCommitConfigRefreshTimeoutMs", "-1")]
    [InlineData("Aevatar:AI:PostTurnProcessingTimeoutMs", "0")]
    [InlineData("Aevatar:AI:PostTurnProcessingTimeoutMs", "-1")]
    public void AddAevatarAIFeatures_WhenPostCommitTimeoutIsNotPositive_ShouldFailComposition(
        string key,
        string configuredValue)
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = configuredValue })
            .Build();

        var act = () => services.AddAevatarAIFeatures(
            config,
            options => options.EnableMEAIProviders = false);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("Aevatar:AI:PostCommitConfigRefreshTimeoutMs")]
    [InlineData("Aevatar:AI:PostTurnProcessingTimeoutMs")]
    public void AddAevatarAIFeatures_WhenPostCommitTimeoutIsNotAnInteger_ShouldFailComposition(
        string key)
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = "not-an-integer" })
            .Build();

        var act = () => services.AddAevatarAIFeatures(
            config,
            options => options.EnableMEAIProviders = false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{key}*positive integer*");
    }

    private static IConfiguration BuildConfiguration(
        string maxTurnDeadlineMs,
        string? postCommitConfigRefreshTimeoutMs = null,
        string? postTurnProcessingTimeoutMs = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:AI:MaxTurnDeadlineMs"] = maxTurnDeadlineMs,
                ["Aevatar:AI:PostCommitConfigRefreshTimeoutMs"] = postCommitConfigRefreshTimeoutMs,
                ["Aevatar:AI:PostTurnProcessingTimeoutMs"] = postTurnProcessingTimeoutMs,
            })
            .Build();
}
