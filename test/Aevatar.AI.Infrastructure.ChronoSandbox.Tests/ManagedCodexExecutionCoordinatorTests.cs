using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aevatar.AI.Infrastructure.ChronoSandbox.Tests;

public sealed class ManagedCodexExecutionCoordinatorTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-25T00:00:00Z");

    private readonly IManagedCodexCredentialQueryPort _query =
        Substitute.For<IManagedCodexCredentialQueryPort>();
    private readonly IManagedCodexChronoTransport _transport =
        Substitute.For<IManagedCodexChronoTransport>();
    private readonly ManagedCodexExecutionCoordinator _coordinator;

    public ManagedCodexExecutionCoordinatorTests()
    {
        _coordinator = new ManagedCodexExecutionCoordinator(
            Options.Create(ManagedOptions()),
            _query,
            _transport,
            new FakeTimeProvider(Now),
            NullLogger<ManagedCodexExecutionCoordinator>.Instance);
        _query.ResolveAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<CancellationToken>())
            .Returns(ReadySnapshot());
        _transport.ExecuteAsync(
                Arg.Any<CodexExecutionRequest>(),
                Arg.Any<ManagedCodexCredentialDescriptor>(),
                Arg.Any<CancellationToken>())
            .Returns(new CodexExecutionResult("CODEX_EXEC_READY", 0, "diag-a", 100));
    }

    [Fact]
    public async Task ExecuteAsync_WhenCredentialIsReady_QueriesOnceAndExecutesOnce()
    {
        var events = await CollectAsync(_coordinator.ExecuteAsync(Request()));

        events.Select(static item => item.Kind)
            .Should()
            .Equal(CodexExecutionEventKind.Started, CodexExecutionEventKind.Completed);
        events[^1].Result!.Output.Should().Be("CODEX_EXEC_READY");
        await _query.Received(1).ResolveAsync(
            Owner("user-a"),
            Arg.Any<CancellationToken>());
        await _transport.Received(1).ExecuteAsync(
            Arg.Is<CodexExecutionRequest>(request => request.Prompt == Request().Prompt),
            Arg.Is<ManagedCodexCredentialDescriptor>(value => value.ApiKeyId == "key-a"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenCredentialIsMissing_FailsWithoutChrono()
    {
        _query.ResolveAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<CancellationToken>())
            .Returns((ManagedCodexCredentialSnapshot?)null);

        var events = await CollectAsync(_coordinator.ExecuteAsync(Request()));

        events[^1].Kind.Should().Be(CodexExecutionEventKind.Failed);
        events[^1].Failure!.Kind.Should().Be(CodexExecutionFailureKind.ProvisioningFailed);
        events[^1].Failure!.Code.Should().Be("managed_credential_not_provisioned");
        await _query.Received(1).ResolveAsync(
            Owner("user-a"),
            Arg.Any<CancellationToken>());
        await _transport.DidNotReceiveWithAnyArgs().ExecuteAsync(
            default!,
            default!,
            default);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCredentialReferenceIsInvalid_FailsWithoutChrono()
    {
        var snapshot = ReadySnapshot();
        snapshot.Credential.SecretReference.OwnerScopeKey =
            "managed-codex-credential:nyxid::user-b";
        _query.ResolveAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<CancellationToken>())
            .Returns(snapshot);

        var events = await CollectAsync(_coordinator.ExecuteAsync(Request()));

        events[^1].Failure!.Kind.Should().Be(CodexExecutionFailureKind.ProvisioningFailed);
        events[^1].Failure!.Code.Should().Be("managed_credential_reference_invalid");
        await _query.Received(1).ResolveAsync(
            Owner("user-a"),
            Arg.Any<CancellationToken>());
        await _transport.DidNotReceiveWithAnyArgs().ExecuteAsync(
            default!,
            default!,
            default);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAuthorizationIsDenied_DoesNotRepairOrRetryInActorTurn()
    {
        _transport.ExecuteAsync(
                Arg.Any<CodexExecutionRequest>(),
                Arg.Any<ManagedCodexCredentialDescriptor>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<CodexExecutionResult>(
                TransportFailure("managed_proxy_authorization_denied")));

        var events = await CollectAsync(_coordinator.ExecuteAsync(Request()));

        events[^1].Kind.Should().Be(CodexExecutionEventKind.Failed);
        events[^1].Failure!.Code.Should().Be("managed_proxy_authorization_denied");
        await _query.Received(1).ResolveAsync(
            Owner("user-a"),
            Arg.Any<CancellationToken>());
        await _transport.Received(1).ExecuteAsync(
            Arg.Any<CodexExecutionRequest>(),
            Arg.Any<ManagedCodexCredentialDescriptor>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenCredentialIsUnavailable_DoesNotRepairOrRetryInActorTurn()
    {
        _transport.ExecuteAsync(
                Arg.Any<CodexExecutionRequest>(),
                Arg.Any<ManagedCodexCredentialDescriptor>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<CodexExecutionResult>(
                TransportFailure("managed_credential_unavailable")));

        var events = await CollectAsync(_coordinator.ExecuteAsync(Request()));

        events[^1].Failure!.Code.Should().Be("managed_credential_unavailable");
        await _transport.Received(1).ExecuteAsync(
            Arg.Any<CodexExecutionRequest>(),
            Arg.Any<ManagedCodexCredentialDescriptor>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("managed_proxy_timeout", CodexExecutionFailureKind.TimedOut)]
    [InlineData("managed_proxy_unavailable", CodexExecutionFailureKind.CapacityUnavailable)]
    [InlineData("managed_response_invalid", CodexExecutionFailureKind.MalformedOutput)]
    public async Task ExecuteAsync_WhenTransportFails_EmitsTheOriginalFailure(
        string code,
        CodexExecutionFailureKind kind)
    {
        _transport.ExecuteAsync(
                Arg.Any<CodexExecutionRequest>(),
                Arg.Any<ManagedCodexCredentialDescriptor>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<CodexExecutionResult>(
                TransportFailure(code, kind)));

        var events = await CollectAsync(_coordinator.ExecuteAsync(Request()));

        events[^1].Failure!.Code.Should().Be(code);
        events[^1].Failure!.Kind.Should().Be(kind);
        await _transport.Received(1).ExecuteAsync(
            Arg.Any<CodexExecutionRequest>(),
            Arg.Any<ManagedCodexCredentialDescriptor>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenTargetIsDisabled_FailsBeforeCredentialQuery()
    {
        var coordinator = new ManagedCodexExecutionCoordinator(
            Options.Create(ManagedOptions(enabled: false)),
            _query,
            _transport,
            new FakeTimeProvider(Now),
            NullLogger<ManagedCodexExecutionCoordinator>.Instance);

        var events = await CollectAsync(coordinator.ExecuteAsync(Request()));

        events[^1].Failure!.Kind.Should().Be(CodexExecutionFailureKind.TargetNotConfigured);
        events[^1].Failure!.Code.Should().Be("managed_target_disabled");
        await _query.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
        await _transport.DidNotReceiveWithAnyArgs().ExecuteAsync(
            default!,
            default!,
            default);
    }

    [Fact]
    public async Task ExecuteAsync_WhenOwnerIsIneligible_FailsBeforeCredentialQuery()
    {
        var events = await CollectAsync(_coordinator.ExecuteAsync(
            Request(authority: new CodexExecutionNyxIdAuthority(
                OwnerScope.NyxIdPlatform,
                string.Empty,
                "user-b"))));

        events[^1].Failure!.Kind.Should().Be(CodexExecutionFailureKind.AdmissionDenied);
        events[^1].Failure!.Code.Should().Be("managed_feature_not_enabled");
        await _query.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
        await _transport.DidNotReceiveWithAnyArgs().ExecuteAsync(
            default!,
            default!,
            default);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCallerCancels_EmitsCancelledTerminalEvent()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var events = await CollectAsync(
            _coordinator.ExecuteAsync(Request(), cts.Token),
            CancellationToken.None);

        events[^1].Kind.Should().Be(CodexExecutionEventKind.Failed);
        var failure = events[^1].Failure;
        failure.Should().NotBeNull();
        failure!.Kind.Should().Be(CodexExecutionFailureKind.Cancelled);
        failure.Code.Should().Be("managed_execution_cancelled");
    }

    [Fact]
    public async Task ExecuteAsync_WhenNativeAuthorityIsMissing_FailsBeforeDependencies()
    {
        var events = await CollectAsync(
            _coordinator.ExecuteAsync(Request(authority: null)));

        events[^1].Failure!.Code.Should().Be("managed_identity_unavailable");
        await _query.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
        await _transport.DidNotReceiveWithAnyArgs().ExecuteAsync(
            default!,
            default!,
            default);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNativeAuthorityCarriesTenant_FailsBeforeDependencies()
    {
        var events = await CollectAsync(
            _coordinator.ExecuteAsync(Request(
                new CodexExecutionNyxIdAuthority(
                    OwnerScope.NyxIdPlatform,
                    "unattested-tenant",
                    "user-a"))));

        events[^1].Failure!.Code.Should().Be("managed_identity_unavailable");
        await _query.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
        await _transport.DidNotReceiveWithAnyArgs().ExecuteAsync(
            default!,
            default!,
            default);
    }

    private static CodexExecutionRequest Request(string? bearer = "caller-token") =>
        Request(
            new CodexExecutionNyxIdAuthority(
                OwnerScope.NyxIdPlatform,
                string.Empty,
                "user-a"),
            bearer);

    private static CodexExecutionRequest Request(
        CodexExecutionNyxIdAuthority? authority,
        string? bearer = "caller-token") =>
        new(
            new CodexExecutionTarget { ManagedSandbox = new CodexManagedSandboxTarget() },
            new CodexExecutionWorkspace { EmptyGit = new CodexEmptyGitWorkspace() },
            "Reply with exactly CODEX_EXEC_READY",
            180,
            new CodexExecutionCallerContext(
                bearer,
                authority,
                "scope-a",
                "run-a",
                "step-a",
                "call-a"));

    private static ManagedCodexOptions ManagedOptions(bool enabled = true) => new()
    {
        Enabled = enabled,
        RolloutBoundary = ManagedCodexRolloutBoundary.InternalOnly,
        Eligibility = new ManagedCodexEligibilityOptions
        {
            Mode = ManagedCodexEligibilityMode.Allowlist,
            AllowedNyxIdUserIds = ["user-a"],
        },
    };

    private static ExternalSubjectRef Owner(string userId) => new()
    {
        Platform = OwnerScope.NyxIdPlatform,
        Tenant = string.Empty,
        ExternalUserId = userId,
    };

    private static ManagedCodexCredentialSnapshot ReadySnapshot() => new()
    {
        Credential = ReadyDescriptor(),
        StateVersion = 7,
        LastEventId = "event-7",
    };

    private static ManagedCodexCredentialDescriptor ReadyDescriptor()
    {
        var expiresAt = Now.AddDays(30);
        return new ManagedCodexCredentialDescriptor
        {
            Owner = Owner("user-a"),
            ApiKeyId = "key-a",
            SecretReference = new SecretReference
            {
                Ref = "sec-a",
                Purpose = CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
                OwnerScopeKey = "managed-codex-credential:nyxid::user-a",
                Fingerprint = "fingerprint-a",
                Version = 1,
                ExpiresAtUnixMs = expiresAt.ToUnixTimeMilliseconds(),
            },
            ChronoSandboxUserServiceId = "us-sandbox",
            ChronoLlmUserServiceId = "us-llm",
            ChronoSandboxServiceSlug = ManagedCodexOptions.ChronoSandboxServiceSlug,
            ExpiresAt = Timestamp.FromDateTimeOffset(expiresAt),
            Status = ManagedCodexCredentialStatus.Active,
        };
    }

    private static ManagedCodexTransportException TransportFailure(
        string code,
        CodexExecutionFailureKind kind = CodexExecutionFailureKind.AdmissionDenied) =>
        new(new CodexExecutionFailure(kind, code, "transport failed"));

    private static async Task<List<CodexExecutionEvent>> CollectAsync(
        IAsyncEnumerable<CodexExecutionEvent> source,
        CancellationToken ct = default)
    {
        var result = new List<CodexExecutionEvent>();
        await foreach (var item in source.WithCancellation(ct))
            result.Add(item);
        return result;
    }
}
