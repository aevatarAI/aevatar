using FluentAssertions;

namespace Aevatar.AI.Infrastructure.OpenSandbox.Tests;

public sealed class OpenSandboxCodexOptionsValidatorTests
{
    private readonly OpenSandboxCodexOptionsValidator _validator = new();

    [Fact]
    public void Validate_WhenDisabled_DoesNotRequireInfrastructureSecrets()
    {
        _validator.Validate(null, new OpenSandboxCodexOptions())
            .Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenEnabledWithPinnedConfiguration_Succeeds()
    {
        _validator.Validate(null, ValidOptions()).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenEnabledWithMutableImageOrNoAllowlist_FailsClosed()
    {
        var options = ValidOptions();
        options.RunnerImage = "ghcr.io/aevatarai/codex-runner:latest";
        options.AllowedNyxIdUserIds = [];

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(message => message.Contains("digest-pinned", StringComparison.Ordinal));
        result.Failures.Should().Contain(message => message.Contains("AllowedNyxIdUserIds", StringComparison.Ordinal));
    }

    internal static OpenSandboxCodexOptions ValidOptions() => new()
    {
        Enabled = true,
        Domain = "opensandbox.example.internal",
        ApiKey = "open-sandbox-secret",
        Protocol = "https",
        UseServerProxy = true,
        RunnerImage = OpenSandboxCodexOptions.PublishedRunnerImage,
        RunnerArchitecture = "amd64",
        NyxIdGatewayUrl = "https://nyx.example.com/api/v1/llm/gateway/v1",
        Model = "gpt-5.4",
        AllowedNyxIdUserIds = ["user-alpha"],
    };
}
