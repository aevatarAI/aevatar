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
    public void Validate_WhenAllowlistContainsNormalizedUsers_Succeeds()
    {
        var options = ValidOptions();
        options.Eligibility = new ManagedCodexEligibilityOptions
        {
            Mode = ManagedCodexEligibilityMode.Allowlist,
            AllowedNyxIdUserIds = ["user-a", "user-b"],
        };

        _validator.Validate(null, options).Succeeded.Should().BeTrue();
        options.IsEligible("user-a").Should().BeTrue();
        options.IsEligible("user-c").Should().BeFalse();
    }

    [Fact]
    public void Validate_WhenAllModeHasNoAllowlist_SucceedsForEveryNormalizedUser()
    {
        var options = ValidOptions();
        options.Eligibility = new ManagedCodexEligibilityOptions
        {
            Mode = ManagedCodexEligibilityMode.All,
            AllowedNyxIdUserIds = [],
        };

        _validator.Validate(null, options).Succeeded.Should().BeTrue();
        options.IsEligible("user-a").Should().BeTrue();
        options.IsEligible("user-b").Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenAllowlistIsEmpty_Fails()
    {
        var options = ValidOptions();
        options.Eligibility = new ManagedCodexEligibilityOptions
        {
            Mode = ManagedCodexEligibilityMode.Allowlist,
            AllowedNyxIdUserIds = [],
        };

        _validator.Validate(null, options).Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenAllowlistEntryIsNotNormalized_Fails()
    {
        var options = ValidOptions();
        options.Eligibility = new ManagedCodexEligibilityOptions
        {
            Mode = ManagedCodexEligibilityMode.Allowlist,
            AllowedNyxIdUserIds = [" user-a "],
        };

        _validator.Validate(null, options).Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenEnabledWithoutInternalOnlyRolloutBoundary_Fails()
    {
        var options = ValidOptions();
        options.RolloutBoundary = ManagedCodexRolloutBoundary.Unspecified;

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(
            "RolloutBoundary must be InternalOnly while managed Codex uses the internal delegation boundary.");
    }

    [Fact]
    public void Validate_WhenAllModeAlsoHasUsers_Fails()
    {
        var options = ValidOptions();
        options.Eligibility = new ManagedCodexEligibilityOptions
        {
            Mode = ManagedCodexEligibilityMode.All,
            AllowedNyxIdUserIds = ["user-a"],
        };

        _validator.Validate(null, options).Failed.Should().BeTrue();
    }

    [Fact]
    public void Options_DoNotRetainTheProvisioningNamedAllowlist()
    {
        typeof(ManagedCodexOptions)
            .GetProperty("ProvisioningAllowedNyxIdUserIds")
            .Should().BeNull();
    }

    [Fact]
    public void Validate_WhenLeaseMarginCannotCoverSafetyCompensationAndRecording_Fails()
    {
        var insufficient = ValidOptions();
        insufficient.MutationCompletionSeconds = 60;
        insufficient.MutationLeaseSeconds = 89;
        var sufficient = ValidOptions();
        sufficient.MutationCompletionSeconds = 60;
        sufficient.MutationLeaseSeconds = 90;

        _validator.Validate(null, insufficient).Failed.Should().BeTrue();
        _validator.Validate(null, sufficient).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenExecutionLifecycleGraceIsOutsideItsRange_Fails()
    {
        var tooShort = ValidOptions();
        tooShort.ExecutionLifecycleGraceSeconds = 119;
        var minimum = ValidOptions();
        minimum.ExecutionLifecycleGraceSeconds = 120;
        var tooLong = ValidOptions();
        tooLong.ExecutionLifecycleGraceSeconds = 181;

        _validator.Validate(null, tooShort).Failed.Should().BeTrue();
        _validator.Validate(null, minimum).Succeeded.Should().BeTrue();
        _validator.Validate(null, tooLong).Failed.Should().BeTrue();
    }

    internal static ManagedCodexOptions ValidOptions() => new()
    {
        Enabled = true,
        RolloutBoundary = ManagedCodexRolloutBoundary.InternalOnly,
        Eligibility = new ManagedCodexEligibilityOptions
        {
            Mode = ManagedCodexEligibilityMode.Allowlist,
            AllowedNyxIdUserIds = ["user-a"],
        },
        CredentialLifetimeDays = 30,
        MaxResponseBytes = 1_048_576,
    };
}
