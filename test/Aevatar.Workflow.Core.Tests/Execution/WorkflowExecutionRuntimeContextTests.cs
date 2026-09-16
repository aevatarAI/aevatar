using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.Foundation.Abstractions.Credentials.Testing;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Execution;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Workflow.Core.Tests.Execution;

public sealed class WorkflowExecutionRuntimeContextTests
{
    [Fact]
    public async Task SetRequestMetadata_ShouldNotPromoteConnectorAuthorization_AndGuardPassthrough()
    {
        var host = new RecordingStateHost(new InMemoryRuntimeSecretStore());

        await WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadataAsync(
            host,
            new Dictionary<string, string>
            {
                [" trace-id "] = "  abc  ",
                ["llm.model_override"] = " model ",
                ["model_override"] = " model ",
                ["llm.max_tool_rounds"] = "3",
                ["llm.user_memory_prompt"] = " memory ",
                ["connector.http.authorization"] = " Bearer secret ",
                [" "] = "ignored",
                ["empty"] = " ",
            });

        host.ExecutionContextState.CallerCredential.Should().BeNull();
        host.ExecutionContextState.Llm.Should().BeNull();
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().ContainKey("trace-id");
        host.RuntimeContext.RequestPassthroughMetadata.Values["trace-id"].Should().Be("abc");
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().NotContainKey("connector.http.authorization");
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().NotContainKey("llm.model_override");
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().NotContainKey("model_override");
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().NotContainKey("llm.max_tool_rounds");
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().NotContainKey("llm.user_memory_prompt");
    }

    [Fact]
    public async Task SetLlmControl_ShouldPromoteOnlyDurableLlmValuesToTypedState()
    {
        var host = new RecordingStateHost();

        await WorkflowRequestMetadataRuntimeContextAccess.SetLlmControlAsync(
            host,
            new WorkflowLlmControlContext
            {
                ModelOverride = " model ",
                MaxToolRoundsOverride = 3,
                UserMemoryPrompt = " memory ",
                RoutePreference = " route-a ",
            });

        host.ExecutionContextState.Llm.ModelOverride.Should().Be("model");
        host.ExecutionContextState.Llm.MaxToolRoundsOverride.Should().Be(3);
        host.ExecutionContextState.Llm.UserMemoryPrompt.Should().Be("memory");
        host.ExecutionContextState.Llm.RoutePreference.Should().Be("route-a");
    }

    [Fact]
    public async Task SetRequestMetadata_ShouldOnlyClearPassthroughWhenMetadataIsNullEmptyOrInvalid()
    {
        var host = new RecordingStateHost(new InMemoryRuntimeSecretStore());
        await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
            host,
            new WorkflowCallerCredential { BearerToken = "typed" });
        await WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadataAsync(
            host,
            new Dictionary<string, string>
            {
                ["trace-id"] = "abc",
                ["connector.http.authorization"] = "Bearer secret",
            });

        await WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadataAsync(host, null);

        host.ExecutionContextState.CallerCredential!.BearerToken.Should().BeEmpty();
        host.ExecutionContextState.CallerCredential.RuntimeSecretReference.Should().NotBeNull();
        host.ExecutionContextState.CallerCredential.RuntimeSecretReference.Purpose
            .Should().Be(CredentialSecretPurposes.WorkflowCallerBearerToken);
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().BeEmpty();

        await WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadataAsync(
            host,
            new Dictionary<string, string>
            {
                [" "] = " ",
            });

        host.ExecutionContextState.CallerCredential!.BearerToken.Should().BeEmpty();
        host.ExecutionContextState.CallerCredential.RuntimeSecretReference.Should().NotBeNull();
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task SetRequestMetadata_ShouldThrowAndNotMutatePassthroughWhenCancellationRequested()
    {
        var host = new RecordingStateHost();
        await WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadataAsync(
            host,
            new Dictionary<string, string>
            {
                ["trace-id"] = "abc",
            });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadataAsync(
                host,
                new Dictionary<string, string>
                {
                    ["trace-id"] = "changed",
                    ["request-id"] = "request-1",
                },
                cts.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();

        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().ContainSingle();
        host.RuntimeContext.RequestPassthroughMetadata.Values["trace-id"].Should().Be("abc");
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().NotContainKey("request-id");
    }

    [Fact]
    public async Task RemoveRequestMetadata_ShouldValidateAndClearTypedExecutionContext()
    {
        var host = new RecordingStateHost();
        await WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadataAsync(
            host,
            new Dictionary<string, string>
            {
                ["trace-id"] = "abc",
                ["connector.http.authorization"] = "Bearer secret",
            });
        await WorkflowRequestMetadataRuntimeContextAccess.SetLlmControlAsync(
            host,
            new WorkflowLlmControlContext
            {
                ModelOverride = "model",
            });

        await WorkflowRequestMetadataRuntimeContextAccess.RemoveRequestMetadataAsync(host);

        host.ExecutionContextState.Llm.Should().BeNull();
        host.ExecutionContextState.CallerCredential.Should().BeNull();
        host.RuntimeContext.RequestPassthroughMetadata.Values.Should().BeEmpty();

        await FluentActions.Awaiting(() => WorkflowRequestMetadataRuntimeContextAccess.RemoveRequestMetadataAsync(null!))
            .Should()
            .ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void BuildCallerCredentialDelta_ShouldPromoteOnlyTypedCallerCredential()
    {
        var delta = WorkflowRunExecutionContextStateAccess.BuildCallerCredentialDelta(
            new WorkflowCallerCredential
            {
                BearerToken = " secret ",
                NyxIdAuthority = CreateCallerAuthority(),
            });

        delta.ClearCallerCredential.Should().BeTrue();
        delta.CallerCredential!.BearerToken.Should().BeEmpty();
        delta.CallerCredential.RuntimeSecretReference.Should().NotBeNull();
        delta.CallerCredential.RuntimeSecretReference.Purpose.Should().Be(CredentialSecretPurposes.WorkflowCallerBearerToken);
        delta.CallerCredential.RuntimeSecretReference.OwnerRunId.Should().Be("run-1");
        delta.CallerCredential.RuntimeSecretReference.OwnerStepId.Should().Be("workflow.caller");
        delta.CallerCredential.NyxIdAuthority.ExternalUserId.Should().Be("external-user-42");

        var emptyDelta = WorkflowRunExecutionContextStateAccess.BuildCallerCredentialDelta(
            new WorkflowCallerCredential { BearerToken = " " });
        emptyDelta.ClearCallerCredential.Should().BeTrue();
        emptyDelta.CallerCredential.Should().BeNull();

        FluentActions.Invoking(() => WorkflowRunExecutionContextStateAccess.BuildCallerCredentialDelta(
                new WorkflowCallerCredential { BearerToken = "Bearer secret" }))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*caller credential*invalid*");

        var durableRef = new DurableCallerCredentialRef
        {
            Ref = "sec_scheduled",
            Purpose = CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
            OwnerScopeKey = "schedule:schedule-1",
            SubjectId = "subject-1",
            SourceKind = DurableCallerCredentialSourceKind.ScheduledDispatch,
        };
        var durableDelta = WorkflowRunExecutionContextStateAccess.BuildCallerCredentialDelta(
            new WorkflowCallerCredential
            {
                DurableCallerCredential = durableRef,
                NyxIdAuthority = CreateCallerAuthority(),
            });
        durableDelta.ClearCallerCredential.Should().BeTrue();
        durableDelta.CallerCredential!.BearerToken.Should().BeEmpty();
        durableDelta.CallerCredential.RuntimeSecretReference.Should().BeNull();
        durableDelta.CallerCredential.DurableCallerCredential.Should().NotBeNull();
        durableDelta.CallerCredential.DurableCallerCredential.Ref.Should().Be("sec_scheduled");
        durableDelta.CallerCredential.NyxIdAuthority.Scope.Should().Be("proxy");

        FluentActions.Invoking(() => WorkflowRunExecutionContextStateAccess.BuildCallerCredentialDelta(
                new WorkflowCallerCredential
                {
                    BearerToken = "secret",
                    DurableCallerCredential = durableRef,
                }))
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*must not carry both durable and bearer credentials*");
    }

    [Fact]
    public async Task WorkflowCallerCredentialRuntimeAccess_ShouldStoreBearerInRuntimeSecretStore_AndPersistOnlyReference()
    {
        var runtimeSecrets = new InMemoryRuntimeSecretStore();
        var host = new RecordingStateHost(runtimeSecrets);

        await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
            host,
            new WorkflowCallerCredential { BearerToken = " secret " });

        host.ExecutionContextState.CallerCredential!.BearerToken.Should().BeEmpty();
        host.ExecutionContextState.CallerCredential.RuntimeSecretReference.Should().NotBeNull();
        host.ExecutionContextState.CallerCredential.RuntimeSecretReference.Purpose
            .Should().Be(CredentialSecretPurposes.WorkflowCallerBearerToken);

        var credential = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(host);
        credential.Found.Should().BeTrue();
        credential.Credential.BearerToken.Should().Be("secret");

        await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
            host,
            new WorkflowCallerCredential { BearerToken = " " });

        host.ExecutionContextState.CallerCredential.Should().BeNull();

        await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
            host,
            new WorkflowCallerCredential { BearerToken = "secret" });
        await WorkflowCallerCredentialRuntimeContextAccess.RemoveCredentialAsync(host);

        host.ExecutionContextState.CallerCredential.Should().BeNull();

        await FluentActions.Awaiting(() => WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
                host,
                new WorkflowCallerCredential
                {
                    BearerToken = "secret",
                    DurableCallerCredential = CreateDurableCallerCredentialRef(),
                }))
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*must not carry both durable and bearer credentials*");
        host.ExecutionContextState.CallerCredential.Should().BeNull();

        await FluentActions.Awaiting(() => WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
                host,
                new WorkflowCallerCredential { BearerToken = "Bearer secret" }))
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*caller credential*invalid*");

        await FluentActions.Awaiting(() => WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
                null!,
                new WorkflowCallerCredential { BearerToken = "secret" }))
            .Should()
            .ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => WorkflowCallerCredentialRuntimeContextAccess.RemoveCredentialAsync(null!))
            .Should()
            .ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WorkflowCallerCredentialRuntimeAccess_ShouldStoreAndRecoverDistinctExecutionAndSourceReadableCredentials()
    {
        var runtimeSecrets = new InMemoryRuntimeSecretStore();
        var host = new RecordingStateHost(runtimeSecrets);

        await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
            host,
            new WorkflowCallerCredential
            {
                BearerToken = "delegation-alpha",
                Kind = NyxIdCallerCredentialKind.ProxyDelegation,
                SourceReadableUserBearerToken = "source-alpha",
            });

        var state = host.ExecutionContextState.CallerCredential!;
        state.BearerToken.Should().BeEmpty();
        state.SourceReadableUserBearerToken.Should().BeEmpty();
        state.RuntimeSecretReference.Should().NotBeNull();
        state.SourceReadableUserBearerRuntimeSecretReference.Should().NotBeNull();
        state.RuntimeSecretReference.Ref.Should().NotBe(
            state.SourceReadableUserBearerRuntimeSecretReference.Ref);

        var resolved = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(host);

        resolved.Found.Should().BeTrue();
        resolved.Credential.BearerToken.Should().Be("delegation-alpha");
        resolved.Credential.Kind.Should().Be(NyxIdCallerCredentialKind.ProxyDelegation);
        resolved.Credential.SourceReadableUserBearerToken.Should().Be("source-alpha");
    }

    [Fact]
    public async Task WorkflowCallerCredentialRuntimeAccess_ShouldPersistAndRecoverCredentialKind()
    {
        var host = new RecordingStateHost(new InMemoryRuntimeSecretStore());

        await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
            host,
            new WorkflowCallerCredential
            {
                BearerToken = "delegation-token",
                Kind = NyxIdCallerCredentialKind.ProxyDelegation,
            });

        var serializedState = WorkflowRunExecutionContextState.Parser.ParseFrom(
            host.ExecutionContextState.ToByteArray());
        serializedState.CallerCredential!.Kind.Should().Be(NyxIdCallerCredentialKind.ProxyDelegation);

        var credential = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(host);

        credential.Found.Should().BeTrue();
        credential.Credential.BearerToken.Should().Be("delegation-token");
        credential.Credential.Kind.Should().Be(NyxIdCallerCredentialKind.ProxyDelegation);
    }

    [Fact]
    public async Task WorkflowCallerCredentialRuntimeAccess_ShouldPersistAndRecoverAuthorityWithoutSecretReference()
    {
        var host = new RecordingStateHost();

        await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
            host,
            new WorkflowCallerCredential { NyxIdAuthority = CreateCallerAuthority() });

        host.ExecutionContextState.CallerCredential.Should().NotBeNull();
        host.ExecutionContextState.CallerCredential!.RuntimeSecretReference.Should().BeNull();
        host.ExecutionContextState.CallerCredential.DurableCallerCredential.Should().BeNull();
        host.ExecutionContextState.CallerCredential.NyxIdAuthority.Should().BeEquivalentTo(CreateCallerAuthority());

        var credential = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(host);

        credential.Found.Should().BeTrue();
        credential.Credential.BearerToken.Should().BeEmpty();
        credential.Credential.NyxIdAuthority.Should().BeEquivalentTo(CreateCallerAuthority());
    }

    [Fact]
    public async Task WorkflowCallerCredentialRuntimeAccess_ShouldResolveDurableHandleThroughVault()
    {
        var vault = new InMemorySecretVault();
        var stored = await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
            "schedule:schedule-1",
            "lark:tenant:user-1",
            " durable-token ",
            "test"));
        var handle = new DurableCallerCredentialRef
        {
            Ref = stored.Reference.Ref,
            Purpose = stored.Reference.Purpose,
            OwnerScopeKey = stored.Reference.OwnerScopeKey,
            SubjectId = "lark:tenant:user-1",
            SourceKind = DurableCallerCredentialSourceKind.ScheduledDispatch,
        };
        var host = new RecordingStateHost(secretVault: vault);

        await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
            host,
            new WorkflowCallerCredential
            {
                DurableCallerCredential = handle,
                NyxIdAuthority = CreateCallerAuthority(),
            });

        host.ExecutionContextState.CallerCredential!.BearerToken.Should().BeEmpty();
        host.ExecutionContextState.CallerCredential.RuntimeSecretReference.Should().BeNull();
        host.ExecutionContextState.CallerCredential.DurableCallerCredential.Ref.Should().Be(stored.Reference.Ref);

        var credential = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(host);

        credential.Found.Should().BeTrue();
        credential.Credential.BearerToken.Should().Be("durable-token");
        credential.Credential.NyxIdAuthority.Should().BeEquivalentTo(CreateCallerAuthority());
    }

    [Fact]
    public async Task WorkflowCallerCredentialRuntimeAccess_ShouldLateResolveBorrowedScheduledAgentKeyOnEveryCall()
    {
        var vault = new DeterministicBorrowedCredentialVault("agent-key-token", "rotated-agent-key-token");
        var host = new RecordingStateHost(secretVault: vault);
        var handle = new DurableCallerCredentialRef
        {
            Ref = "sec-agent-key",
            Purpose = CredentialSecretPurposes.ScheduledInvocationAgentKey,
            OwnerScopeKey = "scope-key",
            SubjectId = "key-schedule",
            SourceKind = DurableCallerCredentialSourceKind.ScheduledDispatch,
        };
        await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
            host,
            new WorkflowCallerCredential { DurableCallerCredential = handle });

        var first = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(host);
        vault.AdvanceBy(TimeSpan.FromMinutes(6));
        var second = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(host);

        first.Found.Should().BeTrue();
        first.Credential.BearerToken.Should().Be("agent-key-token");
        second.Found.Should().BeTrue();
        second.Credential.BearerToken.Should().Be("rotated-agent-key-token");
        vault.ResolveRequests.Should().HaveCount(2);
    }

    [Fact]
    public async Task WorkflowCallerCredentialRuntimeAccess_ShouldResolveExactChannelAgentKeyHandle()
    {
        var vault = new InMemorySecretVault();
        var stored = await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
            "scope-channel",
            "key-channel",
            "channel-agent-key",
            "test"));
        var handle = new DurableCallerCredentialRef
        {
            Ref = stored.Reference.Ref,
            Purpose = stored.Reference.Purpose,
            OwnerScopeKey = stored.Reference.OwnerScopeKey,
            SubjectId = "key-channel",
            SourceKind = DurableCallerCredentialSourceKind.ChannelRegistration,
            SecretReference = stored.Reference.Clone(),
        };
        var host = new RecordingStateHost(secretVault: vault);
        await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
            host,
            new WorkflowCallerCredential
            {
                DurableCallerCredential = handle,
                Kind = NyxIdCallerCredentialKind.ProxyDelegation,
            });

        var resolved = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(host);

        resolved.Found.Should().BeTrue();
        resolved.Credential.BearerToken.Should().Be("channel-agent-key");
        resolved.Credential.Kind.Should().Be(NyxIdCallerCredentialKind.ProxyDelegation);
        resolved.Credential.NyxIdAuthority.Should().BeNull();
        resolved.Credential.DurableCallerCredential.Should().Be(handle);
    }

    [Fact]
    public async Task WorkflowCallerCredentialRuntimeAccess_ShouldFailClosedForRevokedOrMismatchedBorrowedHandle()
    {
        var vault = new InMemorySecretVault();
        var stored = await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            "scope-key",
            "key-schedule",
            "agent-key-token",
            "test"));
        var handle = new DurableCallerCredentialRef
        {
            Ref = stored.Reference.Ref,
            Purpose = stored.Reference.Purpose,
            OwnerScopeKey = stored.Reference.OwnerScopeKey,
            SubjectId = "wrong-key",
            SourceKind = DurableCallerCredentialSourceKind.ScheduledDispatch,
        };
        var host = new RecordingStateHost(secretVault: vault);
        await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
            host,
            new WorkflowCallerCredential { DurableCallerCredential = handle });

        var mismatched = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(host);
        handle.SubjectId = "key-schedule";
        await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
            host,
            new WorkflowCallerCredential { DurableCallerCredential = handle });
        await vault.RevokeAsync(new RevokeSecretRequest(
            handle.Ref,
            handle.Purpose,
            handle.OwnerScopeKey,
            handle.SubjectId,
            "test revoke"));
        var revoked = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(host);

        var expiredSecret = await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.ScheduledInvocationAgentKey,
            "scope-key",
            "expired-key",
            "expired-agent-key-token",
            "test",
            DateTimeOffset.UtcNow.AddMinutes(-1)));
        handle.Ref = expiredSecret.Reference.Ref;
        handle.SubjectId = "expired-key";
        await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
            host,
            new WorkflowCallerCredential { DurableCallerCredential = handle });
        var expired = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(host);

        mismatched.Found.Should().BeFalse();
        revoked.Found.Should().BeFalse();
        expired.Found.Should().BeFalse();
    }

    [Fact]
    public async Task WorkflowCallerCredentialRuntimeAccess_ShouldFailClosed_WhenDurableHandleCannotResolve()
    {
        var noVaultHost = new RecordingStateHost();
        noVaultHost.ExecutionContextState.CallerCredential = new WorkflowCallerCredentialState
        {
            DurableCallerCredential = CreateDurableCallerCredentialRef(),
        };
        var missingVaultCredential = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(noVaultHost);
        missingVaultCredential.Found.Should().BeFalse();
        missingVaultCredential.Credential.BearerToken.Should().BeEmpty();

        var vault = new InMemorySecretVault();
        var incompleteReferenceHost = new RecordingStateHost(secretVault: vault);
        incompleteReferenceHost.ExecutionContextState.CallerCredential = new WorkflowCallerCredentialState
        {
            DurableCallerCredential = CreateDurableCallerCredentialRef(ownerScopeKey: string.Empty),
        };
        var incompleteReferenceCredential =
            await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(incompleteReferenceHost);
        incompleteReferenceCredential.Found.Should().BeFalse();
        incompleteReferenceCredential.Credential.BearerToken.Should().BeEmpty();

        var unresolvedHost = new RecordingStateHost(secretVault: vault);
        unresolvedHost.ExecutionContextState.CallerCredential = new WorkflowCallerCredentialState
        {
            DurableCallerCredential = CreateDurableCallerCredentialRef(reference: "missing"),
        };
        var unresolvedCredential = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(unresolvedHost);
        unresolvedCredential.Found.Should().BeFalse();
        unresolvedCredential.Credential.BearerToken.Should().BeEmpty();

        var invalidSecret = await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
            "schedule:schedule-invalid",
            "lark:tenant:user-invalid",
            "Bearer invalid",
            "test"));
        var invalidSecretHost = new RecordingStateHost(secretVault: vault);
        invalidSecretHost.ExecutionContextState.CallerCredential = new WorkflowCallerCredentialState
        {
            DurableCallerCredential = CreateDurableCallerCredentialRef(
                invalidSecret.Reference.Ref,
                invalidSecret.Reference.Purpose,
                invalidSecret.Reference.OwnerScopeKey,
                "lark:tenant:user-invalid"),
        };
        var invalidSecretCredential =
            await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(invalidSecretHost);
        invalidSecretCredential.Found.Should().BeFalse();
        invalidSecretCredential.Credential.BearerToken.Should().BeEmpty();

        var emptySecret = await vault.PutAsync(new StoreSecretRequest(
            CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
            "schedule:schedule-empty",
            "lark:tenant:user-empty",
            " ",
            "test"));
        var emptySecretHost = new RecordingStateHost(secretVault: vault);
        emptySecretHost.ExecutionContextState.CallerCredential = new WorkflowCallerCredentialState
        {
            DurableCallerCredential = CreateDurableCallerCredentialRef(
                emptySecret.Reference.Ref,
                emptySecret.Reference.Purpose,
                emptySecret.Reference.OwnerScopeKey,
                "lark:tenant:user-empty"),
        };
        var emptySecretCredential =
            await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(emptySecretHost);
        emptySecretCredential.Found.Should().BeFalse();
        emptySecretCredential.Credential.BearerToken.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowRunCommittedStateRedactionHook_ShouldClearDurableCallerCredentialFromDeltaAndStateRoot()
    {
        var durableRef = CreateDurableCallerCredentialRef();
        var published = new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                EventId = "evt-1",
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Version = 1,
                AgentId = "run-1",
                EventType = nameof(WorkflowRunExecutionContextUpdatedEvent),
                EventData = Any.Pack(new WorkflowRunExecutionContextUpdatedEvent
                {
                    RunId = "run-1",
                    ExecutionContextDelta = new WorkflowRunExecutionContextDelta
                    {
                        ClearCallerCredential = true,
                        CallerCredential = new WorkflowCallerCredential
                        {
                            BearerToken = "secret",
                            SourceReadableUserBearerToken = "source-secret",
                            DurableCallerCredential = durableRef.Clone(),
                            NyxIdAuthority = CreateCallerAuthority(),
                        },
                    },
                }),
            },
            StateRoot = Any.Pack(new WorkflowRunState
            {
                RunId = "run-1",
                ExecutionContext = new WorkflowRunExecutionContextState
                {
                    CallerCredential = new WorkflowCallerCredentialState
                    {
                        BearerToken = "secret",
                        SourceReadableUserBearerToken = "source-secret",
                        DurableCallerCredential = durableRef.Clone(),
                        NyxIdAuthority = CreateCallerAuthority(),
                    },
                },
            }),
        };
        var hook = new WorkflowRunCommittedStateRedactionHook();

        await hook.BeforePublishAsync(new CommittedStatePublicationContext
        {
            ActorId = "run-1",
            ActorType = typeof(WorkflowRunGAgent),
            Published = published,
        }, CancellationToken.None);

        var updateEvent = published.StateEvent.EventData.Unpack<WorkflowRunExecutionContextUpdatedEvent>();
        updateEvent.ExecutionContextDelta.CallerCredential!.BearerToken.Should().BeEmpty();
        updateEvent.ExecutionContextDelta.CallerCredential.SourceReadableUserBearerToken.Should().BeEmpty();
        updateEvent.ExecutionContextDelta.CallerCredential.DurableCallerCredential.Should().BeNull();
        updateEvent.ExecutionContextDelta.CallerCredential.NyxIdAuthority.Should().BeNull();
        var stateRoot = published.StateRoot.Unpack<WorkflowRunState>();
        stateRoot.ExecutionContext.CallerCredential!.BearerToken.Should().BeEmpty();
        stateRoot.ExecutionContext.CallerCredential.SourceReadableUserBearerToken.Should().BeEmpty();
        stateRoot.ExecutionContext.CallerCredential.DurableCallerCredential.Should().BeNull();
        stateRoot.ExecutionContext.CallerCredential.NyxIdAuthority.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowRunCommittedStateRedactionHook_ShouldHidePendingWorkflowStartFromProjection()
    {
        var pendingStart = new StartWorkflowEvent
        {
            RunId = "run-1",
            WorkflowName = "invoice",
            Input = "private-input",
            ValueRepresentation = WorkflowExecutionValueRepresentation.Legacy,
        };
        var published = new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                EventId = "evt-start-1",
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Version = 1,
                AgentId = "run-1",
                EventType = nameof(WorkflowRunExecutionStartedEvent),
                EventData = Any.Pack(new WorkflowRunExecutionStartedEvent
                {
                    RunId = "run-1",
                    WorkflowName = "invoice",
                    PendingStartWorkflow = pendingStart.Clone(),
                }),
            },
            StateRoot = Any.Pack(new WorkflowRunState
            {
                RunId = "run-1",
                PendingStartWorkflow = pendingStart.Clone(),
            }),
        };
        var hook = new WorkflowRunCommittedStateRedactionHook();

        await hook.BeforePublishAsync(new CommittedStatePublicationContext
        {
            ActorId = "run-1",
            ActorType = typeof(WorkflowRunGAgent),
            Published = published,
        }, CancellationToken.None);

        published.StateEvent.EventData
            .Unpack<WorkflowRunExecutionStartedEvent>()
            .PendingStartWorkflow.Should().BeNull();
        published.StateRoot.Unpack<WorkflowRunState>()
            .PendingStartWorkflow.Should().BeNull();
    }

    [Fact]
    public async Task WorkflowCallerCredentialRuntimeAccess_ShouldFailClosed_WhenRuntimeReferenceCannotResolve()
    {
        var context = new RecordingWorkflowExecutionContext();
        context.ExecutionContextState.CallerCredential = new WorkflowCallerCredentialState
        {
            RuntimeSecretReference = new RuntimeSecretReference
            {
                Ref = "missing",
                Purpose = CredentialSecretPurposes.WorkflowCallerBearerToken,
                OwnerRunId = "run-1",
                OwnerStepId = "workflow.caller",
            },
        };

        var credential = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(
            (IWorkflowExecutionContext)context);
        credential.Found.Should().BeFalse();
        credential.Credential.BearerToken.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowCallerCredentialRuntimeAccess_ShouldFailClosed_WhenSupplementalRuntimeReferenceCannotResolve()
    {
        var runtimeSecrets = new InMemoryRuntimeSecretStore();
        var host = new RecordingStateHost(runtimeSecrets);
        await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
            host,
            new WorkflowCallerCredential
            {
                BearerToken = "delegation-alpha",
                Kind = NyxIdCallerCredentialKind.ProxyDelegation,
                SourceReadableUserBearerToken = "source-alpha",
            });
        host.ExecutionContextState.CallerCredential!
            .SourceReadableUserBearerRuntimeSecretReference.Ref = "missing";

        var credential = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(host);

        credential.Found.Should().BeFalse();
        credential.Credential.BearerToken.Should().BeEmpty();
        credential.Credential.SourceReadableUserBearerToken.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkflowCallerCredentialRuntimeAccess_ShouldFailClosed_WhenSupplementalReferenceKindIsNotDelegation()
    {
        var runtimeSecrets = new InMemoryRuntimeSecretStore();
        var host = new RecordingStateHost(runtimeSecrets);
        await WorkflowCallerCredentialRuntimeContextAccess.SetCredentialAsync(
            host,
            new WorkflowCallerCredential
            {
                BearerToken = "delegation-alpha",
                Kind = NyxIdCallerCredentialKind.ProxyDelegation,
                SourceReadableUserBearerToken = "source-alpha",
            });
        host.ExecutionContextState.CallerCredential!.Kind =
            NyxIdCallerCredentialKind.SourceReadableUserBearer;

        var credential = await WorkflowCallerCredentialRuntimeContextAccess.TryGetCredentialAsync(host);

        credential.Found.Should().BeFalse();
        credential.Credential.BearerToken.Should().BeEmpty();
        credential.Credential.SourceReadableUserBearerToken.Should().BeEmpty();
    }

    [Fact]
    public void WorkflowCallerCredentialRuntimeAccess_ShouldReadLegacyPlaintextStateOnlyForExistingState()
    {
        var context = new RecordingWorkflowExecutionContext();
        context.ExecutionContextState.CallerCredential = new WorkflowCallerCredentialState
        {
            BearerToken = " secret ",
        };

        WorkflowCallerCredentialRuntimeContextAccess.TryGetCredential(context, out var credential)
            .Should()
            .BeTrue();
        credential.BearerToken.Should().Be("secret");

        context.ExecutionContextState.CallerCredential.BearerToken = " ";
        WorkflowCallerCredentialRuntimeContextAccess.TryGetCredential(context, out credential)
            .Should()
            .BeFalse();
        credential.BearerToken.Should().BeEmpty();

        context.ExecutionContextState.CallerCredential.BearerToken = "Bearer secret";
        WorkflowCallerCredentialRuntimeContextAccess.TryGetCredential(context, out credential)
            .Should()
            .BeFalse();
        credential.BearerToken.Should().BeEmpty();

        WorkflowCallerCredentialRuntimeContextAccess.TryGetCredential(new ContextWithoutRuntimeAccessor(), out credential)
            .Should()
            .BeFalse();
        credential.BearerToken.Should().BeEmpty();
        FluentActions.Invoking(() => WorkflowCallerCredentialRuntimeContextAccess.TryGetCredential(null!, out _))
            .Should()
            .Throw<ArgumentNullException>();
    }

    [Fact]
    public void CopyRequestMetadata_ShouldReturnZeroWhenRuntimeContextIsMissingOrEmpty()
    {
        var target = new Dictionary<string, string>(StringComparer.Ordinal);

        WorkflowRequestMetadataRuntimeContextAccess.CopyRequestMetadata(new ContextWithoutRuntimeAccessor(), target)
            .Should()
            .Be(0);

        WorkflowRequestMetadataRuntimeContextAccess.CopyRequestMetadata(new RecordingWorkflowExecutionContext(), target)
            .Should()
            .Be(0);
    }

    [Fact]
    public void CopyRequestMetadata_ShouldCopyOnlyPassthroughEntries()
    {
        var context = new RecordingWorkflowExecutionContext();
        context.RuntimeContext.ApplyRequestMetadata(
            new Dictionary<string, string>
            {
                ["trace-id"] = "abc",
                ["llm.model_override"] = "model",
                ["connector.http.authorization"] = "Bearer secret",
                ["empty"] = " ",
            });
        var target = new Dictionary<string, string>(StringComparer.Ordinal);

        var copied = WorkflowRequestMetadataRuntimeContextAccess.CopyRequestMetadata(context, target);

        copied.Should().Be(1);
        target.Should().HaveCount(1);
        target["trace-id"].Should().Be("abc");
        target.Should().NotContainKey("llm.model_override");
        target.Should().NotContainKey("connector.http.authorization");
    }

    [Fact]
    public async Task CopyRequestMetadata_ShouldValidateArguments()
    {
        var context = new RecordingWorkflowExecutionContext();
        var target = new Dictionary<string, string>(StringComparer.Ordinal);

        FluentActions.Invoking(() => WorkflowRequestMetadataRuntimeContextAccess.CopyRequestMetadata(null!, target))
            .Should()
            .Throw<ArgumentNullException>();
        FluentActions.Invoking(() => WorkflowRequestMetadataRuntimeContextAccess.CopyRequestMetadata(context, null!))
            .Should()
            .Throw<ArgumentNullException>();
        await FluentActions.Awaiting(() => WorkflowRequestMetadataRuntimeContextAccess.SetRequestMetadataAsync(null!, target))
            .Should()
            .ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task SecureInputRuntimeAccess_ShouldStoreRemoveAndClearTypedCapturedValues()
    {
        var runtimeSecrets = new InMemoryRuntimeSecretStore();
        var context = new RecordingWorkflowExecutionContext(runtimeSecrets);

        await SecureInputRuntimeContextAccess.SetCapturedValueAsync(
            context,
            " run-1 ",
            " api_key ",
            "secret",
            CancellationToken.None);

        var capturedValue = await SecureInputRuntimeContextAccess.TryGetCapturedValueAsync(context, "run-1", "api_key");
        capturedValue.Found.Should().BeTrue();
        capturedValue.Value.Should().Be("secret");
        context.SecureInputState.Captured.Should().ContainKey("run-1::api_key");
        var captured = context.SecureInputState.Captured["run-1::api_key"];
        captured.Value.Should().BeEmpty();
        captured.ValueReference.Should().NotBeNull();
        captured.ValueReference.Purpose.Should().Be(CredentialSecretPurposes.WorkflowSecureInputValue);
        captured.ValueReference.OwnerRunId.Should().Be("run-1");
        captured.ValueReference.OwnerStepId.Should().Be("api_key");

        (await SecureInputRuntimeContextAccess.RemoveCapturedValueAsync(
            context,
            "run-1",
            "api_key",
            CancellationToken.None)).Should().BeTrue();
        (await SecureInputRuntimeContextAccess.TryGetCapturedValueAsync(context, "run-1", "api_key"))
            .Found
            .Should()
            .BeFalse();

        await SecureInputRuntimeContextAccess.SetCapturedValueAsync(context, "run-1", "api_key", "secret", CancellationToken.None);
        await SecureInputRuntimeContextAccess.SetCapturedValueAsync(context, "run-2", "api_key", "other", CancellationToken.None);
        await SecureInputRuntimeContextAccess.RemoveRunAsync(context, "run-1", CancellationToken.None);

        context.SecureInputState.Captured.Should().NotContainKey("run-1::api_key");
        context.SecureInputState.Captured.Should().ContainKey("run-2::api_key");
    }

    [Fact]
    public async Task SecureInputRuntimeAccess_ShouldFailClosed_WhenReferenceCannotResolve()
    {
        var context = new RecordingWorkflowExecutionContext();
        var state = new SecureInputModuleState();
        state.Captured["run-1::api_key"] = new CapturedSecureInputState
        {
            RunId = "run-1",
            VariableName = "api_key",
            ValueReference = new RuntimeSecretReference
            {
                Ref = "missing",
                Purpose = CredentialSecretPurposes.WorkflowSecureInputValue,
                OwnerRunId = "run-1",
                OwnerStepId = "api_key",
            },
        };
        await context.SaveStateAsync(SecureInputStateAccess.ModuleStateKey, state);

        var value = await SecureInputRuntimeContextAccess.TryGetCapturedValueAsync(context, "run-1", "api_key");
        value.Found.Should().BeFalse();
        value.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task SecureInputModule_ShouldKeepPendingAndNotPublishCapture_WhenRuntimeSecretStoreFails()
    {
        var context = new RecordingWorkflowExecutionContext(new FailingRuntimeSecretStore());
        var module = new SecureInputModule();
        var pendingKey = SecureInputStateAccess.BuildPendingKey("run-1", "secure-step");
        var state = new SecureInputModuleState();
        state.Pending[pendingKey] = new PendingSecureInputState
        {
            StepId = "secure-step",
            RunId = "run-1",
            Input = "original-input",
            OnTimeout = "fail",
            AllowEmpty = false,
            VariableName = "api_key",
            MaskedOutput = "[masked]",
        };
        await context.SaveStateAsync(SecureInputStateAccess.ModuleStateKey, state);

        await FluentActions.Awaiting(() => module.HandleAsync(
                Envelope(new WorkflowResumedEvent
                {
                    RunId = "run-1",
                    StepId = "secure-step",
                    Approved = true,
                    UserInput = "secret",
                }),
                context,
                CancellationToken.None))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("runtime secret store unavailable");

        var persisted = context.SecureInputState;
        persisted.Pending.Should().ContainKey(pendingKey);
        persisted.Captured.Should().NotContainKey("run-1::api_key");
        persisted.Captured.Values.Select(x => x.Value).Should().NotContain("secret");
        context.Published.Select(x => x.Event).OfType<SecureValueCapturedEvent>().Should().BeEmpty();
        context.Published.Select(x => x.Event).OfType<StepCompletedEvent>().Should().BeEmpty();
    }

    private static EventEnvelope Envelope(IMessage evt) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("test", TopologyAudience.Self),
        };

    private static WorkflowCallerNyxIdAuthority CreateCallerAuthority() =>
        new()
        {
            Platform = "lark",
            Tenant = "tenant-a",
            ExternalUserId = "external-user-42",
            Scope = "proxy",
        };

    private static DurableCallerCredentialRef CreateDurableCallerCredentialRef(
        string reference = "sec_scheduled",
        string purpose = CredentialSecretPurposes.WorkflowCallerDurableBearerToken,
        string ownerScopeKey = "schedule:schedule-1",
        string subjectId = "lark:tenant:user-1") =>
        new()
        {
            Ref = reference,
            Purpose = purpose,
            OwnerScopeKey = ownerScopeKey,
            SubjectId = subjectId,
            SourceKind = DurableCallerCredentialSourceKind.ScheduledDispatch,
        };

    private sealed class RecordingStateHost : IWorkflowExecutionStateHost, IRuntimeSecretStoreAccessor, ISecretVaultAccessor
    {
        private readonly Dictionary<string, Any> _states = new(StringComparer.Ordinal);

        public RecordingStateHost(IRuntimeSecretStore? runtimeSecretStore = null, ISecretVault? secretVault = null)
        {
            RuntimeSecretStore = runtimeSecretStore;
            SecretVault = secretVault;
        }

        public string RunId => "run-1";

        public IRuntimeSecretStore? RuntimeSecretStore { get; }

        public ISecretVault? SecretVault { get; }

        public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextState { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextSnapshot => ExecutionContextState.Clone();

        public Task UpdateExecutionContextAsync(
            WorkflowRunExecutionContextDelta delta,
            CancellationToken ct = default)
        {
            ApplyDelta(ExecutionContextState, delta);
            return Task.CompletedTask;
        }

        public Task ClearExecutionContextAsync(CancellationToken ct = default)
        {
            ExecutionContextState.Llm = null;
            ExecutionContextState.CallerCredential = null;
            return Task.CompletedTask;
        }

        public Any? GetExecutionState(string scopeKey) =>
            _states.TryGetValue(scopeKey, out var state) ? state : null;

        public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() => _states.ToList();

        public Task UpsertExecutionStateAsync(string scopeKey, Any state, CancellationToken ct = default)
        {
            _states[scopeKey] = state;
            return Task.CompletedTask;
        }

        public Task ClearExecutionStateAsync(string scopeKey, CancellationToken ct = default)
        {
            _states.Remove(scopeKey);
            return Task.CompletedTask;
        }

        Task<WorkflowCompensationTransitionResult> IWorkflowExecutionStateHost.TryStartCompensationAsync(
            WorkflowCompletedEvent terminalFailure,
            StepCompletedEvent? terminalStep,
            CancellationToken ct)
        {
            _ = terminalFailure;
            _ = terminalStep;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        Task IWorkflowExecutionStateHost.RecordCompensableStepDispatchAsync(
            CompensableStepDispatchedEvent evt,
            CancellationToken ct)
        {
            _ = evt;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationStepCompletionAsync(
            CompensationStepCompletedEvent completion,
            CancellationToken ct = default)
        {
            _ = completion;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationPhaseDeadlineExceededAsync(
            string runId,
            string error,
            CancellationToken ct = default)
        {
            _ = runId;
            _ = error;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        private static WorkflowCompensationTransitionResult NoCompensableLedger() =>
            new(
                WorkflowCompensationTransitionStatus.NoCompensableLedger,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);
    }

    private sealed class RecordingWorkflowExecutionContext :
        ContextWithoutRuntimeAccessor,
        IWorkflowExecutionRuntimeContextAccessor,
        IWorkflowExecutionStateHost,
        IRuntimeSecretStoreAccessor,
        ISecretVaultAccessor
    {
        private readonly Dictionary<string, Any> _states = new(StringComparer.Ordinal);

        public RecordingWorkflowExecutionContext(IRuntimeSecretStore? runtimeSecretStore = null, ISecretVault? secretVault = null)
        {
            RuntimeSecretStore = runtimeSecretStore;
            SecretVault = secretVault;
        }

        public IRuntimeSecretStore? RuntimeSecretStore { get; }

        public ISecretVault? SecretVault { get; }

        public WorkflowExecutionRuntimeContext RuntimeContext { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextState { get; } = new();

        public WorkflowRunExecutionContextState ExecutionContextSnapshot => ExecutionContextState.Clone();

        public List<(IMessage Event, TopologyAudience Audience)> Published { get; } = [];

        public SecureInputModuleState SecureInputState =>
            _states.TryGetValue(SecureInputStateAccess.ModuleStateKey, out var state) &&
            state.Is(SecureInputModuleState.Descriptor)
                ? state.Unpack<SecureInputModuleState>()
                : new SecureInputModuleState();

        public override TState LoadState<TState>(string scopeKey)
        {
            if (_states.TryGetValue(scopeKey, out var state) &&
                state.Is(new TState().Descriptor))
            {
                return state.Unpack<TState>() ?? new TState();
            }

            return new TState();
        }

        public override IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
        {
            return _states
                .Where(x => string.IsNullOrEmpty(scopeKeyPrefix) || x.Key.StartsWith(scopeKeyPrefix, StringComparison.Ordinal))
                .Where(x => x.Value.Is(new TState().Descriptor))
                .Select(x => new KeyValuePair<string, TState>(x.Key, x.Value.Unpack<TState>() ?? new TState()))
                .ToList();
        }

        public override Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
        {
            _states[scopeKey] = Any.Pack(state);
            return Task.CompletedTask;
        }

        public override Task ClearStateAsync(string scopeKey, CancellationToken ct = default)
        {
            _states.Remove(scopeKey);
            return Task.CompletedTask;
        }

        public Any? GetExecutionState(string scopeKey) =>
            _states.TryGetValue(scopeKey, out var state) ? state : null;

        public IReadOnlyList<KeyValuePair<string, Any>> GetExecutionStates() => _states.ToList();

        public Task UpsertExecutionStateAsync(string scopeKey, Any state, CancellationToken ct = default)
        {
            _states[scopeKey] = state;
            return Task.CompletedTask;
        }

        public Task ClearExecutionStateAsync(string scopeKey, CancellationToken ct = default)
        {
            _states.Remove(scopeKey);
            return Task.CompletedTask;
        }

        Task<WorkflowCompensationTransitionResult> IWorkflowExecutionStateHost.TryStartCompensationAsync(
            WorkflowCompletedEvent terminalFailure,
            StepCompletedEvent? terminalStep,
            CancellationToken ct)
        {
            _ = terminalFailure;
            _ = terminalStep;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        Task IWorkflowExecutionStateHost.RecordCompensableStepDispatchAsync(
            CompensableStepDispatchedEvent evt,
            CancellationToken ct)
        {
            _ = evt;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationStepCompletionAsync(
            CompensationStepCompletedEvent completion,
            CancellationToken ct = default)
        {
            _ = completion;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        public Task<WorkflowCompensationTransitionResult> RecordCompensationPhaseDeadlineExceededAsync(
            string runId,
            string error,
            CancellationToken ct = default)
        {
            _ = runId;
            _ = error;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(NoCompensableLedger());
        }

        private static WorkflowCompensationTransitionResult NoCompensableLedger() =>
            new(
                WorkflowCompensationTransitionStatus.NoCompensableLedger,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);

        public Task UpdateExecutionContextAsync(
            WorkflowRunExecutionContextDelta delta,
            CancellationToken ct = default)
        {
            ApplyDelta(ExecutionContextState, delta);
            return Task.CompletedTask;
        }

        public Task ClearExecutionContextAsync(CancellationToken ct = default)
        {
            ExecutionContextState.Llm = null;
            ExecutionContextState.CallerCredential = null;
            return Task.CompletedTask;
        }

        public override Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
        {
            ct.ThrowIfCancellationRequested();
            _ = options;
            Published.Add((evt, audience));
            return Task.CompletedTask;
        }
    }

    private sealed class FailingRuntimeSecretStore : IRuntimeSecretStore
    {
        public Task<StoreRuntimeSecretResult> PutAsync(StoreRuntimeSecretRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = request;
            throw new InvalidOperationException("runtime secret store unavailable");
        }

        public Task<ResolveRuntimeSecretResult> ResolveAsync(ResolveRuntimeSecretRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = request;
            return Task.FromResult(new ResolveRuntimeSecretResult(null, null));
        }

        public Task<ConsumeRuntimeSecretResult> ConsumeAsync(ConsumeRuntimeSecretRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = request;
            return Task.FromResult(new ConsumeRuntimeSecretResult(false));
        }

        public Task<RevokeRuntimeSecretResult> RevokeAsync(RevokeRuntimeSecretRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _ = request;
            return Task.FromResult(new RevokeRuntimeSecretResult(false));
        }
    }

    private sealed class DeterministicBorrowedCredentialVault(
        string initialSecret,
        string rotatedSecret) : ISecretVault
    {
        private TimeSpan _elapsed;

        public List<ResolveSecretRequest> ResolveRequests { get; } = [];

        public void AdvanceBy(TimeSpan elapsed) => _elapsed += elapsed;

        public Task<StoreSecretResult> PutAsync(StoreSecretRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ResolveSecretResult> ResolveAsync(
            ResolveSecretRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ResolveRequests.Add(request);
            var secret = _elapsed > TimeSpan.FromMinutes(5) ? rotatedSecret : initialSecret;
            return Task.FromResult(new ResolveSecretResult(new SecretReference
            {
                Ref = request.Ref,
                Purpose = request.Purpose,
                OwnerScopeKey = request.OwnerScopeKey,
            }, secret));
        }

        public Task<RotateSecretResult> RotateAsync(RotateSecretRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RevokeSecretResult> RevokeAsync(RevokeSecretRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static void ApplyDelta(
        WorkflowRunExecutionContextState state,
        WorkflowRunExecutionContextDelta delta)
    {
        if (delta.ClearLlm)
            state.Llm = null;
        if (delta.ClearCallerCredential)
            state.CallerCredential = null;
        if (delta.Llm != null)
        {
            state.Llm = new WorkflowLlmExecutionContextState
            {
                ModelOverride = delta.Llm.ModelOverride,
                UserMemoryPrompt = delta.Llm.UserMemoryPrompt,
                RoutePreference = delta.Llm.RoutePreference,
            };
            if (delta.Llm.HasMaxToolRoundsOverride)
                state.Llm.MaxToolRoundsOverride = delta.Llm.MaxToolRoundsOverride;
        }

        if (delta.CallerCredential != null)
        {
            state.CallerCredential = new WorkflowCallerCredentialState
            {
                BearerToken = delta.CallerCredential.BearerToken,
                SourceReadableUserBearerToken = delta.CallerCredential.SourceReadableUserBearerToken,
                RuntimeSecretReference = delta.CallerCredential.RuntimeSecretReference?.Clone(),
                SourceReadableUserBearerRuntimeSecretReference =
                    delta.CallerCredential.SourceReadableUserBearerRuntimeSecretReference?.Clone(),
                DurableCallerCredential = delta.CallerCredential.DurableCallerCredential?.Clone(),
                NyxIdAuthority = delta.CallerCredential.NyxIdAuthority?.Clone(),
                Kind = delta.CallerCredential.Kind,
            };
        }
    }

    private class ContextWithoutRuntimeAccessor : IWorkflowExecutionContext
    {
        public EventEnvelope InboundEnvelope { get; } = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        };

        public string AgentId => "agent-1";

        public IServiceProvider Services { get; } = new EmptyServiceProvider();

        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;

        public string RunId => "run-1";

        public virtual TState LoadState<TState>(string scopeKey)
            where TState : class, IMessage<TState>, new() => new();

        public virtual IReadOnlyList<KeyValuePair<string, TState>> LoadStates<TState>(string scopeKeyPrefix = "")
            where TState : class, IMessage<TState>, new() => [];

        public virtual Task SaveStateAsync<TState>(string scopeKey, TState state, CancellationToken ct = default)
            where TState : class, IMessage<TState> => Task.CompletedTask;

        public virtual Task ClearStateAsync(string scopeKey, CancellationToken ct = default) => Task.CompletedTask;

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;

        public virtual Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage => Task.CompletedTask;

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage => Task.CompletedTask;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(System.Type serviceType) => null;
    }
}
