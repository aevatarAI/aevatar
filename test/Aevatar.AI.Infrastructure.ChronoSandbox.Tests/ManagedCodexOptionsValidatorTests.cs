using FluentAssertions;

namespace Aevatar.AI.Infrastructure.ChronoSandbox.Tests;

public sealed class ManagedCodexOptionsValidatorTests
{
    private readonly ManagedCodexOptionsValidator _validator = new();

    [Fact]
    public void Validate_WhenDisabled_DoesNotRequireAnAllowlist()
    {
        _validator.Validate(null, new ManagedCodexOptions()).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenEnabledForExplicitInternalUsers_Succeeds()
    {
        _validator.Validate(null, ValidOptions()).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenEnabledWithoutExplicitEligibility_FailsClosed()
    {
        var options = ValidOptions();
        options.ProvisioningAllowedNyxIdUserIds = [];

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(message =>
            message.Contains("ProvisioningAllowedNyxIdUserIds", StringComparison.Ordinal));
    }

    [Fact]
    public void Options_DoNotExposeAnAllowAllAdmissionBypassForTheTemporaryCredentialModel()
    {
        typeof(ManagedCodexOptions).GetProperty("AllowAllAuthenticatedUsers").Should().BeNull();
    }

    internal static ManagedCodexOptions ValidOptions() => new()
    {
        Enabled = true,
        ProvisioningAllowedNyxIdUserIds = ["user-a"],
        CredentialLifetimeDays = 30,
        MaxResponseBytes = 1_048_576,
    };
}
