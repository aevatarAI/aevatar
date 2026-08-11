using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.ExactServiceApprovals;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public sealed partial class NyxIdChatTurnGAgentTests
{
    [Fact]
    public async Task ExactConnectedServiceEffect_ShouldRequestTierABeforeProviderInvocation()
    {
        var authority = ExactApprovalAuthority();
        var approvalPort = new RecordingExactServiceApprovalPort
        {
            CreateResult = new NyxIdExactServiceApprovalCreateResult(
                NyxIdExactServiceApprovalCreateDisposition.Created,
                new NyxIdExactServiceApprovalSnapshot(
                    NyxIdExactServiceApprovalState.Pending,
                    authority)),
        };
        var admission = ExactWriteAdmission();
        var generation = new StreamingCapabilityReplyExecutor(admission);
        var executor = ExactApprovalExecutor(generation, approvalPort);
        var (session, call) = await PrepareExactServiceCallAsync(executor);

        var execution = await executor.ExecuteAsync(
            ExactServiceToolCommand(call),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        generation.ToolExecutions.Should().Be(0,
            "the connected-service provider must not run before exact approval redemption");
        approvalPort.CreateInputs.Should().ContainSingle();
        var input = approvalPort.CreateInputs[0];
        input.AccessToken.Should().Be("user-token");
        input.Admission.Should().BeEquivalentTo(
            AgentToolOperationAdmissionPayloadMapper.FromPayload(call.OperationAdmission));
        input.Arguments.ToJsonString().Should().Be("{\"value\":1}");
        input.OperationId.Should().Be("operation-tool-alpha");
        input.OperationGeneration.Should().Be(1);
        input.IdempotencyKey.Should().Be("operation-tool-alpha");
        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Tool);
        execution.Result.Tool.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotStarted);
        execution.Result.Tool.Receipt.Status.Should().Be(
            AgentToolReceiptStatus.ApprovalRequired);
        execution.Result.Tool.Receipt.ApprovalRequestId.Should().Be(authority.RequestId);
        execution.Result.Tool.Receipt.ExactServiceApproval.Should().BeEquivalentTo(authority);
    }

    [Theory]
    [InlineData(NyxIdExactServiceApprovalCreateDisposition.TierAUnavailable)]
    [InlineData(NyxIdExactServiceApprovalCreateDisposition.ApprovalNotRequired)]
    public async Task ExactConnectedServiceEffect_WhenTierAFallsBack_ShouldUseTierB(
        NyxIdExactServiceApprovalCreateDisposition disposition)
    {
        var approvalPort = new RecordingExactServiceApprovalPort
        {
            CreateResult = new NyxIdExactServiceApprovalCreateResult(disposition),
        };
        var generation = new StreamingCapabilityReplyExecutor(ExactWriteAdmission());
        var executor = ExactApprovalExecutor(generation, approvalPort);
        var (session, call) = await PrepareExactServiceCallAsync(executor);

        var execution = await executor.ExecuteAsync(
            ExactServiceToolCommand(call),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        approvalPort.CreateInputs.Should().ContainSingle();
        generation.ToolExecutions.Should().Be(1);
        execution.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success);
    }

    [Fact]
    public async Task ExactConnectedServiceEffect_WhenTierARejects_ShouldFailBeforeEffect()
    {
        var approvalPort = new RecordingExactServiceApprovalPort
        {
            CreateResult = new NyxIdExactServiceApprovalCreateResult(
                NyxIdExactServiceApprovalCreateDisposition.Rejected,
                FailureCode: "requester_scope_mismatch"),
        };
        var generation = new StreamingCapabilityReplyExecutor(ExactWriteAdmission());
        var executor = ExactApprovalExecutor(generation, approvalPort);
        var (session, call) = await PrepareExactServiceCallAsync(executor);

        var execution = await executor.ExecuteAsync(
            ExactServiceToolCommand(call),
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        generation.ToolExecutions.Should().Be(0);
        execution.Result.ResultCase.Should().Be(
            NyxIdChatOperationResultSignal.ResultOneofCase.Failure);
        execution.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.ExactServiceApprovalFailedCode);
        execution.Result.Failure.ExternalEffect.Should().Be(
            NyxIdChatEffectEvidence.NotStarted);
    }

    [Fact]
    public async Task ExactApprovalContinuation_AfterTransientSessionLoss_ShouldDecideAndRedeemSameAuthority()
    {
        var authority = ExactApprovalAuthority();
        var receipt = new NyxIdExactServiceApprovalReceipt(
            201,
            "{\"id\":\"message-alpha\"}",
            "sha256:response");
        var approvalPort = new RecordingExactServiceApprovalPort
        {
            DecideResult = new NyxIdExactServiceApprovalSnapshot(
                NyxIdExactServiceApprovalState.Approved,
                authority),
            RedeemResult = new NyxIdExactServiceApprovalSnapshot(
                NyxIdExactServiceApprovalState.Redeemed,
                authority,
                receipt),
        };
        var executor = ExactApprovalExecutor(
            new CapabilityGeneratingReplyExecutor(), approvalPort);

        var execution = await executor.ExecuteAsync(
            ExactApprovalContinuation(authority, approved: true),
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        approvalPort.Decisions.Should().ContainSingle().Which.Should().BeTrue();
        approvalPort.DecisionAuthorities.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(authority);
        approvalPort.RedeemAuthorities.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(authority);
        execution.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success);
        execution.Result.Tool.Receipt.ResultJson.Should().Be(receipt.ResponseBody);
        execution.Result.Tool.ResultJson.Should().Be(receipt.ResponseBody);
        execution.Result.Tool.ExternalEffect.Should().Be(
            NyxIdChatEffectEvidence.MayHaveChanged);
    }

    [Fact]
    public async Task ExactApprovalDenial_ShouldNotRedeem()
    {
        var authority = ExactApprovalAuthority();
        var approvalPort = new RecordingExactServiceApprovalPort
        {
            DecideResult = new NyxIdExactServiceApprovalSnapshot(
                NyxIdExactServiceApprovalState.Denied,
                authority),
        };
        var executor = ExactApprovalExecutor(
            new CapabilityGeneratingReplyExecutor(), approvalPort);

        var execution = await executor.ExecuteAsync(
            ExactApprovalContinuation(authority, approved: false),
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        approvalPort.Decisions.Should().ContainSingle().Which.Should().BeFalse();
        approvalPort.RedeemAuthorities.Should().BeEmpty();
        execution.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.Denied);
        execution.Result.Tool.Receipt.NyxIdApprovalTerminalOutcome.Should().Be(
            NyxIdApprovalTerminalOutcome.Rejected);
        execution.Result.Tool.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
    }

    [Theory]
    [InlineData(NyxIdExactServiceApprovalState.Expired, "approval_expired")]
    [InlineData(NyxIdExactServiceApprovalState.Revoked, "approval_revoked")]
    [InlineData(NyxIdExactServiceApprovalState.Drifted, "approval_drifted")]
    [InlineData(NyxIdExactServiceApprovalState.Redeeming, "redemption_in_progress")]
    [InlineData(NyxIdExactServiceApprovalState.Failed, "provider_response_too_large")]
    public async Task ExactApprovalTerminalOrDriftState_ShouldFailClosed(
        NyxIdExactServiceApprovalState state,
        string failureCode)
    {
        var authority = ExactApprovalAuthority();
        var approvalPort = new RecordingExactServiceApprovalPort
        {
            DecideResult = new NyxIdExactServiceApprovalSnapshot(
                state,
                authority,
                FailureCode: failureCode),
        };
        var executor = ExactApprovalExecutor(
            new CapabilityGeneratingReplyExecutor(), approvalPort);

        var execution = await executor.ExecuteAsync(
            ExactApprovalContinuation(authority, approved: true),
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        approvalPort.RedeemAuthorities.Should().BeEmpty();
        execution.Result.Tool.Receipt.Status.Should().NotBe(AgentToolReceiptStatus.Success);
        execution.Result.Tool.ExternalEffect.Should().Be(
            state is NyxIdExactServiceApprovalState.Redeeming or
                NyxIdExactServiceApprovalState.Failed
                ? NyxIdChatEffectEvidence.MayHaveChanged
                : NyxIdChatEffectEvidence.NotApplied);
    }

    [Fact]
    public async Task ExactCreateRecovery_AfterCompletionLoss_ShouldRecoverSameRequestIdentity()
    {
        var authority = ExactApprovalAuthority();
        var approvalPort = new RecordingExactServiceApprovalPort
        {
            CreateResult = new NyxIdExactServiceApprovalCreateResult(
                NyxIdExactServiceApprovalCreateDisposition.Created,
                new NyxIdExactServiceApprovalSnapshot(
                    NyxIdExactServiceApprovalState.Pending,
                    authority)),
        };
        var command = ExactServiceToolCommand(new NyxIdChatToolCall
        {
            CallId = "call-alpha",
            ToolName = "tool-alpha",
            ArgumentsJson = "{\"value\":1}",
            OperationAdmission = ExactWriteAdmission(),
        });
        var credentials = RecoveryToolContext("user-token").Credentials;

        await approvalPort.CreateAsync(
            "user-token",
            AgentToolOperationAdmissionPayloadMapper.FromPayload(ExactWriteAdmission())!,
            JsonNode.Parse(command.Tool.ArgumentsJson)!,
            command.Key.OperationId,
            command.Key.OperationGeneration,
            command.Tool.IdempotencyKey,
            CancellationToken.None);

        var (vault, input) = await ExactRecoveryInputAsync(
            command,
            credentials,
            NyxIdChatExactServiceRecoveryStage.Create);
        var reconciliation = ExactReconciliationPort(vault, approvalPort);

        var result = await reconciliation.ReconcileAsync(input, CancellationToken.None);

        approvalPort.CreateInputs.Should().HaveCount(2);
        approvalPort.CreateInputs[1].AccessToken.Should().Be(
            approvalPort.CreateInputs[0].AccessToken);
        approvalPort.CreateInputs[1].Admission.Should().BeEquivalentTo(
            approvalPort.CreateInputs[0].Admission);
        approvalPort.CreateInputs[1].Arguments.ToJsonString().Should().Be(
            approvalPort.CreateInputs[0].Arguments.ToJsonString());
        approvalPort.CreateInputs[1].OperationId.Should().Be(
            approvalPort.CreateInputs[0].OperationId);
        approvalPort.CreateInputs[1].OperationGeneration.Should().Be(
            approvalPort.CreateInputs[0].OperationGeneration);
        approvalPort.CreateInputs[1].IdempotencyKey.Should().Be(
            approvalPort.CreateInputs[0].IdempotencyKey);
        result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.ApprovalRequired);
        result.Tool.Receipt.ExactServiceApproval.Should().BeEquivalentTo(authority);
        await AssertRecoveryCredentialRevokedAsync(vault, input);
    }

    [Theory]
    [InlineData(NyxIdExactServiceApprovalCreateDisposition.TierAUnavailable)]
    [InlineData(NyxIdExactServiceApprovalCreateDisposition.ApprovalNotRequired)]
    public async Task ExactCreateRecovery_WhenTierAIsUnavailable_ShouldStayUncertainWithoutProviderFallback(
        NyxIdExactServiceApprovalCreateDisposition disposition)
    {
        var approvalPort = new RecordingExactServiceApprovalPort
        {
            CreateResult = new NyxIdExactServiceApprovalCreateResult(disposition),
        };
        var command = ExactServiceToolCommand(new NyxIdChatToolCall
        {
            CallId = "call-alpha",
            ToolName = "tool-alpha",
            ArgumentsJson = "{\"value\":1}",
            OperationAdmission = ExactWriteAdmission(),
        });
        var (vault, input) = await ExactRecoveryInputAsync(
            command,
            RecoveryToolContext("user-token").Credentials,
            NyxIdChatExactServiceRecoveryStage.Create);

        var result = await ExactReconciliationPort(vault, approvalPort)
            .ReconcileAsync(input, CancellationToken.None);

        approvalPort.CreateInputs.Should().ContainSingle();
        result.Failure.FailureCode.Should().Be(
            UnavailableNyxIdChatTurnOperationReconciliationPort.OutcomeUncertainCode);
        result.Failure.ExternalEffect.Should().Be(
            NyxIdChatEffectEvidence.MayHaveChanged);
        await AssertRecoveryCredentialActiveAsync(vault, input);
    }

    [Fact]
    public async Task ExactRedeemRecovery_AfterCompletionLoss_ShouldReplayStoredReceiptWithoutReinvocation()
    {
        var authority = ExactApprovalAuthority();
        var receipt = new NyxIdExactServiceApprovalReceipt(
            201,
            "{\"id\":\"message-alpha\"}",
            "sha256:response");
        var redeemed = new NyxIdExactServiceApprovalSnapshot(
            NyxIdExactServiceApprovalState.Redeemed,
            authority,
            receipt);
        var approvalPort = new RecordingExactServiceApprovalPort
        {
            ObserveResult = redeemed,
            RedeemResult = redeemed,
        };
        var command = ExactApprovalContinuation(authority, approved: true);
        var credentials = RecoveryToolContext("fresh-user-token").Credentials;

        await approvalPort.RedeemAsync(
            credentials.NyxIdAccessToken,
            authority,
            CancellationToken.None);

        var (vault, input) = await ExactRecoveryInputAsync(
            command,
            credentials,
            NyxIdChatExactServiceRecoveryStage.DecideRedeem);
        var reconciliation = ExactReconciliationPort(vault, approvalPort);

        var result = await reconciliation.ReconcileAsync(input, CancellationToken.None);

        approvalPort.ObservedAuthorities.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(authority);
        approvalPort.RedeemAuthorities.Should().ContainSingle(
            "the completed redemption must not invoke the provider a second time");
        result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success);
        result.Tool.ResultJson.Should().Be(receipt.ResponseBody);
        result.Tool.Receipt.ResultJson.Should().Be(receipt.ResponseBody);
        await AssertRecoveryCredentialRevokedAsync(vault, input);
    }

    [Fact]
    public async Task ExactDecisionRecovery_WhenPending_ShouldReplayDecisionThenRedeem()
    {
        var authority = ExactApprovalAuthority();
        var receipt = new NyxIdExactServiceApprovalReceipt(
            201,
            "{\"id\":\"message-alpha\"}",
            "sha256:response");
        var approvalPort = new RecordingExactServiceApprovalPort
        {
            ObserveResult = new NyxIdExactServiceApprovalSnapshot(
                NyxIdExactServiceApprovalState.Pending,
                authority),
            DecideResult = new NyxIdExactServiceApprovalSnapshot(
                NyxIdExactServiceApprovalState.Approved,
                authority),
            RedeemResult = new NyxIdExactServiceApprovalSnapshot(
                NyxIdExactServiceApprovalState.Redeemed,
                authority,
                receipt),
        };
        var command = ExactApprovalContinuation(authority, approved: true);
        var (vault, input) = await ExactRecoveryInputAsync(
            command,
            RecoveryToolContext("fresh-user-token").Credentials,
            NyxIdChatExactServiceRecoveryStage.DecideRedeem);

        var result = await ExactReconciliationPort(vault, approvalPort)
            .ReconcileAsync(input, CancellationToken.None);

        approvalPort.ObservedAuthorities.Should().ContainSingle();
        approvalPort.Decisions.Should().ContainSingle().Which.Should().BeTrue();
        approvalPort.RedeemAuthorities.Should().ContainSingle();
        result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success);
        result.Tool.ResultJson.Should().Be(receipt.ResponseBody);
        await AssertRecoveryCredentialRevokedAsync(vault, input);
    }

    [Fact]
    public void RecoverySecretCodec_ShouldReadLegacyCredentialPayload()
    {
        var credentials = RecoveryToolContext("legacy-token").Credentials;
        credentials.NyxIdOrgToken = "legacy-org-token";
        credentials.SenderNyxIdAccessToken = "legacy-sender-token";
        var encoded = Convert.ToBase64String(credentials.ToByteArray());

        NyxIdChatRecoverySecretPayloadCodec.TryDecode(encoded, out var decoded)
            .Should().BeTrue();

        decoded.IsWrapped.Should().BeFalse();
        decoded.ExactServiceCommand.Should().BeNull();
        decoded.Credentials.Should().BeEquivalentTo(credentials);
    }

    [Fact]
    public void RecoverySecretCodec_WhenCredentialRotates_ShouldPreserveFrozenCommand()
    {
        var command = ExactApprovalContinuation(ExactApprovalAuthority(), approved: true);
        command.ToolApprovalContinuation.ToolContext = null;
        var original = RecoveryToolContext("original-token").Credentials;
        var refreshed = RecoveryToolContext("refreshed-token").Credentials;
        NyxIdChatRecoverySecretPayloadCodec.TryDecode(
                NyxIdChatRecoverySecretPayloadCodec.Encode(original, command),
                out var decoded)
            .Should().BeTrue();

        NyxIdChatRecoverySecretPayloadCodec.TryDecode(
                NyxIdChatRecoverySecretPayloadCodec.Encode(decoded, refreshed),
                out var rotated)
            .Should().BeTrue();

        rotated.IsWrapped.Should().BeTrue();
        rotated.Credentials.NyxIdAccessToken.Should().Be("refreshed-token");
        rotated.ExactServiceCommand.Should().BeEquivalentTo(command);
        rotated.ExactServiceCommand!.ToolApprovalContinuation.ToolContext.Should().BeNull();
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    public void RecoverySecretCodec_WhenWrapperVersionIsUnsupported_ShouldFailClosed(
        uint schemaVersion)
    {
        var payload = new NyxIdChatRecoverySecretPayload
        {
            Format = NyxIdChatRecoverySecretPayloadCodec.FormatMarker,
            SchemaVersion = schemaVersion,
            Credentials = RecoveryToolContext("user-token").Credentials,
            ExactServiceCommand = ExactApprovalContinuation(
                ExactApprovalAuthority(),
                approved: true),
        };

        NyxIdChatRecoverySecretPayloadCodec.TryDecode(
                Convert.ToBase64String(payload.ToByteArray()),
                out _)
            .Should().BeFalse();
    }

    [Fact]
    public void RecoverySecretCodec_WhenWrapperFormatIsUnknown_ShouldFailClosed()
    {
        var payload = new NyxIdChatRecoverySecretPayload
        {
            Format = "unknown-recovery-format",
            SchemaVersion = NyxIdChatRecoverySecretPayloadCodec.CurrentSchemaVersion,
            Credentials = RecoveryToolContext("user-token").Credentials,
            ExactServiceCommand = ExactApprovalContinuation(
                ExactApprovalAuthority(),
                approved: true),
        };

        NyxIdChatRecoverySecretPayloadCodec.TryDecode(
                Convert.ToBase64String(payload.ToByteArray()),
                out _)
            .Should().BeFalse();
    }

    [Fact]
    public void RecoverySecretCodec_WhenWrapperIsIncomplete_ShouldFailClosed()
    {
        var payload = new NyxIdChatRecoverySecretPayload
        {
            Format = NyxIdChatRecoverySecretPayloadCodec.FormatMarker,
            SchemaVersion = NyxIdChatRecoverySecretPayloadCodec.CurrentSchemaVersion,
            Credentials = RecoveryToolContext("user-token").Credentials,
        };

        NyxIdChatRecoverySecretPayloadCodec.TryDecode(
                Convert.ToBase64String(payload.ToByteArray()),
                out _)
            .Should().BeFalse();
    }

    [Fact]
    public async Task AutoGateExactCreate_WithoutCommandToolContext_ShouldSealTransientSessionContext()
    {
        var clock = new FixedTimeProvider(
            new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero));
        var vault = new InMemorySecretVault(clock);
        var eventStore = new InMemoryEventStoreForTests();
        var executor = new RecordingOperationExecutor(command => new NyxIdChatOperationResultSignal
        {
            Key = command.Key.Clone(),
        });
        var operationDispatch = new RecordingOperationDispatchPort(executor)
        {
            CapturedToolContext = RecoveryToolContext("captured-user-token"),
        };
        using var services = BuildEventSourcingServices(eventStore);
        var agent = CreateAgent(
            services,
            operationDispatch,
            new RecordingDispatchPort(),
            timeProvider: clock,
            secretVault: vault);
        await agent.ActivateAsync();
        var command = ExactServiceToolCommand(new NyxIdChatToolCall
        {
            CallId = "call-alpha",
            ToolName = "tool-alpha",
            ArgumentsJson = "{\"value\":1}",
            OperationAdmission = ExactWriteAdmission(),
        });

        await agent.HandleOperationAsync(command);

        agent.State.ExactServiceRecoveryStage.Should().Be(
            NyxIdChatExactServiceRecoveryStage.Create);
        agent.State.RecoveryCredential.Should().NotBeNull();
        executor.Commands.Should().ContainSingle();
        var credential = agent.State.RecoveryCredential;
        var resolved = await vault.ResolveAsync(new ResolveSecretRequest(
            credential.Ref,
            credential.Purpose,
            credential.OwnerScopeKey,
            credential.SubjectId,
            "inspect exact-service recovery seal"));
        NyxIdChatRecoverySecretPayloadCodec.TryDecode(resolved.Secret, out var decoded)
            .Should().BeTrue();
        decoded.IsWrapped.Should().BeTrue();
        decoded.Credentials.NyxIdAccessToken.Should().Be("captured-user-token");
        decoded.ExactServiceCommand.Should().NotBeNull();
        decoded.ExactServiceCommand!.Tool.ToolContext.Should().BeNull();
        decoded.ExactServiceCommand.Key.Should().BeEquivalentTo(command.Key);
    }

    private static AdmittedNyxIdChatTurnOperationReconciliationPort ExactReconciliationPort(
        ISecretVault vault,
        INyxIdExactServiceApprovalPort approvalPort) => new(
        new NyxIdChatToolVerificationPort(),
        vault,
        new NyxIdChatDelegationCredentialLifecyclePort(TimeProvider.System),
        approvalPort,
        NullLogger<AdmittedNyxIdChatTurnOperationReconciliationPort>.Instance);

    private static async Task<(InMemorySecretVault Vault,
        NyxIdChatTurnOperationReconciliationInput Input)> ExactRecoveryInputAsync(
        NyxIdChatOperationDispatchCommand command,
        AgentToolCredentialsPayload credentials,
        NyxIdChatExactServiceRecoveryStage stage)
    {
        var context = RecoveryToolContext(credentials.NyxIdAccessToken);
        var frozen = command.Clone();
        if (frozen.Tool is not null)
            frozen.Tool.ToolContext = null;
        if (frozen.ToolApprovalContinuation is not null)
            frozen.ToolApprovalContinuation.ToolContext = null;
        var vault = new InMemorySecretVault(TimeProvider.System);
        var ownerScopeKey = $"nyxid-chat:{command.Key.ConversationActorId}";
        var ownerSubject = context.Caller.OwnerSubject;
        var stored = await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.NyxIdChatRecoveryCredential,
            ownerScopeKey,
            ownerSubject,
            NyxIdChatRecoverySecretPayloadCodec.Encode(credentials, frozen),
            "test exact-service recovery",
            DateTimeOffset.UtcNow.AddMinutes(30)));
        return (vault, new NyxIdChatTurnOperationReconciliationInput
        {
            Key = command.Key.Clone(),
            RecoveryContext = AgentToolExecutionContextMapper.ToRecoveryPayload(
                AgentToolExecutionContextMapper.FromPayload(context)),
            RecoveryCredential = new DurableCallerCredentialRef
            {
                Ref = stored.Reference.Ref,
                Purpose = CredentialSecretPurposes.NyxIdChatRecoveryCredential,
                OwnerScopeKey = ownerScopeKey,
                SubjectId = ownerSubject,
                SourceKind = DurableCallerCredentialSourceKind.NyxIdChat,
            },
            ExactServiceRecoveryStage = stage,
        });
    }

    private static async Task AssertRecoveryCredentialRevokedAsync(
        InMemorySecretVault vault,
        NyxIdChatTurnOperationReconciliationInput input)
    {
        var resolved = await ResolveRecoveryCredentialAsync(vault, input);
        resolved.Resolved.Should().BeFalse();
        resolved.FailureReason.Should().Be(SecretResolutionFailureReason.Revoked);
    }

    private static async Task AssertRecoveryCredentialActiveAsync(
        InMemorySecretVault vault,
        NyxIdChatTurnOperationReconciliationInput input)
    {
        var resolved = await ResolveRecoveryCredentialAsync(vault, input);
        resolved.Resolved.Should().BeTrue();
    }

    private static Task<ResolveSecretResult> ResolveRecoveryCredentialAsync(
        InMemorySecretVault vault,
        NyxIdChatTurnOperationReconciliationInput input) =>
        vault.ResolveAsync(new ResolveSecretRequest(
            input.RecoveryCredential.Ref,
            input.RecoveryCredential.Purpose,
            input.RecoveryCredential.OwnerScopeKey,
            input.RecoveryCredential.SubjectId,
            "assert exact-service recovery credential lifecycle"));

    private static AgentToolExecutionContextPayload RecoveryToolContext(string token) => new()
    {
        Caller = new AgentToolCallerContextPayload
        {
            OwnerSubject = "owner-alpha",
            ScopeId = "scope-alpha",
        },
        Credentials = new AgentToolCredentialsPayload
        {
            NyxIdAccessToken = token,
            NyxIdCredentialKind = AgentToolNyxIdCredentialKindPayload.SourceReadableUserBearer,
        },
    };

    private static NyxIdChatTurnOperationExecutor ExactApprovalExecutor(
        IAgentRunReplyGenerationExecutorPort generation,
        INyxIdExactServiceApprovalPort approvalPort) => new(
        generation,
        new UnavailableNyxIdActionPostconditionPort(),
        null,
        new NyxIdChatDelegationCredentialLifecyclePort(TimeProvider.System),
        new NyxIdChatToolVerificationPort(),
        approvalPort,
        NullLogger<NyxIdChatTurnOperationExecutor>.Instance);

    private static async Task<(
        NyxIdChatTransientExecutionSession Session,
        NyxIdChatToolCall Call)> PrepareExactServiceCallAsync(
        NyxIdChatTurnOperationExecutor executor)
    {
        var session = new NyxIdChatTransientExecutionSession();
        var execution = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = CreateKey(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    Request = new ChatRequestEvent
                    {
                        Prompt = "perform the exact connected-service effect",
                        SessionId = "turn-alpha",
                        ToolContext = FreshToolContext("user-token"),
                    },
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        return (session, execution.Result.Llm.ToolCalls.Should().ContainSingle().Which);
    }

    private static NyxIdChatOperationDispatchCommand ExactServiceToolCommand(
        NyxIdChatToolCall call) => new()
    {
        Key = CreateKey("step-tool-alpha", "operation-tool-alpha", 1),
        Tool = new NyxIdChatToolOperationInput
        {
            CallId = call.CallId,
            ToolName = call.ToolName,
            ArgumentsJson = call.ArgumentsJson,
            MayChangeExternalState = true,
            Idempotent = false,
            IdempotencyKey = "operation-tool-alpha",
            OperationAdmission = call.OperationAdmission.Clone(),
        },
    };

    private static NyxIdChatOperationDispatchCommand ExactApprovalContinuation(
        NyxIdExactServiceApprovalAuthority authority,
        bool approved) => new()
    {
        Key = CreateKey("step-tool-alpha", "operation-tool-alpha", 2),
        ToolApprovalContinuation = new NyxIdChatToolApprovalContinuationInput
        {
            ApprovalRequestId = authority.RequestId,
            Approved = approved,
            ToolContext = FreshToolContext("fresh-user-token"),
            MayChangeExternalState = true,
            IdempotencyKey = authority.IdempotencyKey,
            ExactServiceApproval = authority.Clone(),
            ToolCallId = "call-alpha",
            ToolName = "tool-alpha",
        },
    };

    private static NyxIdExactServiceApprovalAuthority ExactApprovalAuthority() => new()
    {
        RequestId = "request-alpha",
        UserServiceId = "connected-service-alpha",
        EndpointId = "operation.write",
        CatalogDigest = $"sha256:{new string('a', 64)}",
        EndpointContractDigest = $"sha256:{new string('b', 64)}",
        OperationDigest = $"sha256:{new string('c', 64)}",
        OperationId = "operation-tool-alpha",
        OperationGeneration = 1,
        IdempotencyKey = "operation-tool-alpha",
        ExpiresAt = Timestamp.FromDateTimeOffset(
            new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero)),
    };

    private sealed record ExactApprovalCreateInput(
        string AccessToken,
        AgentToolOperationAdmission Admission,
        JsonNode Arguments,
        string OperationId,
        long OperationGeneration,
        string IdempotencyKey);

    private sealed class RecordingExactServiceApprovalPort : INyxIdExactServiceApprovalPort
    {
        public NyxIdExactServiceApprovalCreateResult CreateResult { get; set; } = new(
            NyxIdExactServiceApprovalCreateDisposition.Rejected);

        public NyxIdExactServiceApprovalSnapshot? DecideResult { get; set; }

        public NyxIdExactServiceApprovalSnapshot? RedeemResult { get; set; }

        public NyxIdExactServiceApprovalSnapshot? ObserveResult { get; set; }

        public List<ExactApprovalCreateInput> CreateInputs { get; } = [];

        public List<bool> Decisions { get; } = [];

        public List<NyxIdExactServiceApprovalAuthority> DecisionAuthorities { get; } = [];

        public List<NyxIdExactServiceApprovalAuthority> RedeemAuthorities { get; } = [];

        public List<NyxIdExactServiceApprovalAuthority> ObservedAuthorities { get; } = [];

        public Task<NyxIdExactServiceApprovalCreateResult> CreateAsync(
            string accessToken,
            AgentToolOperationAdmission admission,
            JsonNode arguments,
            string operationId,
            long operationGeneration,
            string idempotencyKey,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            CreateInputs.Add(new ExactApprovalCreateInput(
                accessToken,
                admission,
                arguments.DeepClone(),
                operationId,
                operationGeneration,
                idempotencyKey));
            return Task.FromResult(CreateResult);
        }

        public Task<NyxIdExactServiceApprovalSnapshot> ObserveAsync(
            string accessToken,
            NyxIdExactServiceApprovalAuthority authority,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            accessToken.Should().Be("fresh-user-token");
            ObservedAuthorities.Add(authority.Clone());
            return Task.FromResult(ObserveResult ?? throw new InvalidOperationException(
                "An observation result was not configured."));
        }

        public Task<NyxIdExactServiceApprovalSnapshot> DecideAsync(
            string accessToken,
            NyxIdExactServiceApprovalAuthority authority,
            bool approved,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            accessToken.Should().Be("fresh-user-token");
            Decisions.Add(approved);
            DecisionAuthorities.Add(authority.Clone());
            return Task.FromResult(DecideResult ?? throw new InvalidOperationException(
                "A decision result was not configured."));
        }

        public Task<NyxIdExactServiceApprovalSnapshot> RedeemAsync(
            string accessToken,
            NyxIdExactServiceApprovalAuthority authority,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            accessToken.Should().Be("fresh-user-token");
            RedeemAuthorities.Add(authority.Clone());
            return Task.FromResult(RedeemResult ?? throw new InvalidOperationException(
                "A redemption result was not configured."));
        }
    }
}
