using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.AI.Core.Chat;
using Aevatar.AI.Core.Tools;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.NyxidChat;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StudioConfig = Aevatar.Studio.Application.Studio.Abstractions.UserConfig;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

/// <summary>
/// Pins the deferred-run Channel credential split of
/// <see cref="AgentRunReplyGenerationExecutor"/>: LLM calls use the registration
/// Agent Key, while a persisted sender binding and exact NyxID authority allow
/// connected-service tools to re-mint the sender's short-lived bearer token. On
/// <see cref="BindingRevokedException"/>
/// it triggers a local binding reconcile, leaves connected-service authority
/// empty, and keeps LLM credential resolution independent.
/// A binding whose grant lacks a required service is preserved so <c>/init</c>
/// can update that grant in place.
/// Without a sender binding it touches neither broker nor reconciler.
/// </summary>
public sealed class AgentRunReplyGenerationExecutorSenderTokenTests
{
    private const string SenderBindingId = "bnd_sender_x";

    [Fact]
    public async Task BuildInitialStepState_WithTypedNyxIdAuthority_ReMintsForExactAuthorityInsteadOfChannelIdentity()
    {
        var vault = new InMemorySecretVault();
        var stored = (await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ChannelNyxIdAgentKey,
            "scope-1",
            "key-reg-1",
            "registration-agent-key",
            "test channel registration agent key"))).Reference;
        var broker = Substitute.For<INyxIdCapabilityBroker>();
        broker
            .IssueShortLivedByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                SenderBindingId,
                Arg.Any<CapabilityScope>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CapabilityHandle { AccessToken = "fresh-sender-token" }));
        var reconciler = Substitute.For<IBindingRevocationReconciler>();
        var generator = new EchoStepPlanReplyGenerator();
        var executor = CreateExecutor(generator, broker, reconciler, secretVault: vault);

        var state = await executor.BuildInitialStepStateAsync(
            BuildRequest(
                senderBindingId: SenderBindingId,
                senderTenant: "legacy-channel-tenant",
                platform: "legacy-channel-platform",
                senderId: "legacy-channel-user",
                nyxIdAuthority: new AgentToolNyxIdAuthorityContext(
                    "LARK",
                    "tenant-authority-alpha",
                    "ou-authority-alpha"),
                workflowResultDeliveryCredential: new ChannelWorkflowResultDeliveryCredential
                {
                    SubjectId = "key-reg-1",
                    SecretReference = stored.Clone(),
                }),
            CancellationToken.None);

        // LLM authority is registration-owned; the freshly minted sender token is
        // confined to the connected-service tool context.
        generator.CapturedLlmControl.Should().NotBeNull();
        generator.CapturedLlmControl!.NyxIdAccessToken.Should().Be("registration-agent-key");
        generator.CapturedLlmControl.NyxIdOrgToken.Should().BeNull();
        generator.CapturedLlmControl.SenderNyxIdAccessToken.Should().BeNull();

        // And it must project into the resulting step-state tool credentials so
        // ToolCallCredentialPolicyMiddleware admits sender-credentialed tools.
        var toolContext = AgentToolExecutionContextMapper.FromPayload(state.ToolContext);
        toolContext.Credentials.NyxIdAccessToken.Should().Be("fresh-sender-token");
        toolContext.Credentials.SenderNyxIdAccessToken.Should().Be("fresh-sender-token");

        // The subject must come exclusively from the exact typed NyxID authority,
        // never from the independently-scoped channel routing identity.
        var subject = broker.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(INyxIdCapabilityBroker.IssueShortLivedByBindingIdAsync))
            .GetArguments()[0] as ExternalSubjectRef;
        subject.Should().NotBeNull();
        subject!.Platform.Should().Be("lark");
        subject.Tenant.Should().Be("tenant-authority-alpha");
        subject.ExternalUserId.Should().Be("ou-authority-alpha");

        await reconciler.DidNotReceiveWithAnyArgs()
            .ReconcileRevokedAsync(default!, default!, default);
    }

    [Fact]
    public async Task BuildInitialStepState_WithRegistrationAgentKeyModeAndSenderBinding_UsesAgentKeyForLlmAndConnectedServices()
    {
        var vault = new InMemorySecretVault();
        var stored = (await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ChannelNyxIdAgentKey,
            "scope-1",
            "key-reg-1",
            "registration-agent-key",
            "test channel registration agent key"))).Reference;
        var broker = Substitute.For<INyxIdCapabilityBroker>();
        var generator = new EchoStepPlanReplyGenerator();
        var executor = CreateExecutor(generator, broker, Substitute.For<IBindingRevocationReconciler>(), secretVault: vault);

        var state = await executor.BuildInitialStepStateAsync(
            BuildRequest(
                senderBindingId: SenderBindingId,
                senderTenant: "tenant-authority-alpha",
                credentialSourceMode: ChannelBotRuntimeCredentialSourceMode.RegistrationAgentKey,
                workflowResultDeliveryCredential: new ChannelWorkflowResultDeliveryCredential
                {
                    SubjectId = "key-reg-1",
                    SecretReference = stored.Clone(),
                }),
            CancellationToken.None);

        generator.CapturedLlmControl.Should().NotBeNull();
        generator.CapturedLlmControl!.NyxIdAccessToken.Should().Be("registration-agent-key");
        generator.CapturedLlmControl.NyxIdOrgToken.Should().BeNull();
        generator.CapturedLlmControl.SenderNyxIdAccessToken.Should().BeNull();

        var control = AgentRunReplyStepMappers.LlmControlFromProto(state);
        control.NyxIdAccessToken.Should().Be("registration-agent-key");
        control.SenderNyxIdAccessToken.Should().BeNull();

        generator.CapturedToolContext.Should().NotBeNull();
        generator.CapturedToolContext!.CredentialSource.Should().Be(AgentToolCredentialSource.ChannelRegistration);
        generator.CapturedToolContext.Credentials.NyxIdAccessToken.Should().Be("registration-agent-key");
        generator.CapturedToolContext.Credentials.SenderNyxIdAccessToken.Should().BeNull();
        generator.CapturedToolContext.Credentials.SourceReadableNyxIdAccessToken.Should().BeNull();
        generator.CapturedToolContext.Credentials.NyxIdCredentialKind.Should().Be(AgentToolNyxIdCredentialKind.AgentKey);
        generator.CapturedToolContext.DurableNyxIdCredential.Should().NotBeNull();

        var toolContext = AgentToolExecutionContextMapper.FromPayload(state.ToolContext);
        toolContext.CredentialSource.Should().Be(AgentToolCredentialSource.ChannelRegistration);
        toolContext.Credentials.NyxIdAccessToken.Should().Be("registration-agent-key");
        toolContext.Credentials.SenderNyxIdAccessToken.Should().BeNull();
        toolContext.Credentials.SourceReadableNyxIdAccessToken.Should().BeNull();
        toolContext.Credentials.NyxIdCredentialKind.Should().Be(AgentToolNyxIdCredentialKind.AgentKey);
        toolContext.DurableNyxIdCredential.Should().NotBeNull();
        await broker.DidNotReceiveWithAnyArgs()
            .IssueShortLivedByBindingIdAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task BuildInitialAndContinuation_WithSenderBindingMode_UseRegistrationAgentKeyForLlmAndSenderForConnectedServices()
    {
        var vault = new InMemorySecretVault();
        var stored = (await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ChannelNyxIdAgentKey,
            "scope-1",
            "key-reg-1",
            "registration-agent-key",
            "test channel registration agent key"))).Reference;
        var broker = Substitute.For<INyxIdCapabilityBroker>();
        broker
            .IssueShortLivedByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                SenderBindingId,
                Arg.Any<CapabilityScope>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CapabilityHandle { AccessToken = "fresh-sender-token" }));
        var generator = new EchoStepPlanReplyGenerator(disableTools: false);
        var executor = CreateExecutor(
            generator,
            broker,
            Substitute.For<IBindingRevocationReconciler>(),
            secretVault: vault);
        var workItem = BuildRequest(
            senderBindingId: SenderBindingId,
            senderTenant: "tenant-authority-alpha",
            credentialSourceMode: ChannelBotRuntimeCredentialSourceMode.SenderBinding,
            workflowResultDeliveryCredential: new ChannelWorkflowResultDeliveryCredential
            {
                SubjectId = "key-reg-1",
                SecretReference = stored.Clone(),
            });

        var state = await executor.BuildInitialStepStateAsync(workItem, CancellationToken.None);

        AssertAgentKeyLlmWithSenderToolCredential(generator.CapturedLlmControl, generator.CapturedToolContext);
        AssertAgentKeyLlmWithSenderToolCredential(
            AgentRunReplyStepMappers.LlmControlFromProto(state),
            AgentToolExecutionContextMapper.FromPayload(state.ToolContext));

        await ExecuteContinuationAsync(executor, workItem, state);

        AssertAgentKeyLlmWithSenderToolCredential(generator.CapturedLlmControl, generator.CapturedToolContext);
    }

    [Fact]
    public async Task BuildInitialAndContinuation_WithSenderBindingModeAndUnboundSender_DoNotProjectAgentKeyToTools()
    {
        var vault = new InMemorySecretVault();
        var stored = (await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ChannelNyxIdAgentKey,
            "scope-1",
            "key-reg-1",
            "registration-agent-key",
            "test channel registration agent key"))).Reference;
        var generator = new EchoStepPlanReplyGenerator(disableTools: false);
        var executor = CreateExecutor(
            generator,
            broker: null,
            Substitute.For<IBindingRevocationReconciler>(),
            secretVault: vault);
        var workItem = BuildRequest(
            senderBindingId: null,
            senderTenant: null,
            llmControl: new LLMControlContext(
                "owner-token",
                "owner-org-token",
                SenderNyxIdAccessToken: null,
                ModelOverride: null,
                NyxIdRoutePreference: null,
                MaxToolRoundsOverride: null,
                UserMemoryPrompt: null),
            credentialSourceMode: ChannelBotRuntimeCredentialSourceMode.SenderBinding,
            workflowResultDeliveryCredential: new ChannelWorkflowResultDeliveryCredential
            {
                SubjectId = "key-reg-1",
                SecretReference = stored.Clone(),
            });
        workItem.Request.Activity!.TransportExtras = new TransportExtras
        {
            NyxUserAccessToken = "activity-user-token",
        };

        var state = await executor.BuildInitialStepStateAsync(workItem, CancellationToken.None);

        generator.CapturedLlmControl.Should().NotBeNull();
        generator.CapturedLlmControl!.NyxIdAccessToken.Should().Be("registration-agent-key");
        generator.CapturedLlmControl.NyxIdOrgToken.Should().BeNull();
        generator.CapturedLlmControl.SenderNyxIdAccessToken.Should().BeNull();
        generator.CapturedToolContext.Should().NotBeNull();
        generator.CapturedToolContext!.Credentials.NyxIdAccessToken.Should().Be("activity-user-token");
        generator.CapturedToolContext.Credentials.NyxIdOrgToken.Should().Be("activity-user-token");
        generator.CapturedToolContext.Credentials.SenderNyxIdAccessToken.Should().BeNull();
        generator.CapturedToolContext.Credentials.NyxIdAccessToken.Should().NotBe("registration-agent-key");
        var persistedToolContext = AgentToolExecutionContextMapper.FromPayload(state.ToolContext);
        persistedToolContext.Credentials.NyxIdAccessToken.Should().Be("activity-user-token");
        persistedToolContext.Credentials.NyxIdAccessToken.Should().NotBe("registration-agent-key");

        await ExecuteContinuationAsync(executor, workItem, state);

        generator.CapturedLlmControl!.NyxIdAccessToken.Should().Be("registration-agent-key");
        generator.CapturedLlmControl.NyxIdOrgToken.Should().BeNull();
        generator.CapturedLlmControl.SenderNyxIdAccessToken.Should().BeNull();
        generator.CapturedToolContext!.Credentials.NyxIdAccessToken.Should().Be("activity-user-token");
        generator.CapturedToolContext.Credentials.NyxIdAccessToken.Should().NotBe("registration-agent-key");
    }

    [Fact]
    public async Task BuildInitialAndContinuation_WithSenderBindingModeAndMissingAgentKey_FailLlmClosedWithoutChangingSenderToolCredential()
    {
        var broker = Substitute.For<INyxIdCapabilityBroker>();
        broker
            .IssueShortLivedByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                SenderBindingId,
                Arg.Any<CapabilityScope>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CapabilityHandle { AccessToken = "fresh-sender-token" }));
        var generator = new EchoStepPlanReplyGenerator(disableTools: false);
        var executor = CreateExecutor(
            generator,
            broker,
            Substitute.For<IBindingRevocationReconciler>(),
            secretVault: new InMemorySecretVault());
        var workItem = BuildRequest(
            senderBindingId: SenderBindingId,
            senderTenant: "tenant-authority-alpha",
            llmControl: new LLMControlContext(
                "owner-token",
                "owner-org-token",
                "stale-sender-token",
                ModelOverride: null,
                NyxIdRoutePreference: null,
                MaxToolRoundsOverride: null,
                UserMemoryPrompt: null),
            credentialSourceMode: ChannelBotRuntimeCredentialSourceMode.SenderBinding,
            workflowResultDeliveryCredential: new ChannelWorkflowResultDeliveryCredential
            {
                SubjectId = "key-reg-1",
                SecretReference = new SecretReference
                {
                    Ref = "missing-agent-key",
                    Purpose = CredentialSecretPurposes.ChannelNyxIdAgentKey,
                    OwnerScopeKey = "scope-1",
                },
            });
        workItem.Request.Activity!.TransportExtras = new TransportExtras
        {
            NyxUserAccessToken = "activity-user-token",
        };

        var state = await executor.BuildInitialStepStateAsync(workItem, CancellationToken.None);

        generator.CapturedLlmControl.Should().NotBeNull();
        generator.CapturedLlmControl!.NyxIdAccessToken.Should().BeNull();
        generator.CapturedLlmControl.NyxIdOrgToken.Should().BeNull();
        generator.CapturedLlmControl.SenderNyxIdAccessToken.Should().BeNull();
        generator.CapturedToolContext.Should().NotBeNull();
        generator.CapturedToolContext!.Credentials.NyxIdAccessToken.Should().Be("fresh-sender-token");
        generator.CapturedToolContext.Credentials.SenderNyxIdAccessToken.Should().Be("fresh-sender-token");
        var persistedControl = AgentRunReplyStepMappers.LlmControlFromProto(state);
        persistedControl.NyxIdAccessToken.Should().BeNull();
        persistedControl.NyxIdOrgToken.Should().BeNull();
        persistedControl.SenderNyxIdAccessToken.Should().BeNull();

        await ExecuteContinuationAsync(executor, workItem, state);

        generator.CapturedLlmControl!.NyxIdAccessToken.Should().BeNull();
        generator.CapturedLlmControl.NyxIdOrgToken.Should().BeNull();
        generator.CapturedLlmControl.SenderNyxIdAccessToken.Should().BeNull();
        generator.CapturedToolContext!.Credentials.NyxIdAccessToken.Should().Be("fresh-sender-token");
        generator.CapturedToolContext.Credentials.SenderNyxIdAccessToken.Should().Be("fresh-sender-token");
    }

    [Fact]
    public async Task BuildToolStepContinuation_WithRegistrationAgentKeyModeAndSenderBinding_PreservesRegistrationContextWithoutSenderBearer()
    {
        var capabilityIssuer = Substitute.For<INyxIdConnectedServiceCapabilityIssuer>();
        capabilityIssuer
            .IssueByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                SenderBindingId,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CapabilityHandle { AccessToken = "fresh-sender-token" }));
        var executor = CreateExecutor(
            new EchoStepPlanReplyGenerator(),
            broker: null,
            Substitute.For<IBindingRevocationReconciler>(),
            connectedServiceCapabilityIssuer: capabilityIssuer);
        var request = BuildRequest(
            senderBindingId: SenderBindingId,
            senderTenant: "tenant-authority-alpha",
            credentialSourceMode: ChannelBotRuntimeCredentialSourceMode.RegistrationAgentKey).Request;
        var toolCall = new AgentRunToolCall
        {
            Id = "call-1",
            Name = "nyxid_invoke_operation",
            ArgumentsJson = "{}",
        };
        var staleAgentKeyContext = AgentToolExecutionContextMapper.FromPayload(request.ToolContext) with
        {
            CredentialSource = AgentToolCredentialSource.ChannelRegistration,
            Credentials = new AgentToolCredentials(
                "registration-agent-key",
                NyxIdOrgToken: null,
                SenderNyxIdAccessToken: null,
                NyxIdCredentialKind: AgentToolNyxIdCredentialKind.AgentKey,
                SourceReadableNyxIdAccessToken: null,
                NyxIdCredentialAuthority: AgentToolNyxIdCredentialAuthority.ToolExecutionContext),
        };
        AgentToolExecutionContext? executedContext = null;
        var authorizedStep = new AgentRunAuthorizedToolStep(
            "run-1",
            request.CorrelationId,
            attempt: 1,
            stepIndex: 2,
            [toolCall],
            staleAgentKeyContext,
            (context, _) =>
            {
                executedContext = context;
                return Task.FromResult(new AgentRunToolStepResult { AdvanceRound = true });
            });
        var stepState = new AgentRunReplyStepState
        {
            RunId = "run-1",
            CorrelationId = request.CorrelationId,
            Attempt = 1,
            NextStepIndex = 2,
            ToolContext = staleAgentKeyContext.ToPayload(),
        };
        stepState.PendingToolCalls.Add(toolCall.Clone());

        await executor.BuildToolStepContinuationAsync(
            new AgentRunReplyStepExecutionRequest(
                "run-1",
                "conversation-actor",
                Attempt: 1,
                StepIndex: 2,
                request,
                stepState),
            authorizedStep,
            CancellationToken.None);

        executedContext.Should().BeSameAs(staleAgentKeyContext);
        await capabilityIssuer.DidNotReceiveWithAnyArgs()
            .IssueByBindingIdAsync(default!, default!, default);
    }

    [Fact]
    public async Task BuildInitialStepState_WhenBindingRevoked_ReconcilesAndKeepsTokenEmpty()
    {
        var vault = new InMemorySecretVault();
        var stored = (await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ChannelNyxIdAgentKey,
            "scope-1",
            "key-reg-1",
            "registration-agent-key",
            "test channel registration agent key"))).Reference;
        var subjectSeen = new ExternalSubjectRef { Platform = "lark", Tenant = "ou_tenant_x", ExternalUserId = "ou_user_y" };
        var broker = Substitute.For<INyxIdCapabilityBroker>();
        broker
            .IssueShortLivedByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                SenderBindingId,
                Arg.Any<CapabilityScope>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<CapabilityHandle>>(_ => throw new BindingRevokedException(subjectSeen));
        var reconcileSignal = new TaskCompletionSource<(ExternalSubjectRef Subject, string Reason)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reconciler = Substitute.For<IBindingRevocationReconciler>();
        reconciler
            .ReconcileRevokedAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                reconcileSignal.TrySetResult(
                    (call.ArgAt<ExternalSubjectRef>(0), call.ArgAt<string>(1)));
                return Task.CompletedTask;
            });
        var generator = new EchoStepPlanReplyGenerator();
        var executor = CreateExecutor(generator, broker, reconciler, secretVault: vault);

        var state = await executor.BuildInitialStepStateAsync(
            BuildRequest(
                senderBindingId: SenderBindingId,
                senderTenant: "ou_tenant_x",
                workflowResultDeliveryCredential: new ChannelWorkflowResultDeliveryCredential
                {
                    SubjectId = "key-reg-1",
                    SecretReference = stored.Clone(),
                }),
            CancellationToken.None);

        // LLM authority remains registration-owned, but the failed sender authority
        // is fail-closed for connected services and never adopts the Agent Key.
        generator.CapturedLlmControl.Should().NotBeNull();
        generator.CapturedLlmControl!.NyxIdAccessToken.Should().Be("registration-agent-key");
        generator.CapturedLlmControl!.SenderNyxIdAccessToken.Should().BeNull();
        var toolContext = AgentToolExecutionContextMapper.FromPayload(state.ToolContext);
        toolContext.CredentialSource.Should().Be(AgentToolCredentialSource.BearerToken);
        toolContext.Credentials.NyxIdAccessToken.Should().BeNull();
        toolContext.Credentials.SenderNyxIdAccessToken.Should().BeNull();
        toolContext.Credentials.SourceReadableNyxIdAccessToken.Should().BeNull();
        toolContext.Credentials.NyxIdCredentialKind.Should()
            .Be(AgentToolNyxIdCredentialKind.SourceReadableUserBearer);
        toolContext.DurableNyxIdCredential.Should().BeNull();

        // Reconcile fires (best-effort, fire-and-forget) with the invalid_grant reason.
        // WaitAsync gives a deterministic one-shot timeout (throws TimeoutException if the
        // fire-and-forget reconcile never runs) without a polling Task.Delay.
        var (reconciledSubject, reason) = await reconcileSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        reason.Should().Be("nyx_invalid_grant");
        reconciledSubject.Platform.Should().Be("lark");
        reconciledSubject.ExternalUserId.Should().Be("ou_user_y");
    }

    [Fact]
    public async Task BuildInitialStepState_WhenBindingLacksRequiredService_PreservesBindingAndKeepsTokenEmpty()
    {
        var subjectSeen = new ExternalSubjectRef { Platform = "lark", Tenant = "ou_tenant_x", ExternalUserId = "ou_user_y" };
        var broker = Substitute.For<INyxIdCapabilityBroker>();
        broker
            .IssueShortLivedByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                SenderBindingId,
                Arg.Any<CapabilityScope>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<CapabilityHandle>>(_ => throw new BindingServiceAccessMismatchException(
                subjectSeen,
                ["https://nyxid.test/api/v1/proxy/s/chrono-llm-public"]));
        var reconciler = Substitute.For<IBindingRevocationReconciler>();
        var generator = new EchoStepPlanReplyGenerator();
        var executor = CreateExecutor(generator, broker, reconciler);

        var state = await executor.BuildInitialStepStateAsync(
            BuildRequest(senderBindingId: SenderBindingId, senderTenant: "ou_tenant_x"),
            CancellationToken.None);

        generator.CapturedLlmControl.Should().NotBeNull();
        generator.CapturedLlmControl!.SenderNyxIdAccessToken.Should().BeNull();
        AgentToolExecutionContextMapper.FromPayload(state.ToolContext)
            .Credentials.SenderNyxIdAccessToken.Should().BeNull();

        await reconciler.DidNotReceiveWithAnyArgs()
            .ReconcileRevokedAsync(default!, default!, default);
    }

    [Fact]
    public async Task BuildInitialStepState_WithoutSenderBinding_DoesNotTouchBrokerOrReconciler()
    {
        var broker = Substitute.For<INyxIdCapabilityBroker>();
        var reconciler = Substitute.For<IBindingRevocationReconciler>();
        var generator = new EchoStepPlanReplyGenerator();
        var executor = CreateExecutor(generator, broker, reconciler);

        var state = await executor.BuildInitialStepStateAsync(
            BuildRequest(senderBindingId: null, senderTenant: null),
            CancellationToken.None);

        generator.CapturedLlmControl.Should().NotBeNull();
        generator.CapturedLlmControl!.SenderNyxIdAccessToken.Should().BeNull();
        var toolContext = AgentToolExecutionContextMapper.FromPayload(state.ToolContext);
        toolContext.Credentials.SenderNyxIdAccessToken.Should().BeNull();

        await broker.DidNotReceiveWithAnyArgs()
            .IssueShortLivedByBindingIdAsync(default!, default!, default!, default);
        await broker.DidNotReceiveWithAnyArgs()
            .IssueShortLivedAsync(default!, default!, default);
        await reconciler.DidNotReceiveWithAnyArgs()
            .ReconcileRevokedAsync(default!, default!, default);
    }

    [Fact]
    public async Task BuildInitialStepState_WhenCapabilityBrokerMissing_KeepsSenderTokenEmpty()
    {
        var generator = new EchoStepPlanReplyGenerator();
        var executor = CreateExecutor(generator, broker: null, Substitute.For<IBindingRevocationReconciler>());

        var state = await executor.BuildInitialStepStateAsync(
            BuildRequest(senderBindingId: SenderBindingId, senderTenant: "ou_tenant_x"),
            CancellationToken.None);

        generator.CapturedLlmControl.Should().NotBeNull();
        generator.CapturedLlmControl!.SenderNyxIdAccessToken.Should().BeNull();
        AgentToolExecutionContextMapper.FromPayload(state.ToolContext)
            .Credentials.SenderNyxIdAccessToken.Should().BeNull();
    }

    [Fact]
    public async Task BuildInitialStepState_WhenTypedNyxIdAuthorityIsMissing_DoesNotGuessFromCompleteChannelIdentity()
    {
        var broker = Substitute.For<INyxIdCapabilityBroker>();
        var generator = new EchoStepPlanReplyGenerator();
        var executor = CreateExecutor(generator, broker, Substitute.For<IBindingRevocationReconciler>());

        var state = await executor.BuildInitialStepStateAsync(
            BuildRequest(
                senderBindingId: SenderBindingId,
                senderTenant: "ou_tenant_x",
                platform: "lark",
                senderId: "ou-channel-alpha",
                nyxIdAuthority: AgentToolNyxIdAuthorityContext.Empty),
            CancellationToken.None);

        generator.CapturedLlmControl.Should().NotBeNull();
        generator.CapturedLlmControl!.SenderNyxIdAccessToken.Should().BeNull();
        AgentToolExecutionContextMapper.FromPayload(state.ToolContext)
            .Credentials.SenderNyxIdAccessToken.Should().BeNull();
        await broker.DidNotReceiveWithAnyArgs()
            .IssueShortLivedByBindingIdAsync(default!, default!, default!, default);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BuildInitialStepState_WhenRemintedSenderTokenIsEmpty_KeepsOwnerFallback(string returnedToken)
    {
        var broker = Substitute.For<INyxIdCapabilityBroker>();
        broker
            .IssueShortLivedByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                SenderBindingId,
                Arg.Any<CapabilityScope>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CapabilityHandle { AccessToken = returnedToken }));
        var generator = new EchoStepPlanReplyGenerator();
        var executor = CreateExecutor(generator, broker, Substitute.For<IBindingRevocationReconciler>());

        var state = await executor.BuildInitialStepStateAsync(
            BuildRequest(senderBindingId: SenderBindingId, senderTenant: "ou_tenant_x"),
            CancellationToken.None);

        generator.CapturedLlmControl.Should().NotBeNull();
        generator.CapturedLlmControl!.SenderNyxIdAccessToken.Should().BeNull();
        AgentToolExecutionContextMapper.FromPayload(state.ToolContext)
            .Credentials.SenderNyxIdAccessToken.Should().BeNull();
        AgentRunReplyStepMappers.LlmControlFromProto(state).SenderNyxIdAccessToken.Should().BeNull();
        LLMControlContextMapper.FromPayload(state.OwnerFallbackLlmControl)
            .SenderNyxIdAccessToken.Should().BeNull();
    }

    [Fact]
    public async Task BuildInitialStepState_WhenBotOwnerScopeResolves_AppliesUserConfigToLlmControl()
    {
        var scopeResolver = Substitute.For<INyxIdRelayScopeResolver>();
        scopeResolver.ResolveScopeIdByApiKeyAsync("api-key-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("owner-scope-1"));
        var userConfigQueryPort = Substitute.For<IUserConfigQueryPort>();
        userConfigQueryPort.GetAsync(
                UserConfigResourceKey.ForOwnerScope("owner-scope-1"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StudioConfig(
                DefaultModel: " owner-model ",
                PreferredLlmRoute: " /api/v1/proxy/s/owner ",
                MaxToolRounds: 7,
                LlmSelection: new LLMSelection
                {
                    RouteKind = LLMRouteKind.NyxIdUserService,
                    RouteValue = "/api/v1/proxy/s/owner",
                    NyxIdUserServiceId = "us-owner",
                    ServiceSlugSnapshot = "owner",
                    ModelSelection = new LLMModelSelection
                    {
                        Kind = LLMModelSelectionKind.ExplicitModel,
                        ModelId = "owner-model",
                    },
                })));
        var generator = new EchoStepPlanReplyGenerator();
        var executor = CreateExecutor(
            generator,
            broker: null,
            Substitute.For<IBindingRevocationReconciler>(),
            scopeResolver,
            userConfigQueryPort);

        var state = await executor.BuildInitialStepStateAsync(
            BuildRequest(senderBindingId: null, senderTenant: null, botId: "api-key-1"),
            CancellationToken.None);

        generator.CapturedLlmControl.Should().NotBeNull();
        generator.CapturedLlmControl!.ModelOverride.Should().Be("owner-model");
        generator.CapturedLlmControl.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/owner");
        generator.CapturedLlmControl.MaxToolRoundsOverride.Should().Be(7);
        var stateControl = AgentRunReplyStepMappers.LlmControlFromProto(state);
        stateControl.ModelOverride.Should().Be("owner-model");
        stateControl.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/owner");
        stateControl.MaxToolRoundsOverride.Should().Be(7);
    }

    [Fact]
    public async Task BuildInitialStepState_WhenBotOwnerScopeIsUnresolved_KeepsIncomingLlmControl()
    {
        var scopeResolver = Substitute.For<INyxIdRelayScopeResolver>();
        scopeResolver.ResolveScopeIdByApiKeyAsync("api-key-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        var userConfigQueryPort = Substitute.For<IUserConfigQueryPort>();
        var generator = new EchoStepPlanReplyGenerator();
        var executor = CreateExecutor(
            generator,
            broker: null,
            Substitute.For<IBindingRevocationReconciler>(),
            scopeResolver,
            userConfigQueryPort);

        var state = await executor.BuildInitialStepStateAsync(
            BuildRequest(
                senderBindingId: null,
                senderTenant: null,
                botId: "api-key-1",
                llmControl: new LLMControlContext(
                    NyxIdAccessToken: null,
                    NyxIdOrgToken: null,
                    SenderNyxIdAccessToken: null,
                    ModelOverride: "incoming-model",
                    NyxIdRoutePreference: "/api/v1/proxy/s/incoming",
                    MaxToolRoundsOverride: 3,
                    UserMemoryPrompt: null)),
            CancellationToken.None);

        generator.CapturedLlmControl.Should().NotBeNull();
        generator.CapturedLlmControl!.ModelOverride.Should().Be("incoming-model");
        generator.CapturedLlmControl.NyxIdRoutePreference.Should().Be("/api/v1/proxy/s/incoming");
        generator.CapturedLlmControl.MaxToolRoundsOverride.Should().Be(3);
        AgentRunReplyStepMappers.LlmControlFromProto(state).Should().Be(generator.CapturedLlmControl);
        await userConfigQueryPort.DidNotReceive().GetAsync(
            Arg.Any<UserConfigResourceKey>(),
            Arg.Any<CancellationToken>());
    }

    private static AgentRunReplyGenerationExecutor CreateExecutor(
        EchoStepPlanReplyGenerator generator,
        INyxIdCapabilityBroker? broker,
        IBindingRevocationReconciler? reconciler,
        INyxIdRelayScopeResolver? scopeResolver = null,
        IUserConfigQueryPort? userConfigQueryPort = null,
        ISecretVault? secretVault = null,
        INyxIdConnectedServiceCapabilityIssuer? connectedServiceCapabilityIssuer = null) =>
        new(
            Substitute.For<IActorDispatchPort>(),
            generator,
            interactiveReplyCollector: null,
            relayOptions: null,
            NullLogger<AgentRunReplyGenerationExecutor>.Instance,
            scopeResolver: scopeResolver,
            userConfigQueryPort: userConfigQueryPort,
            timeProvider: null,
            capabilityBroker: broker,
            bindingRevocationReconciler: reconciler,
            secretVault: secretVault,
            connectedServiceCapabilityIssuer: connectedServiceCapabilityIssuer);

    private static void AssertAgentKeyLlmWithSenderToolCredential(
        LLMControlContext? control,
        AgentToolExecutionContext? toolContext)
    {
        control.Should().NotBeNull();
        control!.NyxIdAccessToken.Should().Be("registration-agent-key");
        control.NyxIdOrgToken.Should().BeNull();
        control.SenderNyxIdAccessToken.Should().BeNull();
        toolContext.Should().NotBeNull();
        toolContext!.Credentials.NyxIdAccessToken.Should().Be("fresh-sender-token");
        toolContext.Credentials.NyxIdOrgToken.Should().BeNull();
        toolContext.Credentials.SenderNyxIdAccessToken.Should().Be("fresh-sender-token");
        toolContext.Credentials.SourceReadableNyxIdAccessToken.Should().Be("fresh-sender-token");
        toolContext.Credentials.NyxIdCredentialKind.Should().Be(AgentToolNyxIdCredentialKind.SourceReadableUserBearer);
        toolContext.Credentials.NyxIdCredentialAuthority.Should()
            .Be(AgentToolNyxIdCredentialAuthority.ToolExecutionContext);
    }

    private static async Task ExecuteContinuationAsync(
        AgentRunReplyGenerationExecutor executor,
        AgentRunReplyGenerationExecutionRequest workItem,
        AgentRunReplyStepState state)
    {
        var persistedState = AgentRunReplyStepCredentials.StripRuntimeCredentials(state);
        await executor.BuildLlmStepExecutionAsync(
            new AgentRunReplyStepExecutionRequest(
                workItem.RunId,
                workItem.RunActorId,
                workItem.Attempt,
                persistedState.NextStepIndex,
                workItem.Request.Clone(),
                persistedState),
            CancellationToken.None);
    }

    private static AgentRunReplyGenerationExecutionRequest BuildRequest(
        string? senderBindingId,
        string? senderTenant,
        string? platform = "lark",
        string? senderId = "ou_user_y",
        string botId = "reg-1",
        LLMControlContext? llmControl = null,
        AgentToolNyxIdAuthorityContext? nyxIdAuthority = null,
        ChannelBotRuntimeCredentialSourceMode credentialSourceMode =
            ChannelBotRuntimeCredentialSourceMode.SenderBinding,
        ChannelWorkflowResultDeliveryCredential? workflowResultDeliveryCredential = null)
    {
        var toolContext = AgentToolExecutionContext.Empty with
        {
            ExecutionOwner = AgentToolExecutionOwners.ChannelRegistration("reg-1"),
            Channel = new AgentToolChannelContext(
                Platform: platform,
                SenderId: senderId,
                RegistrationScopeId: "scope-1",
                MessageId: "msg-1",
                PlatformMessageId: null,
                WorkflowResultDeliveryCredential: workflowResultDeliveryCredential,
                BotRegistrationId: "reg-1"),
            SenderBinding = senderBindingId is null
                ? AgentToolSenderBindingContext.Empty
                : new AgentToolSenderBindingContext(senderBindingId, NyxUserId: null, SenderTenant: senderTenant),
            NyxIdAuthority = nyxIdAuthority ?? (senderBindingId is null
                ? AgentToolNyxIdAuthorityContext.Empty
                : new AgentToolNyxIdAuthorityContext(platform, senderTenant, senderId)),
        };

        var evt = new NeedsLlmReplyEvent
        {
            RunId = "run-1",
            CorrelationId = "corr-1",
            TargetActorId = "conversation-actor",
            Activity = new ChatActivity
            {
                Id = "activity-1",
                Bot = BotInstanceId.From(botId),
                Content = new MessageContent { Text = "/chrono-llm-token-usage" },
            },
            ToolContext = toolContext.ToPayload(),
            LlmControl = (llmControl ?? LLMControlContext.Empty).ToPayload(),
            ChannelRuntimeConfig = new ChannelRuntimeConfigProof
            {
                RegistrationId = "reg-1",
                ConfigRevision = 1,
                CredentialSourceMode = credentialSourceMode,
            },
        };

        return new AgentRunReplyGenerationExecutionRequest(
            RunId: "run-1", RunActorId: "conversation-actor", Attempt: 1, Request: evt);
    }

    /// <summary>
    /// Reply generator that records the llm control + tool context handed to
    /// BuildStepPlanAsync and returns a plan whose ToolContext is the control
    /// projected onto the incoming tool context — mirroring how production maps
    /// control credentials into the tool context.
    /// </summary>
    private sealed class EchoStepPlanReplyGenerator : IAgentRunStepConversationReplyGenerator
    {
        private readonly bool _disableTools;

        public EchoStepPlanReplyGenerator(bool disableTools = true)
        {
            _disableTools = disableTools;
        }

        public LLMControlContext? CapturedLlmControl { get; private set; }
        public AgentToolExecutionContext? CapturedToolContext { get; private set; }

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
            CapturedLlmControl = llmControl;
            CapturedToolContext = toolContext;

            var control = llmControl ?? LLMControlContext.Empty;
            var projectedToolContext = control.ToToolContext(toolContext ?? AgentToolExecutionContext.Empty);
            var runtime = new ChatRuntime(
                providerFactory: () => new NoopProvider(),
                history: new ChatHistory(),
                toolLoop: new ToolCallLoop(new ToolManager()),
                hooks: null,
                requestBuilder: static _ => new LLMRequest { Messages = [] });
            var plan = new AgentRunReplyStepPlan(
                runtime.CreateStepExecutor(turnCatalog: null),
                new Dictionary<string, string>(metadata, StringComparer.Ordinal),
                control,
                projectedToolContext,
                InitialMessages: [],
                MaxToolRounds: 1,
                DisableTools: _disableTools);
            return Task.FromResult(plan);
        }

        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            LLMControlContext? llmControl,
            AgentToolExecutionContext? toolContext,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) =>
            throw new NotSupportedException("Sender-token tests drive BuildInitialStepStateAsync only.");

        public Task<ConversationReplyResult> GenerateReplyAsync(
            ChatActivity activity,
            IReadOnlyDictionary<string, string> metadata,
            IStreamingReplySink? streamingSink,
            CancellationToken ct) =>
            throw new NotSupportedException("Sender-token tests drive BuildInitialStepStateAsync only.");

        private sealed class NoopProvider : ILLMProvider
        {
            public string Name => "noop-provider";

            public async IAsyncEnumerable<LLMStreamChunk> ChatStreamAsync(
                LLMRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                yield break;
            }
        }
    }
}
