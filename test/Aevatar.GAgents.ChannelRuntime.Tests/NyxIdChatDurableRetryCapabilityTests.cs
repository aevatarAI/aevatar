using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.NyxId.ConnectedServices;
using Aevatar.AI.ToolProviders.ToolSetRegistry;
using Aevatar.Audit;
using Aevatar.Audit.Abstractions.Identity;
using Aevatar.Audit.Abstractions.Models;
using Aevatar.Audit.Abstractions.Ports;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.GAgents.NyxidChat.AgentProfiles;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

[Collection(ChannelRuntimeTestCollections.NyxIdInventoryRequestContext)]
public sealed class NyxIdChatDurableRetryCapabilityTests
{
    private static readonly Timestamp Now = Timestamp.FromDateTimeOffset(
        new DateTimeOffset(2026, 8, 8, 8, 0, 0, TimeSpan.Zero));

    private const string DurableRetryArguments =
        """{"body":{"approval_code":"approval-alpha","user_id":"user-alpha","form":"[]","uuid":"canary-uuid-alpha"}}""";

    private const string DurableRetryServiceInventory = """
        {
          "keys": [
            {
              "id": "usvc-lark",
              "slug": "api-lark-bot",
              "label": "Lark canary",
              "catalog_service_id": "svc-lark",
              "catalog_service_slug": "api-lark-bot",
              "endpoint_id": "instance-endpoint-alpha",
              "endpoint_url": "https://lark.test",
              "is_active": true,
              "connected": true,
              "status": "active",
              "credential_source": { "type": "personal" }
            }
          ]
        }
        """;

    private const string DurableRetryMcpCatalog = """
        {
          "contract_version": "1.0",
          "catalog_digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "user_id": "nyx-user-alpha",
          "services": [
            {
              "service_id": "usvc-lark",
              "service_name": "Lark",
              "service_slug": "api-lark-bot",
              "is_user_service": true,
              "is_generic_proxy": false,
              "endpoints": [
                {
                  "endpoint_id": "lark-create-approval",
                  "name": "lark_create_approval_instance",
                  "method": "POST",
                  "path": "/open-apis/approval/v4/instances",
                  "parameters": [],
                  "request_body_schema": {
                    "type": "object",
                    "properties": {
                      "approval_code": { "type": "string" },
                      "user_id": { "type": "string" },
                      "form": { "type": "string" },
                      "uuid": { "type": "string" }
                    },
                    "required": ["approval_code", "user_id", "form", "uuid"],
                    "additionalProperties": false
                  },
                  "request_content_type": "application/json",
                  "request_body_required": true,
                  "response": { "content_types": ["application/json"], "binary_artifact": false }
                },
                {
                  "endpoint_id": "lark-get-approval",
                  "name": "lark_get_approval_instance",
                  "method": "GET",
                  "path": "/open-apis/approval/v4/instances/{instance_id}",
                  "parameters": [
                    { "name": "instance_id", "in": "path", "required": true, "schema": { "type": "string" } }
                  ],
                  "request_body_schema": null,
                  "request_content_type": null,
                  "request_body_required": false,
                  "response": { "content_types": ["application/json"], "binary_artifact": false }
                }
              ]
            }
          ]
        }
        """;

    [Theory]
    [InlineData("fresh-per-request-token", AgentToolReceiptStatus.ApprovalRequired)]
    [InlineData("valid-grant-token", AgentToolReceiptStatus.Success)]
    public async Task DirectEffectRetry_AfterFreshTurnSession_ShouldRematerializeExactCapability(
        string retryToken,
        AgentToolReceiptStatus expectedStatus)
    {
        var tool = new RetryEffectTool();
        var executor = CreateTurnExecutor(tool);
        var (state, originalSession) = await BuildReconciledNotAppliedStateAsync(executor, tool);
        var toolStep = state.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Tool);

        var retry = NyxIdChatControlCommands.Retry(
            state,
            BuildRetryCommand(toolStep, retryToken, expectedStateVersion: 20),
            stateVersion: 20,
            Now);
        retry.ShouldCommit.Should().BeTrue();
        retry.ShouldDispatch.Should().BeTrue();
        retry.NextCommand.Should().NotBeNull();
        retry.NextCommand!.Key.OperationGeneration.Should().Be(2);
        retry.NextCommand.Tool.RematerializeDurableAuthorization.Should().BeTrue();
        retry.NextCommand.Tool.RetryAuthorizationSourceKey.Should().NotBeNull();

        var freshSession = new NyxIdChatTransientExecutionSession();
        var result = await executor.ExecuteAsync(
            retry.NextCommand,
            freshSession,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        result.Result.Failure.Should().BeNull();
        result.Result.Tool.Should().NotBeNull();
        result.Result.Tool.Receipt.Status.Should().Be(expectedStatus);
        if (expectedStatus == AgentToolReceiptStatus.ApprovalRequired)
        {
            result.Result.Tool.Receipt.ApprovalRequestId.Should().Be("approval-generation-2");
            result.Result.Tool.Receipt.NyxIdApprovalDecisionMode.Should().Be(
                NyxIdApprovalDecisionMode.PerRequest);
        }
        else
        {
            result.Result.Tool.Receipt.ProviderResourceId.Should().Be("resource-generation-2");
        }

        tool.ExecutionTokens.Should().Equal("uncertain-token", retryToken);
        originalSession.AuthorizedToolStep.Should().BeNull(
            "the original transient capability was consumed before the fresh-session retry");
        Encoding.UTF8.GetString(retry.State.ToByteArray()).Should().NotContain(retryToken);
        retry.State.ToString().Should().NotContain(retryToken);
    }

    [Fact]
    public async Task DirectEffectRetry_WithRematerializedConnectedServiceTool_ShouldPreserveNyxIdApprovalRequest()
    {
        const string firstToken = "generation-one-token";
        const string nearExpiryToken = "near-expiry-token";
        const string refreshedToken = "refreshed-token";
        var handler = new DurableRetryNyxIdHandler();
        handler.CatalogsByToken[firstToken] = DurableRetryMcpCatalog;
        handler.CatalogsByToken[refreshedToken] = DurableRetryMcpCatalog;
        handler.KeysByToken[firstToken] = DurableRetryServiceInventory;
        handler.KeysByToken[refreshedToken] = DurableRetryServiceInventory;
        handler.AllowedProxyTokens.UnionWith([firstToken, refreshedToken]);
        handler.ProxyResponses.Enqueue(new DurableRetryProxyResponse(
            HttpStatusCode.BadGateway,
            """{"error":"upstream_result_lost","error_code":9000}"""));

        var options = new NyxIdToolOptions
        {
            BaseUrl = "https://nyx.test",
            EnableAssistantConnectedServiceEffects = true,
            AssistantOperationReadBackBindings = [DurableRetryApprovalReadBackBinding()],
        };
        var client = new NyxIdApiClient(options, new HttpClient(handler));
        var source = new CountingToolSource(new NyxIdConnectedServiceToolSource(
            options,
            client,
            new NyxIdServiceInstanceClient(client)));
        IAgentTool initialTool;
        using (AgentToolContextScope.Push(
                   AgentToolExecutionContextMapper.FromPayload(ToolContext(firstToken))))
        {
            initialTool = (await source.DiscoverToolsAsync())
                .Single(tool => !tool.IsReadOnly);
        }
        initialTool.GetType().Name.Should().Be("NyxIdConnectedServiceOperationTool");

        var executionPort = new RecordingExecutionPort(new AdmittedAgentToolExecutor(
            new AlwaysStartingAdmissionLedger(),
            new AppendedAuditTrail(),
            new StableIdentityHasher()));
        var credentialLifecycle = new RefreshingDelegationCredentialLifecycle(
            nearExpiryToken,
            refreshedToken);
        var executor = CreateTurnExecutor(
            initialTool,
            new SourceToolSetRegistry("profile.route", source),
            executionPort,
            DurableRetryArguments,
            credentialLifecycle);
        var state = await BuildReconciledNotAppliedStateAsync(
            executor,
            initialTool,
            firstToken,
            () => executionPort.FormatOutcomes());
        handler.ProxyResponses.Enqueue(new DurableRetryProxyResponse(
            HttpStatusCode.Forbidden,
            """{"error":"approval_required","error_code":7000,"request_id":"approval-generation-2-real","approval_mode":"per_request"}"""));
        var effect = state.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Tool);
        var retry = NyxIdChatControlCommands.Retry(
            state,
            BuildRetryCommand(
                effect,
                nearExpiryToken,
                expectedStateVersion: 70,
                credentialKind: AgentToolNyxIdCredentialKind.ProxyDelegation),
            stateVersion: 70,
            Now);
        retry.ShouldDispatch.Should().BeTrue();
        retry.NextCommand.Should().NotBeNull();

        var discoveriesBeforeRetry = source.DiscoveryCount;
        var execution = await executor.ExecuteAsync(
            retry.NextCommand!,
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.Failure.Should().BeNull(
            "the raw execution outcomes were {0}",
            executionPort.FormatOutcomes());
        execution.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.ApprovalRequired);
        execution.Result.Tool.Receipt.ApprovalRequestId.Should().Be("approval-generation-2-real");
        execution.Result.Tool.Receipt.ApprovalRequestId.Should().NotBe("tool_approval");
        execution.Result.Tool.Receipt.NyxIdApprovalDecisionMode.Should().Be(
            NyxIdApprovalDecisionMode.PerRequest);
        source.DiscoveryCount.Should().BeGreaterThan(discoveriesBeforeRetry,
            "the fresh retry session must rematerialize the connected-service catalog");
        handler.ProxyRequests.Should().Be(2,
            "both generations must cross the real NyxID proxy ingress");
        credentialLifecycle.DelegationTokens.Should().Equal(nearExpiryToken);
        handler.CatalogRequestTokens.Should().Contain(refreshedToken);
        handler.CatalogRequestTokens.Should().NotContain(nearExpiryToken);
        handler.ProxyRequestTokens.Should().Contain(refreshedToken);
        handler.ProxyRequestTokens.Should().NotContain(nearExpiryToken);
    }

    [Fact]
    public async Task UnprofiledDirectEffectPlan_ShouldNotMintDurableAuthorityOrDispatchTool()
    {
        var tool = new RetryEffectTool();
        var executor = CreateTurnExecutor(tool);
        var llmKey = Key("step-llm", "operation-llm", generation: 1);
        var initialState = ActiveLlmState(llmKey, tool.Name, profiled: false);
        var session = new NyxIdChatTransientExecutionSession();
        var llmResult = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = llmKey.Clone(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    Request = new ChatRequestEvent
                    {
                        Prompt = "Create the approval record.",
                        SessionId = "turn-alpha",
                        ScopeId = "scope-alpha",
                        ToolContext = ToolContext("unprofiled-token"),
                    },
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        llmResult.Result.ResultCase.Should().Be(NyxIdChatOperationResultSignal.ResultOneofCase.Llm);
        var call = llmResult.Result.Llm.ToolCalls.Should().ContainSingle().Subject;
        call.OperationAdmission.Should().BeNull();
        var planned = NyxIdChatTaskLifecycle.ApplyOperationResult(initialState, llmResult.Result, Now);
        planned.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        planned.NextCommand.Should().BeNull(
            "restricted-empty turns cannot dispatch a model-invented hidden tool");
        tool.ExecutionTokens.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DurableRetryAuthorityPairMismatch_ShouldFailClosedBeforeExternalExecution(
        bool removeProfile)
    {
        var tool = new RetryEffectTool();
        var executor = CreateTurnExecutor(tool);
        var (state, _) = await BuildReconciledNotAppliedStateAsync(executor, tool);
        var effect = state.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Tool);
        var asymmetricState = state.Clone();
        if (removeProfile)
            asymmetricState.AgentProfile = null;
        else
            asymmetricState.ActiveTurn.AgentProfileTurnAuthority = null;
        var rejected = NyxIdChatControlCommands.Retry(
            asymmetricState,
            BuildRetryCommand(effect, "valid-grant-token", expectedStateVersion: 35),
            stateVersion: 35,
            Now);
        rejected.ShouldCommit.Should().BeTrue();
        rejected.ShouldDispatch.Should().BeFalse();
        rejected.NextCommand.Should().BeNull();

        var retry = NyxIdChatControlCommands.Retry(
            state,
            BuildRetryCommand(effect, "valid-grant-token", expectedStateVersion: 36),
            stateVersion: 36,
            Now);
        var command = retry.NextCommand!.Clone();
        if (removeProfile)
            command.Tool.AgentProfile = null;
        else
            command.Tool.AgentProfileTurnAuthority = null;

        var result = await executor.ExecuteAsync(
            command,
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        result.Result.Failure.Should().NotBeNull();
        result.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.ToolAuthorizationMismatchCode);
        result.Result.Failure.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotStarted);
        tool.ExecutionTokens.Should().Equal("uncertain-token");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DurableRetryMalformedCommand_ShouldFailClosedBeforeExternalExecution(
        bool removeArguments)
    {
        var tool = new RetryEffectTool();
        var executor = CreateTurnExecutor(tool);
        var (state, _) = await BuildReconciledNotAppliedStateAsync(executor, tool);
        var effect = state.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Tool);
        var retry = NyxIdChatControlCommands.Retry(
            state,
            BuildRetryCommand(effect, "valid-grant-token", expectedStateVersion: 37),
            stateVersion: 37,
            Now);
        var command = retry.NextCommand!.Clone();
        if (removeArguments)
            command.Tool.RetryAuthorizationSourceKey = null;
        else
            command.Tool.ToolContext = null;

        var result = await executor.ExecuteAsync(
            command,
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        result.Result.Failure.Should().NotBeNull();
        result.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.ToolAuthorizationMismatchCode);
        result.Result.Failure.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotStarted);
        tool.ExecutionTokens.Should().Equal("uncertain-token");
    }

    [Theory]
    [InlineData("expired-grant-token")]
    [InlineData("revoked-grant-token")]
    [InlineData("scope-mismatched-grant-token")]
    [InlineData("ttl-expired-grant-token")]
    public async Task DirectGrantRetry_WhenStandingGrantIsInvalid_ShouldReenterExactApproval(
        string retryToken)
    {
        var tool = new RetryEffectTool();
        var executor = CreateTurnExecutor(tool);
        var (state, _) = await BuildReconciledNotAppliedStateAsync(executor, tool);
        var effect = state.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Tool);
        var retry = NyxIdChatControlCommands.Retry(
            state,
            BuildRetryCommand(effect, retryToken, expectedStateVersion: 22),
            stateVersion: 22,
            Now);
        var execution = await executor.ExecuteAsync(
            retry.NextCommand!,
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.Failure.Should().BeNull(execution.Result.Failure?.ToString());
        execution.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.ApprovalRequired);
        execution.Result.Tool.Receipt.NyxIdApprovalDecisionMode.Should().Be(
            NyxIdApprovalDecisionMode.Grant);
        execution.Result.Tool.Receipt.ApprovalRequestId.Should().Be("approval-generation-2");
        retry.NextCommand!.Key.OperationGeneration.Should().Be(2);
        tool.ExecutionTokens.Should().Equal("uncertain-token", retryToken);
    }

    [Fact]
    public async Task DirectPerRequestRetry_WhenDecisionArrivesDuringInvocation_ShouldVerifyGenerationTwo()
    {
        var tool = new RetryEffectTool();
        var executor = CreateTurnExecutor(tool);
        var (state, _) = await BuildReconciledNotAppliedStateAsync(executor, tool);
        var effect = state.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Tool);
        const string retryToken = "approved-per-request-token";

        var retry = NyxIdChatControlCommands.Retry(
            state,
            BuildRetryCommand(effect, retryToken, expectedStateVersion: 25),
            stateVersion: 25,
            Now);
        var execution = await executor.ExecuteAsync(
            retry.NextCommand!,
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        execution.Result.Failure.Should().BeNull(execution.Result.Failure?.ToString());
        execution.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success);
        execution.Result.Tool.Receipt.ApprovalRequestId.Should().BeEmpty(
            "Tier B cannot observe the request identity when NyxID synchronously returns the downstream success");
        retry.NextCommand!.Key.OperationGeneration.Should().Be(2);
        tool.PerRequestApprovalIds.Should().Equal("approval-generation-2");
        tool.ExecutionTokens.Should().Equal("uncertain-token", retryToken);

        var afterEffect = NyxIdChatTaskLifecycle.ApplyOperationResult(
            retry.State,
            execution.Result,
            Now);
        afterEffect.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        afterEffect.NextCommand.Should().NotBeNull();
        var verification = afterEffect.State.ActiveTask.Steps.Single(step =>
            string.Equals(
                step.StepId,
                afterEffect.NextCommand!.Key.StepId,
                StringComparison.Ordinal));
        verification.Kind.Should().Be(NyxIdChatStepKind.Postcondition);
        verification.DependsOn.Should().Contain(effect.StepId);
        verification.Operation.Key.OperationGeneration.Should().Be(2);
        var readBack = verification.Source.Postcondition.ToolReadBack;

        var completed = NyxIdChatTaskLifecycle.ApplyOperationResult(
            afterEffect.State,
            new NyxIdChatOperationResultSignal
            {
                Key = verification.Operation.Key.Clone(),
                ToolVerification = new NyxIdChatToolVerificationResult
                {
                    EffectStepId = effect.StepId,
                    Disposition = NyxIdChatToolVerificationDisposition.Applied,
                    ReadOperation = readBack.ReadOperation.Clone(),
                    CheckName = readBack.CheckName,
                },
            },
            Now);

        completed.Outcome.Should().Be(NyxIdChatTransitionOutcome.Accepted);
        completed.NextCommand.Should().BeNull();
        completed.State.ActiveTask.Status.Should().Be(NyxIdChatTaskStatus.Succeeded);
        completed.State.ActiveTurn.Status.Should().Be(NyxIdChatTurnStatus.Succeeded);
        var completedEffect = completed.State.ActiveTask.Steps.Single(step =>
            string.Equals(step.StepId, effect.StepId, StringComparison.Ordinal));
        completedEffect.Operation.Key.OperationGeneration.Should().Be(2);
        completedEffect.Status.Should().Be(NyxIdChatStepStatus.Done);
        completedEffect.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.Confirmed);
        completedEffect.ApprovalRequestId.Should().BeEmpty(
            "the actor must not inherit generation one or fabricate the NyxID-owned generation-two request identity");
        completedEffect.ApprovalObservation.Should().BeNull();
        Encoding.UTF8.GetString(completed.State.ToByteArray()).Should().NotContain(retryToken);
    }

    [Fact]
    public async Task DirectEffectRetry_WhenCurrentDefinitionDrifts_ShouldFailClosedBeforeExecution()
    {
        var tool = new RetryEffectTool();
        var executor = CreateTurnExecutor(tool);
        var (state, _) = await BuildReconciledNotAppliedStateAsync(executor, tool);
        var toolStep = state.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Tool);
        var retry = NyxIdChatControlCommands.Retry(
            state,
            BuildRetryCommand(toolStep, "fresh-token", expectedStateVersion: 30),
            stateVersion: 30,
            Now);
        tool.DescriptionOverride = "The current catalog now exposes a different definition.";

        var result = await executor.ExecuteAsync(
            retry.NextCommand!,
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        result.Result.Failure.Should().NotBeNull();
        result.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.ToolAuthorizationMismatchCode);
        result.Result.Failure.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotStarted);
        tool.ExecutionTokens.Should().Equal("uncertain-token");
    }

    [Fact]
    public async Task DirectEffectRetry_AfterFreshTurnSession_ShouldUseCommittedProfileAuthority()
    {
        var tool = new RetryEffectTool();
        var executor = CreateTurnExecutor(tool);
        var (state, _) = await BuildReconciledNotAppliedStateAsync(executor, tool);
        var toolStep = state.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Tool);

        var retry = NyxIdChatControlCommands.Retry(
            state,
            BuildRetryCommand(toolStep, "valid-grant-token", expectedStateVersion: 40),
            stateVersion: 40,
            Now);

        retry.ShouldCommit.Should().BeTrue();
        retry.ShouldDispatch.Should().BeTrue();
        retry.NextCommand.Should().NotBeNull();
        retry.NextCommand!.Tool.AgentProfile.Should().BeEquivalentTo(state.AgentProfile);
        retry.NextCommand.Tool.AgentProfileTurnAuthority.Should().BeEquivalentTo(
            state.ActiveTurn.AgentProfileTurnAuthority);
        retry.NextCommand.Tool.RematerializeDurableAuthorization.Should().BeTrue();
        var result = await executor.ExecuteAsync(
            retry.NextCommand,
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        result.Result.Failure.Should().BeNull();
        result.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.Success);
        tool.ExecutionTokens.Should().Equal("uncertain-token", "valid-grant-token");
    }

    [Theory]
    [InlineData("command-owner")]
    [InlineData("channel-sender")]
    [InlineData("execution-owner")]
    public async Task DirectEffectRetry_WithTamperedAuthority_ShouldFailClosedBeforeGenerationAdvance(
        string tamper)
    {
        var tool = new RetryEffectTool();
        var executor = CreateTurnExecutor(tool);
        var (state, _) = await BuildReconciledNotAppliedStateAsync(executor, tool);
        var toolStep = state.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Tool);
        var command = BuildRetryCommand(toolStep, "valid-grant-token", expectedStateVersion: 45);
        switch (tamper)
        {
            case "command-owner":
                command.OwnerSubject = "owner-foreign";
                break;
            case "channel-sender":
                command.ToolContext.Channel.SenderId = "owner-alpha";
                break;
            case "execution-owner":
                command.ToolContext.ExecutionOwner.OwnerId = "conversation-foreign";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(tamper), tamper, null);
        }

        var decision = NyxIdChatControlCommands.Retry(
            state,
            command,
            stateVersion: 45,
            Now);

        decision.ShouldCommit.Should().BeTrue("the rejected control outcome is durably recorded");
        decision.ShouldDispatch.Should().BeFalse();
        decision.NextCommand.Should().BeNull();
        decision.Result.Outcome.Should().Be(NyxIdChatTransitionOutcome.Rejected);
        decision.Result.ReasonCode.Should().Be(NyxIdChatControlCommands.StepActionUnavailable);
        decision.State.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Tool)
            .Operation.Key.OperationGeneration.Should().Be(1);
        tool.ExecutionTokens.Should().Equal("uncertain-token");
    }

    [Fact]
    public async Task DirectEffectRetry_WhenCurrentCatalogRevokesTool_ShouldFailClosed()
    {
        var tool = new RetryEffectTool();
        var registry = new StaticToolSetRegistry("profile.route", [tool]);
        var executor = CreateTurnExecutor(tool, registry);
        var (state, _) = await BuildReconciledNotAppliedStateAsync(executor, tool);
        var toolStep = state.ActiveTask.Steps.Single(step => step.Kind == NyxIdChatStepKind.Tool);
        var retry = NyxIdChatControlCommands.Retry(
            state,
            BuildRetryCommand(toolStep, "valid-grant-token", expectedStateVersion: 50),
            stateVersion: 50,
            Now);
        var committedAuthority = retry.State.ActiveTurn.AgentProfileTurnAuthority.Clone();
        registry.ReplaceTools([]);
        retry.ShouldCommit.Should().BeTrue();
        retry.NextCommand.Should().NotBeNull();
        retry.NextCommand!.Tool.AgentProfileTurnAuthority
            .Should().BeEquivalentTo(committedAuthority);
        retry.NextCommand.Tool.AgentProfileTurnAuthority
            .AuthorityCeilingToolNames.Should().ContainSingle(tool.Name);
        var result = await executor.ExecuteAsync(
            retry.NextCommand,
            new NyxIdChatTransientExecutionSession(),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        result.Result.Failure.Should().NotBeNull();
        result.Result.Failure.FailureCode.Should().Be(
            NyxIdChatTurnOperationExecutor.ToolAuthorizationMismatchCode);
        result.Result.Failure.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotStarted);
        tool.ExecutionTokens.Should().Equal("uncertain-token");
    }

    [Fact]
    public async Task TransientToolStep_WithHigherGeneration_ShouldNotRematerializeDurableAuthorization()
    {
        var tool = new RetryEffectTool();
        var executor = CreateTurnExecutor(tool);
        var llmKey = Key("step-llm", "operation-llm", generation: 1);
        var initialState = ActiveLlmState(llmKey, tool.Name);
        var session = new NyxIdChatTransientExecutionSession();
        var llmResult = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = llmKey.Clone(),
                Llm = new NyxIdChatLLMOperationInput
                {
                    Request = new ChatRequestEvent
                    {
                        Prompt = "Create the approval record.",
                        SessionId = "turn-alpha",
                        ScopeId = "scope-alpha",
                        ToolContext = ToolContext("uncertain-token"),
                    },
                    AgentProfile = initialState.AgentProfile.Clone(),
                    AgentProfileTurnAuthority =
                        initialState.ActiveTurn.AgentProfileTurnAuthority.Clone(),
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        var call = llmResult.Result.Llm.ToolCalls.Should().ContainSingle().Subject;
        var planned = NyxIdChatTaskLifecycle.ApplyOperationResult(
            initialState,
            llmResult.Result,
            Now);
        var plannedTool = planned.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Tool);
        plannedTool.Operation.Key.OperationGeneration = 2;
        plannedTool.RematerializeDurableAuthorization.Should().BeFalse();
        planned.NextCommand.Should().NotBeNull();
        planned.NextCommand!.Tool.RematerializeDurableAuthorization.Should().BeFalse();
        planned.NextCommand.Tool.RetryAuthorizationSourceKey.Should().BeNull();

        var result = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = Key("step-tool", "operation-tool", generation: 2),
                Tool = new NyxIdChatToolOperationInput
                {
                    CallId = call.CallId,
                    ToolName = call.ToolName,
                    ArgumentsJson = call.ArgumentsJson,
                    MayChangeExternalState = call.Safety.MayChangeExternalState,
                    IdempotencyKey = "operation-tool",
                    OperationAdmission = call.OperationAdmission.Clone(),
                },
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        result.Result.Failure.Should().BeNull(result.Result.Failure?.ToString());
        result.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.Error);
        tool.ExecutionTokens.Should().Equal("uncertain-token");
    }

    private static NyxIdChatTurnOperationExecutor CreateTurnExecutor(
        RetryEffectTool tool,
        StaticToolSetRegistry? registry = null)
    {
        var provider = new ExactToolCallProvider(tool.Name);
        var generationExecutor = new AgentRunReplyGenerationExecutor(
            Substitute.For<IActorDispatchPort>(),
            new RebuildingStepPlanReplyGenerator(
                tool,
                provider,
                staticToolAvailable: () => tool.IsBaseRouteAvailable),
            interactiveReplyCollector: null,
            relayOptions: null,
            NullLogger<AgentRunReplyGenerationExecutor>.Instance);
        var materializer = new AgentTurnToolCatalogMaterializer(
            registry ?? new StaticToolSetRegistry("profile.route", [tool]),
            new NoMatchClassifier());
        return new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            new UnavailableNyxIdActionPostconditionPort(),
            materializer,
            new AcceptingDelegationCredentialLifecycle());
    }

    private static NyxIdChatTurnOperationExecutor CreateTurnExecutor(
        IAgentTool tool,
        IToolSetRegistry registry,
        IAgentToolExecutionPort executionPort,
        string argumentsJson,
        INyxIdChatDelegationCredentialLifecyclePort? credentialLifecycle = null)
    {
        var provider = new ExactToolCallProvider(tool.Name, argumentsJson);
        var generationExecutor = new AgentRunReplyGenerationExecutor(
            Substitute.For<IActorDispatchPort>(),
            new RebuildingStepPlanReplyGenerator(
                tool,
                provider,
                executionPort),
            interactiveReplyCollector: null,
            relayOptions: null,
            NullLogger<AgentRunReplyGenerationExecutor>.Instance);
        var materializer = new AgentTurnToolCatalogMaterializer(
            registry,
            new NoMatchClassifier());
        return new NyxIdChatTurnOperationExecutor(
            generationExecutor,
            new UnavailableNyxIdActionPostconditionPort(),
            materializer,
            credentialLifecycle ?? new AcceptingDelegationCredentialLifecycle());
    }

    private static async Task<(NyxIdChatConversationGAgentState State,
        NyxIdChatTransientExecutionSession OriginalSession)> BuildReconciledNotAppliedStateAsync(
        NyxIdChatTurnOperationExecutor executor,
        IAgentTool tool,
        bool profiled = true,
        string initialToken = "uncertain-token",
        Func<string>? executionDiagnostics = null)
    {
        var llmKey = Key("step-llm", "operation-llm", generation: 1);
        var initialState = ActiveLlmState(llmKey, tool.Name, profiled);
        var session = new NyxIdChatTransientExecutionSession();
        var llmInput = new NyxIdChatLLMOperationInput
        {
            Request = new ChatRequestEvent
            {
                Prompt = "Create the approval record.",
                SessionId = "turn-alpha",
                ScopeId = "scope-alpha",
                ToolContext = ToolContext(initialToken),
            },
        };
        if (initialState.AgentProfile is not null)
        {
            llmInput.AgentProfile = initialState.AgentProfile.Clone();
            llmInput.AgentProfileTurnAuthority =
                initialState.ActiveTurn.AgentProfileTurnAuthority.Clone();
        }
        var llmResult = await executor.ExecuteAsync(
            new NyxIdChatOperationDispatchCommand
            {
                Key = llmKey.Clone(),
                Llm = llmInput,
            },
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        llmResult.Result.ResultCase.Should().Be(NyxIdChatOperationResultSignal.ResultOneofCase.Llm);
        llmResult.Result.Llm.ToolCalls.Should().ContainSingle();
        var call = llmResult.Result.Llm.ToolCalls.Should().ContainSingle().Subject;
        call.OperationAdmission.DurableAuthorization.Should().NotBeNull();
        call.OperationAdmission.DurableAuthorization.ToolDefinitionFingerprint.Should().NotBeNullOrWhiteSpace();
        NyxIdChatOperationAdmissionPolicy.IsValid(call.OperationAdmission, call.Safety).Should().BeTrue();

        var planned = NyxIdChatTaskLifecycle.ApplyOperationResult(
            initialState,
            llmResult.Result,
            Now);
        planned.Outcome.Should().Be(
            NyxIdChatTransitionOutcome.Accepted,
            "the initial exact-service plan should not be rejected: {0} {1}",
            planned.ReasonCode,
            planned.SafeMessage);
        planned.NextCommand.Should().NotBeNull();
        var firstToolResult = await executor.ExecuteAsync(
            planned.NextCommand!,
            session,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);
        firstToolResult.Result.Failure.Should().BeNull(
            "the raw execution outcomes were {0}",
            executionDiagnostics?.Invoke() ?? "not recorded");
        firstToolResult.Result.Tool.Receipt.Status.Should().Be(AgentToolReceiptStatus.Error);
        if (tool is RetryEffectTool retryEffectTool)
            retryEffectTool.ExecutionTokens.Should().Equal(initialToken);

        var uncertain = NyxIdChatTaskLifecycle.ApplyOperationResult(
            planned.State,
            firstToolResult.Result,
            Now);
        uncertain.NextCommand.Should().NotBeNull();
        var verification = uncertain.NextCommand!;
        var effectStep = uncertain.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Tool);
        var readBack = effectStep.Source.Tool.OperationAdmission.ReadBack;
        var reconciled = NyxIdChatTaskLifecycle.ApplyOperationResult(
            uncertain.State,
            new NyxIdChatOperationResultSignal
            {
                Key = verification.Key.Clone(),
                ToolVerification = new NyxIdChatToolVerificationResult
                {
                    EffectStepId = effectStep.StepId,
                    Disposition = NyxIdChatToolVerificationDisposition.NotApplied,
                    ReadOperation = readBack.ReadOperation.Clone(),
                    CheckName = readBack.CheckName,
                    FailureCode = "EFFECT_NOT_FOUND",
                    SafeMessage = "The read-back proved that the effect was not applied.",
                },
            },
            Now);
        var reconciledTool = reconciled.State.ActiveTask.Steps.Single(step =>
            step.Kind == NyxIdChatStepKind.Tool);
        reconciledTool.ExternalEffect.Should().Be(NyxIdChatEffectEvidence.NotApplied);
        reconciledTool.AvailableActions.Retry.Should().BeTrue();
        return (reconciled.State, session);
    }

    private static async Task<NyxIdChatConversationGAgentState> BuildReconciledNotAppliedStateAsync(
        NyxIdChatTurnOperationExecutor executor,
        IAgentTool tool,
        string initialToken,
        Func<string>? executionDiagnostics = null)
    {
        var (state, _) = await BuildReconciledNotAppliedStateAsync(
            executor,
            tool,
            profiled: true,
            initialToken: initialToken,
            executionDiagnostics);
        return state;
    }

    private static NyxIdAssistantOperationReadBackBinding DurableRetryApprovalReadBackBinding() => new()
    {
        CatalogServiceSlug = "api-lark-bot",
        EffectHttpMethod = "POST",
        EffectPathTemplate = "/open-apis/approval/v4/instances",
        ReadHttpMethod = "GET",
        ReadPathTemplate = "/open-apis/approval/v4/instances/{instance_id}",
        CheckName = "lark_approval_instance_exists_by_caller_uuid",
        Match = AgentToolReadBackMatch.Equals,
        JsonPointer = "/data/instance_code",
        EffectResultIdentityJsonPointer = "/data/instance_code",
        ProviderResourceArgument = new NyxIdAssistantReadBackProviderResourceArgument
        {
            ReadLocation = NyxIdAssistantOperationArgumentLocation.Path,
            ReadArgumentName = "instance_id",
        },
        NotAppliedEvidence = new NyxIdAssistantReadBackNotAppliedEvidence
        {
            JsonPointer = "/code",
            ExpectedValue = Google.Protobuf.WellKnownTypes.Value.ForNumber(1390003),
        },
    };

    private static NyxIdChatRetryStepCommand BuildRetryCommand(
        NyxIdChatTaskStepState step,
        string token,
        long expectedStateVersion,
        AgentToolNyxIdCredentialKind credentialKind =
            AgentToolNyxIdCredentialKind.SourceReadableUserBearer) => new()
    {
        ScopeId = "scope-alpha",
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = step.StepId,
        RetryRequestId = $"retry-{expectedStateVersion}",
        ClientRequestId = $"client-retry-{expectedStateVersion}",
        CommandId = $"command-retry-{expectedStateVersion}",
        CorrelationId = $"correlation-retry-{expectedStateVersion}",
        OwnerSubject = "owner-alpha",
        ExpectedOperationGeneration = step.Operation.Key.OperationGeneration,
        ExpectedStateVersion = expectedStateVersion,
        ToolContext = ToolContext(token, $"retry-{expectedStateVersion}", credentialKind),
    };

    private static AgentToolExecutionContextPayload ToolContext(
        string token,
        string? requestId = null,
        AgentToolNyxIdCredentialKind credentialKind =
            AgentToolNyxIdCredentialKind.SourceReadableUserBearer) =>
        (AgentToolExecutionContext.Empty with
        {
            Request = new AgentToolRequestIdentity(requestId, null),
            Caller = new AgentToolCallerContext(
                "scope-alpha",
                "owner-alpha",
                requestId,
                "scope-alpha"),
            Channel = new AgentToolChannelContext(
                NyxIdChatServiceDefaults.ServiceId,
                null,
                "scope-alpha",
                null,
                null),
            Credentials = AgentToolCredentials.Empty with
            {
                NyxIdAccessToken = token,
                NyxIdCredentialKind = credentialKind,
            },
            NyxIdAuthority = new AgentToolNyxIdAuthorityContext(
                "nyxid",
                string.Empty,
                "owner-alpha",
                "proxy"),
            ExecutionOwner = new AgentToolExecutionOwner
            {
                Kind = AgentToolExecutionOwnerKind.Actor,
                OwnerId = "conversation-alpha",
            },
            Chat = new AgentChatInvocationContext(
                AgentChatInvocationSurface.NyxIdAssistant,
                "conversation-alpha",
                "turn-alpha",
                "task-alpha",
                null,
                null),
        }).ToPayload();

    private static NyxIdChatConversationGAgentState ActiveLlmState(
        NyxIdChatOperationKey key,
        string toolName,
        bool profiled = true)
    {
        var step = new NyxIdChatTaskStepState
        {
            StepId = key.StepId,
            Order = 1,
            Kind = NyxIdChatStepKind.Llm,
            Status = NyxIdChatStepStatus.Running,
            Required = true,
            Description = "Plan the exact connected-service operation.",
            Source = new NyxIdChatStepSource { Llm = new NyxIdChatLLMStepSource() },
            RetryInputRebuildable = true,
            Operation = new NyxIdChatOperationState
            {
                Key = key.Clone(),
                Kind = NyxIdChatStepKind.Llm,
                Phase = NyxIdChatOperationPhase.Dispatched,
                Idempotent = true,
                IdempotencyKey = key.OperationId,
                RequestedAt = Now.Clone(),
                DispatchedAt = Now.Clone(),
            },
            AddedBy = NyxIdChatStepAddedBy.Initial,
            AddedInPlanRevision = 1,
            UpdatedAt = Now.Clone(),
        };
        var task = new NyxIdChatTaskState
        {
            TaskId = key.TaskId,
            TurnId = key.TurnId,
            Status = NyxIdChatTaskStatus.Active,
            ActiveStepId = key.StepId,
            ActiveOperationId = key.OperationId,
            SchemaVersion = 4,
            ActorId = key.ConversationActorId,
            PlanId = "plan-alpha",
            PlanRevision = 1,
            CreatedAt = Now.Clone(),
            UpdatedAt = Now.Clone(),
        };
        task.Steps.Add(step);
        task.PlanRevisions.Add(new NyxIdChatPlanRevisionRecord
        {
            PlanRevision = 1,
            RevisionCause = NyxIdChatPlanRevisionCause.Initial,
            CommittedAt = Now.Clone(),
            AddedStepIds = { step.StepId },
        });
        var turn = new NyxIdChatTurnState
        {
            TurnId = key.TurnId,
            TaskId = key.TaskId,
            Prompt = "Create the approval record.",
            Status = NyxIdChatTurnStatus.Active,
        };
        if (profiled)
            turn.AgentProfileTurnAuthority = BuildProfileAuthority(toolName);
        var state = new NyxIdChatConversationGAgentState
        {
            ConversationActorId = key.ConversationActorId,
            ScopeId = "scope-alpha",
            OwnerSubject = "owner-alpha",
            ActiveTurn = turn,
            LatestTurn = turn.Clone(),
            ActiveTask = task,
            UpdatedAt = Now.Clone(),
        };
        if (profiled)
            state.AgentProfile = BuildProfile(toolName);
        return state;
    }

    private static AgentProfileSnapshot BuildProfile(string toolName) =>
        AgentProfileSnapshotCodec.Seal(new AgentProfileSnapshot
        {
            ProfileId = "profile-alpha",
            ProfileVersion = "profile-v1",
            AgentKind = NyxIdChatServiceDefaults.GAgentKind,
            PolicyRevision = "policy-v1",
            RouteToolSetRef = "profile.route",
            MaximumToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { toolName },
            },
            RecoveryToolPolicy = new AgentProfileToolPolicy
            {
                ToolNames = { toolName },
            },
            ActivationMode = AgentProfileActivationMode.Enforced,
        });

    private static AgentProfileTurnAuthorityState BuildProfileAuthority(string toolName) => new()
    {
        ReconciliationKey = new AgentProfileTurnReconciliationKey
        {
            SessionId = "turn-alpha",
            Attempt = 1,
        },
        AuthorityKind = AgentProfileTurnAuthorityKind.Recovery,
        AuthorityCeilingToolNames = { toolName },
    };

    private static NyxIdChatOperationKey Key(string stepId, string operationId, long generation) => new()
    {
        ConversationActorId = "conversation-alpha",
        TurnId = "turn-alpha",
        TaskId = "task-alpha",
        StepId = stepId,
        OperationId = operationId,
        OperationGeneration = generation,
    };

    private sealed class RebuildingStepPlanReplyGenerator(
        IAgentTool tool,
        ILLMProvider provider,
        IAgentToolExecutionPort? toolExecutionPort = null,
        Func<bool>? staticToolAvailable = null) : IAgentRunStepConversationReplyGenerator
    {
        public Task<AgentRunReplyStepPlan> BuildStepPlanAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            LLMControlContext? llmControl,
            AgentToolExecutionContext? toolContext,
            IReadOnlyList<ConversationHistoryEntry>? priorHistory,
            ChatAttachmentInputContext? attachmentContext,
            bool forceDisableTools,
            CancellationToken ct,
            AgentTurnToolCatalog? turnCatalog = null)
        {
            var tools = new ToolManager();
            if (!forceDisableTools && (staticToolAvailable?.Invoke() ?? true))
            {
                tools.Register(turnCatalog is null
                    ? [tool]
                    : turnCatalog.ExactTools.Values);
            }
            var runtime = new ChatRuntime(
                () => provider,
                new ChatHistory(),
                new ToolCallLoop(
                    tools,
                    toolExecutionPort: toolExecutionPort ?? new PassthroughExecutionPort()),
                hooks: null,
                requestBuilder: _ => new LLMRequest
                {
                    Messages = [],
                    Tools = tools.GetAll(),
                    ToolContext = toolContext ?? AgentToolExecutionContext.Empty,
                });
            return Task.FromResult(new AgentRunReplyStepPlan(
                runtime.CreateStepExecutor(turnCatalog: turnCatalog),
                new Dictionary<string, string>(),
                llmControl ?? LLMControlContext.Empty,
                toolContext ?? AgentToolExecutionContext.Empty,
                [ChatMessage.User(activity.Content?.Text ?? string.Empty)],
                MaxToolRounds: 1,
                DisableTools: forceDisableTools));
        }

        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            LLMControlContext? llmControl,
            AgentToolExecutionContext? toolContext,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class StaticToolSetRegistry(
        string name,
        IReadOnlyList<IAgentTool> tools) : IToolSetRegistry
    {
        private IReadOnlyList<IAgentTool> _tools = tools;

        public void ReplaceTools(IReadOnlyList<IAgentTool> replacement) => _tools = replacement;

        public IReadOnlyList<string> GetRegisteredNames() => [name];

        public ToolSetResolveResult Resolve(string? requestedName) =>
            string.Equals(requestedName, name, StringComparison.Ordinal)
                ? ToolSetResolveResult.Success(name, [new StaticToolSource(_tools)])
                : ToolSetResolveResult.Failure(new ToolSetResolveError(
                    ToolSetResolveError.UnknownNameCode,
                    requestedName ?? string.Empty,
                    "missing",
                    GetRegisteredNames()));
    }

    private sealed class SourceToolSetRegistry(
        string name,
        IAgentToolSource source) : IToolSetRegistry
    {
        public IReadOnlyList<string> GetRegisteredNames() => [name];

        public ToolSetResolveResult Resolve(string? requestedName) =>
            string.Equals(requestedName, name, StringComparison.Ordinal)
                ? ToolSetResolveResult.Success(name, [source])
                : ToolSetResolveResult.Failure(new ToolSetResolveError(
                    ToolSetResolveError.UnknownNameCode,
                    requestedName ?? string.Empty,
                    "missing",
                    GetRegisteredNames()));
    }

    private sealed class CountingToolSource(IAgentToolSource inner) : IAgentToolSource
    {
        public int DiscoveryCount { get; private set; }

        public async Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(
            CancellationToken ct = default)
        {
            DiscoveryCount++;
            return await inner.DiscoverToolsAsync(ct);
        }
    }

    private sealed class PassthroughExecutionPort : IAgentToolExecutionPort
    {
        public async Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            using var contextScope = AgentToolContextScope.Push(request.ExecutionContext);
            var terminal = await request.Tool.ExecuteWithOutcomeAsync(
                request.ExecutionContext.Request.CallId ?? string.Empty,
                request.Tool.Name,
                request.ArgumentsJson,
                ct);
            var safety = request.Tool.GetCallSafety(request.ArgumentsJson);
            return new AgentToolExecutionOutcome(
                AgentToolExecutionOutcomeKind.Executed,
                terminal.ResultJson,
                terminal.Receipt!,
                IsMutation: !safety.IsReadOnly,
                FailureCode: string.Empty,
                SafeMessage: string.Empty,
                AgentToolExecutionFailureStage.None,
                TerminalInvoked: true,
                Retryable: false,
                AuditCompleted: false);
        }
    }

    private sealed class StaticToolSource(IReadOnlyList<IAgentTool> tools) : IAgentToolSource
    {
        public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(
            CancellationToken ct = default) => Task.FromResult(tools);
    }

    private sealed class NoMatchClassifier : IAgentProfileTurnClassifier
    {
        public Task<AgentProfileTurnClassificationResult> ClassifyAsync(
            AgentProfileTurnClassificationRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(AgentProfileTurnClassificationResult.NoMatch());
    }

    private sealed class AcceptingDelegationCredentialLifecycle
        : INyxIdChatDelegationCredentialLifecyclePort
    {
        public Task<NyxIdChatDelegationCredentialResolution> ResolveAsync(
            string delegationToken,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new NyxIdChatDelegationCredentialResolution(
                true,
                delegationToken));
        }
    }

    private sealed class RefreshingDelegationCredentialLifecycle(
        string expectedToken,
        string refreshedToken) : INyxIdChatDelegationCredentialLifecyclePort
    {
        public List<string> DelegationTokens { get; } = [];

        public Task<NyxIdChatDelegationCredentialResolution> ResolveAsync(
            string delegationToken,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            DelegationTokens.Add(delegationToken);
            return Task.FromResult(string.Equals(
                delegationToken,
                expectedToken,
                StringComparison.Ordinal)
                ? new NyxIdChatDelegationCredentialResolution(
                    true,
                    refreshedToken,
                    Refreshed: true)
                : new NyxIdChatDelegationCredentialResolution(
                    false,
                    Detail: "unexpected_delegation_token"));
        }
    }

    private sealed class ExactToolCallProvider(
        string toolName,
        string argumentsJson = "{\"approvalCode\":\"canary\"}") : ILLMProvider
    {
        public string Name => "exact-tool-call-provider";

        public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
            LLMRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new LLMStreamChunk
            {
                DeltaToolCall = new ToolCall
                {
                    Id = "call-effect",
                    Name = toolName,
                    ArgumentsJson = argumentsJson,
                },
            };
            await Task.Yield();
        }
    }

    private sealed record DurableRetryProxyResponse(
        HttpStatusCode StatusCode,
        string Body);

    private sealed class DurableRetryNyxIdHandler : HttpMessageHandler
    {
        public Dictionary<string, string> KeysByToken { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> CatalogsByToken { get; } = new(StringComparer.Ordinal);
        public HashSet<string> AllowedProxyTokens { get; } = new(StringComparer.Ordinal);
        public List<string> CatalogRequestTokens { get; } = [];
        public List<string> ProxyRequestTokens { get; } = [];
        public Queue<DurableRetryProxyResponse> ProxyResponses { get; } = new();
        public int ProxyRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var token = request.Headers.Authorization?.Parameter ?? string.Empty;
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/api/v1/keys")
            {
                CatalogRequestTokens.Add(token);
                return Task.FromResult(KeysByToken.TryGetValue(token, out var keys)
                    ? Json(keys)
                    : Json("{\"error\":\"invalid_token\"}", HttpStatusCode.Unauthorized));
            }
            if (path == "/api/v1/mcp/config")
            {
                CatalogRequestTokens.Add(token);
                return Task.FromResult(CatalogsByToken.TryGetValue(token, out var catalog)
                    ? Json(catalog)
                    : Json("{\"error\":\"invalid_token\"}", HttpStatusCode.Unauthorized));
            }
            if (path.StartsWith("/api/v1/proxy/", StringComparison.Ordinal))
            {
                ProxyRequests++;
                ProxyRequestTokens.Add(token);
                if (!AllowedProxyTokens.Contains(token))
                {
                    return Task.FromResult(Json(
                        "{\"error\":\"invalid_token\"}",
                        HttpStatusCode.Unauthorized));
                }
                var response = ProxyResponses.Dequeue();
                return Task.FromResult(Json(response.Body, response.StatusCode));
            }

            return Task.FromResult(Json("{}", HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(
            string body,
            HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class RecordingExecutionPort(IAgentToolExecutionPort inner)
        : IAgentToolExecutionPort
    {
        private readonly List<string> _outcomes = [];

        public async Task<AgentToolExecutionOutcome> ExecuteAsync(
            AgentToolExecutionRequest request,
            CancellationToken ct = default)
        {
            var safety = request.Tool.GetCallSafety(request.ArgumentsJson);
            var outcome = await inner.ExecuteAsync(request, ct);
            _outcomes.Add(
                $"tool={request.Tool.GetType().Name} " +
                $"approvalMode={request.Tool.ApprovalMode} " +
                $"requiresApproval={safety.RequiresApproval} " +
                $"kind={outcome.Kind} status={outcome.Receipt?.Status} " +
                $"approvalRequestId={outcome.Receipt?.ApprovalRequestId} " +
                $"nyxIdApprovalMode={outcome.Receipt?.NyxIdApprovalDecisionMode} " +
                $"errorCode={outcome.Receipt?.ErrorCode} " +
                $"result={outcome.ResultJson}");
            return outcome;
        }

        public string FormatOutcomes() => string.Join(" | ", _outcomes);
    }

    private sealed class AlwaysStartingAdmissionLedger : IAgentToolAdmissionLedger
    {
        public Task<AgentToolAdmissionResult> TryStartAsync(
            AgentToolAdmissionFact fact,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new AgentToolAdmissionResult(
                AgentToolAdmissionStatus.Started));
        }
    }

    private sealed class AppendedAuditTrail : IAuditTrailAppender
    {
        public Task<AuditTrailAppendResult> AppendAsync(
            AuditRecord record,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AuditTrailAppendResult.Appended(record.AuditId));
    }

    private sealed class StableIdentityHasher : IAuditActorIdentityHasher
    {
        public AuditActorIdentity Hash(string canonicalActorKey) =>
            new("actor-hash", "key-1");

        public bool Verify(
            string canonicalActorKey,
            string auditActorId,
            string identityKeyId) => true;
    }

    private sealed class RetryEffectTool : IAgentTool, IAgentToolOperationAdmissionOwner
    {
        private readonly AgentToolOperationAdmission _admission =
            AgentToolOperationAdmissionPayloadMapper.FromPayload(ExactWriteAdmission())!;

        public List<string> ExecutionTokens { get; } = [];
        public List<string> PerRequestApprovalIds { get; } = [];
        public bool IsBaseRouteAvailable { get; set; } = true;
        public string? DescriptionOverride { get; set; }
        public string Name => "connected-effect-alpha";
        public string Description => DescriptionOverride ?? "Create one exact connected-service approval record.";
        public string ParametersSchema =>
            "{\"type\":\"object\",\"properties\":{\"approvalCode\":{\"type\":\"string\"}},\"required\":[\"approvalCode\"]}";
        public ToolApprovalMode ApprovalMode => ToolApprovalMode.NeverRequire;
        public bool IsReadOnly => false;
        public bool IsDestructive => false;
        public string SideEffectKind => "connected_service_operation";
        public AgentToolOperationAdmission OperationAdmission => _admission;

        public AgentToolCallSafety GetCallSafety(string argumentsJson) => new(
            RequiresApproval: false,
            IsReadOnly: false,
            IsDestructive: false);

        public AgentToolReplayPolicy ResolveReplayPolicy(string argumentsJson) =>
            AgentToolReplayPolicy.NonReplayable;

        public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default) =>
            (await ExecuteWithOutcomeAsync(string.Empty, Name, argumentsJson, ct)).ResultJson;

        public Task<AgentToolTerminalOutcome> ExecuteWithOutcomeAsync(
            string callId,
            string toolName,
            string argumentsJson,
            CancellationToken ct = default)
        {
            var credentials = AgentToolRequestContext.Current?.Credentials;
            var token = credentials?.NyxIdAccessToken ??
                        credentials?.SourceReadableNyxIdAccessToken ??
                        string.Empty;
            ExecutionTokens.Add(token);
            var generation = ExecutionTokens.Count;
            var receipt = new AgentToolReceipt
            {
                CallId = callId,
                ToolName = toolName,
                Effect = AgentToolReceiptEffect.Mutating,
                SideEffectKind = SideEffectKind,
            };
            if (string.Equals(token, "uncertain-token", StringComparison.Ordinal))
            {
                receipt.Status = AgentToolReceiptStatus.Error;
                receipt.ErrorCode = "UPSTREAM_RESULT_LOST";
                receipt.ErrorMessage = "The upstream result could not be observed.";
            }
            else if (string.Equals(token, "fresh-per-request-token", StringComparison.Ordinal))
            {
                receipt.Status = AgentToolReceiptStatus.ApprovalRequired;
                receipt.ApprovalRequestId = $"approval-generation-{generation}";
                receipt.NyxIdApprovalDecisionMode = NyxIdApprovalDecisionMode.PerRequest;
                PerRequestApprovalIds.Add(receipt.ApprovalRequestId);
            }
            else if (string.Equals(token, "approved-per-request-token", StringComparison.Ordinal))
            {
                PerRequestApprovalIds.Add($"approval-generation-{generation}");
                receipt.Status = AgentToolReceiptStatus.Success;
                receipt.ProviderResourceId = $"resource-generation-{generation}";
                receipt.NyxIdApprovalDecisionMode = NyxIdApprovalDecisionMode.PerRequest;
            }
            else if (string.Equals(token, "valid-grant-token", StringComparison.Ordinal))
            {
                receipt.Status = AgentToolReceiptStatus.Success;
                receipt.ProviderResourceId = $"resource-generation-{generation}";
                receipt.NyxIdApprovalDecisionMode = NyxIdApprovalDecisionMode.Grant;
            }
            else if (token is "expired-grant-token" or
                     "revoked-grant-token" or
                     "scope-mismatched-grant-token" or
                     "ttl-expired-grant-token")
            {
                receipt.Status = AgentToolReceiptStatus.ApprovalRequired;
                receipt.ApprovalRequestId = $"approval-generation-{generation}";
                receipt.NyxIdApprovalDecisionMode = NyxIdApprovalDecisionMode.Grant;
            }
            else
            {
                receipt.Status = AgentToolReceiptStatus.Error;
                receipt.ErrorCode = "UNEXPECTED_TEST_CREDENTIAL";
                receipt.ErrorMessage = "The test credential was not admitted.";
            }
            receipt.ResultJson = "{\"status\":\"bounded\"}";
            return Task.FromResult(new AgentToolTerminalOutcome(receipt.ResultJson, receipt));
        }
    }

    private static AgentToolOperationAdmissionPayload ExactWriteAdmission() => new()
    {
        ServiceInstanceId = "svc-lark",
        ServiceSlug = "lark",
        PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
        {
            EndpointId = "lark-create-approval",
        },
        AuthorizationBasis = AgentToolOperationAuthorizationBasisPayload.PublishedContract,
        HttpMethod = "POST",
        PathTemplate = "/open-apis/approval/v4/instances",
        ContractDigest = new string('b', 64),
        CatalogDigest = $"sha256:{new string('a', 64)}",
        ExecutionPolicy = new AgentToolOperationExecutionPolicyPayload
        {
            Risk = AgentToolOperationRiskPayload.Write,
            Approval = AgentToolOperationApprovalPayload.Required,
            EnforcementOwner = AgentToolOperationEnforcementOwnerPayload.Aevatar,
            AllowedExecutionModes = { AgentToolOperationExecutionModePayload.Interactive },
        },
        ReadBack = new AgentToolOperationReadBackPayload
        {
            ReadOperation = new AgentToolOperationAdmissionPayload
            {
                ServiceInstanceId = "svc-lark",
                ServiceSlug = "lark",
                PublishedEndpoint = new AgentToolPublishedEndpointIdentityPayload
                {
                    EndpointId = "lark-get-approval",
                },
                AuthorizationBasis = AgentToolOperationAuthorizationBasisPayload.PublishedContract,
                HttpMethod = "GET",
                PathTemplate = "/open-apis/approval/v4/instances/{instance_id}",
                ContractDigest = new string('c', 64),
                CatalogDigest = $"sha256:{new string('a', 64)}",
                ExecutionPolicy = new AgentToolOperationExecutionPolicyPayload
                {
                    Risk = AgentToolOperationRiskPayload.ReadOnly,
                    Approval = AgentToolOperationApprovalPayload.None,
                    EnforcementOwner = AgentToolOperationEnforcementOwnerPayload.Aevatar,
                    AllowedExecutionModes = { AgentToolOperationExecutionModePayload.Interactive },
                },
                Parameters =
                {
                    new AgentToolOperationParameterPayload
                    {
                        Name = "instance_id",
                        Location = AgentToolOperationParameterLocationPayload.Path,
                        Required = true,
                        Schema = new AgentToolOperationValueSchemaPayload
                        {
                            Kind = AgentToolOperationValueKindPayload.String,
                        },
                    },
                },
            },
            Arguments = new Struct(),
            Assertion = new AgentToolReadBackAssertionPayload
            {
                Match = AgentToolReadBackMatchPayload.Equals,
                JsonPointer = "/data/instance_code",
                ExpectedValueSource = AgentToolReadBackExpectedValueSourcePayload.ProviderResourceId,
            },
            ProviderResourceArgument = new AgentToolReadBackProviderResourceArgumentPayload
            {
                Location = AgentToolOperationParameterLocationPayload.Path,
                ArgumentName = "instance_id",
            },
            CheckName = "approval_exists",
            EffectResultIdentityJsonPointer = "/data/instance_code",
        },
    };
}
