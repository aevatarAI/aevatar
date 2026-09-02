using Aevatar.AI.Abstractions;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatTaskContractTests
{
    [Fact]
    public void ConversationState_ShouldRoundTripDistinctTaskControlAndActionIdentities()
    {
        var operationKey = new NyxIdChatOperationKey
        {
            ConversationActorId = "conversation-alpha",
            TurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = "step-alpha",
            OperationId = "operation-alpha",
            OperationGeneration = 7,
        };
        var state = new NyxIdChatConversationGAgentState
        {
            ConversationActorId = "conversation-alpha",
            ScopeId = "scope-alpha",
            RoleConfiguration = new AIAgentConfigOverrides
            {
                Model = "model-alpha",
                MaxToolRounds = 4,
            },
            AgentProfile = new AgentProfileSnapshot
            {
                ProfileId = "profile-alpha",
                ProfileVersion = "profile-version-alpha",
            },
            ActiveTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                ClientRequestId = "client-alpha",
                Status = NyxIdChatTurnStatus.Active,
            },
            LatestTurn = new NyxIdChatTurnState
            {
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                ClientRequestId = "client-alpha",
                Status = NyxIdChatTurnStatus.Active,
            },
            ActiveTask = new NyxIdChatTaskState
            {
                TaskId = "task-alpha",
                TurnId = "turn-alpha",
                Status = NyxIdChatTaskStatus.Active,
                ActiveStepId = "step-alpha",
            },
            PendingApproval = new NyxIdChatPendingApprovalState
            {
                ApprovalRequestId = "approval-alpha",
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                StepId = "step-alpha",
                ToolName = "tool-alpha",
            },
            ControlFence = new NyxIdChatControlFenceState
            {
                Kind = NyxIdChatControlKind.Stop,
                RequestId = "stop-alpha",
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                OperationGeneration = 7,
            },
            ContinuationAdmission = new NyxIdChatContinuationAdmissionState
            {
                Kind = NyxIdChatContinuationKind.Steering,
                RequestId = "steering-alpha",
                OriginTurnId = "turn-alpha",
                ContinuationTurnId = "turn-beta",
                Status = NyxIdChatContinuationAdmissionStatus.Accepted,
            },
            ProgressSequence = 19,
        };
        state.ActiveTask.Steps.Add(new NyxIdChatTaskStepState
        {
            StepId = "step-alpha",
            Order = 1,
            Kind = NyxIdChatStepKind.Tool,
            Status = NyxIdChatStepStatus.Running,
            Required = true,
            Description = "Call the exact connected service",
            MayChangeExternalState = true,
            ExternalEffect = NyxIdChatEffectEvidence.NotStarted,
            Operation = new NyxIdChatOperationState
            {
                Key = operationKey,
                Kind = NyxIdChatStepKind.Tool,
                Phase = NyxIdChatOperationPhase.Running,
                MayChangeExternalState = true,
            },
            Source = new NyxIdChatStepSource
            {
                Tool = new NyxIdChatToolStepSource
                {
                    ToolName = "tool-alpha",
                },
            },
        });
        state.PendingActions.Add(new NyxIdChatActionRequestState
        {
            SchemaVersion = 4,
            ConversationActorId = "conversation-alpha",
            OriginTurnId = "turn-alpha",
            TaskId = "task-alpha",
            StepId = "step-alpha",
            ActionRequestId = "action-alpha",
            Action = NyxIdAssistantActionKind.ServiceConnect,
            Params = new NyxIdAssistantActionParams
            {
                CatalogServiceConnect = new NyxIdCatalogServiceConnectParams
                {
                    ServiceSlug = "api-github",
                    RequestedScopes = { "repo" },
                },
            },
        });
        state.RecentTerminalTurns.Add(new NyxIdChatTurnSummary
        {
            TurnId = "turn-terminal-alpha",
            TaskId = "task-terminal-alpha",
            Status = NyxIdChatTurnStatus.Failed,
            FailureCode = "TOOL_FAILED",
        });

        var roundTripped = NyxIdChatConversationGAgentState.Parser.ParseFrom(state.ToByteArray());

        roundTripped.Should().BeEquivalentTo(state);
        roundTripped.ActiveTask.Steps.Single().Operation.Key.Should().BeEquivalentTo(operationKey);
        roundTripped.PendingActions.Single().Params.ParamsCase.Should()
            .Be(NyxIdAssistantActionParams.ParamsOneofCase.CatalogServiceConnect);
        roundTripped.ConversationActorId.Should().NotBe(roundTripped.ActiveTurn.TurnId);
        roundTripped.ActiveTurn.TurnId.Should().NotBe(roundTripped.ActiveTask.TaskId);
        roundTripped.ActiveTask.TaskId.Should().NotBe(roundTripped.ActiveTask.Steps.Single().StepId);
        roundTripped.ActiveTask.Steps.Single().StepId.Should()
            .NotBe(roundTripped.ActiveTask.Steps.Single().Operation.Key.OperationId);
        roundTripped.PendingActions.Single().ActionRequestId.Should()
            .NotBe(roundTripped.ActiveTask.Steps.Single().Operation.Key.OperationId);
    }

    [Fact]
    public void LifecycleEnums_ShouldExposeOnlyClosedTypedStates()
    {
        Enum.GetValues<NyxIdChatTaskStatus>().Should().Equal(
            NyxIdChatTaskStatus.Unspecified,
            NyxIdChatTaskStatus.Active,
            NyxIdChatTaskStatus.Succeeded,
            NyxIdChatTaskStatus.Failed,
            NyxIdChatTaskStatus.Stopped,
            NyxIdChatTaskStatus.Blocked);
        Enum.GetValues<NyxIdChatStepStatus>().Should().Equal(
            NyxIdChatStepStatus.Unspecified,
            NyxIdChatStepStatus.Planned,
            NyxIdChatStepStatus.Waiting,
            NyxIdChatStepStatus.Running,
            NyxIdChatStepStatus.Done,
            NyxIdChatStepStatus.Failed,
            NyxIdChatStepStatus.Skipped,
            NyxIdChatStepStatus.Cancelled,
            NyxIdChatStepStatus.Uncertain);
        Enum.GetValues<NyxIdChatEffectEvidence>().Should().Equal(
            NyxIdChatEffectEvidence.Unspecified,
            NyxIdChatEffectEvidence.NotStarted,
            NyxIdChatEffectEvidence.NotApplied,
            NyxIdChatEffectEvidence.Confirmed,
            NyxIdChatEffectEvidence.MayHaveChanged);
        Enum.GetValues<NyxIdChatActionDisposition>().Should().Equal(
            NyxIdChatActionDisposition.Unspecified,
            NyxIdChatActionDisposition.Completed,
            NyxIdChatActionDisposition.Declined,
            NyxIdChatActionDisposition.Failed,
            NyxIdChatActionDisposition.Cancelled,
            NyxIdChatActionDisposition.Expired);

        AssertEnumField<NyxIdChatTaskState>("status", nameof(NyxIdChatTaskStatus));
        AssertEnumField<NyxIdChatTaskStepState>("status", nameof(NyxIdChatStepStatus));
        AssertEnumField<NyxIdChatTaskStepState>("external_effect", nameof(NyxIdChatEffectEvidence));
        AssertEnumField<NyxIdChatActionReport>("disposition", nameof(NyxIdChatActionDisposition));
    }

    [Fact]
    public void OperationSignalsAndResourceReferences_ShouldUseTypedOneofs()
    {
        var signal = new NyxIdChatOperationResultSignal
        {
            Key = new NyxIdChatOperationKey
            {
                ConversationActorId = "conversation-alpha",
                TurnId = "turn-alpha",
                TaskId = "task-alpha",
                StepId = "step-alpha",
                OperationId = "operation-alpha",
                OperationGeneration = 1,
            },
            ActionPostcondition = new NyxIdChatActionPostconditionResult
            {
                ActionRequestId = "action-alpha",
                Disposition = NyxIdChatActionDisposition.Completed,
                Verified = true,
                Resource = new NyxIdChatSafeResourceRef
                {
                    UserService = new NyxIdChatUserServiceRef
                    {
                        UserServiceId = "service-alpha",
                    },
                },
            },
        };

        var roundTripped = NyxIdChatOperationResultSignal.Parser.ParseFrom(signal.ToByteArray());

        roundTripped.ResultCase.Should()
            .Be(NyxIdChatOperationResultSignal.ResultOneofCase.ActionPostcondition);
        roundTripped.ActionPostcondition.Resource.ResourceCase.Should()
            .Be(NyxIdChatSafeResourceRef.ResourceOneofCase.UserService);
        NyxIdChatOperationResultSignal.Descriptor.Oneofs.Should().ContainSingle();
        NyxIdChatSafeResourceRef.Descriptor.Oneofs.Should().ContainSingle();
    }

    [Fact]
    public void DurableContracts_ShouldNotExposeSecretOrGenericBagFields()
    {
        var descriptors = NyxidChatTaskReflection.Descriptor.MessageTypes
            .SelectMany(Flatten)
            .ToArray();
        var forbiddenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "metadata",
            "headers",
            "items",
            "access_token",
            "refresh_token",
            "authorization",
            "cookie",
            "client_secret",
            "user_code",
            "raw_body",
            "raw_upstream_body",
        };

        descriptors
            .SelectMany(static descriptor => descriptor.Fields.InFieldNumberOrder())
            .Where(field => forbiddenNames.Contains(field.Name))
            .Should()
            .BeEmpty();
        descriptors.Should().NotContain(static descriptor =>
            descriptor.Name.Contains("Metadata", StringComparison.Ordinal));
    }

    [Fact]
    public void BrowserActionContracts_ShouldPersistReportsAndTypedPostconditionCorrelation()
    {
        NyxIdChatActionRequestState.Descriptor.FindFieldByName("reports")
            .Should().NotBeNull();
        NyxIdChatActionRequestState.Descriptor.FindFieldByName("postcondition_result")
            .Should().NotBeNull();
        NyxIdChatActionRequestedEvent.Descriptor.FindFieldByName("state")
            .Should().NotBeNull();
        NyxIdChatActionPostconditionInput.Descriptor.FindFieldByName("scope_id")
            .Should().NotBeNull();
        NyxIdChatActionPostconditionInput.Descriptor.FindFieldByName("owner_subject")
            .Should().NotBeNull();
        NyxIdChatActionPostconditionInput.Descriptor.FindFieldByName("origin_turn_id")
            .Should().NotBeNull();
        NyxIdChatActionPostconditionInput.Descriptor.FindFieldByName("reported_disposition")
            .Should().NotBeNull();
        NyxIdChatActionPostconditionInput.Descriptor.FindFieldByName("params")
            .Should().NotBeNull();
        NyxIdChatConversationGAgentState.Descriptor.FindFieldByName("recent_actions")
            .Should().NotBeNull();
        NyxIdChatActionContinueCommand.Descriptor.FindFieldByName("continuation_turn_id")
            .Should().NotBeNull();
        NyxIdChatActionContinueCommand.Descriptor.FindFieldByName("owner_subject")
            .Should().NotBeNull();
    }

    private static void AssertEnumField<TMessage>(string name, string enumName)
        where TMessage : IMessage<TMessage>
    {
        var messageDescriptor = (MessageDescriptor)typeof(TMessage)
            .GetProperty("Descriptor")!
            .GetValue(null)!;
        var field = messageDescriptor.FindFieldByName(name);

        field.Should().NotBeNull();
        field!.FieldType.Should().Be(FieldType.Enum);
        field.EnumType.Name.Should().Be(enumName);
    }

    private static IEnumerable<MessageDescriptor> Flatten(MessageDescriptor descriptor)
    {
        yield return descriptor;
        foreach (var nested in descriptor.NestedTypes.SelectMany(Flatten))
            yield return nested;
    }
}
