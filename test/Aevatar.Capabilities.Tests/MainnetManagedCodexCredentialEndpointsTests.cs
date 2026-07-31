using System.Security.Claims;
using System.Text.Json;
using Aevatar.AI.Application.CodexExecution;
using Aevatar.AI.Infrastructure.ChronoSandbox;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.Mainnet.Host.Api.ManagedCodex;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aevatar.Capabilities.Tests;

public sealed class MainnetManagedCodexCredentialEndpointsTests
{
    [Fact]
    public async Task StatusAsync_WithoutAuthenticatedNyxIdSubject_ReturnsUnauthorized()
    {
        var query = Substitute.For<IManagedCodexCredentialQueryPort>();

        var result = await ManagedCodexCredentialEndpoints.StatusAsync(
            Context(authenticated: false),
            query,
            Options.Create(ManagedOptions(enabled: false)),
            TimeProvider.System,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status401Unauthorized);
        await query.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
    }

    [Fact]
    public async Task StatusAsync_WhenCredentialIsMissing_ReportsEligibilityWithoutProvisioning()
    {
        var query = Substitute.For<IManagedCodexCredentialQueryPort>();

        var result = await ManagedCodexCredentialEndpoints.StatusAsync(
            Context(subject: "user-a"),
            query,
            Options.Create(ManagedOptions(enabled: true)),
            TimeProvider.System,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status200OK);
        using var payload = JsonDocument.Parse(
            JsonSerializer.Serialize(((IValueHttpResult)result).Value));
        payload.RootElement.GetProperty("enabled").GetBoolean().Should().BeTrue();
        payload.RootElement.GetProperty("eligible").GetBoolean().Should().BeTrue();
        payload.RootElement.GetProperty("status").GetString().Should().Be("not_provisioned");
        payload.RootElement.GetProperty("execution_ready").GetBoolean().Should().BeFalse();
        payload.RootElement.GetProperty("execution_readiness_reason").GetString()
            .Should().Be("managed_credential_not_provisioned");
        await query.Received(1).ResolveAsync(
            Arg.Is<ExternalSubjectRef>(owner =>
                owner.ExternalUserId == "user-a"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionAsync_DerivesTheUserFromClaimsAndReturnsOnlyAnAcceptedReceipt()
    {
        var lifecycle = Substitute.For<IManagedCodexCredentialLifecycle>();
        lifecycle.ProvisionAsync("user-bearer", "user-a", Arg.Any<CancellationToken>())
            .Returns(new ManagedCodexCredentialMutationResult(
                "provisioning_accepted",
                "managed-codex-credential:nyxid::user-a",
                "key-1",
                1_800_000_000_000,
                "command-1"));

        var result = await ManagedCodexCredentialEndpoints.ProvisionAsync(
            Context(subject: "user-a", bearer: "user-bearer"),
            lifecycle,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status202Accepted);
        var json = JsonSerializer.Serialize(((IValueHttpResult)result).Value);
        json.Should().Contain("command-1").And.Contain("key-1");
        json.Should().NotContain("secret").And.NotContain("user-bearer");
        await lifecycle.Received(1).ProvisionAsync(
            "user-bearer",
            "user-a",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionAsync_IgnoresScopeIdAndUsesTheNyxIdUid()
    {
        var lifecycle = Substitute.For<IManagedCodexCredentialLifecycle>();
        lifecycle.ProvisionAsync("user-bearer", "uid-user", Arg.Any<CancellationToken>())
            .Returns(new ManagedCodexCredentialMutationResult(
                "provisioning_accepted",
                "managed-codex-credential:nyxid::uid-user",
                "key-1",
                1_800_000_000_000,
                "command-1"));
        var http = Context(bearer: "user-bearer");
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("scope_id", "scope-user"),
            new Claim("uid", "uid-user"),
            new Claim("sub", "sub-user"),
        ], "test"));

        var result = await ManagedCodexCredentialEndpoints.ProvisionAsync(
            http,
            lifecycle,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status202Accepted);
        await lifecycle.Received(1).ProvisionAsync(
            "user-bearer",
            "uid-user",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StatusAsync_WhenScopeIdDiffersFromNyxIdSubject_UsesNyxIdSubject()
    {
        var http = Context();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("scope_id", "scope-alpha"),
            new Claim("sub", "user-alpha"),
        ], "test"));
        var query = Substitute.For<IManagedCodexCredentialQueryPort>();

        _ = await ManagedCodexCredentialEndpoints.StatusAsync(
            http,
            query,
            Options.Create(ManagedOptions(enabled: true)),
            TimeProvider.System,
            CancellationToken.None);

        await query.Received(1).ResolveAsync(
            Arg.Is<ExternalSubjectRef>(owner =>
                owner.ExternalUserId == "user-alpha"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StatusAsync_WhenDisabled_StillReadsOnlyTheUsersProjection()
    {
        var query = Substitute.For<IManagedCodexCredentialQueryPort>();
        query.ResolveAsync(
                Arg.Is<ExternalSubjectRef>(owner =>
                    owner.Platform == "nyxid" && owner.ExternalUserId == "user-a"),
                Arg.Any<CancellationToken>())
            .Returns(new ManagedCodexCredentialSnapshot
            {
                Credential = Descriptor(),
                StateVersion = 7,
                LastEventId = "event-7",
            });

        var result = await ManagedCodexCredentialEndpoints.StatusAsync(
            Context(subject: "user-a"),
            query,
            Options.Create(ManagedOptions(enabled: false)),
            TimeProvider.System,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status200OK);
        var json = JsonSerializer.Serialize(((IValueHttpResult)result).Value);
        using var payload = JsonDocument.Parse(json);
        payload.RootElement.GetProperty("status").GetString().Should().Be("active");
        payload.RootElement.GetProperty("execution_ready").GetBoolean().Should().BeFalse();
        payload.RootElement.GetProperty("execution_readiness_reason").GetString()
            .Should().Be("managed_target_disabled");
        json.Should().Contain("\"enabled\":false");
        json.Should().NotContain("sec-1")
            .And.NotContain("fingerprint")
            .And.NotContain("key-1");
    }

    [Fact]
    public async Task StatusAsync_WhenCommittedCredentialIsExecutionReady_ReportsReady()
    {
        var query = Substitute.For<IManagedCodexCredentialQueryPort>();
        query.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(new ManagedCodexCredentialSnapshot
            {
                Credential = Descriptor(),
                StateVersion = 7,
                LastEventId = "event-7",
            });

        var result = await ManagedCodexCredentialEndpoints.StatusAsync(
            Context(subject: "user-a"),
            query,
            Options.Create(ManagedOptions(enabled: true)),
            new FakeTimeProvider(DateTimeOffset.Parse("2026-07-28T00:00:00Z")),
            CancellationToken.None);

        using var payload = JsonDocument.Parse(
            JsonSerializer.Serialize(((IValueHttpResult)result).Value));
        payload.RootElement.GetProperty("status").GetString().Should().Be("active");
        payload.RootElement.GetProperty("execution_ready").GetBoolean().Should().BeTrue();
        payload.RootElement.GetProperty("execution_readiness_reason").GetString()
            .Should().Be("ready");
        payload.RootElement.GetProperty("state_version").GetInt64().Should().Be(7);
    }

    [Fact]
    public async Task StatusAsync_WhenActiveCredentialHasInvalidReference_ReportsNotReadyWithoutSecrets()
    {
        var credential = Descriptor();
        credential.SecretReference.OwnerScopeKey =
            "managed-codex-credential:nyxid::other-user";
        var query = Substitute.For<IManagedCodexCredentialQueryPort>();
        query.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(new ManagedCodexCredentialSnapshot
            {
                Credential = credential,
                StateVersion = 8,
                LastEventId = "event-8",
            });

        var result = await ManagedCodexCredentialEndpoints.StatusAsync(
            Context(subject: "user-a"),
            query,
            Options.Create(ManagedOptions(enabled: true)),
            new FakeTimeProvider(DateTimeOffset.Parse("2026-07-28T00:00:00Z")),
            CancellationToken.None);

        var json = JsonSerializer.Serialize(((IValueHttpResult)result).Value);
        using var payload = JsonDocument.Parse(json);
        payload.RootElement.GetProperty("status").GetString().Should().Be("active");
        payload.RootElement.GetProperty("execution_ready").GetBoolean().Should().BeFalse();
        payload.RootElement.GetProperty("execution_readiness_reason").GetString()
            .Should().Be("managed_credential_reference_invalid");
        json.Should().NotContain("sec-1")
            .And.NotContain("fingerprint")
            .And.NotContain("key-1")
            .And.NotContain("other-user");
    }

    [Fact]
    public async Task StatusAsync_WhenUserIsIneligible_ReportsFeatureNotEnabled()
    {
        var query = Substitute.For<IManagedCodexCredentialQueryPort>();
        query.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(new ManagedCodexCredentialSnapshot
            {
                Credential = Descriptor(),
                StateVersion = 7,
            });

        var result = await ManagedCodexCredentialEndpoints.StatusAsync(
            Context(subject: "user-b"),
            query,
            Options.Create(ManagedOptions(enabled: true)),
            TimeProvider.System,
            CancellationToken.None);

        using var payload = JsonDocument.Parse(
            JsonSerializer.Serialize(((IValueHttpResult)result).Value));
        payload.RootElement.GetProperty("eligible").GetBoolean().Should().BeFalse();
        payload.RootElement.GetProperty("execution_ready").GetBoolean().Should().BeFalse();
        payload.RootElement.GetProperty("execution_readiness_reason").GetString()
            .Should().Be("managed_feature_not_enabled");
    }

    [Fact]
    public async Task StatusAsync_WhenActiveCredentialIsPastExpiry_ReturnsExpiredWithoutMutation()
    {
        var query = Substitute.For<IManagedCodexCredentialQueryPort>();
        query.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(new ManagedCodexCredentialSnapshot
            {
                Credential = Descriptor(),
                StateVersion = 7,
                LastEventId = "event-7",
            });
        var now = new FakeTimeProvider(DateTimeOffset.Parse("2031-01-01T00:00:00Z"));

        var result = await ManagedCodexCredentialEndpoints.StatusAsync(
            Context(subject: "user-a"),
            query,
            Options.Create(ManagedOptions(enabled: true)),
            now,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status200OK);
        using var json = JsonDocument.Parse(
            JsonSerializer.Serialize(((IValueHttpResult)result).Value));
        json.RootElement.GetProperty("status").GetString().Should().Be("expired");
        json.RootElement.GetProperty("execution_ready").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("execution_readiness_reason").GetString()
            .Should().Be("managed_credential_expired");
        json.RootElement.GetProperty("state_version").GetInt64().Should().Be(7);
    }

    [Fact]
    public async Task ProvisionAsync_WhenKillSwitchIsOff_ReturnsServiceUnavailable()
    {
        var lifecycle = Substitute.For<IManagedCodexCredentialLifecycle>();
        lifecycle.ProvisionAsync("user-bearer", "user-a", Arg.Any<CancellationToken>())
            .Returns<Task<ManagedCodexCredentialMutationResult>>(_ =>
                throw new ManagedCodexCredentialLifecycleException(
                    "managed_target_disabled",
                    "Managed Codex execution is disabled."));

        var result = await ManagedCodexCredentialEndpoints.ProvisionAsync(
            Context(subject: "user-a", bearer: "user-bearer"),
            lifecycle,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status503ServiceUnavailable);
        JsonSerializer.Serialize(((IValueHttpResult)result).Value)
            .Should().Contain("managed_target_disabled");
    }

    [Theory]
    [InlineData("managed_user_authorization_unavailable", StatusCodes.Status401Unauthorized)]
    [InlineData("managed_feature_not_enabled", StatusCodes.Status403Forbidden)]
    [InlineData("managed_credential_commit_timeout", StatusCodes.Status503ServiceUnavailable)]
    [InlineData("managed_user_services_unavailable", StatusCodes.Status503ServiceUnavailable)]
    public async Task ProvisionAsync_MapsTransparentReadinessFailures(
        string code,
        int expectedStatus)
    {
        var lifecycle = Substitute.For<IManagedCodexCredentialLifecycle>();
        lifecycle.ProvisionAsync("user-bearer", "user-a", Arg.Any<CancellationToken>())
            .Returns<Task<ManagedCodexCredentialMutationResult>>(_ =>
                throw new ManagedCodexCredentialLifecycleException(
                    code,
                    "Managed Codex readiness failed."));

        var result = await ManagedCodexCredentialEndpoints.ProvisionAsync(
            Context(subject: "user-a", bearer: "user-bearer"),
            lifecycle,
            CancellationToken.None);

        StatusCode(result).Should().Be(expectedStatus);
        JsonSerializer.Serialize(((IValueHttpResult)result).Value)
            .Should().Contain(code);
    }

    [Theory]
    [InlineData("managed_credential_persistence_pending")]
    [InlineData("managed_credential_vault_unavailable")]
    public async Task RotateAsync_WhenManagedDependencyIsUnavailable_ReturnsServiceUnavailable(
        string code)
    {
        var lifecycle = Substitute.For<IManagedCodexCredentialLifecycle>();
        lifecycle.RotateAsync("user-bearer", "user-a", Arg.Any<CancellationToken>())
            .Returns<Task<ManagedCodexCredentialMutationResult>>(_ =>
                throw new ManagedCodexCredentialLifecycleException(
                    code,
                    "Managed Codex dependency is temporarily unavailable."));

        var result = await ManagedCodexCredentialEndpoints.RotateAsync(
            Context(subject: "user-a", bearer: "user-bearer"),
            lifecycle,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status503ServiceUnavailable);
        JsonSerializer.Serialize(((IValueHttpResult)result).Value)
            .Should().Contain(code);
    }

    [Fact]
    public async Task RevokeAsync_DoesNotDependOnTheKillSwitch()
    {
        var lifecycle = Substitute.For<IManagedCodexCredentialLifecycle>();
        lifecycle.RevokeAsync("user-bearer", "user-a", Arg.Any<CancellationToken>())
            .Returns(new ManagedCodexCredentialMutationResult(
                "revocation_accepted",
                "managed-codex-credential:nyxid::user-a",
                "key-1",
                1_800_000_000_000,
                "command-2"));

        var result = await ManagedCodexCredentialEndpoints.RevokeAsync(
            Context(subject: "user-a", bearer: "user-bearer"),
            lifecycle,
            CancellationToken.None);

        StatusCode(result).Should().Be(StatusCodes.Status202Accepted);
        await lifecycle.Received(1).RevokeAsync(
            "user-bearer",
            "user-a",
            Arg.Any<CancellationToken>());
    }

    private static DefaultHttpContext Context(
        bool authenticated = true,
        string? subject = null,
        string? bearer = null)
    {
        var context = new DefaultHttpContext();
        var identity = new ClaimsIdentity(
            subject is null ? [] : [new Claim("sub", subject)],
            authenticated ? "test" : null);
        context.User = new ClaimsPrincipal(identity);
        if (bearer is not null)
            context.Request.Headers.Authorization = $"Bearer {bearer}";
        return context;
    }

    private static ManagedCodexOptions ManagedOptions(bool enabled) => new()
    {
        Enabled = enabled,
        RolloutBoundary = ManagedCodexRolloutBoundary.InternalOnly,
        Eligibility = new ManagedCodexEligibilityOptions
        {
            Mode = ManagedCodexEligibilityMode.Allowlist,
            AllowedNyxIdUserIds = ["user-a"],
        },
    };

    private static ManagedCodexCredentialDescriptor Descriptor()
    {
        var expiresAt = DateTimeOffset.Parse("2030-01-01T00:00:00Z");
        return new ManagedCodexCredentialDescriptor
        {
            Owner = new ExternalSubjectRef
            {
                Platform = "nyxid",
                ExternalUserId = "user-a",
            },
            ApiKeyId = "key-1",
            SecretReference = new SecretReference
            {
                Ref = "sec-1",
                Purpose = "managed.codex-invocation-agent-key",
                OwnerScopeKey = "managed-codex-credential:nyxid::user-a",
                Fingerprint = "fingerprint",
                Version = 1,
                ExpiresAtUnixMs = expiresAt.ToUnixTimeMilliseconds(),
            },
            ChronoSandboxUserServiceId = "us-sandbox",
            ChronoLlmUserServiceId = "us-llm",
            ChronoSandboxServiceSlug = "chrono-sandbox",
            ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(expiresAt),
            Status = ManagedCodexCredentialStatus.Active,
        };
    }

    private static int StatusCode(IResult result) =>
        ((IStatusCodeHttpResult)result).StatusCode ?? StatusCodes.Status200OK;
}
