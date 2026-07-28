using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelWorkflowResultDeliveryRepairServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 21, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RepairAsync_NewHistoricalRegistration_CompletesInOrderWithoutLeakingSecrets()
    {
        var fixture = new Fixture(Registration());

        var result = await fixture.Service.RepairAsync(
            "reg-alpha",
            "scope-alpha",
            "user-alpha",
            "user-bearer-alpha");

        result.Status.Should().Be(ChannelWorkflowResultDeliveryRepairResultStatus.Repaired);
        result.RegistrationId.Should().Be("reg-alpha");
        result.NyxAgentApiKeyId.Should().Be("key-new-alpha");
        result.RequestId.Should().NotBeNullOrWhiteSpace();
        fixture.Operations.Should().Equal(
            "query",
            "observation.bind",
            "command.request",
            "observation.wait.Requested",
            "nyx.rotate:key-old-alpha",
            "vault.put",
            "observation.bind",
            "command.prepare",
            "observation.wait.Prepared",
            "nyx.route:route-alpha:key-new-alpha",
            "observation.bind",
            "command.complete",
            "observation.wait.Completed");
        fixture.Nyx.ListCalls.Should().Be(0, "a newly admitted request cannot hide a prior rotation");
        fixture.Vault.Requests.Should().ContainSingle().Which.Should().Be(
            new StoreSecretRequest(
                CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
                "scope-alpha",
                "key-new-alpha",
                "nyxid_ag_secret_alpha",
                $"channel-workflow-result-delivery-repair:reg-alpha:{result.RequestId}"));
        fixture.Commands.Requests.Should().ContainSingle();
        fixture.Commands.Prepares.Should().ContainSingle();
        fixture.Commands.Completes.Should().ContainSingle();
        fixture.Commands.AllRequestIds.Should().OnlyContain(requestId => requestId == result.RequestId);
        fixture.Commands.AllExpectedApiKeyIds.Should().OnlyContain(id => id == "key-old-alpha");
        fixture.Commands.Requests[0].ExpectedConversationRouteId.Should().Be("route-alpha");
        var safeText = string.Join('\n', fixture.Logger.Messages.Append(result.ToString()));
        safeText.Should().NotContain("user-bearer-alpha");
        safeText.Should().NotContain("nyxid_ag_secret_alpha");
        safeText.Should().NotContain("sec-repair-alpha");
    }

    [Fact]
    public void RepairContract_HasNoPlatformAdminAuthorityInput()
    {
        typeof(IChannelWorkflowResultDeliveryRepairService)
            .GetMethod(nameof(IChannelWorkflowResultDeliveryRepairService.RepairAsync))!
            .GetParameters()
            .Select(static parameter => parameter.Name)
            .Should().Equal(
                "registrationId",
                "callerScopeId",
                "requestedBySubjectId",
                "accessToken",
                "ct");
    }

    [Theory]
    [InlineData("scope-beta")]
    [InlineData("")]
    public async Task RepairAsync_NonOwnerIsHiddenWithoutSideEffects(string callerScopeId)
    {
        var fixture = new Fixture(Registration());

        var result = await fixture.Service.RepairAsync(
            "reg-alpha",
            callerScopeId,
            "user-alpha",
            "user-bearer-alpha");

        result.Status.Should().Be(ChannelWorkflowResultDeliveryRepairResultStatus.NotFound);
        fixture.AssertNoMutationSideEffects();
    }

    [Fact]
    public async Task RepairAsync_NonLarkAndAlreadyEnabledReturnBeforeSideEffects()
    {
        var telegram = new Fixture(Registration(platform: "telegram"));
        var unsupported = await telegram.Service.RepairAsync(
            "reg-alpha",
            "scope-alpha",
            "user-alpha",
            "user-bearer-alpha");
        unsupported.Status.Should().Be(
            ChannelWorkflowResultDeliveryRepairResultStatus.UnsupportedPlatform);
        telegram.AssertNoMutationSideEffects();

        var enabled = Registration();
        enabled.WorkflowResultDeliveryCredential = PreparedReference();
        var enabledFixture = new Fixture(enabled);
        var alreadyEnabled = await enabledFixture.Service.RepairAsync(
            "reg-alpha",
            "scope-alpha",
            "user-alpha",
            "user-bearer-alpha");
        alreadyEnabled.Status.Should().Be(
            ChannelWorkflowResultDeliveryRepairResultStatus.AlreadyEnabled);
        enabledFixture.AssertNoMutationSideEffects();
    }

    [Fact]
    public async Task RepairAsync_CancellationBeforeRotationHasNoSideEffects()
    {
        var fixture = new Fixture(Registration());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => fixture.Service.RepairAsync(
            "reg-alpha",
            "scope-alpha",
            "user-alpha",
            "user-bearer-alpha",
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        fixture.AssertNoMutationSideEffects();
    }

    [Fact]
    public async Task RepairAsync_AfterRotationUsesDetachedBoundedCompletionToken()
    {
        using var callerCancellation = new CancellationTokenSource();
        var fixture = new Fixture(Registration());
        fixture.Nyx.AfterRotate = callerCancellation.Cancel;

        var result = await fixture.Service.RepairAsync(
            "reg-alpha",
            "scope-alpha",
            "user-alpha",
            "user-bearer-alpha",
            callerCancellation.Token);

        result.Status.Should().Be(ChannelWorkflowResultDeliveryRepairResultStatus.Repaired);
        callerCancellation.IsCancellationRequested.Should().BeTrue();
        fixture.Vault.Tokens.Should().OnlyContain(token =>
            token.CanBeCanceled && !token.IsCancellationRequested);
        fixture.Commands.Tokens.Skip(1).Should().OnlyContain(token =>
            token.CanBeCanceled && !token.IsCancellationRequested);
        fixture.Nyx.RouteTokens.Should().OnlyContain(token =>
            token.CanBeCanceled && !token.IsCancellationRequested);
    }

    [Fact]
    public async Task RepairAsync_VaultFailureAttemptsThreeTimesAndCommitsNonSecretFailure()
    {
        var fixture = new Fixture(Registration());
        fixture.Vault.FailPutAttempts = 3;

        var result = await fixture.Service.RepairAsync(
            "reg-alpha",
            "scope-alpha",
            "user-alpha",
            "user-bearer-alpha");

        result.Status.Should().Be(ChannelWorkflowResultDeliveryRepairResultStatus.RepairFailed);
        result.FailurePhase.Should().Be(ChannelWorkflowResultDeliveryRepairPhase.VaultStorage);
        result.FailureReason.Should().Be(
            ChannelWorkflowResultDeliveryRepairFailureReason.VaultStorageFailed);
        fixture.Vault.Requests.Should().HaveCount(3);
        fixture.Commands.Failures.Should().ContainSingle();
        fixture.Commands.Failures[0].RotatedApiKeyId.Should().Be("key-new-alpha");
        fixture.Commands.Failures[0].PreparedSecretReference.Should().BeNull();
        fixture.Commands.Failures[0].ToString().Should().NotContain("nyxid_ag_secret_alpha");
        fixture.Operations.Count(operation => operation == "vault.put").Should().Be(3);
    }

    [Theory]
    [InlineData(ChannelWorkflowResultDeliveryRepairStatus.CredentialPrepared)]
    [InlineData(ChannelWorkflowResultDeliveryRepairStatus.Failed)]
    public async Task RepairAsync_PreparedCredentialResumesRouteAndCompletionWithoutRotation(
        ChannelWorkflowResultDeliveryRepairStatus status)
    {
        var entry = Registration();
        entry.WorkflowResultDeliveryRepair = PreparedRepair(status);
        var fixture = new Fixture(entry);

        var result = await fixture.Service.RepairAsync(
            "reg-alpha",
            "scope-alpha",
            "user-alpha",
            "user-bearer-alpha");

        result.Status.Should().Be(ChannelWorkflowResultDeliveryRepairResultStatus.Repaired);
        fixture.Nyx.RotationSourceKeyIds.Should().BeEmpty();
        fixture.Vault.Requests.Should().BeEmpty();
        fixture.Commands.Requests.Should().BeEmpty();
        fixture.Commands.Prepares.Should().BeEmpty();
        fixture.Commands.Completes.Should().ContainSingle();
        fixture.Operations.Should().Equal(
            "query",
            "nyx.route:route-alpha:key-new-alpha",
            "observation.bind",
            "command.complete",
            "observation.wait.Completed");
    }

    [Fact]
    public async Task RepairAsync_RouteFailureRetainsPreparedFactsAndCommitsTypedFailure()
    {
        var entry = Registration();
        entry.WorkflowResultDeliveryRepair = PreparedRepair(
            ChannelWorkflowResultDeliveryRepairStatus.CredentialPrepared);
        var fixture = new Fixture(entry);
        fixture.Nyx.FailRoute = true;

        var result = await fixture.Service.RepairAsync(
            "reg-alpha",
            "scope-alpha",
            "user-alpha",
            "user-bearer-alpha");

        result.Status.Should().Be(ChannelWorkflowResultDeliveryRepairResultStatus.RepairFailed);
        result.FailurePhase.Should().Be(ChannelWorkflowResultDeliveryRepairPhase.RouteRebinding);
        result.FailureReason.Should().Be(
            ChannelWorkflowResultDeliveryRepairFailureReason.RouteUpdateFailed);
        fixture.Commands.Failures.Should().ContainSingle();
        fixture.Commands.Failures[0].RotatedApiKeyId.Should().Be("key-new-alpha");
        fixture.Commands.Failures[0].PreparedSecretReference.Should().Be(PreparedReference());
    }

    [Fact]
    public async Task RepairAsync_VaultFailureRetryRotatesRecordedReplacementKey()
    {
        var entry = Registration();
        entry.WorkflowResultDeliveryRepair = RequestedRepair();
        entry.WorkflowResultDeliveryRepair.Status = ChannelWorkflowResultDeliveryRepairStatus.Failed;
        entry.WorkflowResultDeliveryRepair.RotatedApiKeyId = "key-replacement-alpha";
        entry.WorkflowResultDeliveryRepair.FailurePhase =
            ChannelWorkflowResultDeliveryRepairPhase.VaultStorage;
        entry.WorkflowResultDeliveryRepair.FailureReason =
            ChannelWorkflowResultDeliveryRepairFailureReason.VaultStorageFailed;
        var fixture = new Fixture(entry);

        var result = await fixture.Service.RepairAsync(
            "reg-alpha",
            "scope-alpha",
            "user-alpha",
            "user-bearer-alpha");

        result.Status.Should().Be(ChannelWorkflowResultDeliveryRepairResultStatus.Repaired);
        fixture.Nyx.RotationSourceKeyIds.Should().Equal("key-replacement-alpha");
        fixture.Nyx.ListCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(false, "key-old-alpha")]
    [InlineData(true, "key-recovered-alpha")]
    public async Task RepairAsync_ExistingRequestedStateRecoversUniqueActiveReplacement(
        bool hasCandidate,
        string expectedRotationSource)
    {
        var entry = Registration();
        entry.WorkflowResultDeliveryRepair = RequestedRepair();
        var fixture = new Fixture(entry);
        if (hasCandidate)
        {
            fixture.Nyx.Keys.Add(new ChannelNyxAgentKeySummary(
                "key-recovered-alpha",
                ChannelWorkflowResultDeliveryRepairNyxPort.RelayKeyName("reg-alpha"),
                true,
                Now));
        }
        else
        {
            fixture.Nyx.Keys.Add(new ChannelNyxAgentKeySummary(
                "key-old-alpha",
                ChannelWorkflowResultDeliveryRepairNyxPort.RelayKeyName("reg-alpha"),
                true,
                Now.AddMinutes(-1)));
        }

        var result = await fixture.Service.RepairAsync(
            "reg-alpha",
            "scope-alpha",
            "user-alpha",
            "user-bearer-alpha");

        result.Status.Should().Be(ChannelWorkflowResultDeliveryRepairResultStatus.Repaired);
        fixture.Nyx.ListCalls.Should().Be(1);
        fixture.Nyx.RotationSourceKeyIds.Should().Equal(expectedRotationSource);
    }

    [Fact]
    public async Task RepairAsync_ExistingRequestedStateWithoutActiveSourceCommitsAmbiguousRecovery()
    {
        var entry = Registration();
        entry.WorkflowResultDeliveryRepair = RequestedRepair();
        var fixture = new Fixture(entry);

        var result = await fixture.Service.RepairAsync(
            "reg-alpha",
            "scope-alpha",
            "user-alpha",
            "user-bearer-alpha");

        result.Status.Should().Be(ChannelWorkflowResultDeliveryRepairResultStatus.RepairFailed);
        result.FailurePhase.Should().Be(
            ChannelWorkflowResultDeliveryRepairPhase.RotatedKeyRecovery);
        result.FailureReason.Should().Be(
            ChannelWorkflowResultDeliveryRepairFailureReason.AmbiguousRotatedKeyRecovery);
        fixture.Nyx.RotationSourceKeyIds.Should().BeEmpty();
        fixture.Commands.Failures.Should().ContainSingle();
    }

    [Fact]
    public async Task RepairAsync_AmbiguousRecoveredKeysCommitsFailureWithoutGuessing()
    {
        var entry = Registration();
        entry.WorkflowResultDeliveryRepair = RequestedRepair();
        var fixture = new Fixture(entry);
        var keyName = ChannelWorkflowResultDeliveryRepairNyxPort.RelayKeyName("reg-alpha");
        fixture.Nyx.Keys.Add(new ChannelNyxAgentKeySummary(
            "key-recovered-alpha",
            keyName,
            true,
            Now));
        fixture.Nyx.Keys.Add(new ChannelNyxAgentKeySummary(
            "key-recovered-beta",
            keyName,
            true,
            Now.AddSeconds(1)));

        var result = await fixture.Service.RepairAsync(
            "reg-alpha",
            "scope-alpha",
            "user-alpha",
            "user-bearer-alpha");

        result.Status.Should().Be(ChannelWorkflowResultDeliveryRepairResultStatus.RepairFailed);
        result.FailurePhase.Should().Be(
            ChannelWorkflowResultDeliveryRepairPhase.RotatedKeyRecovery);
        result.FailureReason.Should().Be(
            ChannelWorkflowResultDeliveryRepairFailureReason.AmbiguousRotatedKeyRecovery);
        fixture.Nyx.RotationSourceKeyIds.Should().BeEmpty();
        fixture.Commands.Failures.Should().ContainSingle();
    }

    [Fact]
    public async Task RepairAsync_KeyDiscoveryFailureCommitsAmbiguousRecoveryWithoutGuessing()
    {
        var entry = Registration();
        entry.WorkflowResultDeliveryRepair = RequestedRepair();
        var fixture = new Fixture(entry);
        fixture.Nyx.FailList = true;

        var result = await fixture.Service.RepairAsync(
            "reg-alpha",
            "scope-alpha",
            "user-alpha",
            "user-bearer-alpha");

        result.Status.Should().Be(ChannelWorkflowResultDeliveryRepairResultStatus.RepairFailed);
        result.FailurePhase.Should().Be(
            ChannelWorkflowResultDeliveryRepairPhase.RotatedKeyRecovery);
        result.FailureReason.Should().Be(
            ChannelWorkflowResultDeliveryRepairFailureReason.AmbiguousRotatedKeyRecovery);
        fixture.Nyx.RotationSourceKeyIds.Should().BeEmpty();
        fixture.Commands.Failures.Should().ContainSingle();
    }

    private static ChannelBotRegistrationEntry Registration(string platform = "lark") =>
        new()
        {
            Id = "reg-alpha",
            Platform = platform,
            ScopeId = "scope-alpha",
            NyxProviderSlug = "api-lark-bot",
            WebhookUrl = "https://nyx.example/api/v1/webhooks/channel/lark/bot-alpha",
            NyxChannelBotId = "bot-alpha",
            NyxAgentApiKeyId = "key-old-alpha",
            NyxConversationRouteId = "route-alpha",
            DefaultSkillName = "team-entry-alpha",
        };

    private static SecretReference PreparedReference() =>
        new()
        {
            Ref = "sec-repair-alpha",
            Purpose = CredentialSecretPurposes.ChannelWorkflowResultDeliveryAgentKey,
            OwnerScopeKey = "scope-alpha",
            Version = 1,
        };

    private static ChannelWorkflowResultDeliveryRepairState RequestedRepair() =>
        new()
        {
            RequestId = "repair-alpha",
            Status = ChannelWorkflowResultDeliveryRepairStatus.Requested,
            ExpectedApiKeyId = "key-old-alpha",
            ExpectedConversationRouteId = "route-alpha",
            RequestedBySubjectId = "user-alpha",
            RequestedAtUnixMs = Now.ToUnixTimeMilliseconds(),
            UpdatedAtUnixMs = Now.ToUnixTimeMilliseconds(),
        };

    private static ChannelWorkflowResultDeliveryRepairState PreparedRepair(
        ChannelWorkflowResultDeliveryRepairStatus status)
    {
        var repair = RequestedRepair();
        repair.Status = status;
        repair.RotatedApiKeyId = "key-new-alpha";
        repair.PreparedSecretReference = PreparedReference();
        if (status == ChannelWorkflowResultDeliveryRepairStatus.Failed)
        {
            repair.FailurePhase = ChannelWorkflowResultDeliveryRepairPhase.RouteRebinding;
            repair.FailureReason = ChannelWorkflowResultDeliveryRepairFailureReason.RouteUpdateFailed;
        }

        return repair;
    }

    private sealed class Fixture
    {
        public Fixture(ChannelBotRegistrationEntry? registration)
        {
            Registration = registration;
            Query = new RecordingQueryPort(this);
            Commands = new RecordingCommandPort(this);
            Observation = new RecordingObservationPort(this);
            Nyx = new RecordingNyxPort(this);
            Vault = new RecordingVault(this);
            Logger = new RecordingLogger<ChannelWorkflowResultDeliveryRepairService>();
            Service = new ChannelWorkflowResultDeliveryRepairService(
                Query,
                Commands,
                Observation,
                Nyx,
                Vault,
                Logger,
                new FakeTimeProvider(Now));
        }

        public ChannelBotRegistrationEntry? Registration { get; }
        public List<string> Operations { get; } = [];
        public RecordingQueryPort Query { get; }
        public RecordingCommandPort Commands { get; }
        public RecordingObservationPort Observation { get; }
        public RecordingNyxPort Nyx { get; }
        public RecordingVault Vault { get; }
        public RecordingLogger<ChannelWorkflowResultDeliveryRepairService> Logger { get; }
        public ChannelWorkflowResultDeliveryRepairService Service { get; }

        public void AssertNoMutationSideEffects()
        {
            Commands.AllRequestIds.Should().BeEmpty();
            Observation.BindCalls.Should().Be(0);
            Nyx.RotationSourceKeyIds.Should().BeEmpty();
            Nyx.ListCalls.Should().Be(0);
            Nyx.RouteTokens.Should().BeEmpty();
            Vault.Requests.Should().BeEmpty();
        }
    }

    private sealed class RecordingQueryPort(Fixture owner) : IChannelBotRegistrationQueryPort
    {
        public Task<ChannelBotRegistrationEntry?> GetAsync(
            string registrationId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            owner.Operations.Add("query");
            return Task.FromResult(owner.Registration?.Clone());
        }

        public Task<long?> GetStateVersionAsync(
            string registrationId,
            CancellationToken ct = default) =>
            Task.FromResult<long?>(1);

        public Task<IReadOnlyList<ChannelBotRegistrationEntry>> QueryAllAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ChannelBotRegistrationEntry>>([]);
    }

    private sealed class RecordingCommandPort(Fixture owner)
        : IChannelWorkflowResultDeliveryRepairCommandPort
    {
        public List<ChannelBotWorkflowResultDeliveryRepairRequestCommand> Requests { get; } = [];
        public List<ChannelBotWorkflowResultDeliveryRepairPrepareCommand> Prepares { get; } = [];
        public List<ChannelBotWorkflowResultDeliveryRepairCompleteCommand> Completes { get; } = [];
        public List<ChannelBotWorkflowResultDeliveryRepairFailCommand> Failures { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        public IEnumerable<string> AllRequestIds =>
            Requests.Select(static command => command.RequestId)
                .Concat(Prepares.Select(static command => command.RequestId))
                .Concat(Completes.Select(static command => command.RequestId))
                .Concat(Failures.Select(static command => command.RequestId));

        public IEnumerable<string> AllExpectedApiKeyIds =>
            Requests.Select(static command => command.ExpectedApiKeyId)
                .Concat(Prepares.Select(static command => command.ExpectedApiKeyId))
                .Concat(Completes.Select(static command => command.ExpectedApiKeyId))
                .Concat(Failures.Select(static command => command.ExpectedApiKeyId));

        public Task<ChannelRegistrationCommandAcceptedReceipt> RequestAsync(
            ChannelBotWorkflowResultDeliveryRepairRequestCommand command,
            CancellationToken ct = default) =>
            Record("request", command, Requests, ct);

        public Task<ChannelRegistrationCommandAcceptedReceipt> PrepareAsync(
            ChannelBotWorkflowResultDeliveryRepairPrepareCommand command,
            CancellationToken ct = default) =>
            Record("prepare", command, Prepares, ct);

        public Task<ChannelRegistrationCommandAcceptedReceipt> CompleteAsync(
            ChannelBotWorkflowResultDeliveryRepairCompleteCommand command,
            CancellationToken ct = default) =>
            Record("complete", command, Completes, ct);

        public Task<ChannelRegistrationCommandAcceptedReceipt> FailAsync(
            ChannelBotWorkflowResultDeliveryRepairFailCommand command,
            CancellationToken ct = default) =>
            Record("fail", command, Failures, ct);

        private Task<ChannelRegistrationCommandAcceptedReceipt> Record<T>(
            string name,
            T command,
            ICollection<T> destination,
            CancellationToken ct)
            where T : Google.Protobuf.IMessage<T>
        {
            ct.ThrowIfCancellationRequested();
            owner.Operations.Add("command." + name);
            destination.Add(command.Clone());
            Tokens.Add(ct);
            return Task.FromResult(new ChannelRegistrationCommandAcceptedReceipt(
                ChannelBotRegistrationGAgent.WellKnownId,
                Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N")));
        }
    }

    private sealed class RecordingObservationPort(Fixture owner)
        : IChannelWorkflowResultDeliveryRepairObservationPort
    {
        public int BindCalls { get; private set; }

        public Task<IChannelWorkflowResultDeliveryRepairObservationLease> BindAsync(
            string requestId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            BindCalls++;
            owner.Operations.Add("observation.bind");
            return Task.FromResult<IChannelWorkflowResultDeliveryRepairObservationLease>(
                new RecordingLease(owner));
        }

        private sealed class RecordingLease(Fixture owner)
            : IChannelWorkflowResultDeliveryRepairObservationLease
        {
            public Task<ChannelBotWorkflowResultDeliveryRepairOutcome> WaitAsync(
                ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase expected,
                CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                owner.Operations.Add("observation.wait." + expected);
                return Task.FromResult(BuildOutcome(expected));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            private ChannelBotWorkflowResultDeliveryRepairOutcome BuildOutcome(
                ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase expected) =>
                expected switch
                {
                    ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Requested =>
                        new()
                        {
                            Requested = new ChannelBotWorkflowResultDeliveryRepairRequestedEvent
                            {
                                RegistrationId = owner.Commands.Requests[^1].RegistrationId,
                                Repair = new ChannelWorkflowResultDeliveryRepairState
                                {
                                    RequestId = owner.Commands.Requests[^1].RequestId,
                                    Status = ChannelWorkflowResultDeliveryRepairStatus.Requested,
                                    ExpectedApiKeyId = owner.Commands.Requests[^1].ExpectedApiKeyId,
                                    ExpectedConversationRouteId = owner.Commands.Requests[^1].ExpectedConversationRouteId,
                                    RequestedBySubjectId = owner.Commands.Requests[^1].RequestedBySubjectId,
                                    RequestedAtUnixMs = owner.Commands.Requests[^1].RequestedAtUnixMs,
                                    UpdatedAtUnixMs = owner.Commands.Requests[^1].RequestedAtUnixMs,
                                },
                            },
                        },
                    ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Prepared =>
                        new()
                        {
                            Prepared = new ChannelBotWorkflowResultDeliveryRepairPreparedEvent
                            {
                                RegistrationId = owner.Commands.Prepares[^1].RegistrationId,
                                Repair = PreparedOutcome(owner.Commands.Prepares[^1]),
                            },
                        },
                    ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Completed =>
                        new()
                        {
                            Completed = new ChannelBotWorkflowResultDeliveryRepairCompletedEvent
                            {
                                RegistrationId = owner.Commands.Completes[^1].RegistrationId,
                                RequestId = owner.Commands.Completes[^1].RequestId,
                                RotatedApiKeyId = owner.Commands.Completes[^1].RotatedApiKeyId,
                            },
                        },
                    ChannelBotWorkflowResultDeliveryRepairOutcome.OutcomeOneofCase.Failed =>
                        new()
                        {
                            Failed = new ChannelBotWorkflowResultDeliveryRepairFailedEvent
                            {
                                RegistrationId = owner.Commands.Failures[^1].RegistrationId,
                                Repair = FailedOutcome(owner.Commands.Failures[^1]),
                            },
                        },
                    _ => throw new InvalidOperationException("Unexpected observation case."),
                };

            private static ChannelWorkflowResultDeliveryRepairState PreparedOutcome(
                ChannelBotWorkflowResultDeliveryRepairPrepareCommand command) =>
                new()
                {
                    RequestId = command.RequestId,
                    Status = ChannelWorkflowResultDeliveryRepairStatus.CredentialPrepared,
                    ExpectedApiKeyId = command.ExpectedApiKeyId,
                    ExpectedConversationRouteId = "route-alpha",
                    RotatedApiKeyId = command.RotatedApiKeyId,
                    PreparedSecretReference = command.PreparedSecretReference?.Clone(),
                    RequestedBySubjectId = "user-alpha",
                    RequestedAtUnixMs = Now.ToUnixTimeMilliseconds(),
                    UpdatedAtUnixMs = command.UpdatedAtUnixMs,
                };

            private static ChannelWorkflowResultDeliveryRepairState FailedOutcome(
                ChannelBotWorkflowResultDeliveryRepairFailCommand command) =>
                new()
                {
                    RequestId = command.RequestId,
                    Status = ChannelWorkflowResultDeliveryRepairStatus.Failed,
                    ExpectedApiKeyId = command.ExpectedApiKeyId,
                    ExpectedConversationRouteId = "route-alpha",
                    RotatedApiKeyId = command.RotatedApiKeyId,
                    PreparedSecretReference = command.PreparedSecretReference?.Clone(),
                    FailurePhase = command.FailurePhase,
                    FailureReason = command.FailureReason,
                    RequestedBySubjectId = "user-alpha",
                    RequestedAtUnixMs = Now.ToUnixTimeMilliseconds(),
                    UpdatedAtUnixMs = command.UpdatedAtUnixMs,
                };
        }
    }

    private sealed class RecordingNyxPort(Fixture owner)
        : IChannelWorkflowResultDeliveryRepairNyxPort
    {
        public List<string> RotationSourceKeyIds { get; } = [];
        public List<ChannelNyxAgentKeySummary> Keys { get; } = [];
        public List<CancellationToken> RouteTokens { get; } = [];
        public int ListCalls { get; private set; }
        public bool FailRoute { get; set; }
        public bool FailList { get; set; }
        public Action? AfterRotate { get; set; }

        public Task<ChannelRotatedNyxAgentCredential> RotateAgentKeyAsync(
            string accessToken,
            string apiKeyId,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RotationSourceKeyIds.Add(apiKeyId);
            owner.Operations.Add("nyx.rotate:" + apiKeyId);
            AfterRotate?.Invoke();
            return Task.FromResult(new ChannelRotatedNyxAgentCredential(
                "key-new-alpha",
                "nyxid_ag_secret_alpha",
                Now));
        }

        public Task<IReadOnlyList<ChannelNyxAgentKeySummary>> ListAgentKeysAsync(
            string accessToken,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ListCalls++;
            owner.Operations.Add("nyx.list");
            if (FailList)
            {
                return Task.FromException<IReadOnlyList<ChannelNyxAgentKeySummary>>(
                    new InvalidOperationException("key list unavailable"));
            }
            return Task.FromResult<IReadOnlyList<ChannelNyxAgentKeySummary>>(Keys.ToArray());
        }

        public Task RebindConversationRouteAsync(
            string accessToken,
            string routeId,
            string apiKeyId,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RouteTokens.Add(ct);
            owner.Operations.Add($"nyx.route:{routeId}:{apiKeyId}");
            return FailRoute
                ? Task.FromException(new InvalidOperationException("route failed"))
                : Task.CompletedTask;
        }
    }

    private sealed class RecordingVault(Fixture owner) : ISecretVault
    {
        public List<StoreSecretRequest> Requests { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public int FailPutAttempts { get; set; }

        public Task<StoreSecretResult> PutAsync(
            StoreSecretRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            owner.Operations.Add("vault.put");
            Requests.Add(request);
            Tokens.Add(ct);
            if (FailPutAttempts > 0)
            {
                FailPutAttempts--;
                return Task.FromException<StoreSecretResult>(
                    new InvalidOperationException("vault unavailable"));
            }

            return Task.FromResult(new StoreSecretResult(PreparedReference()));
        }

        public Task<ResolveSecretResult> ResolveAsync(
            ResolveSecretRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RotateSecretResult> RotateAsync(
            RotateSecretRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RevokeSecretResult> RevokeAsync(
            RevokeSecretRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
