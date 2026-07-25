using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aevatar.AI.Infrastructure.ChronoSandbox.Tests;

public sealed class ManagedCodexExecutionCoordinatorTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-25T00:00:00Z");

    private readonly IManagedCodexCredentialLifecycle _lifecycle =
        Substitute.For<IManagedCodexCredentialLifecycle>();
    private readonly IManagedCodexChronoTransport _transport =
        Substitute.For<IManagedCodexChronoTransport>();
    private readonly ManagedCodexExecutionCoordinator _coordinator;

    public ManagedCodexExecutionCoordinatorTests()
    {
        _coordinator = new ManagedCodexExecutionCoordinator(
            _lifecycle,
            _transport,
            NullLogger<ManagedCodexExecutionCoordinator>.Instance);
        _lifecycle.EnsureReadyAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<string?>(),
                Arg.Any<ManagedCodexCredentialReadinessMode>(),
                Arg.Any<CancellationToken>())
            .Returns(ReadyDescriptor());
        _transport.ExecuteAsync(
                Arg.Any<CodexExecutionRequest>(),
                Arg.Any<ManagedCodexCredentialDescriptor>(),
                Arg.Any<CancellationToken>())
            .Returns(new CodexExecutionResult("CODEX_EXEC_READY", 0, "diag-a", 100));
    }

    [Fact]
    public async Task ExecuteAsync_WhenCredentialIsMissing_EnsuresThenExecutesInSameCall()
    {
        var events = await CollectAsync(_coordinator.ExecuteAsync(Request()));

        events.Select(static item => item.Kind)
            .Should()
            .Equal(CodexExecutionEventKind.Started, CodexExecutionEventKind.Completed);
        events[^1].Result!.Output.Should().Be("CODEX_EXEC_READY");
        await _lifecycle.Received(1).EnsureReadyAsync(
            Owner("user-a"),
            "caller-token",
            ManagedCodexCredentialReadinessMode.Normal,
            Arg.Any<CancellationToken>());
        await _transport.Received(1).ExecuteAsync(
            Arg.Is<CodexExecutionRequest>(request => request.Prompt == Request().Prompt),
            Arg.Is<ManagedCodexCredentialDescriptor>(value => value.ApiKeyId == "key-a"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenAuthorizationIsDenied_RepairsAndRetriesOnce()
    {
        _transport.ExecuteAsync(
                Arg.Any<CodexExecutionRequest>(),
                Arg.Any<ManagedCodexCredentialDescriptor>(),
                Arg.Any<CancellationToken>())
            .Returns(
                _ => throw TransportFailure("managed_proxy_authorization_denied"),
                _ => new CodexExecutionResult("CODEX_EXEC_READY", 0));

        var events = await CollectAsync(_coordinator.ExecuteAsync(Request()));

        events[^1].Kind.Should().Be(CodexExecutionEventKind.Completed);
        await _lifecycle.Received(1).EnsureReadyAsync(
            Owner("user-a"),
            "caller-token",
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation,
            Arg.Any<CancellationToken>());
        await _transport.Received(2).ExecuteAsync(
            Arg.Any<CodexExecutionRequest>(),
            Arg.Any<ManagedCodexCredentialDescriptor>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenAuthorizationIsDeniedTwice_FailsAfterOneRepair()
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
        await _lifecycle.Received(1).EnsureReadyAsync(
            Owner("user-a"),
            "caller-token",
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation,
            Arg.Any<CancellationToken>());
        await _transport.Received(2).ExecuteAsync(
            Arg.Any<CodexExecutionRequest>(),
            Arg.Any<ManagedCodexCredentialDescriptor>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("managed_proxy_timeout", CodexExecutionFailureKind.TimedOut)]
    [InlineData("managed_proxy_unavailable", CodexExecutionFailureKind.CapacityUnavailable)]
    [InlineData("managed_response_invalid", CodexExecutionFailureKind.MalformedOutput)]
    public async Task ExecuteAsync_WhenFailureIsNotRepairable_DoesNotForceRepair(
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
        await _lifecycle.DidNotReceive().EnsureReadyAsync(
            Arg.Any<ExternalSubjectRef>(),
            Arg.Any<string?>(),
            ManagedCodexCredentialReadinessMode.ForceRemoteValidation,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenFirstUseAuthorizationIsUnavailable_MapsProvisioningFailure()
    {
        _lifecycle.EnsureReadyAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<string?>(),
                ManagedCodexCredentialReadinessMode.Normal,
                Arg.Any<CancellationToken>())
            .Returns<Task<ManagedCodexCredentialDescriptor>>(_ =>
                throw new ManagedCodexCredentialLifecycleException(
                    "managed_user_authorization_unavailable",
                    "authorization required"));

        var events = await CollectAsync(
            _coordinator.ExecuteAsync(Request(bearer: null)));

        var failure = events[^1].Failure;
        failure.Should().NotBeNull();
        failure!.Kind.Should().Be(CodexExecutionFailureKind.ProvisioningFailed);
        failure.Code.Should().Be("managed_user_authorization_unavailable");
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
        await _lifecycle.DidNotReceiveWithAnyArgs().EnsureReadyAsync(
            default!,
            default,
            default,
            default);
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

    private static ExternalSubjectRef Owner(string userId) => new()
    {
        Platform = OwnerScope.NyxIdPlatform,
        Tenant = string.Empty,
        ExternalUserId = userId,
    };

    private static ManagedCodexCredentialDescriptor ReadyDescriptor() => new()
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
            ExpiresAtUnixMs = Now.AddDays(30).ToUnixTimeMilliseconds(),
        },
        ChronoSandboxUserServiceId = "us-sandbox",
        ChronoLlmUserServiceId = "us-llm",
        ChronoSandboxServiceSlug = ManagedCodexOptions.ChronoSandboxServiceSlug,
        ExpiresAt = Timestamp.FromDateTimeOffset(Now.AddDays(30)),
        Status = ManagedCodexCredentialStatus.Active,
    };

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
