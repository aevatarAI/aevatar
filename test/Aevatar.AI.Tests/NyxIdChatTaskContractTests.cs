using System.Diagnostics;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions.Tools;
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
                SchemaVersion = 4,
                ActorId = "conversation-alpha",
                PlanId = "plan-alpha",
                PlanRevision = 2,
                PlanRevisionHistoryStart = 1,
                Title = "Update GitHub safely",
                PlanRevisions =
                {
                    new NyxIdChatPlanRevisionRecord
                    {
                        PlanRevision = 1,
                        RevisionCause = NyxIdChatPlanRevisionCause.Initial,
                    },
                    new NyxIdChatPlanRevisionRecord
                    {
                        PlanRevision = 2,
                        RevisionCause = NyxIdChatPlanRevisionCause.ScopeResolution,
                        AddedStepIds = { "step-alpha" },
                    },
                },
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
            AddedBy = NyxIdChatStepAddedBy.Replan,
            AddedInPlanRevision = 2,
            DependsOn = { "step-plan" },
            Estimate = new NyxIdChatStepEstimate
            {
                Kind = NyxIdChatStepEstimateKind.Duration,
                Seconds = 20,
            },
            Substeps =
            {
                new NyxIdChatSubstepState
                {
                    SubstepId = "substep-alpha",
                    Title = "Validate repository",
                    Status = NyxIdChatSubstepStatus.Done,
                },
            },
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
        roundTripped.ActiveTask.PlanId.Should().Be("plan-alpha");
        roundTripped.ActiveTask.PlanRevision.Should().Be(2);
        roundTripped.ActiveTask.PlanRevisionHistoryStart.Should().Be(1);
        roundTripped.ActiveTask.Steps.Single().AddedBy.Should().Be(NyxIdChatStepAddedBy.Replan);
        roundTripped.ActiveTask.Steps.Single().AddedInPlanRevision.Should().Be(2);
        roundTripped.ActiveTask.PlanRevisions.Select(static revision =>
                (revision.PlanRevision, revision.RevisionCause))
            .Should().Equal(
                (1, NyxIdChatPlanRevisionCause.Initial),
                (2, NyxIdChatPlanRevisionCause.ScopeResolution));
        roundTripped.ActiveTask.PlanRevisions[1].AddedStepIds.Should().Equal("step-alpha");
        roundTripped.ActiveTask.Steps.Single().DependsOn.Should().Equal("step-plan");
        roundTripped.ActiveTask.Steps.Single().Substeps.Should().ContainSingle().Which.Status
            .Should().Be(NyxIdChatSubstepStatus.Done);
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
        Enum.GetValues<NyxIdChatStepAddedBy>().Should().Equal(
            NyxIdChatStepAddedBy.Unspecified,
            NyxIdChatStepAddedBy.Initial,
            NyxIdChatStepAddedBy.Replan,
            NyxIdChatStepAddedBy.Steering);
        Enum.GetValues<NyxIdChatPlanRevisionCause>().Should().Equal(
            NyxIdChatPlanRevisionCause.Unspecified,
            NyxIdChatPlanRevisionCause.Initial,
            NyxIdChatPlanRevisionCause.ScopeResolution,
            NyxIdChatPlanRevisionCause.FailureRecovery,
            NyxIdChatPlanRevisionCause.Steering,
            NyxIdChatPlanRevisionCause.UserRevision);

        AssertEnumField<NyxIdChatTaskState>("status", nameof(NyxIdChatTaskStatus));
        AssertEnumField<NyxIdChatTaskStepState>("status", nameof(NyxIdChatStepStatus));
        AssertEnumField<NyxIdChatTaskStepState>("external_effect", nameof(NyxIdChatEffectEvidence));
        AssertEnumField<NyxIdChatTaskStepState>("added_by", nameof(NyxIdChatStepAddedBy));
        AssertEnumField<NyxIdChatPlanRevisionRecord>(
            "revision_cause",
            nameof(NyxIdChatPlanRevisionCause));
        AssertEnumField<NyxIdChatStepEstimate>("kind", nameof(NyxIdChatStepEstimateKind));
        AssertEnumField<NyxIdChatSubstepState>("status", nameof(NyxIdChatSubstepStatus));
        AssertEnumField<NyxIdChatTaskPlanStepChanged>(
            "change_kind",
            nameof(NyxIdChatStepChangeKind));
        AssertEnumField<NyxIdChatActionReport>("disposition", nameof(NyxIdChatActionDisposition));
    }

    [Fact]
    public void VerifiedAuthorizationContinuation_ShouldBeAClosedCredentialFreeContract()
    {
        var continuationField = NyxIdChatLLMOperationInput.Descriptor
            .FindFieldByName("verified_authorization_continuation");

        continuationField.Should().NotBeNull();
        continuationField!.MessageType.Fields.InFieldNumberOrder()
            .Select(static field => field.Name)
            .Should().Equal(
                "action_request_id",
                "origin_turn_id",
                "source_tool_step_id",
                "postcondition_step_id",
                "verified_resource",
                "service_slug",
                "verified_at",
                "resume_requirement",
                "authorization_readiness");
        continuationField.MessageType.Fields.InFieldNumberOrder()
            .Select(static field => field.Name)
            .Should().NotContain(name =>
                name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("metadata", StringComparison.OrdinalIgnoreCase));

        var readinessField = continuationField.MessageType.Fields
            .InFieldNumberOrder()
            .Single(field => field.Name == "authorization_readiness");
        readinessField.MessageType.Fields.InFieldNumberOrder()
            .Select(static field => field.Name)
            .Should().Equal("tool_name", "params");
        readinessField.MessageType.Fields.InFieldNumberOrder()
            .Single(field => field.Name == "params")
            .MessageType.Fields.InFieldNumberOrder()
            .Select(static field => field.Name)
            .Should().Equal(
                "service_slug",
                "requested_scopes",
                "service_label",
                "resource_uri");

        var stepRequirement = NyxIdChatLLMStepSource.Descriptor
            .FindFieldByName("resume_requirement");
        stepRequirement.Should().NotBeNull();
        stepRequirement!.FieldType.Should().Be(FieldType.Enum);
    }

    [Fact]
    public void PublicTaskPlan_ShouldExcludeExecutionOnlyActorState()
    {
        NyxIdChatTaskPlan.Descriptor.Fields.InFieldNumberOrder()
            .Select(static field => field.Name)
            .Should().NotContain("retry_input_rebuildable");
        NyxIdChatTaskPlanStep.Descriptor.Fields.InFieldNumberOrder()
            .Select(static field => field.Name)
            .Should().NotContain("retry_input_rebuildable");
        NyxIdChatTaskPlanOperation.Descriptor.Fields.InFieldNumberOrder()
            .Select(static field => field.Name)
            .Should().NotContain("idempotency_key");
        NyxIdChatTaskPlanStepChanged.Descriptor.FindFieldByName("step").MessageType
            .Should().BeSameAs(NyxIdChatTaskPlanStep.Descriptor);
        NyxIdChatTaskStepChanged.Descriptor.FindFieldByName("step").MessageType
            .Should().BeSameAs(
                NyxIdChatTaskStepState.Descriptor,
                "the legacy message type must retain its published wire layout");
        NyxIdChatOperationPhaseProgress.Descriptor.Fields.InFieldNumberOrder()
            .Select(static field => field.Name)
            .Should().Equal("substep_id", "title", "status");
        NyxIdChatOperationPhaseProgress.Descriptor.Fields.InFieldNumberOrder()
            .Select(static field => field.Name)
            .Should().NotContain([
                "operation_key",
                "external_effect",
                "available_actions",
                "substeps",
            ]);
    }

    [Theory]
    [InlineData(9_007_199_254_740_992, 0, "operationGeneration")]
    [InlineData(0, -9_007_199_254_740_992, "latestProgressSequence")]
    public void PublicTaskPlan_ShouldRejectBrowserUnsafeIntegers(
        long operationGeneration,
        long latestProgressSequence,
        string fieldName)
    {
        var plan = new NyxIdChatTaskPlan();
        plan.Steps.Add(new NyxIdChatTaskPlanStep
        {
            Operation = new NyxIdChatTaskPlanOperation
            {
                OperationGeneration = operationGeneration,
                LatestProgressSequence = latestProgressSequence,
            },
        });

        var act = () => NyxIdChatTaskPlanJsonFormatter.FormatTaskPlan(plan);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*'{fieldName}'*browser-safe integer range*");
    }

    [Fact]
    public void FormatterOutput_ShouldIncludeWireRequiredBooleansWhenFalse()
    {
        var step = new NyxIdChatTaskPlanStep
        {
            StepId = "step-optional-read",
            Required = false,
            MayChangeExternalState = false,
            Operation = new NyxIdChatTaskPlanOperation
            {
                OperationId = "operation-read",
                MayChangeExternalState = false,
            },
        };
        var plan = new NyxIdChatTaskPlan();
        plan.Steps.Add(step);

        var planStep = NyxIdChatTaskPlanJsonFormatter
            .FormatTaskPlan(plan)["steps"]![0]!;
        planStep["required"]!.GetValue<bool>().Should().BeFalse();
        planStep["mayChangeExternalState"]!.GetValue<bool>().Should().BeFalse();
        planStep["operation"]!["mayChangeExternalState"]!
            .GetValue<bool>().Should().BeFalse();

        var changedStep = NyxIdChatTaskPlanJsonFormatter
            .FormatTaskPlan(new NyxIdChatTaskPlanStepChanged { Step = step })["step"]!;
        changedStep["required"]!.GetValue<bool>().Should().BeFalse();
        changedStep["mayChangeExternalState"]!.GetValue<bool>().Should().BeFalse();
        changedStep["operation"]!["mayChangeExternalState"]!
            .GetValue<bool>().Should().BeFalse();
    }

    [Theory]
    [InlineData(ToolPresentationKind.Generic, "generic")]
    [InlineData(ToolPresentationKind.BuiltIn, "builtIn")]
    [InlineData(ToolPresentationKind.NyxIdOperation, "nyxIdOperation")]
    [InlineData(ToolPresentationKind.Mcp, "mcp")]
    [InlineData(ToolPresentationKind.Skill, "skill")]
    public void FormatterOutput_ShouldUseCanonicalToolPresentationKind(
        ToolPresentationKind kind,
        string expected)
    {
        var presentation = new ToolPresentationDescriptor
        {
            Kind = kind,
            Availability = ToolAvailability.Available,
        };

        var node = NyxIdChatTaskPlanJsonFormatter.FormatProtobuf(presentation);

        node["kind"]!.GetValue<string>().Should().Be(expected);
    }

    [Theory]
    [InlineData(ToolAvailability.Available, "available")]
    [InlineData(ToolAvailability.Unavailable, "unavailable")]
    public void FormatterOutput_ShouldUseCanonicalToolAvailability(
        ToolAvailability availability,
        string expected)
    {
        var presentation = new ToolPresentationDescriptor
        {
            Kind = ToolPresentationKind.Generic,
            Availability = availability,
        };

        var node = NyxIdChatTaskPlanJsonFormatter.FormatProtobuf(presentation);

        node["availability"]!.GetValue<string>().Should().Be(expected);
    }

    [Fact]
    public async Task FormatterOutput_ShouldRoundTripThroughStudioProtocolWithRepeatedDefaults()
    {
        var plan = new NyxIdChatTaskPlan
        {
            SchemaVersion = 4,
            ActorId = "conversation-alpha",
            TaskId = "task-alpha",
            TurnId = "turn-alpha",
            PlanId = "plan-alpha",
            PlanRevision = 2,
            PlanRevisionHistoryStart = 1,
            Status = NyxIdChatTaskStatus.Active,
        };
        plan.PlanRevisions.Add(new NyxIdChatPlanRevisionRecord
        {
            PlanRevision = 1,
            RevisionCause = NyxIdChatPlanRevisionCause.Initial,
            AddedStepIds = { "step-initial" },
        });
        plan.PlanRevisions.Add(new NyxIdChatPlanRevisionRecord
        {
            PlanRevision = 2,
            RevisionCause = NyxIdChatPlanRevisionCause.ScopeResolution,
            AddedStepIds = { "step-action" },
        });

        var formatterFixture = NyxIdChatTaskPlanJsonFormatter
            .FormatTaskPlan(plan)
            .ToJsonString();
        formatterFixture.Should().Contain("\"revisionCause\":\"scope_resolution\"");
        formatterFixture.Should().NotContain("cancelledStepIds",
            "protobuf JSON omits empty repeated fields");

        var repositoryRoot = GetRepositoryRoot();
        var protocolPath = Path.Combine(
            repositoryRoot,
            "src",
            "workflow",
            "Aevatar.Workflow.Infrastructure",
            "CapabilityApi",
            "StudioAssistant",
            "protocol.js");
        var script = """
            const assert = require('node:assert/strict');
            const vm = require('node:vm');
            const fs = require('node:fs');
            const source = fs.readFileSync(process.argv[1], 'utf8').replace(/^export /gm, '');
            const payload = JSON.parse(Buffer.from(process.argv[2], 'base64').toString('utf8'));
            const context = { structuredClone, TextDecoder, URL, console };
            vm.createContext(context);
            vm.runInContext(source, context);
            const frame = {type:'CUSTOM', sequence:17,
              custom:{name:'nyxid.task.snapshot', payload}};
            const first = context.normalizeFrame(frame);
            assert.equal(first.type, 'task_snapshot');
            assert.deepEqual(
              JSON.parse(JSON.stringify(first.payload.planRevisions[0].cancelledStepIds)), []);
            assert.deepEqual(
              JSON.parse(JSON.stringify(first.payload.planRevisions[1].cancelledStepIds)), []);
            const second = context.normalizeFrame({type:'CUSTOM', sequence:18,
              custom:{name:'nyxid.task.snapshot', payload:first.payload}});
            assert.deepEqual(
              JSON.parse(JSON.stringify(second.payload)),
              JSON.parse(JSON.stringify(first.payload)));
            """;
        var startInfo = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--eval");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add(protocolPath);
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(formatterFixture)));
        using var process = Process.Start(startInfo);
        process.Should().NotBeNull("Node.js is required to verify the shipped Studio decoder");
        var outputTask = process!.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        process.ExitCode.Should().Be(0, await errorTask + await outputTask);
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
        NyxIdChatActionPostconditionInput.Descriptor.FindFieldByName("requested_at")
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

    private static string GetRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "aevatar.slnx")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
