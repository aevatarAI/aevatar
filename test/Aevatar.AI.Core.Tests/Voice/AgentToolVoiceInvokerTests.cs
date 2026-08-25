using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.Core.Voice;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.VoicePresence.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.AI.Core.Tests.Voice;

public class AgentToolVoiceInvokerTests
{
    private const string OwnerActorId = "voice-actor";
    private const string SessionId = "voice-session";
    private const long IssuedAtUnixMs = 1_800_000_000_000;

    [Fact]
    public async Task ExecuteAsync_ShouldResolveToolFromSources()
    {
        var credentials = CredentialProvider("voice-tool:ref-1");
        var invoker = new AgentToolVoiceInvoker([
            new StubToolSource(new FakeAgentTool("door.open", """{"ok":true}""")),
        ], new PassThroughExecutionPort(), credentials);

        var result = await invoker.ExecuteAsync(
            OwnerActorId,
            SessionId,
            "voice-call-resolve",
            IssuedAtUnixMs,
            "door.open",
            """{"target":"front"}""",
            CreateToolContext("voice-tool:ref-1", "door.open"));

        result.Should().Be("""{"ok":true}""");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldThrowWhenToolMissing()
    {
        var invoker = new AgentToolVoiceInvoker([], new PassThroughExecutionPort());

        var act = () => invoker.ExecuteAsync(
            OwnerActorId,
            SessionId,
            "voice-call-missing",
            IssuedAtUnixMs,
            "missing",
            "{}");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Tool 'missing' not found");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRediscoverAgainstPinnedSessionProof()
    {
        var source = new CountingToolSource(new FakeAgentTool("door.open", """{"ok":true}"""));
        var invoker = new AgentToolVoiceInvoker(
            [source],
            new PassThroughExecutionPort(),
            CredentialProvider("voice-tool:ref-1"));
        var context = CreateToolContext("voice-tool:ref-1", "door.open");

        await invoker.ExecuteAsync(OwnerActorId, SessionId, "voice-call-1", IssuedAtUnixMs, "door.open", "{}", context);
        await invoker.ExecuteAsync(OwnerActorId, SessionId, "voice-call-2", IssuedAtUnixMs, "door.open", "{}", context);

        source.DiscoverCalls.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_ConcurrentCalls_ShouldUseRequestScopedDiscovery()
    {
        using var source = new BlockingCountingToolSource(new FakeAgentTool("door.open", """{"ok":true}"""));
        var invoker = new AgentToolVoiceInvoker(
            [source],
            new PassThroughExecutionPort(),
            CredentialProvider("voice-tool:ref-1"));
        var context = CreateToolContext("voice-tool:ref-1", "door.open");
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;

        var tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(async () =>
            {
                if (Interlocked.Increment(ref readyCount) == 32)
                    ready.TrySetResult(true);

                await start.Task;
                return await invoker.ExecuteAsync(
                    OwnerActorId,
                    SessionId,
                    $"voice-call-concurrent-{_}",
                    IssuedAtUnixMs,
                    "door.open",
                    "{}",
                    context);
            }))
            .ToArray();

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        start.SetResult(true);
        await source.WaitForFirstDiscoveryAsync();
        source.Release();

        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));

        source.DiscoverCalls.Should().Be(32);
        results.Should().OnlyContain(result => result == """{"ok":true}""");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldResolveCredentialRefAndExposeCallerNyxIdTokenToTool()
    {
        var captured = new CapturingAgentTool("nyxid_proxy");
        var credentials = new StubCredentialProvider(("voice-tool:ref-1", "caller-token-123"));
        var invoker = new AgentToolVoiceInvoker(
            [new StubToolSource(captured)],
            new PassThroughExecutionPort(),
            credentials);
        var toolContext = new VoiceToolExecutionContext
        {
            CredentialRef = "voice-tool:ref-1",
            ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5)),
        };
        toolContext.AllowedToolNames.Add("nyxid_proxy");

        await invoker.ExecuteAsync(
            OwnerActorId,
            SessionId,
            "voice-call-credential",
            IssuedAtUnixMs,
            "nyxid_proxy",
            "{}",
            toolContext);

        credentials.RequestedRefs.Should().ContainSingle().Which.Should().Be("voice-tool:ref-1");
        captured.CapturedNyxIdAccessToken.Should().Be("caller-token-123");
    }

    [Fact]
    public async Task ExecuteAsync_WithCredentialRef_ShouldMapVoiceBusinessContextAndRejectHiddenTool()
    {
        var allowed = new CapturingAgentTool("door.open");
        var hidden = new CapturingAgentTool("lights.toggle");
        var credentials = new StubCredentialProvider(("voice-tool:ref-2", "caller-token-456"));
        var invoker = new AgentToolVoiceInvoker(
            [new StubToolSource(allowed, hidden)],
            new PassThroughExecutionPort(),
            credentials);
        var toolContext = CreateFullToolContext("voice-tool:ref-2");

        await invoker.ExecuteAsync(
            OwnerActorId,
            SessionId,
            "voice-call-visible",
            IssuedAtUnixMs,
            "door.open",
            "{}",
            toolContext);
        var hiddenAct = () => invoker.ExecuteAsync(
            OwnerActorId,
            SessionId,
            "voice-call-hidden",
            IssuedAtUnixMs,
            "lights.toggle",
            "{}",
            toolContext);

        allowed.CapturedContext.Should().NotBeNull();
        var captured = allowed.CapturedContext!;
        captured.Credentials.NyxIdAccessToken.Should().Be("caller-token-456");
        captured.Caller.ScopeId.Should().Be("caller-scope-1");
        captured.Caller.OwnerSubject.Should().Be("owner-subject-1");
        captured.Caller.ResponseId.Should().Be("response-1");
        captured.Channel.Platform.Should().Be("lark");
        captured.Channel.SenderId.Should().Be("sender-1");
        captured.Channel.RegistrationScopeId.Should().Be("registration-scope-1");
        captured.Channel.MessageId.Should().Be("message-1");
        captured.Channel.PlatformMessageId.Should().Be("platform-message-1");
        captured.Channel.DeliveryTargetId.Should().Be("delivery-1");
        captured.SenderBinding.BindingId.Should().Be("sender-binding-1");
        captured.Routing.NyxIdRoutePreference.Should().Be("direct");
        captured.ConnectedServices.ContextJson.Should().Be("""{"service":"ctx"}""");
        captured.ToolVisibility.IsRestricted.Should().BeTrue();
        captured.ToolVisibility.Allows("door.open").Should().BeTrue();
        captured.ToolVisibility.Allows("lights.toggle").Should().BeFalse();
        await hiddenAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Tool 'lights.toggle' not found");
        hidden.CapturedContext.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoCredentialScope_ShouldRejectWithoutDiscovering()
    {
        var captured = new CapturingAgentTool("nyxid_proxy");
        var invoker = new AgentToolVoiceInvoker(
            [new StubToolSource(captured)],
            new PassThroughExecutionPort());

        var act = () => invoker.ExecuteAsync(
            OwnerActorId,
            SessionId,
            "voice-call-no-scope",
            IssuedAtUnixMs,
            "nyxid_proxy",
            "{}");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Tool 'nyxid_proxy' not found");
        captured.CapturedContext.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenLogicalCallIsRedelivered_ShouldReuseProviderCallIdentity()
    {
        var executionPort = new PassThroughExecutionPort();
        var invoker = new AgentToolVoiceInvoker([
            new StubToolSource(new FakeAgentTool("door.open", "{}")),
        ], executionPort, CredentialProvider("voice-tool:ref-3"));
        var toolContext = CreateFullToolContext("voice-tool:ref-3");

        await invoker.ExecuteAsync(OwnerActorId, SessionId, "provider-call-1", IssuedAtUnixMs, "door.open", "{}", toolContext);
        await invoker.ExecuteAsync(OwnerActorId, SessionId, "provider-call-1", IssuedAtUnixMs, "door.open", "{}", toolContext);
        await invoker.ExecuteAsync(OwnerActorId, SessionId, "provider-call-2", IssuedAtUnixMs + 1, "door.open", "{}", toolContext);

        executionPort.Requests.Should().HaveCount(3);
        executionPort.Requests[0].ExecutionContext.Request
            .Should().BeEquivalentTo(executionPort.Requests[1].ExecutionContext.Request);
        executionPort.Requests[0].ExecutionContext.Request.CallId.Should().Be("provider-call-1");
        executionPort.Requests[0].ExecutionContext.Request.IssuedAtUnixMs.Should().Be(IssuedAtUnixMs);
        executionPort.Requests[2].ExecutionContext.Request.CallId.Should().Be("provider-call-2");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldScopeProviderCallIdentityToActorAndSession()
    {
        var executionPort = new PassThroughExecutionPort();
        var invoker = new AgentToolVoiceInvoker([
            new StubToolSource(new FakeAgentTool("door.open", "{}")),
        ], executionPort, CredentialProvider("voice-tool:ref-1"));
        var toolContext = CreateToolContext("voice-tool:ref-1", "door.open");

        await invoker.ExecuteAsync(
            "voice-actor-a",
            "voice-session-a",
            "provider-call-1",
            IssuedAtUnixMs,
            "door.open",
            "{}",
            toolContext);
        await invoker.ExecuteAsync(
            "voice-actor-b",
            "voice-session-b",
            "provider-call-1",
            IssuedAtUnixMs,
            "door.open",
            "{}",
            toolContext);

        executionPort.Requests.Should().HaveCount(2);
        executionPort.Requests[0].ExecutionOwner.Kind.Should().Be(AgentToolExecutionOwnerKind.Actor);
        executionPort.Requests[0].ExecutionOwner.OwnerId.Should().Be("voice-actor-a");
        executionPort.Requests[0].ExecutionContext.Request.RequestId
            .Should().Be("voice:v1:voice-session-a:provider-call-1");
        executionPort.Requests[1].ExecutionOwner.Kind.Should().Be(AgentToolExecutionOwnerKind.Actor);
        executionPort.Requests[1].ExecutionOwner.OwnerId.Should().Be("voice-actor-b");
        executionPort.Requests[1].ExecutionContext.Request.RequestId
            .Should().Be("voice:v1:voice-session-b:provider-call-1");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStartOnceWithinEachActorSession()
    {
        var ledger = new DeduplicatingAdmissionLedger();
        var tool = new FakeAgentTool("door.open", "{}");
        var invoker = new AgentToolVoiceInvoker([
            new StubToolSource(tool),
        ], new AdmittedAgentToolExecutor(
            ledger,
            new AcceptingAuditTrailAppender(),
            new StableIdentityHasher()), CredentialProvider("voice-tool:ref-1"));
        var toolContext = CreateToolContext("voice-tool:ref-1", "door.open");

        await invoker.ExecuteAsync("voice-actor-a", "voice-session-a", "provider-call-1", IssuedAtUnixMs, "door.open", "{}", toolContext);
        await invoker.ExecuteAsync("voice-actor-a", "voice-session-a", "provider-call-1", IssuedAtUnixMs, "door.open", "{}", toolContext);
        await invoker.ExecuteAsync("voice-actor-a", "voice-session-b", "provider-call-1", IssuedAtUnixMs, "door.open", "{}", toolContext);
        await invoker.ExecuteAsync("voice-actor-b", "voice-session-a", "provider-call-1", IssuedAtUnixMs, "door.open", "{}", toolContext);

        ledger.Decisions.Should().Equal(
            AgentToolAdmissionStatus.Started,
            AgentToolAdmissionStatus.Duplicate,
            AgentToolAdmissionStatus.Started,
            AgentToolAdmissionStatus.Started);
        ledger.Facts.Select(static fact => fact.AdmissionId).Distinct().Should().HaveCount(3);
        tool.ExecutionCalls.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldKeepRequestIdentityUnambiguous()
    {
        var executionPort = new PassThroughExecutionPort();
        var invoker = new AgentToolVoiceInvoker([
            new StubToolSource(new FakeAgentTool("door.open", "{}")),
        ], executionPort, CredentialProvider("voice-tool:ref-1"));
        var toolContext = CreateToolContext("voice-tool:ref-1", "door.open");

        await invoker.ExecuteAsync(
            OwnerActorId,
            "voice-session:a",
            "provider-call-1",
            IssuedAtUnixMs,
            "door.open",
            "{}",
            toolContext);
        await invoker.ExecuteAsync(
            OwnerActorId,
            "voice-session",
            "a:provider-call-1",
            IssuedAtUnixMs,
            "door.open",
            "{}",
            toolContext);

        executionPort.Requests.Select(static request => request.ExecutionContext.Request.RequestId)
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ExecuteAsync_WhenRematerializedSchemaDiffersFromPinnedProof_ShouldFailBeforeExecution()
    {
        var source = new MutableToolSource(new FakeAgentTool("door.open", "{}", "{}"));
        var credentials = CredentialProvider("voice-tool:ref-1");
        var materializer = new VoiceAgentTurnToolCatalogMaterializer([source], [credentials]);
        var context = CreateToolContext("voice-tool:ref-1", "door.open");
        var snapshot = await new AgentToolVoiceCatalog(materializer).DiscoverAsync(context);
        context.ToolCatalogProof = snapshot.Proof.Clone();
        context.ToolCatalogPolicyVersion = snapshot.PolicyVersion;
        source.Tool = new FakeAgentTool(
            "door.open",
            "{}",
            """{"type":"object","properties":{"target":{"type":"string"}}}""");
        var executionPort = new PassThroughExecutionPort();
        var invoker = new AgentToolVoiceInvoker(materializer, executionPort);

        var act = () => invoker.ExecuteAsync(
            OwnerActorId,
            SessionId,
            "voice-call-proof-mismatch",
            IssuedAtUnixMs,
            "door.open",
            "{}",
            context);

        var exception = await act.Should().ThrowAsync<AgentTurnToolCatalogException>();
        exception.Which.Failure.Code.Should().Be(AgentTurnToolCatalogFailureCode.CatalogProofMismatch);
        executionPort.Requests.Should().BeEmpty();
    }

    private static StubCredentialProvider CredentialProvider(string credentialRef) =>
        new((credentialRef, "caller-token"));

    private static VoiceToolExecutionContext CreateToolContext(string credentialRef, string toolName)
    {
        var context = new VoiceToolExecutionContext
        {
            CredentialRef = credentialRef,
            ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5)),
        };
        context.AllowedToolNames.Add(toolName);
        return context;
    }

    private static VoiceToolExecutionContext CreateFullToolContext(string credentialRef)
    {
        var toolContext = new VoiceToolExecutionContext
        {
            CredentialRef = credentialRef,
            ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5)),
            CallerScopeId = " caller-scope-1 ",
            OwnerSubject = " owner-subject-1 ",
            ResponseId = " response-1 ",
            ChannelPlatform = " lark ",
            ChannelSenderId = " sender-1 ",
            ChannelRegistrationScopeId = " registration-scope-1 ",
            ChannelMessageId = " message-1 ",
            ChannelPlatformMessageId = " platform-message-1 ",
            ChannelDeliveryTargetId = " delivery-1 ",
            SenderBindingId = " sender-binding-1 ",
            NyxIdRoutePreference = " direct ",
            ConnectedServicesContextJson = """ {"service":"ctx"} """,
        };
        toolContext.AllowedToolNames.Add(" door.open ");
        return toolContext;
    }

    private sealed class CapturingAgentTool(string name) : IAgentTool
    {
        public string Name { get; } = name;
        public string Description => "capturing";
        public string ParametersSchema => "{}";
        public string? CapturedNyxIdAccessToken { get; private set; }
        public AgentToolExecutionContext? CapturedContext { get; private set; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            _ = argumentsJson;
            _ = ct;
            CapturedNyxIdAccessToken = AgentToolRequestContext.NyxIdAccessToken;
            CapturedContext = AgentToolRequestContext.Current;
            return Task.FromResult("{}");
        }
    }

    private sealed class StubToolSource(params IAgentTool[] tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            _ = ct;
            return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
        }
    }

    private sealed class PassThroughExecutionPort : IAgentToolExecutionPort
    {
        public List<AgentToolExecutionRequest> Requests { get; } = [];

        public async Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            string resultJson;
            using (AgentToolContextScope.Push(request.ExecutionContext))
                resultJson = await request.Tool.ExecuteAsync(request.ArgumentsJson, ct);

            return new AgentToolExecutionOutcome(
                AgentToolExecutionOutcomeKind.Executed,
                resultJson,
                new AgentToolReceipt
                {
                    CallId = request.ExecutionContext.Request.CallId ?? string.Empty,
                    ToolName = request.Tool.Name,
                    Status = AgentToolReceiptStatus.Success,
                    ResultJson = resultJson,
                },
                IsMutation: false,
                FailureCode: string.Empty,
                SafeMessage: string.Empty,
                AgentToolExecutionFailureStage.None,
                TerminalInvoked: true,
                Retryable: false,
                AuditCompleted: true);
        }
    }

    private sealed class StubCredentialProvider(params (string Ref, string Token)[] credentials) : ICredentialProvider
    {
        private readonly Dictionary<string, string> _credentials = credentials.ToDictionary(
            static credential => credential.Ref,
            static credential => credential.Token,
            StringComparer.Ordinal);

        public List<string> RequestedRefs { get; } = [];

        public Task<string?> ResolveAsync(string credentialRef, CancellationToken ct = default)
        {
            _ = ct;
            RequestedRefs.Add(credentialRef);
            return Task.FromResult(_credentials.GetValueOrDefault(credentialRef));
        }
    }

    private sealed class CountingToolSource(params IAgentTool[] tools) : IAgentToolSource
    {
        public int DiscoverCalls { get; private set; }

        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            _ = ct;
            DiscoverCalls++;
            return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
        }
    }

    private sealed class BlockingCountingToolSource(params IAgentTool[] tools) : IAgentToolSource, IDisposable
    {
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _discoverCalls;

        public int DiscoverCalls => Volatile.Read(ref _discoverCalls);

        public async Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _discoverCalls);
            _entered.TrySetResult(true);
            await _release.Task.WaitAsync(ct);
            return tools;
        }

        public Task WaitForFirstDiscoveryAsync() =>
            _entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Release() => _release.SetResult(true);

        public void Dispose()
        {
        }
    }

    private sealed class FakeAgentTool(
        string name,
        string resultJson,
        string parametersSchema = "{}") : IAgentTool
    {
        public string Name { get; } = name;
        public string Description => "fake";
        public string ParametersSchema { get; } = parametersSchema;
        public int ExecutionCalls { get; private set; }

        public Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
        {
            _ = argumentsJson;
            _ = ct;
            ExecutionCalls++;
            return Task.FromResult(resultJson);
        }
    }

    private sealed class MutableToolSource(IAgentTool tool) : IAgentToolSource
    {
        public IAgentTool Tool { get; set; } = tool;

        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<IAgentTool>>([Tool]);
        }
    }

    private sealed class DeduplicatingAdmissionLedger : IAgentToolAdmissionLedger
    {
        private readonly HashSet<string> _admissionIds = new(StringComparer.Ordinal);

        public List<AgentToolAdmissionFact> Facts { get; } = [];
        public List<AgentToolAdmissionStatus> Decisions { get; } = [];

        public Task<AgentToolAdmissionResult> TryStartAsync(
            AgentToolAdmissionFact fact,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Facts.Add(fact.Clone());
            var status = _admissionIds.Add(fact.AdmissionId)
                ? AgentToolAdmissionStatus.Started
                : AgentToolAdmissionStatus.Duplicate;
            Decisions.Add(status);
            return Task.FromResult(new AgentToolAdmissionResult(status));
        }
    }

    private sealed class AcceptingAuditTrailAppender : IAuditTrailAppender
    {
        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
        }
    }

    private sealed class StableIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) => new("actor-hash", "key-1");

        public bool Verify(string canonicalActorKey, string auditActorId, string identityKeyId) => true;
    }
}
