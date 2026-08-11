using Aevatar.AI.Abstractions;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatTurnOperationExecutorReadAuthorityTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 12, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ProductionConstructor_ShouldRequireReadAuthorityPort()
    {
        var constructor = typeof(NyxIdChatTurnOperationExecutor)
            .GetConstructors()
            .OrderByDescending(static candidate => candidate.GetParameters().Length)
            .First();

        constructor.GetParameters().Should().ContainSingle(parameter =>
            parameter.ParameterType == typeof(INyxIdActionReadAuthorityPort));
    }

    [Fact]
    public async Task KeyCreate_WithIssuedAuthorityAndEmptySession_ShouldVerifyProviderReadBack()
    {
        var clock = new FakeTimeProvider(Now);
        var authorityPort = CreateAuthorityPort(clock);
        var issued = await authorityPort.IssueAsync(
            "bearer-fresh-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");
        var evidence = new RecordingActionEvidenceReadPort
        {
            AgentApiKey = AgentKey(),
        };
        var executor = CreateExecutor(
            new NyxIdActionPostconditionPort(null, evidence, clock),
            authorityPort,
            clock);

        var execution = await executor.ExecuteAsync(
            KeyCreateCommand(issued.Authority),
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        evidence.AgentKeyReads.Should().ContainSingle().Which.Should().Be("key-alpha");
        evidence.BearerTokens.Should().ContainSingle().Which.Should().Be("bearer-fresh-alpha");
        execution.Result.ActionPostcondition.Verified.Should().BeTrue();
        execution.Result.ActionPostcondition.Resource.Key.KeyId.Should().Be("key-alpha");
        execution.Result.ToString().Should().NotContain("bearer-fresh-alpha");
    }

    [Fact]
    public async Task KeyCreate_AfterExecutorRestartWithSameVault_ShouldVerifyProviderReadBack()
    {
        var clock = new FakeTimeProvider(Now);
        var vault = new InMemorySecretVault(clock);
        var issuingAuthorityPort = CreateAuthorityPort(vault, clock);
        var issued = await issuingAuthorityPort.IssueAsync(
            "bearer-fresh-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");
        var restartedAuthorityPort = CreateAuthorityPort(vault, clock);
        var evidence = new RecordingActionEvidenceReadPort
        {
            AgentApiKey = AgentKey(),
        };
        var restartedExecutor = CreateExecutor(
            new NyxIdActionPostconditionPort(null, evidence, clock),
            restartedAuthorityPort,
            clock);

        var execution = await restartedExecutor.ExecuteAsync(
            KeyCreateCommand(issued.Authority),
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        evidence.AgentKeyReads.Should().ContainSingle().Which.Should().Be("key-alpha");
        evidence.BearerTokens.Should().ContainSingle().Which.Should().Be("bearer-fresh-alpha");
        execution.Result.ActionPostcondition.Verified.Should().BeTrue();
        execution.Result.ToString().Should().NotContain("bearer-fresh-alpha");
    }

    [Fact]
    public async Task KeyCreate_WithHistoricalSessionBearer_ShouldUseResolvedAuthorityOnly()
    {
        var clock = new FakeTimeProvider(Now);
        var authorityPort = CreateAuthorityPort(clock);
        var issued = await authorityPort.IssueAsync(
            "bearer-fresh-alpha",
            "scope-alpha",
            "owner-alpha",
            "command-action-alpha");
        var postconditionPort = new RecordingActionPostconditionPort();
        var executor = CreateExecutor(postconditionPort, authorityPort, clock);
        var session = new NyxIdChatTransientExecutionSession
        {
            Request = new NeedsLlmReplyEvent
            {
                ToolContext = TransientContext("bearer-stale-alpha"),
            },
        };

        await executor.ExecuteAsync(
            KeyCreateCommand(issued.Authority),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        postconditionPort.TransientToolContexts.Should().ContainSingle()
            .Which.Credentials.NyxIdAccessToken.Should().Be("bearer-fresh-alpha");
    }

    [Theory]
    [InlineData("missing", NyxIdActionReadAuthorityPort.MissingCode)]
    [InlineData("expired", NyxIdActionReadAuthorityPort.ExpiredCode)]
    [InlineData("revoked", NyxIdActionReadAuthorityPort.RevokedCode)]
    [InlineData("scope", NyxIdActionReadAuthorityPort.ScopeMismatchCode)]
    [InlineData("owner", NyxIdActionReadAuthorityPort.OwnerMismatchCode)]
    [InlineData("unavailable", NyxIdActionReadAuthorityPort.UnavailableCode)]
    [InlineData("no-port", NyxIdActionReadAuthorityPort.UnavailableCode)]
    public async Task KeyCreate_AuthorityFailure_ShouldFailClosedWithoutProviderRead(
        string variant,
        string expectedFailureCode)
    {
        var clock = new FakeTimeProvider(Now);
        var authorityPort = CreateAuthorityPort(clock);
        NyxIdReadAuthorityRef? authority = null;
        if (variant is not ("missing" or "unavailable" or "no-port"))
        {
            authority = (await authorityPort.IssueAsync(
                    "bearer-fresh-alpha",
                    "scope-alpha",
                    "owner-alpha",
                    "command-action-alpha"))
                .Authority;
        }

        switch (variant)
        {
            case "expired":
                clock.Advance(TimeSpan.FromMinutes(11));
                break;
            case "revoked":
                (await authorityPort.RevokeAsync(
                        authority,
                        "scope-alpha",
                        "owner-alpha"))
                    .Should().BeTrue();
                break;
            case "scope":
                authority!.ScopeId = "scope-other";
                break;
            case "owner":
                authority!.OwnerSubject = "owner-other";
                break;
        }

        INyxIdActionReadAuthorityPort? resolver = variant switch
        {
            "unavailable" => new ThrowingReadAuthorityPort(),
            "no-port" => null,
            _ => authorityPort,
        };
        var postconditionPort = new RecordingActionPostconditionPort();
        var executor = CreateExecutor(postconditionPort, resolver, clock);

        var execution = await executor.ExecuteAsync(
            KeyCreateCommand(authority),
            new NyxIdChatTransientExecutionSession
            {
                Request = new NeedsLlmReplyEvent
                {
                    ToolContext = TransientContext("bearer-stale-alpha"),
                },
            },
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        postconditionPort.Inputs.Should().BeEmpty();
        postconditionPort.TransientToolContexts.Should().BeEmpty();
        execution.Result.ActionPostcondition.Verified.Should().BeFalse();
        execution.Result.ActionPostcondition.FailureCode.Should().Be(expectedFailureCode);
        execution.Result.ToString().Should().NotContain("bearer-stale-alpha");
    }

    private static NyxIdChatTurnOperationExecutor CreateExecutor(
        INyxIdActionPostconditionPort postconditionPort,
        INyxIdActionReadAuthorityPort? authorityPort,
        TimeProvider clock) =>
        authorityPort is null
            ? new NyxIdChatTurnOperationExecutor(
                new UnusedGenerationExecutor(),
                postconditionPort)
            : new NyxIdChatTurnOperationExecutor(
                new UnusedGenerationExecutor(),
                postconditionPort,
                null,
                new NyxIdChatDelegationCredentialLifecyclePort(clock),
                new NyxIdChatToolVerificationPort(),
                authorityPort,
                NullLogger<NyxIdChatTurnOperationExecutor>.Instance);

    private static NyxIdActionReadAuthorityPort CreateAuthorityPort(TimeProvider clock) =>
        CreateAuthorityPort(new InMemorySecretVault(clock), clock);

    private static NyxIdActionReadAuthorityPort CreateAuthorityPort(
        InMemorySecretVault vault,
        TimeProvider clock) =>
        new(
            vault,
            clock,
            TimeSpan.FromMinutes(10),
            TimeSpan.FromHours(24));

    private static NyxIdChatOperationDispatchCommand KeyCreateCommand(
        NyxIdReadAuthorityRef? authority) =>
        new()
        {
            Key = new NyxIdChatOperationKey
            {
                ConversationActorId = "conversation-alpha",
                TurnId = "turn-action-alpha",
                TaskId = "task-alpha",
                StepId = "step-postcondition-alpha",
                OperationId = "operation-postcondition-alpha",
                OperationGeneration = 1,
            },
            ActionPostcondition = new NyxIdChatActionPostconditionInput
            {
                ScopeId = "scope-alpha",
                OwnerSubject = "owner-alpha",
                OriginTurnId = "turn-origin-alpha",
                ActionRequestId = "action-alpha",
                Action = NyxIdAssistantActionKind.KeyCreate,
                ReportedDisposition = NyxIdChatActionDisposition.Completed,
                RequestedAt = Timestamp.FromDateTimeOffset(Now.AddMinutes(-2)),
                ResourceHint = new NyxIdChatSafeResourceRef
                {
                    Key = new NyxIdChatKeyRef { KeyId = "key-alpha" },
                },
                Params = new NyxIdAssistantActionParams
                {
                    KeyCreate = new NyxIdKeyCreateParams
                    {
                        Name = "Key Alpha",
                        Platform = "codex",
                        AllowedServiceIds = { "service-alpha", "service-beta" },
                    },
                },
                ReadAuthority = authority?.Clone(),
            },
        };

    private static AgentToolExecutionContextPayload TransientContext(string bearerToken) =>
        new()
        {
            Caller = new AgentToolCallerContextPayload
            {
                ScopeId = "scope-alpha",
                OwnerSubject = "owner-alpha",
            },
            Credentials = new AgentToolCredentialsPayload
            {
                NyxIdAccessToken = bearerToken,
                NyxIdCredentialKind =
                    AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
            },
        };

    private static NyxIdAgentApiKeyEvidence AgentKey() =>
        new(
            "key-alpha",
            "Key Alpha",
            ["proxy"],
            "codex",
            true,
            ["service-beta", "service-alpha"],
            false,
            [],
            false,
            Now.AddMinutes(-1),
            null);

    private sealed class RecordingActionPostconditionPort : INyxIdActionPostconditionPort
    {
        public List<NyxIdChatActionPostconditionInput> Inputs { get; } = [];
        public List<AgentToolExecutionContextPayload> TransientToolContexts { get; } = [];

        public Task<NyxIdChatActionPostconditionResult> VerifyAsync(
            NyxIdChatActionPostconditionInput input,
            AgentToolExecutionContextPayload? transientToolContext = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Inputs.Add(input.Clone());
            if (transientToolContext is not null)
                TransientToolContexts.Add(transientToolContext.Clone());
            return Task.FromResult(new NyxIdChatActionPostconditionResult
            {
                ActionRequestId = input.ActionRequestId,
                Disposition = NyxIdChatActionDisposition.Completed,
                Verified = true,
                Resource = input.ResourceHint?.Clone(),
            });
        }
    }

    private sealed class RecordingActionEvidenceReadPort : INyxIdActionEvidenceReadPort
    {
        public NyxIdAgentApiKeyEvidence? AgentApiKey { get; init; }
        public List<string> AgentKeyReads { get; } = [];
        public List<string> BearerTokens { get; } = [];

        public Task<NyxIdApiAccessResult<NyxIdUserServiceAuthorizationEvidence>>
            GetUserServiceAuthorizationAsync(
                string bearerToken,
                string userServiceId,
                CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<NyxIdApiAccessResult<NyxIdAgentApiKeyEvidence>> GetAgentApiKeyAsync(
            string bearerToken,
            string keyId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            BearerTokens.Add(bearerToken);
            AgentKeyReads.Add(keyId);
            return Task.FromResult(new NyxIdApiAccessResult<NyxIdAgentApiKeyEvidence>(
                AgentApiKey,
                null));
        }
    }

    private sealed class ThrowingReadAuthorityPort : INyxIdActionReadAuthorityPort
    {
        public Task<NyxIdActionReadAuthorityIssueResult> IssueAsync(
            string bearerToken,
            string scopeId,
            string ownerSubject,
            string requestIdentity,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<NyxIdActionReadAuthorityResolution> ResolveAsync(
            NyxIdReadAuthorityRef? authority,
            string expectedScopeId,
            string expectedOwnerSubject,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("vault unavailable");

        public Task<bool> RevokeAsync(
            NyxIdReadAuthorityRef? authority,
            string expectedScopeId,
            string expectedOwnerSubject,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedGenerationExecutor : IAgentRunReplyGenerationExecutorPort
    {
        public Task<AgentRunReplyStepState> BuildInitialStepStateAsync(
            AgentRunReplyGenerationExecutionRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AgentRunLlmStepExecution> BuildLlmStepExecutionAsync(
            AgentRunReplyStepExecutionRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AgentRunNextToolStepRequestedEvent> BuildToolStepContinuationAsync(
            AgentRunReplyStepExecutionRequest request,
            AgentRunAuthorizedToolStep? authorizedToolStep,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
