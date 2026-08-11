using System.Text.Json.Nodes;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.ExactServiceApprovals;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
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

    [Fact]
    public async Task ExactConnectedServiceEffect_WhenTierAIsUnavailable_ShouldUseTierBFallback()
    {
        var approvalPort = new RecordingExactServiceApprovalPort
        {
            CreateResult = new NyxIdExactServiceApprovalCreateResult(
                NyxIdExactServiceApprovalCreateDisposition.TierAUnavailable),
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
            state == NyxIdExactServiceApprovalState.Redeeming
                ? NyxIdChatEffectEvidence.MayHaveChanged
                : NyxIdChatEffectEvidence.NotApplied);
    }

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

        public List<ExactApprovalCreateInput> CreateInputs { get; } = [];

        public List<bool> Decisions { get; } = [];

        public List<NyxIdExactServiceApprovalAuthority> DecisionAuthorities { get; } = [];

        public List<NyxIdExactServiceApprovalAuthority> RedeemAuthorities { get; } = [];

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
            CancellationToken ct) => throw new NotSupportedException();

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
