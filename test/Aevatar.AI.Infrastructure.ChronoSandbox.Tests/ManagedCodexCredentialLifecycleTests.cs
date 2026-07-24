using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aevatar.AI.Infrastructure.ChronoSandbox.Tests;

public sealed class ManagedCodexCredentialLifecycleTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-21T12:00:00Z");
    private const string RawKey = "nyx_k_raw-agent-key-must-remain-secret";

    [Fact]
    public async Task ProvisionAsync_WhenMutationLeaseIsBusy_FailsBeforeReadingCredentialState()
    {
        var handler = new RoutingHandler(MeResponse());
        var vault = Substitute.For<ISecretVault>();
        var query = Substitute.For<IManagedCodexCredentialQueryPort>();
        var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
        var lease = Substitute.For<IManagedCodexCredentialMutationLease>();
        lease.TryAcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IManagedCodexCredentialMutationLeaseHandle?)null);
        var lifecycle = CreateLifecycle(handler, vault, commands, query, mutationLease: lease);

        var act = () => lifecycle.ProvisionAsync("user-bearer", "user-a");

        var exception = (await act.Should()
            .ThrowAsync<ManagedCodexCredentialLifecycleException>()).Which;
        exception.Code.Should().Be("managed_credential_mutation_in_progress");
        handler.Paths.Should().Equal("/api/v1/users/me");
        await query.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
        await vault.DidNotReceiveWithAnyArgs().PutAsync(default!, default);
        await commands.DidNotReceiveWithAnyArgs().CommitProvisionedAsync(default!, default);
    }

    [Fact]
    public async Task ProvisionAsync_CreatesExactKeyAndPersistsOnlyTheVaultReference()
    {
        var handler = new RoutingHandler(
            MeResponse(),
            UserServicesResponse(),
            """{"keys":[]}""",
            IssuedKeyResponse("key-1", RawKey),
            ApiKeyListResponse("key-1", Now.AddDays(30)));
        var vault = Substitute.For<ISecretVault>();
        StoreSecretRequest? stored = null;
        vault.PutAsync(Arg.Do<StoreSecretRequest>(request => stored = request), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new StoreSecretResult(Reference(
                call.Arg<StoreSecretRequest>(),
                call.Arg<StoreSecretRequest>().RequestedRef!,
                version: 1))));
        var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
        ManagedCodexCredentialDescriptor? committed = null;
        commands.CommitProvisionedAsync(
                Arg.Do<ManagedCodexCredentialDescriptor>(value => committed = value.Clone()),
                Arg.Any<CancellationToken>())
            .Returns(Admission());
        var lifecycle = CreateLifecycle(handler, vault, commands);

        var result = await lifecycle.ProvisionAsync("user-bearer", "user-a");

        handler.Paths.Should().Equal(
            "/api/v1/users/me",
            "/api/v1/user-services",
            "/api/v1/api-keys",
            "/api/v1/api-keys",
            "/api/v1/api-keys");
        using var body = JsonDocument.Parse(handler.RequestBodies.Single());
        body.RootElement.GetProperty("scopes").GetString().Should().Be("proxy");
        body.RootElement.GetProperty("platform").GetString().Should().Be("codex");
        body.RootElement.GetProperty("allow_all_services").GetBoolean().Should().BeFalse();
        body.RootElement.GetProperty("allowed_service_ids").EnumerateArray()
            .Select(static item => item.GetString()).Should().Equal("us-sandbox");
        body.RootElement.GetProperty("allow_all_nodes").GetBoolean().Should().BeFalse();
        body.RootElement.GetProperty("allowed_node_ids").GetArrayLength().Should().Be(0);
        stored.Should().NotBeNull();
        stored!.Purpose.Should().Be(CredentialSecretPurposes.ManagedCodexInvocationAgentKey);
        stored.SubjectId.Should().Be("invocation-agent-key");
        stored.Secret.Should().Be(RawKey);
        committed.Should().NotBeNull();
        committed!.ApiKeyId.Should().Be("key-1");
        committed.ChronoSandboxUserServiceId.Should().Be("us-sandbox");
        committed.SecretReference.Ref.Should().Be(stored.RequestedRef);
        committed.ToString().Should().NotContain(RawKey);
        JsonSerializer.Serialize(result).Should().NotContain(RawKey);
        JsonSerializer.Serialize(result).Should().NotContain(stored.RequestedRef!);
    }

    [Fact]
    public async Task ProvisionAsync_ForTwoUsers_PersistsDistinctKeysReferencesAndOwnerScopes()
    {
        var handlerA = new RoutingHandler(
            MeResponse("user-a"),
            UserServicesResponse("us-sandbox-a", "us-llm-a"),
            """{"keys":[]}""",
            IssuedKeyResponse("key-a", "raw-key-a", sandboxServiceId: "us-sandbox-a"),
            ApiKeyListResponse("key-a", Now.AddDays(30), sandboxServiceId: "us-sandbox-a"));
        var handlerB = new RoutingHandler(
            MeResponse("user-b"),
            UserServicesResponse("us-sandbox-b", "us-llm-b"),
            """{"keys":[]}""",
            IssuedKeyResponse("key-b", "raw-key-b", sandboxServiceId: "us-sandbox-b"),
            ApiKeyListResponse("key-b", Now.AddDays(30), sandboxServiceId: "us-sandbox-b"));
        var vault = Substitute.For<ISecretVault>();
        vault.PutAsync(Arg.Any<StoreSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new StoreSecretResult(Reference(
                call.Arg<StoreSecretRequest>(),
                call.Arg<StoreSecretRequest>().RequestedRef!,
                version: 1))));
        var committed = new List<ManagedCodexCredentialDescriptor>();
        var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
        commands.CommitProvisionedAsync(
                Arg.Do<ManagedCodexCredentialDescriptor>(descriptor => committed.Add(descriptor.Clone())),
                Arg.Any<CancellationToken>())
            .Returns(Admission());
        var options = ManagedCodexOptionsValidatorTests.ValidOptions();
        options.Eligibility = new ManagedCodexEligibilityOptions
        {
            Mode = ManagedCodexEligibilityMode.Allowlist,
            AllowedNyxIdUserIds = ["user-a", "user-b"],
        };
        var lifecycleA = CreateLifecycle(handlerA, vault, commands, options: options);
        var lifecycleB = CreateLifecycle(handlerB, vault, commands, options: options);

        await lifecycleA.ProvisionAsync("bearer-a", "user-a");
        await lifecycleB.ProvisionAsync("bearer-b", "user-b");

        committed.Select(static descriptor => descriptor.ApiKeyId).Should().Equal("key-a", "key-b");
        committed.Select(static descriptor => descriptor.SecretReference.Ref).Should().OnlyHaveUniqueItems();
        committed.Select(static descriptor => descriptor.SecretReference.OwnerScopeKey)
            .Should().Equal(
                "managed-codex-credential:nyxid::user-a",
                "managed-codex-credential:nyxid::user-b");
        committed.Select(static descriptor => descriptor.ChronoSandboxUserServiceId)
            .Should().Equal("us-sandbox-a", "us-sandbox-b");
        committed.Should().OnlyContain(static descriptor =>
            !descriptor.ToString().Contains("raw-key-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, true, false, true, "proxy:*", "chrono_sandbox_delegation_misconfigured")]
    [InlineData(true, true, true, true, "proxy:*", "chrono_sandbox_delegation_misconfigured")]
    [InlineData(true, true, false, false, "proxy:*", "chrono_sandbox_delegation_misconfigured")]
    [InlineData(true, true, false, true, "llm:proxy", "chrono_sandbox_delegation_misconfigured")]
    [InlineData(true, true, false, true, "admin", "chrono_sandbox_delegation_misconfigured")]
    [InlineData(true, false, false, true, "proxy:*", "chrono_llm_route_unavailable")]
    public async Task ProvisionAsync_WhenRequiredServiceIsInactiveOrMisconfigured_FailsBeforeIssuingKey(
        bool sandboxActive,
        bool llmActive,
        bool forwardAccessToken,
        bool injectDelegationToken,
        string delegationScope,
        string expectedCode)
    {
        var handler = new RoutingHandler(
            MeResponse(),
            UserServicesResponse(
                sandboxActive: sandboxActive,
                llmActive: llmActive,
                forwardAccessToken: forwardAccessToken,
                injectDelegationToken: injectDelegationToken,
                delegationScope: delegationScope));
        var vault = Substitute.For<ISecretVault>();
        var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
        var lifecycle = CreateLifecycle(handler, vault, commands);

        var act = () => lifecycle.ProvisionAsync("user-bearer", "user-a");

        var exception = (await act.Should().ThrowAsync<ManagedCodexCredentialLifecycleException>()).Which;
        exception.Code.Should().Be(expectedCode);
        handler.Paths.Should().Equal("/api/v1/users/me", "/api/v1/user-services");
        await vault.DidNotReceiveWithAnyArgs().PutAsync(default!, default);
        await commands.DidNotReceiveWithAnyArgs().CommitProvisionedAsync(default!, default);
    }

    [Fact]
    public async Task ProvisionAsync_WhenVaultWriteFails_RevokesTheIssuedNyxIdKey()
    {
        var handler = new RoutingHandler(
            MeResponse(),
            UserServicesResponse(),
            """{"keys":[]}""",
            IssuedKeyResponse("key-orphan", RawKey),
            ApiKeyListResponse("key-orphan", Now.AddDays(30)),
            """{"message":"deleted"}"""
        );
        var vault = Substitute.For<ISecretVault>();
        vault.PutAsync(Arg.Any<StoreSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<StoreSecretResult>>(_ => throw new InvalidOperationException($"failed {RawKey}"));
        vault.RevokeAsync(Arg.Any<RevokeSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RevokeSecretResult(true));
        var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
        var lifecycle = CreateLifecycle(handler, vault, commands);

        var act = () => lifecycle.ProvisionAsync("user-bearer", "user-a");

        var exception = (await act.Should().ThrowAsync<ManagedCodexCredentialLifecycleException>()).Which;
        exception.Code.Should().Be("managed_credential_vault_store_failed");
        exception.Message.Should().NotContain(RawKey);
        exception.ToString().Should().NotContain(RawKey);
        handler.Paths.Should().EndWith("/api/v1/api-keys/key-orphan");
        handler.Methods.Should().EndWith(HttpMethod.Delete);
        await commands.DidNotReceiveWithAnyArgs().CommitProvisionedAsync(default!, default);
        await commands.DidNotReceiveWithAnyArgs().QueueCleanupAsync(default!, default!, default);
    }

    [Fact]
    public async Task ProvisionAsync_WhenVaultWriteIsCancelled_RevokesTheIssuedNyxIdKeyBeforeRethrowing()
    {
        var handler = new RoutingHandler(
            MeResponse(),
            UserServicesResponse(),
            """{"keys":[]}""",
            IssuedKeyResponse("key-cancelled", RawKey),
            ApiKeyListResponse("key-cancelled", Now.AddDays(30)),
            """{"message":"deleted"}"""
        );
        using var cancellation = new CancellationTokenSource();
        var vault = Substitute.For<ISecretVault>();
        vault.PutAsync(Arg.Any<StoreSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<StoreSecretResult>(cancellation.Token);
            });
        vault.RevokeAsync(Arg.Any<RevokeSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RevokeSecretResult(true));
        var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
        var lifecycle = CreateLifecycle(handler, vault, commands);

        var act = () => lifecycle.ProvisionAsync("user-bearer", "user-a", cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.Paths.Should().EndWith("/api/v1/api-keys/key-cancelled");
        handler.Methods.Should().EndWith(HttpMethod.Delete);
        await commands.DidNotReceiveWithAnyArgs().CommitProvisionedAsync(default!, default);
    }

    [Theory]
    [InlineData("proxy admin", false, false, false, false)]
    [InlineData("proxy", true, false, false, false)]
    [InlineData("proxy", false, true, false, false)]
    [InlineData("proxy", false, false, true, false)]
    [InlineData("proxy", false, false, false, true)]
    public async Task ProvisionAsync_WhenIssuedKeyPolicyIsOverBroad_RevokesTheRejectedKey(
        string scopes,
        bool allowAllServices,
        bool allowAllNodes,
        bool includeLlmService,
        bool includeNode)
    {
        var handler = new RoutingHandler(
            MeResponse(),
            UserServicesResponse(),
            """{"keys":[]}""",
            IssuedKeyResponse(
                "key-over-broad",
                RawKey,
                scopes,
                allowAllServices,
                allowAllNodes,
                includeLlmService,
                includeNode),
            """{"message":"deleted"}"""
        );
        var vault = Substitute.For<ISecretVault>();
        var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
        var lifecycle = CreateLifecycle(handler, vault, commands);

        var act = () => lifecycle.ProvisionAsync("user-bearer", "user-a");

        var exception = (await act.Should().ThrowAsync<ManagedCodexCredentialLifecycleException>()).Which;
        exception.Code.Should().Be("managed_api_key_issue_invalid");
        handler.Paths.Should().EndWith("/api/v1/api-keys/key-over-broad");
        handler.Methods.Should().EndWith(HttpMethod.Delete);
        await vault.DidNotReceiveWithAnyArgs().PutAsync(default!, default);
        await commands.DidNotReceiveWithAnyArgs().CommitProvisionedAsync(default!, default);
    }

    [Fact]
    public async Task ProvisionAsync_WhenVaultReturnsInvalidReference_CompensatesTheIssuedResources()
    {
        var handler = new RoutingHandler(
            MeResponse(),
            UserServicesResponse(),
            """{"keys":[]}""",
            IssuedKeyResponse("key-orphan", RawKey),
            ApiKeyListResponse("key-orphan", Now.AddDays(30)),
            """{"message":"deleted"}"""
        );
        var vault = Substitute.For<ISecretVault>();
        string? requestedRef = null;
        vault.PutAsync(
                Arg.Do<StoreSecretRequest>(request => requestedRef = request.RequestedRef),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new StoreSecretResult(new SecretReference
            {
                Ref = call.Arg<StoreSecretRequest>().RequestedRef,
                Purpose = "wrong-purpose",
                OwnerScopeKey = call.Arg<StoreSecretRequest>().OwnerScopeKey,
                Fingerprint = "fingerprint",
                Version = 1,
                ExpiresAtUnixMs = Now.AddDays(30).ToUnixTimeMilliseconds(),
            })));
        vault.RevokeAsync(Arg.Any<RevokeSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RevokeSecretResult(true));
        var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
        var lifecycle = CreateLifecycle(handler, vault, commands);

        var act = () => lifecycle.ProvisionAsync("user-bearer", "user-a");

        var exception = (await act.Should().ThrowAsync<ManagedCodexCredentialLifecycleException>()).Which;
        exception.Code.Should().Be("managed_credential_vault_reference_invalid");
        handler.Paths.Should().EndWith("/api/v1/api-keys/key-orphan");
        await vault.Received(1).RevokeAsync(
            Arg.Is<RevokeSecretRequest>(request =>
                request.Ref == requestedRef &&
                request.OwnerScopeKey == "managed-codex-credential:nyxid::user-a"),
            Arg.Any<CancellationToken>());
        await commands.DidNotReceiveWithAnyArgs().CommitProvisionedAsync(default!, default);
    }

    [Fact]
    public async Task ProvisionAsync_WhenActorCommandThrows_RetainsResourcesForReconciliation()
    {
        var expiresAt = Now.AddDays(30);
        var handler = new RoutingHandler(
            MeResponse(),
            UserServicesResponse(),
            """{"keys":[]}""",
            IssuedKeyResponse("key-unadopted", RawKey),
            ApiKeyListResponse("key-unadopted", expiresAt)
        );
        var vault = Substitute.For<ISecretVault>();
        vault.PutAsync(Arg.Any<StoreSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new StoreSecretResult(Reference(
                call.Arg<StoreSecretRequest>(),
                call.Arg<StoreSecretRequest>().RequestedRef!,
                version: 1))));
        vault.RevokeAsync(Arg.Any<RevokeSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RevokeSecretResult(true));
        var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
        commands.CommitProvisionedAsync(
                Arg.Any<ManagedCodexCredentialDescriptor>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<DispatchAdmission>>(_ => throw new InvalidOperationException("dispatch unavailable"));
        var lifecycle = CreateLifecycle(handler, vault, commands);

        var act = () => lifecycle.ProvisionAsync("user-bearer", "user-a");

        var exception = (await act.Should().ThrowAsync<ManagedCodexCredentialLifecycleException>()).Which;
        exception.Code.Should().Be("managed_credential_persistence_pending");
        exception.Message.Should().NotContain(RawKey);
        handler.Paths.Should().NotContain("/api/v1/api-keys/key-unadopted");
        await vault.DidNotReceiveWithAnyArgs().RevokeAsync(default!, default);
    }

    [Fact]
    public async Task RotateAsync_StoresTheNewKeyInADistinctVaultReference()
    {
        var expiresAt = Now.AddDays(30);
        var handler = new RoutingHandler(
            MeResponse(),
            UserServicesResponse(),
            ApiKeyListResponse("key-1", expiresAt),
            IssuedKeyResponse("key-2", RawKey),
            ApiKeyListResponse("key-2", expiresAt));
        var current = Descriptor("key-1", "sec-1", version: 1);
        var query = Substitute.For<IManagedCodexCredentialQueryPort>();
        query.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(new ManagedCodexCredentialSnapshot(current, [], 4));
        var vault = Substitute.For<ISecretVault>();
        StoreSecretRequest? stored = null;
        vault.PutAsync(
                Arg.Do<StoreSecretRequest>(request => stored = request),
                Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new StoreSecretResult(Reference(
                call.Arg<StoreSecretRequest>(),
                call.Arg<StoreSecretRequest>().RequestedRef!,
                version: 1))));
        vault.RevokeAsync(Arg.Any<RevokeSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RevokeSecretResult(true));
        var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
        commands.CommitRotatedAsync("key-1", Arg.Any<ManagedCodexCredentialDescriptor>(), Arg.Any<CancellationToken>())
            .Returns(Admission());
        var lifecycle = CreateLifecycle(handler, vault, commands, query);

        await lifecycle.RotateAsync("user-bearer", "user-a");

        handler.Paths.Should().Equal(
            "/api/v1/users/me",
            "/api/v1/user-services",
            "/api/v1/api-keys",
            "/api/v1/api-keys/key-1/rotate",
            "/api/v1/api-keys");
        stored.Should().NotBeNull();
        stored!.RequestedRef.Should().StartWith("sec_managed_codex_");
        stored.RequestedRef.Should().NotBe("sec-1");
        stored.SubjectId.Should().Be("invocation-agent-key");
        stored.Secret.Should().Be(RawKey);
        await vault.DidNotReceiveWithAnyArgs().RotateAsync(default!, default);
        await commands.Received(1).CommitRotatedAsync(
            "key-1",
            Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                descriptor.ApiKeyId == "key-2" &&
                descriptor.SecretReference.Ref == stored.RequestedRef &&
                descriptor.SecretReference.Version == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionAsync_WhenNyxIdDoesNotPersistTheRequestedExpiry_RevokesBeforeVaultWrite()
    {
        var requestedExpiry = Now.AddDays(30);
        var handler = new RoutingHandler(
            MeResponse(),
            UserServicesResponse(),
            """{"keys":[]}""",
            IssuedKeyResponse("key-wrong-expiry", RawKey),
            ApiKeyListResponse("key-wrong-expiry", requestedExpiry.AddDays(-1)),
            """{"message":"deleted"}"""
        );
        var vault = Substitute.For<ISecretVault>();
        var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
        var lifecycle = CreateLifecycle(handler, vault, commands);

        var act = () => lifecycle.ProvisionAsync("user-bearer", "user-a");

        var exception = (await act.Should()
            .ThrowAsync<ManagedCodexCredentialLifecycleException>()).Which;
        exception.Code.Should().Be("managed_api_key_expiry_invalid");
        handler.Paths.Should().EndWith("/api/v1/api-keys/key-wrong-expiry");
        await vault.DidNotReceiveWithAnyArgs().PutAsync(default!, default);
        await commands.DidNotReceiveWithAnyArgs().CommitProvisionedAsync(default!, default);
    }

    [Fact]
    public async Task ProvisionAsync_WhenPriorDispatchWasAmbiguous_ReconcilesTheActiveKeyAndVaultReference()
    {
        var expiresAt = Now.AddDays(30);
        var handler = new RoutingHandler(
            MeResponse(),
            UserServicesResponse(),
            ApiKeyListResponse("key-recover", expiresAt));
        var vault = Substitute.For<ISecretVault>();
        vault.ResolveAsync(Arg.Any<ResolveSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => ResolvedSecret(call.Arg<ResolveSecretRequest>(), expiresAt));
        var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
        commands.CommitProvisionedAsync(
                Arg.Any<ManagedCodexCredentialDescriptor>(),
                Arg.Any<CancellationToken>())
            .Returns(Admission());
        var lifecycle = CreateLifecycle(handler, vault, commands);

        var result = await lifecycle.ProvisionAsync("user-bearer", "user-a");

        result.Status.Should().Be("provisioning_reconciliation_accepted");
        handler.Methods.Should().OnlyContain(method => method == HttpMethod.Get);
        await commands.Received(1).CommitProvisionedAsync(
            Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                descriptor.ApiKeyId == "key-recover" &&
                descriptor.SecretReference.Ref.StartsWith("sec_managed_codex_", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionAsync_WhenReconciliationVaultIsUnavailable_DoesNotRevokeTheActiveRemoteKey()
    {
        var expiresAt = Now.AddDays(30);
        var handler = new RoutingHandler(
            MeResponse(),
            UserServicesResponse(),
            ApiKeyListResponse("key-recover", expiresAt));
        var vault = Substitute.For<ISecretVault>();
        vault.ResolveAsync(Arg.Any<ResolveSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<ResolveSecretResult>>(_ =>
                throw new InvalidOperationException($"vault unavailable {RawKey}"));
        var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
        var lifecycle = CreateLifecycle(handler, vault, commands);

        var act = () => lifecycle.ProvisionAsync("user-bearer", "user-a");

        var exception = (await act.Should()
            .ThrowAsync<ManagedCodexCredentialLifecycleException>()).Which;
        exception.Code.Should().Be("managed_credential_vault_unavailable");
        exception.ToString().Should().NotContain(RawKey);
        handler.Methods.Should().OnlyContain(method => method == HttpMethod.Get);
        await commands.DidNotReceiveWithAnyArgs().QueueCleanupAsync(default!, default!, default);
        await commands.DidNotReceiveWithAnyArgs().CommitProvisionedAsync(default!, default);
    }

    [Fact]
    public async Task RotateAsync_WhenPriorDispatchWasAmbiguous_ReconcilesWithoutRotatingAgain()
    {
        var expiresAt = Now.AddDays(30);
        var handler = new RoutingHandler(
            MeResponse(),
            UserServicesResponse(),
            ApiKeyListResponse("key-recover", expiresAt));
        var current = Descriptor("key-old", "sec-old", version: 1);
        var query = Substitute.For<IManagedCodexCredentialQueryPort>();
        query.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(new ManagedCodexCredentialSnapshot(current, [], 4));
        var vault = Substitute.For<ISecretVault>();
        vault.ResolveAsync(Arg.Any<ResolveSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => ResolvedSecret(call.Arg<ResolveSecretRequest>(), expiresAt));
        var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
        commands.CommitRotatedAsync(
                "key-old",
                Arg.Any<ManagedCodexCredentialDescriptor>(),
                Arg.Any<CancellationToken>())
            .Returns(Admission());
        var lifecycle = CreateLifecycle(handler, vault, commands, query);

        var result = await lifecycle.RotateAsync("user-bearer", "user-a");

        result.Status.Should().Be("rotation_reconciliation_accepted");
        handler.Methods.Should().OnlyContain(method => method == HttpMethod.Get);
        await vault.DidNotReceiveWithAnyArgs().PutAsync(default!, default);
        await vault.DidNotReceiveWithAnyArgs().RotateAsync(default!, default);
        await commands.Received(1).CommitRotatedAsync(
            "key-old",
            Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                descriptor.ApiKeyId == "key-recover"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RotateAsync_WhenActorCommandThrows_RetainsTheRotatedCredentialForReconciliation()
    {
        var expiresAt = Now.AddDays(30);
        var handler = new RoutingHandler(
            MeResponse(),
            UserServicesResponse(),
            ApiKeyListResponse("key-1", expiresAt),
            IssuedKeyResponse("key-2", RawKey),
            ApiKeyListResponse("key-2", expiresAt)
        );
        var current = Descriptor("key-1", "sec-1", version: 1);
        var query = Substitute.For<IManagedCodexCredentialQueryPort>();
        query.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(new ManagedCodexCredentialSnapshot(current, [], 4));
        var vault = Substitute.For<ISecretVault>();
        vault.PutAsync(Arg.Any<StoreSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new StoreSecretResult(Reference(
                call.Arg<StoreSecretRequest>(),
                call.Arg<StoreSecretRequest>().RequestedRef!,
                version: 1))));
        var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
        commands.CommitRotatedAsync(
                "key-1",
                Arg.Any<ManagedCodexCredentialDescriptor>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<DispatchAdmission>>(_ => throw new InvalidOperationException("dispatch unavailable"));
        var lifecycle = CreateLifecycle(handler, vault, commands, query);

        var act = () => lifecycle.RotateAsync("user-bearer", "user-a");

        var exception = (await act.Should().ThrowAsync<ManagedCodexCredentialLifecycleException>()).Which;
        exception.Code.Should().Be("managed_credential_persistence_pending");
        handler.Methods.Should().Equal(
            HttpMethod.Get,
            HttpMethod.Get,
            HttpMethod.Get,
            HttpMethod.Post,
            HttpMethod.Get);
        await vault.Received(1).PutAsync(
            Arg.Is<StoreSecretRequest>(request =>
                request.RequestedRef != "sec-1" &&
                request.RequestedRef!.StartsWith("sec_managed_codex_", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await vault.DidNotReceiveWithAnyArgs().RotateAsync(default!, default);
        await vault.DidNotReceiveWithAnyArgs().RevokeAsync(default!, default);
        await commands.DidNotReceiveWithAnyArgs().QueueCleanupAsync(default!, default!, default);
    }

    [Fact]
    public async Task RevokeAsync_WhenVaultFailsButNyxIdSucceeds_RecordsOnlyVaultCleanup()
    {
        var handler = new RoutingHandler(MeResponse(), """{"message":"deleted"}"""
        );
        var current = Descriptor("key-1", "sec-1", version: 1);
        var query = Substitute.For<IManagedCodexCredentialQueryPort>();
        query.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(new ManagedCodexCredentialSnapshot(current, [], 4));
        var vault = Substitute.For<ISecretVault>();
        vault.RevokeAsync(Arg.Any<RevokeSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<RevokeSecretResult>>(_ => throw new InvalidOperationException("vault unavailable"));
        var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
        commands.CommitRevokedAsync(
                Arg.Any<ExternalSubjectRef>(),
                "key-1",
                Arg.Any<ManagedCodexCredentialCleanup>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(Admission());
        var lifecycle = CreateLifecycle(handler, vault, commands, query);

        await lifecycle.RevokeAsync("user-bearer", "user-a");

        await commands.Received(1).CommitRevokedAsync(
            Arg.Any<ExternalSubjectRef>(),
            "key-1",
            Arg.Is<ManagedCodexCredentialCleanup>(cleanup =>
                !cleanup.NyxIdPending && cleanup.VaultPending && cleanup.SecretRef == "sec-1"),
                Now,
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevokeAsync_WhenVaultRejectsRevocation_RecordsVaultCleanup()
    {
        var handler = new RoutingHandler(MeResponse(), """{"message":"deleted"}"""
        );
        var current = Descriptor("key-1", "sec-1", version: 1);
        var query = Substitute.For<IManagedCodexCredentialQueryPort>();
        query.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(new ManagedCodexCredentialSnapshot(current, [], 4));
        var vault = Substitute.For<ISecretVault>();
        vault.RevokeAsync(Arg.Any<RevokeSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RevokeSecretResult(false));
        var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
        commands.CommitRevokedAsync(
                Arg.Any<ExternalSubjectRef>(),
                "key-1",
                Arg.Any<ManagedCodexCredentialCleanup>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(Admission());
        var lifecycle = CreateLifecycle(handler, vault, commands, query);

        await lifecycle.RevokeAsync("user-bearer", "user-a");

        await commands.Received(1).CommitRevokedAsync(
            Arg.Any<ExternalSubjectRef>(),
            "key-1",
            Arg.Is<ManagedCodexCredentialCleanup>(cleanup =>
                !cleanup.NyxIdPending && cleanup.VaultPending && cleanup.SecretRef == "sec-1"),
            Now,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevokeAsync_WhenCallerCancelsAfterVaultRevocation_CompletesNyxIdAndPersistence()
    {
        using var callerCancellation = new CancellationTokenSource();
        var current = Descriptor("key-1", "sec-1", version: 1);
        var query = Substitute.For<IManagedCodexCredentialQueryPort>();
        query.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(new ManagedCodexCredentialSnapshot(current, [], 4));
        var vault = Substitute.For<ISecretVault>();
        CancellationToken vaultToken = default;
        vault.RevokeAsync(Arg.Any<RevokeSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                vaultToken = call.Arg<CancellationToken>();
                callerCancellation.Cancel();
                return new RevokeSecretResult(true);
            });
        var nyxId = Substitute.For<IManagedCodexNyxIdCredentialPort>();
        nyxId.GetCurrentUserIdAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns("user-a");
        CancellationToken nyxIdToken = default;
        nyxId.RevokeApiKeyAsync("user-bearer", "key-1", Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                nyxIdToken = call.Arg<CancellationToken>();
                nyxIdToken.ThrowIfCancellationRequested();
                return true;
            });
        var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
        CancellationToken persistenceToken = default;
        commands.CommitRevokedAsync(
                Arg.Any<ExternalSubjectRef>(),
                "key-1",
                Arg.Any<ManagedCodexCredentialCleanup>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                persistenceToken = call.Arg<CancellationToken>();
                persistenceToken.ThrowIfCancellationRequested();
                return Admission();
            });
        var lifecycle = CreateLifecycle(
            new RoutingHandler(),
            vault,
            commands,
            query,
            nyxIdPort: nyxId);

        var result = await lifecycle.RevokeAsync(
            "user-bearer",
            "user-a",
            callerCancellation.Token);

        result.Status.Should().Be("revocation_accepted");
        vaultToken.Should().NotBe(callerCancellation.Token);
        nyxIdToken.Should().Be(vaultToken);
        persistenceToken.Should().Be(vaultToken);
        vaultToken.CanBeCanceled.Should().BeTrue();
        vaultToken.IsCancellationRequested.Should().BeFalse();
        await commands.Received(1).CommitRevokedAsync(
            Arg.Any<ExternalSubjectRef>(),
            "key-1",
            Arg.Is<ManagedCodexCredentialCleanup>(cleanup =>
                !cleanup.NyxIdPending && !cleanup.VaultPending),
            Now,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevokeAsync_WhenCallerCancelsAfterNyxIdRevocation_StillPersistsCleanupFacts()
    {
        using var callerCancellation = new CancellationTokenSource();
        var current = Descriptor("key-1", "sec-1", version: 1);
        var query = Substitute.For<IManagedCodexCredentialQueryPort>();
        query.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(new ManagedCodexCredentialSnapshot(current, [], 4));
        var vault = Substitute.For<ISecretVault>();
        vault.RevokeAsync(Arg.Any<RevokeSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RevokeSecretResult(true));
        var nyxId = Substitute.For<IManagedCodexNyxIdCredentialPort>();
        nyxId.GetCurrentUserIdAsync("user-bearer", Arg.Any<CancellationToken>())
            .Returns("user-a");
        nyxId.RevokeApiKeyAsync("user-bearer", "key-1", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callerCancellation.Cancel();
                return true;
            });
        var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
        CancellationToken persistenceToken = default;
        commands.CommitRevokedAsync(
                Arg.Any<ExternalSubjectRef>(),
                "key-1",
                Arg.Any<ManagedCodexCredentialCleanup>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                persistenceToken = call.Arg<CancellationToken>();
                persistenceToken.ThrowIfCancellationRequested();
                return Admission();
            });
        var lifecycle = CreateLifecycle(
            new RoutingHandler(),
            vault,
            commands,
            query,
            nyxIdPort: nyxId);

        var result = await lifecycle.RevokeAsync(
            "user-bearer",
            "user-a",
            callerCancellation.Token);

        result.Status.Should().Be("revocation_accepted");
        persistenceToken.Should().NotBe(callerCancellation.Token);
        persistenceToken.CanBeCanceled.Should().BeTrue();
        persistenceToken.IsCancellationRequested.Should().BeFalse();
        await commands.Received(1).CommitRevokedAsync(
            Arg.Any<ExternalSubjectRef>(),
            "key-1",
            Arg.Is<ManagedCodexCredentialCleanup>(cleanup =>
                !cleanup.NyxIdPending && !cleanup.VaultPending),
            Now,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionAsync_WhenIssuedKeyCannotBeRevoked_QueuesOnlyTheNonSecretCleanupLocator()
    {
        var handler = new RoutingHandler(
            MeResponse(),
            UserServicesResponse(),
            """{"keys":[]}""",
            IssuedKeyResponse("key-orphan", RawKey),
            """{"error":true,"status":503,"body":"delete unavailable"}"""
        );
        var vault = Substitute.For<ISecretVault>();
        vault.PutAsync(Arg.Any<StoreSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<StoreSecretResult>>(_ => throw new InvalidOperationException("vault unavailable"));
        vault.RevokeAsync(Arg.Any<RevokeSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RevokeSecretResult(true));
        var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
        commands.QueueCleanupAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<ManagedCodexCredentialCleanup>(),
                Arg.Any<CancellationToken>())
            .Returns(Admission());
        var lifecycle = CreateLifecycle(handler, vault, commands);

        var act = () => lifecycle.ProvisionAsync("user-bearer", "user-a");

        await act.Should().ThrowAsync<ManagedCodexCredentialLifecycleException>();
        await commands.Received(1).QueueCleanupAsync(
            Arg.Any<ExternalSubjectRef>(),
            Arg.Is<ManagedCodexCredentialCleanup>(cleanup =>
                cleanup.ApiKeyId == "key-orphan" &&
                cleanup.NyxIdPending &&
                !cleanup.VaultPending &&
                !cleanup.ToString().Contains(RawKey, StringComparison.Ordinal)),
            Arg.Is<CancellationToken>(token =>
                token.CanBeCanceled && !token.IsCancellationRequested));
    }

    [Fact]
    public async Task ProvisionAsync_RetriesPersistedCleanupTracksBeforeIssuingAnotherKey()
    {
        var handler = new RoutingHandler(
            MeResponse(),
            """{"message":"deleted"}""",
            UserServicesResponse(),
            """{"keys":[]}""",
            IssuedKeyResponse("key-new", RawKey),
            ApiKeyListResponse("key-new", Now.AddDays(30)));
        var previous = Descriptor("key-old", "sec-old", version: 1);
        previous.Status = ManagedCodexCredentialStatus.Revoked;
        var query = Substitute.For<IManagedCodexCredentialQueryPort>();
        query.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(new ManagedCodexCredentialSnapshot(
                previous,
                [new ManagedCodexCredentialCleanup
                {
                    ApiKeyId = "key-orphan",
                    SecretRef = "sec-orphan",
                    NyxIdPending = true,
                    VaultPending = true,
                    RequestedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(Now.AddMinutes(-5)),
                }],
                5));
        var vault = Substitute.For<ISecretVault>();
        vault.RevokeAsync(Arg.Any<RevokeSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RevokeSecretResult(true));
        vault.PutAsync(Arg.Any<StoreSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new StoreSecretResult(Reference(
                call.Arg<StoreSecretRequest>(),
                call.Arg<StoreSecretRequest>().RequestedRef!,
                version: 1))));
        var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
        commands.CompleteCleanupTrackAsync(
                Arg.Any<ExternalSubjectRef>(),
                "key-orphan",
                Arg.Any<ManagedCodexCredentialCleanupTrack>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(Admission());
        commands.CommitProvisionedAsync(
                Arg.Any<ManagedCodexCredentialDescriptor>(),
                Arg.Any<CancellationToken>())
            .Returns(Admission());
        var lifecycle = CreateLifecycle(handler, vault, commands, query);

        await lifecycle.ProvisionAsync("user-bearer", "user-a");

        handler.Paths.Should().Equal(
            "/api/v1/users/me",
            "/api/v1/api-keys/key-orphan",
            "/api/v1/user-services",
            "/api/v1/api-keys",
            "/api/v1/api-keys",
            "/api/v1/api-keys");
        await vault.Received(1).RevokeAsync(
            Arg.Is<RevokeSecretRequest>(request =>
                request.Ref == "sec-orphan" &&
                request.OwnerScopeKey == "managed-codex-credential:nyxid::user-a"),
            Arg.Any<CancellationToken>());
        await commands.Received(1).CompleteCleanupTrackAsync(
            Arg.Any<ExternalSubjectRef>(),
            "key-orphan",
            ManagedCodexCredentialCleanupTrack.NyxId,
            Now,
            Arg.Any<CancellationToken>());
        await commands.Received(1).CompleteCleanupTrackAsync(
            Arg.Any<ExternalSubjectRef>(),
            "key-orphan",
            ManagedCodexCredentialCleanupTrack.Vault,
            Now,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionAsync_WhenPersonalAndInheritedSandboxesExist_UsesOnlyThePersonalService()
    {
        var handler = new RoutingHandler(
            MeResponse(),
            UserServicesWithPersonalAndInheritedSandboxesResponse(),
            """{"keys":[]}""",
            IssuedKeyResponse("key-personal", RawKey),
            ApiKeyListResponse("key-personal", Now.AddDays(30)));
        var vault = Substitute.For<ISecretVault>();
        vault.PutAsync(Arg.Any<StoreSecretRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new StoreSecretResult(Reference(
                call.Arg<StoreSecretRequest>(),
                call.Arg<StoreSecretRequest>().RequestedRef!,
                version: 1))));
        var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
        commands.CommitProvisionedAsync(
                Arg.Any<ManagedCodexCredentialDescriptor>(),
                Arg.Any<CancellationToken>())
            .Returns(Admission());
        var lifecycle = CreateLifecycle(handler, vault, commands);

        await lifecycle.ProvisionAsync("user-bearer", "user-a");

        using var body = JsonDocument.Parse(handler.RequestBodies.Single());
        body.RootElement.GetProperty("allowed_service_ids").EnumerateArray()
            .Select(static item => item.GetString()).Should().Equal("us-sandbox");
        await commands.Received(1).CommitProvisionedAsync(
            Arg.Is<ManagedCodexCredentialDescriptor>(descriptor =>
                descriptor.ChronoSandboxUserServiceId == "us-sandbox"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionAsync_WhenOnlyInheritedSandboxExists_FailsBeforeKeyCreation()
    {
        var handler = new RoutingHandler(
            MeResponse(),
            UserServicesWithInheritedSandboxOnlyResponse());
        var vault = Substitute.For<ISecretVault>();
        var commands = Substitute.For<IManagedCodexCredentialCommandPort>();
        var lifecycle = CreateLifecycle(handler, vault, commands);

        var act = () => lifecycle.ProvisionAsync("user-bearer", "user-a");

        var exception = (await act.Should().ThrowAsync<ManagedCodexCredentialLifecycleException>()).Which;
        exception.Code.Should().Be("chrono_sandbox_service_unavailable");
        handler.Paths.Should().Equal("/api/v1/users/me", "/api/v1/user-services");
        await vault.DidNotReceiveWithAnyArgs().PutAsync(default!, default);
        await commands.DidNotReceiveWithAnyArgs().CommitProvisionedAsync(default!, default);
    }

    private static ManagedCodexCredentialLifecycle CreateLifecycle(
        HttpMessageHandler handler,
        ISecretVault vault,
        IManagedCodexCredentialCommandPort commands,
        IManagedCodexCredentialQueryPort? query = null,
        ManagedCodexOptions? options = null,
        IManagedCodexCredentialMutationLease? mutationLease = null,
        IManagedCodexNyxIdCredentialPort? nyxIdPort = null)
    {
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" },
            new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example.com") });
        if (query is null)
        {
            query = Substitute.For<IManagedCodexCredentialQueryPort>();
            query.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
                .Returns((ManagedCodexCredentialSnapshot?)null);
        }
        return new ManagedCodexCredentialLifecycle(
            Options.Create(options ?? ManagedCodexOptionsValidatorTests.ValidOptions()),
            nyxIdPort ?? new NyxIdManagedCodexCredentialAdapter(new TestNyxIdApiClientFactory(client)),
            vault,
            query,
            commands,
            mutationLease ?? new InMemoryManagedCodexCredentialMutationLease(),
            new FakeTimeProvider(Now),
            NullLogger<ManagedCodexCredentialLifecycle>.Instance);
    }

    private static ManagedCodexCredentialDescriptor Descriptor(string keyId, string secretRef, long version)
    {
        var owner = new ExternalSubjectRef
        {
            Platform = "nyxid",
            Tenant = string.Empty,
            ExternalUserId = "user-a",
        };
        return new ManagedCodexCredentialDescriptor
        {
            Owner = owner,
            ApiKeyId = keyId,
            SecretReference = new SecretReference
            {
                Ref = secretRef,
                Purpose = "managed.codex-invocation-agent-key",
                OwnerScopeKey = "managed-codex-credential:nyxid::user-a",
                Fingerprint = "fingerprint",
                Version = version,
                ExpiresAtUnixMs = Now.AddDays(30).ToUnixTimeMilliseconds(),
            },
            ChronoSandboxUserServiceId = "us-sandbox",
            ChronoSandboxServiceSlug = "chrono-sandbox",
            ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(Now.AddDays(30)),
            Status = ManagedCodexCredentialStatus.Active,
        };
    }

    private static SecretReference Reference(StoreSecretRequest request, string reference, long version) => new()
    {
        Ref = reference,
        Purpose = request.Purpose,
        OwnerScopeKey = request.OwnerScopeKey,
        Fingerprint = "fingerprint",
        Version = version,
        CreatedAtUnixMs = Now.ToUnixTimeMilliseconds(),
        ExpiresAtUnixMs = request.ExpiresAt?.ToUnixTimeMilliseconds() ?? 0,
    };

    private static ResolveSecretResult ResolvedSecret(
        ResolveSecretRequest request,
        DateTimeOffset expiresAt) =>
        new(
            new SecretReference
            {
                Ref = request.Ref,
                Purpose = request.Purpose,
                OwnerScopeKey = request.OwnerScopeKey,
                Fingerprint = "recovered-fingerprint",
                Version = 1,
                CreatedAtUnixMs = Now.ToUnixTimeMilliseconds(),
                ExpiresAtUnixMs = expiresAt.ToUnixTimeMilliseconds(),
            },
            "recovered-raw-key");

    private static DispatchAdmission Admission() =>
        new(true, "command-1", Now, "managed-codex-credential:nyxid::user-a", "command-1");

    private static string MeResponse(string userId = "user-a") =>
        JsonSerializer.Serialize(new { id = userId });

    private static string UserServicesResponse(
        string sandboxId = "us-sandbox",
        string llmId = "us-llm",
        bool sandboxActive = true,
        bool llmActive = true,
        bool forwardAccessToken = false,
        bool injectDelegationToken = true,
        string delegationScope = "proxy:*") =>
        JsonSerializer.Serialize(new
        {
            services = new object[]
            {
                new
                {
                    id = sandboxId,
                    slug = "chrono-sandbox",
                    is_active = sandboxActive,
                    forward_access_token = forwardAccessToken,
                    inject_delegation_token = injectDelegationToken,
                    delegation_token_scope = delegationScope,
                    credential_source = new { type = "personal" },
                },
                new
                {
                    id = llmId,
                    slug = "chrono-llm-public",
                    is_active = llmActive,
                    credential_source = new { type = "personal" },
                },
            },
        });

    private static string UserServicesWithPersonalAndInheritedSandboxesResponse() =>
        """
        {
          "services": [
            {
              "id": "us-sandbox-org",
              "slug": "chrono-sandbox",
              "is_active": true,
              "forward_access_token": false,
              "inject_delegation_token": true,
              "delegation_token_scope": "proxy:*",
              "credential_source": { "type": "org", "org_id": "org-a", "allowed": true }
            },
            {
              "id": "us-sandbox",
              "slug": "chrono-sandbox",
              "is_active": true,
              "forward_access_token": false,
              "inject_delegation_token": true,
              "delegation_token_scope": "proxy:*",
              "credential_source": { "type": "personal" }
            },
            {
              "id": "us-llm",
              "slug": "chrono-llm-public",
              "is_active": true,
              "credential_source": { "type": "personal" }
            }
          ]
        }
        """;

    private static string UserServicesWithInheritedSandboxOnlyResponse() =>
        """
        {
          "services": [
            {
              "id": "us-sandbox-org",
              "slug": "chrono-sandbox",
              "is_active": true,
              "forward_access_token": false,
              "inject_delegation_token": true,
              "delegation_token_scope": "proxy:*",
              "credential_source": { "type": "org", "org_id": "org-a", "allowed": true }
            },
            {
              "id": "us-llm",
              "slug": "chrono-llm-public",
              "is_active": true,
              "credential_source": { "type": "personal" }
            }
          ]
        }
        """;

    private static string IssuedKeyResponse(
        string id,
        string fullKey,
        string scopes = "proxy",
        bool allowAllServices = false,
        bool allowAllNodes = false,
        bool includeLlmService = false,
        bool includeNode = false,
        string sandboxServiceId = "us-sandbox") =>
        JsonSerializer.Serialize(new
        {
            id,
            name = "aevatar-managed-codex",
            full_key = fullKey,
            scopes,
            allowed_service_ids = includeLlmService
                ? new[] { sandboxServiceId, "us-llm" }
                : [sandboxServiceId],
            allowed_node_ids = includeNode ? new[] { "node-1" } : [],
            allow_all_services = allowAllServices,
            allow_all_nodes = allowAllNodes,
        });

    private static string ApiKeyListResponse(
        string id,
        DateTimeOffset expiresAt,
        bool isActive = true,
        string sandboxServiceId = "us-sandbox") =>
        JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    id,
                    name = "aevatar-managed-codex",
                    scopes = "proxy",
                    platform = "codex",
                    is_active = isActive,
                    allowed_service_ids = new[] { sandboxServiceId },
                    allowed_node_ids = Array.Empty<string>(),
                    allow_all_services = false,
                    allow_all_nodes = false,
                    expires_at = expiresAt.ToString("O"),
                },
            },
        });

    private sealed class TestNyxIdApiClientFactory(NyxIdApiClient client) : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() => client;
    }

    private sealed class RoutingHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);
        public List<string> Paths { get; } = [];
        public List<HttpMethod> Methods { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri?.PathAndQuery ?? string.Empty);
            Methods.Add(request.Method);
            if (request.Content is not null)
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            if (!_responses.TryDequeue(out var response))
                throw new InvalidOperationException("No response remains.");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class PathRoutingHandler(Func<HttpRequestMessage, string> route) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(route(request), Encoding.UTF8, "application/json"),
            });
        }
    }
}
