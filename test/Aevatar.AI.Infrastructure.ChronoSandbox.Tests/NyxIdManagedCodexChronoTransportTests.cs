using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions.CodexExecution;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aevatar.AI.Infrastructure.ChronoSandbox.Tests;

public sealed class NyxIdManagedCodexChronoTransportTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-21T12:00:00Z");
    private const string RawKey = "nyx_k_raw-agent-key-must-remain-secret";
    private const string InteractiveBearer = "interactive-bearer-must-not-be-forwarded";

    [Fact]
    public async Task ExecuteAsync_UsesVaultKeyOnlyForTheFixedNyxIdProxyRequest()
    {
        var handler = new RecordingHandler(
            """
            {
              "success": true,
              "output": {
                "text": "CODEX_EXEC_READY",
                "exit_code": 0,
                "execution_time_ms": 125
              },
              "diagnostic_id": "chrono-1"
            }
            """);
        var descriptor = Descriptor();
        var (transport, vault) = CreateTransport(handler);

        var result = await transport.ExecuteAsync(Request(), descriptor);

        result.Output.Should().Be("CODEX_EXEC_READY");
        result.ExitCode.Should().Be(0);
        result.DiagnosticId.Should().Be("chrono-1");
        handler.PathAndQuery.Should().Be(
            "/api/v1/proxy/s/chrono-sandbox/codex/execute?_nyxid_via=us-sandbox");
        handler.Authorization.Should().Be(new AuthenticationHeaderValue("Bearer", RawKey).ToString());
        handler.Authorization.Should().NotContain(InteractiveBearer);
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.EnumerateObject().Select(static property => property.Name)
            .Should().Equal("prompt", "timeout_secs", "workspace");
        body.RootElement.GetProperty("prompt").GetString().Should().Be("Reply with exactly CODEX_EXEC_READY");
        body.RootElement.GetProperty("timeout_secs").GetInt32().Should().Be(180);
        body.RootElement.GetProperty("workspace").GetString().Should().Be("empty_git");
        handler.Body.Should().NotContain(RawKey).And.NotContain(InteractiveBearer);
        await vault.Received(1).ResolveAsync(
            Arg.Is<ResolveSecretRequest>(request =>
                request.Ref == "sec-1" &&
                request.Purpose == CredentialSecretPurposes.ManagedCodexInvocationAgentKey &&
                request.OwnerScopeKey == "managed-codex-credential:nyxid::user-a" &&
                request.SubjectId == "invocation-agent-key"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenReferencePurposeIsWrong_FailsBeforeProxy()
    {
        var handler = new RecordingHandler("{}");
        var descriptor = Descriptor();
        descriptor.SecretReference.Purpose = "wrong-purpose";
        var (transport, vault) = CreateTransport(handler);

        var act = () => transport.ExecuteAsync(Request(), descriptor);

        var exception = (await act.Should().ThrowAsync<ManagedCodexTransportException>()).Which;
        exception.Failure.Code.Should().Be("managed_credential_invalid");
        handler.CallCount.Should().Be(0);
        await vault.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCredentialOwnerDoesNotMatchCaller_FailsBeforeVaultOrProxy()
    {
        var handler = new RecordingHandler("{}");
        var descriptor = Descriptor();
        var (transport, vault) = CreateTransport(handler);

        var act = () => transport.ExecuteAsync(Request("user-b"), descriptor);

        var exception = (await act.Should().ThrowAsync<ManagedCodexTransportException>()).Which;
        exception.Failure.Code.Should().Be("managed_credential_invalid");
        handler.CallCount.Should().Be(0);
        await vault.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNativeAuthorityCarriesTenant_FailsBeforeVaultOrProxy()
    {
        var handler = new RecordingHandler("{}");
        var descriptor = Descriptor();
        var request = Request() with
        {
            Caller = Request().Caller with
            {
                NyxIdAuthority = new CodexExecutionNyxIdAuthority(
                    OwnerScope.NyxIdPlatform,
                    "unattested-tenant",
                    "user-a"),
            },
        };
        var (transport, vault) = CreateTransport(handler);

        var act = () => transport.ExecuteAsync(request, descriptor);

        var exception = (await act.Should().ThrowAsync<ManagedCodexTransportException>()).Which;
        exception.Failure.Code.Should().Be("managed_identity_unavailable");
        handler.CallCount.Should().Be(0);
        await vault.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExecuteAsync_WhenCredentialIsExpiredOrRevoked_FailsBeforeVaultOrProxy(
        bool expired,
        bool revoked)
    {
        var handler = new RecordingHandler("{}");
        var descriptor = Descriptor();
        if (expired)
        {
            descriptor.ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(Now);
            descriptor.SecretReference.ExpiresAtUnixMs = Now.ToUnixTimeMilliseconds();
        }
        if (revoked)
            descriptor.Status = ManagedCodexCredentialStatus.Revoked;
        var (transport, vault) = CreateTransport(handler);

        var act = () => transport.ExecuteAsync(Request(), descriptor);

        var exception = (await act.Should().ThrowAsync<ManagedCodexTransportException>()).Which;
        exception.Failure.Code.Should().Be("managed_credential_invalid");
        handler.CallCount.Should().Be(0);
        await vault.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
    }

    [Theory]
    [InlineData(SecretResolutionFailureReason.Unauthorized)]
    [InlineData(SecretResolutionFailureReason.Revoked)]
    [InlineData(SecretResolutionFailureReason.NotFound)]
    public async Task ExecuteAsync_WhenVaultRejectsTheBoundReference_FailsBeforeProxy(
        SecretResolutionFailureReason failureReason)
    {
        var handler = new RecordingHandler("{}");
        var descriptor = Descriptor();
        var (transport, vault) = CreateTransport(handler);
        vault.ResolveAsync(Arg.Any<ResolveSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ResolveSecretResult(null, null, failureReason));

        var act = () => transport.ExecuteAsync(Request(), descriptor);

        var exception = (await act.Should().ThrowAsync<ManagedCodexTransportException>()).Which;
        exception.Failure.Code.Should().Be("managed_credential_unavailable");
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNyxIdDeniesTheAgentKey_MapsAStableFailureWithoutLeakingBody()
    {
        var handler = new RecordingHandler(
            $$"""{"error":true,"status":403,"body":"denied {{RawKey}}"}""");
        var (transport, _) = CreateTransport(handler);

        var act = () => transport.ExecuteAsync(Request(), Descriptor());

        var exception = (await act.Should().ThrowAsync<ManagedCodexTransportException>()).Which;
        exception.Failure.Kind.Should().Be(CodexExecutionFailureKind.AdmissionDenied);
        exception.Failure.Code.Should().Be("managed_proxy_authorization_denied");
        exception.Message.Should().NotContain(RawKey);
    }

    [Fact]
    public async Task ExecuteAsync_DefensivelyRedactsInvocationKeyFromSuccessfulOutput()
    {
        var handler = new RecordingHandler(
            $$"""
            {
              "success": true,
              "output": {
                "text": "unexpected {{RawKey}}",
                "exit_code": 0,
                "execution_time_ms": 1
              }
            }
            """);
        var (transport, _) = CreateTransport(handler);

        var result = await transport.ExecuteAsync(Request(), Descriptor());

        result.Output.Should().Be("unexpected [REDACTED]");
        result.Output.Should().NotContain(RawKey);
    }

    [Fact]
    public async Task ExecuteAsync_WhenResponseContentLengthExceedsLimit_FailsWithoutReadingBody()
    {
        var content = new ThrowOnReadContent(1_048_577);
        var (transport, _) = CreateTransport(new StaticContentHandler(content));

        var act = () => transport.ExecuteAsync(Request(), Descriptor());

        var exception = (await act.Should().ThrowAsync<ManagedCodexTransportException>()).Which;
        exception.Failure.Kind.Should().Be(CodexExecutionFailureKind.MalformedOutput);
        exception.Failure.Code.Should().Be("managed_response_too_large");
        content.ReadAttempted.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenChronoReportsNonzeroExit_ReturnsTypedTerminalFailure()
    {
        var handler = new RecordingHandler(
            """
            {
              "success": true,
              "output": {
                "text": "command failed",
                "exit_code": 17,
                "execution_time_ms": 9
              },
              "diagnostic_id": "chrono-failed"
            }
            """);
        var (transport, _) = CreateTransport(handler);

        var act = () => transport.ExecuteAsync(Request(), Descriptor());

        var exception = (await act.Should().ThrowAsync<ManagedCodexTransportException>()).Which;
        exception.Failure.Kind.Should().Be(CodexExecutionFailureKind.TerminalFailure);
        exception.Failure.Code.Should().Be("managed_execution_nonzero_exit");
        exception.Failure.DiagnosticId.Should().Be("chrono-failed");
        exception.Message.Should().NotContain("command failed");
    }

    [Fact]
    public async Task ExecuteAsync_StopsAtCompleteLifecycleDeadline()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var options = ManagedCodexOptionsValidatorTests.ValidOptions();
        options.ExecutionLifecycleGraceSeconds = 120;
        var handler = new UnansweredHandler(() => timeProvider.Advance(TimeSpan.FromSeconds(301)));
        var (transport, _) = CreateTransport(handler, options, timeProvider);

        var act = () => transport.ExecuteAsync(Request(timeoutSeconds: 180), Descriptor());

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.ObservedToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_KeepsWaitingBeforeCompleteLifecycleDeadline()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var options = ManagedCodexOptionsValidatorTests.ValidOptions();
        options.ExecutionLifecycleGraceSeconds = 120;
        var handler = new UnansweredHandler(() => timeProvider.Advance(TimeSpan.FromSeconds(299)));
        var (transport, _) = CreateTransport(handler, options, timeProvider);

        var pending = transport.ExecuteAsync(Request(timeoutSeconds: 180), Descriptor());

        pending.IsCompleted.Should().BeFalse();
        handler.ObservedToken.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_StillHonoursCallerCancellation_WhenItsOwnDeadlineHasNotElapsed()
    {
        using var caller = new CancellationTokenSource();
        var handler = new UnansweredHandler(caller.Cancel);
        var options = ManagedCodexOptionsValidatorTests.ValidOptions();
        options.ExecutionLifecycleGraceSeconds = 120;
        var (transport, _) = CreateTransport(handler, options);

        var act = () => transport.ExecuteAsync(Request(timeoutSeconds: 180), Descriptor(), caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.ObservedToken.IsCancellationRequested.Should().BeTrue();
    }

    private static (NyxIdManagedCodexChronoTransport Transport, ISecretVault Vault) CreateTransport(
        HttpMessageHandler handler,
        ManagedCodexOptions? options = null,
        FakeTimeProvider? timeProvider = null)
    {
        var vault = Substitute.For<ISecretVault>();
        var reference = Descriptor().SecretReference.Clone();
        vault.ResolveAsync(Arg.Any<ResolveSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ResolveSecretResult(reference, RawKey));
        var nyxClient = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });
        return (
            new NyxIdManagedCodexChronoTransport(
                Options.Create(options ?? ManagedCodexOptionsValidatorTests.ValidOptions()),
                new TestNyxIdApiClientFactory(nyxClient),
                vault,
                timeProvider ?? new FakeTimeProvider(Now)),
            vault);
    }

    private static ManagedCodexCredentialDescriptor Descriptor() => new()
    {
        Owner = new ExternalSubjectRef
        {
            Platform = "nyxid",
            Tenant = string.Empty,
            ExternalUserId = "user-a",
        },
        ApiKeyId = "key-1",
        SecretReference = new SecretReference
        {
            Ref = "sec-1",
            Purpose = CredentialSecretPurposes.ManagedCodexInvocationAgentKey,
            OwnerScopeKey = "managed-codex-credential:nyxid::user-a",
            Fingerprint = "fingerprint",
            Version = 1,
            ExpiresAtUnixMs = Now.AddDays(30).ToUnixTimeMilliseconds(),
        },
        ChronoSandboxUserServiceId = "us-sandbox",
        ChronoLlmUserServiceId = "us-llm",
        ChronoSandboxServiceSlug = "chrono-sandbox",
        ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(Now.AddDays(30)),
        Status = ManagedCodexCredentialStatus.Active,
    };

    private static CodexExecutionRequest Request(string userId = "user-a", int timeoutSeconds = 180) => new(
        new CodexExecutionTarget { ManagedSandbox = new CodexManagedSandboxTarget() },
        new CodexExecutionWorkspace { EmptyGit = new CodexEmptyGitWorkspace() },
        "Reply with exactly CODEX_EXEC_READY",
        timeoutSeconds,
        new CodexExecutionCallerContext(
            InteractiveBearer,
            new CodexExecutionNyxIdAuthority("nyxid", string.Empty, userId),
            userId,
            "run-a",
            "step-a",
            "call-a"));

    private sealed class TestNyxIdApiClientFactory(NyxIdApiClient client) : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() => client;
    }

    // Answers nothing, so the only way out is cancellation. Completion is driven by a
    // TaskCompletionSource wired to the token rather than by elapsed time.
    private sealed class UnansweredHandler(Action? onSend = null) : HttpMessageHandler
    {
        public CancellationToken ObservedToken { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ObservedToken = cancellationToken;
            onSend?.Invoke();
            var unanswered = new TaskCompletionSource<HttpResponseMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = cancellationToken.Register(
                () => unanswered.TrySetCanceled(cancellationToken));
            return await unanswered.Task;
        }
    }

    private sealed class RecordingHandler(string response) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string? Path { get; private set; }
        public string? PathAndQuery { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Path = request.RequestUri?.AbsolutePath;
            PathAndQuery = request.RequestUri?.PathAndQuery;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StaticContentHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
    }

    private sealed class ThrowOnReadContent : HttpContent
    {
        public ThrowOnReadContent(long contentLength)
        {
            Headers.ContentLength = contentLength;
        }

        public bool ReadAttempted { get; private set; }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
        {
            ReadAttempted = true;
            throw new InvalidOperationException("The oversized body must not be read.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = Headers.ContentLength!.Value;
            return true;
        }
    }
}
