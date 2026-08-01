using System.Net;
using System.Text;
using System.Text.Json;
using Aevatar.AI.Abstractions;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Infrastructure.Schedules.Authorization;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aevatar.GAgentService.Tests.Authorization;

public sealed class NyxIdAuthorizationCatalogRefreshPortTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-21T09:00:00Z");
    private static readonly DateTimeOffset EvaluatedAt = DateTimeOffset.Parse("2026-07-21T08:59:59Z");

    [Fact]
    public void ConcreteRefreshPort_ShouldNotImplementRepairRefreshPort()
    {
        typeof(INyxIdAuthorizationCatalogRepairRefreshPort)
            .IsAssignableFrom(typeof(NyxIdAuthorizationCatalogRefreshPort))
            .Should()
            .BeFalse();
    }

    [Fact]
    public void ConcreteCommandPort_ShouldNotImplementRepairCommandPort()
    {
        typeof(INyxIdAuthorizationCatalogRepairCommandPort)
            .IsAssignableFrom(typeof(NyxIdAuthorizationCatalogCommandPort))
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task RefreshPersonalAsync_ShouldNotTreatDispatchAdmissionAsCommittedBegin()
    {
        var commands = new RecordingCommandPort();
        var handler = new RoutingJsonHandler(
            Ok(UserServicesJson()),
            Ok(ScopePlanJson()));
        var observation = new RecordingObservationRuntime();
        using var cancellation = new CancellationTokenSource();

        var refresh = Create(
                commands,
                handler,
                publishCommittedOutcomes: false,
                observation)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret", cancellation.Token);

        commands.Beginnings.Should().ContainSingle();
        refresh.IsCompleted.Should().BeFalse();
        handler.Requests.Should().BeEmpty();

        cancellation.Cancel();
        var act = () => refresh;
        await act.Should().ThrowAsync<OperationCanceledException>();
        observation.Detached.Should().Be(1);
        observation.ProjectionReleases.Should().Be(1);
        observation.PreparationReleases.Should().Be(1);
    }

    [Fact]
    public async Task RefreshPersonalAsync_ShouldNotTreatTerminalDispatchAdmissionAsCompletion()
    {
        var commands = new RecordingCommandPort { PublishTerminalOutcomes = false };
        var handler = new RoutingJsonHandler(
            Ok(UserServicesJson()),
            Ok(ScopePlanJson()));
        using var cancellation = new CancellationTokenSource();

        var refresh = Create(commands, handler)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret", cancellation.Token);

        commands.Observations.Should().ContainSingle();
        refresh.IsCompleted.Should().BeFalse();
        handler.Requests.Should().HaveCount(2);

        cancellation.Cancel();
        var act = () => refresh;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenCommittedBeginIsNotObserved_ShouldReturnObservationTimedOut()
    {
        var commands = new RecordingCommandPort();
        var handler = new RoutingJsonHandler(
            Ok(UserServicesJson()),
            Ok(ScopePlanJson()));
        var observation = new RecordingObservationRuntime();
        var clock = new FakeTimeProvider(Now);

        var refresh = Create(
                commands,
                handler,
                publishCommittedOutcomes: false,
                observation,
                clock)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        clock.Advance(NyxIdAuthorizationCatalogRefreshPort.CatalogObservationTimeout);
        var result = await refresh;

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.ObservationTimedOut);
        result.FailureCode.Should().Be("nyxid_catalog_refresh_observation_timed_out");
        handler.Requests.Should().BeEmpty();
        observation.Detached.Should().Be(1);
        observation.ProjectionReleases.Should().Be(1);
        observation.PreparationReleases.Should().Be(1);
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenCommittedBeginIsSuperseded_ShouldSkipProviderCalls()
    {
        var commands = new RecordingCommandPort
        {
            BeginOutcomeStatus = NyxIdAuthorizationCatalogRefreshOutcomeStatus.Superseded,
        };
        var handler = new RoutingJsonHandler(
            Ok(UserServicesJson()),
            Ok(ScopePlanJson()));

        var result = await Create(commands, handler)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.Superseded);
        result.FailureCode.Should().Be("nyxid_catalog_refresh_superseded");
        handler.Requests.Should().BeEmpty();
        commands.Observations.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshPersonalAsync_ShouldAcquireAgainstLifecycleFenceReadBeforePreparation()
    {
        var commands = new RecordingCommandPort();
        var catalog = new RecordingCatalogQueryPort(lifecycleFence: 7);
        var observation = new RecordingObservationRuntime
        {
            OnPrepare = () => catalog.Owners.Should().ContainSingle(),
        };

        var result = await Create(
                commands,
                new RoutingJsonHandler(Ok(UserServicesJson()), Ok(ScopePlanJson())),
                observation: observation,
                catalogQuery: catalog)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        result.Success.Should().BeTrue();
        catalog.Owners.Should().ContainSingle().Which.Should().BeEquivalentTo(Owner());
        commands.Beginnings.Should().ContainSingle()
            .Which.ExpectedLifecycleFence.Should().Be(7);
    }

    [Fact]
    public async Task RefreshPersonalAsync_ShouldCaptureStartTimeBeforeLifecycleFenceQueryCompletes()
    {
        var commands = new RecordingCommandPort();
        var catalog = new DelayedCatalogQueryPort(lifecycleFence: 7);
        var clock = new FakeTimeProvider(Now);

        var refresh = Create(
                commands,
                new RoutingJsonHandler(Ok(UserServicesJson()), Ok(ScopePlanJson())),
                timeProvider: clock,
                catalogQuery: catalog)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        await catalog.QueryStarted;
        clock.Advance(TimeSpan.FromMinutes(1));
        catalog.Complete();

        var result = await refresh;

        result.Success.Should().BeTrue();
        commands.Beginnings.Should().ContainSingle()
            .Which.At.Should().Be(Now);
    }

    [Fact]
    public async Task RepairRefreshPersonalAsync_ShouldSkipCatalogQueryAndDispatchRepairBegin()
    {
        var commands = new RecordingCommandPort();
        var repairRefresh = CreateRepair(
            commands,
            new RoutingJsonHandler(Ok(UserServicesJson()), Ok(ScopePlanJson())));

        var result = await repairRefresh.RefreshPersonalAsync(
            " owner-alpha ",
            "bearer-secret",
            minimumSourceStateVersion: 3,
            repairRequestId: " repair-alpha ");

        result.Success.Should().BeTrue();
        typeof(NyxIdAuthorizationCatalogRepairRefreshPort)
            .GetConstructors()
            .SelectMany(static constructor => constructor.GetParameters())
            .Should()
            .NotContain(static parameter =>
                parameter.ParameterType == typeof(INyxIdAuthorizationCatalogQueryPort));
        commands.Beginnings.Should().BeEmpty();
        var beginning = commands.RepairBeginnings.Should().ContainSingle().Subject;
        beginning.Owner.Should().BeEquivalentTo(Owner());
        beginning.MinimumSourceStateVersion.Should().Be(3);
        beginning.RepairRequestId.Should().Be("repair-alpha");
        beginning.RefreshId.Should().NotBeNullOrWhiteSpace();
        beginning.At.Should().Be(Now);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RefreshPersonalAsync_WhenProviderCallTimesOut_ShouldRecordCommittedFailure(
        int providerCallIndex)
    {
        var commands = new RecordingCommandPort();
        var observation = new RecordingObservationRuntime();
        QueuedResponse[] responses = providerCallIndex switch
        {
            0 => [ProviderTimeout()],
            1 => [Ok(UserServicesJson()), ProviderTimeout()],
            _ => throw new ArgumentOutOfRangeException(nameof(providerCallIndex)),
        };
        var handler = new RoutingJsonHandler(responses);
        using var callerCancellation = new CancellationTokenSource();

        var result = await Create(commands, handler, observation: observation)
            .RefreshPersonalAsync(
                "owner-alpha",
                "bearer-secret",
                callerCancellation.Token);

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.Failed);
        result.FailureCode.Should().Be("nyxid_catalog_refresh_provider_timed_out");
        callerCancellation.IsCancellationRequested.Should().BeFalse();
        handler.CancellationStates.Should().OnlyContain(static canceled => !canceled);
        handler.Requests.Should().HaveCount(providerCallIndex + 1);
        commands.Failures.Should().ContainSingle();
        commands.Failures[0].RefreshId.Should().Be(commands.Beginnings.Single().RefreshId);
        commands.Failures[0].Code.Should().Be("nyxid_catalog_refresh_provider_timed_out");
        commands.Observations.Should().BeEmpty();
        commands.Invalidations.Should().BeEmpty();
        observation.Detached.Should().Be(1);
        observation.ProjectionReleases.Should().Be(1);
        observation.PreparationReleases.Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RefreshPersonalAsync_WhenCallerCancelsProviderCall_ShouldRethrowCancellation(
        int providerCallIndex)
    {
        var commands = new RecordingCommandPort();
        var observation = new RecordingObservationRuntime();
        var handler = new CallerCancellationHandler(providerCallIndex);
        using var callerCancellation = new CancellationTokenSource();

        var refresh = Create(commands, handler, observation: observation)
            .RefreshPersonalAsync(
                "owner-alpha",
                "bearer-secret",
                callerCancellation.Token);
        await handler.Blocked;
        callerCancellation.Cancel();

        var act = () => refresh;
        await act.Should().ThrowAsync<OperationCanceledException>();
        commands.Failures.Should().BeEmpty();
        commands.Observations.Should().BeEmpty();
        commands.Invalidations.Should().BeEmpty();
        observation.Detached.Should().Be(1);
        observation.ProjectionReleases.Should().Be(1);
        observation.PreparationReleases.Should().Be(1);
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenDisplacedWhileProviderIsBlocked_ShouldCancelProviderAndReturnSuperseded()
    {
        var commands = new RecordingCommandPort();
        var observation = new RecordingObservationRuntime();
        var handler = new SupersessionBlockingHandler();
        var refresh = Create(commands, handler, observation: observation)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");
        await handler.Blocked;
        var refreshId = commands.Beginnings.Should().ContainSingle().Which.RefreshId;

        observation.Publish(
            refreshId,
            NyxIdAuthorizationCatalogRefreshOutcomeStatus.Superseded,
            "nyxid_catalog_refresh_superseded");

        await handler.CancellationObserved;
        var result = await refresh;
        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.Superseded);
        result.FailureCode.Should().Be("nyxid_catalog_refresh_superseded");
        result.StateVersion.Should().Be(1);
        handler.Requests.Should().ContainSingle().Which.Should().Be("/api/v1/user-services");
        commands.Observations.Should().BeEmpty();
        commands.Failures.Should().BeEmpty();
        commands.Invalidations.Should().BeEmpty();
        observation.Detached.Should().Be(1);
        observation.ProjectionReleases.Should().Be(1);
        observation.PreparationReleases.Should().Be(1);
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenLosingProviderFaults_ShouldNotLogProviderDetails()
    {
        var commands = new RecordingCommandPort
        {
            RefreshFailureException = new InvalidOperationException(
                "private-provider-detail bearer-secret"),
        };
        var observation = new RecordingObservationRuntime();
        var handler = new SupersessionFaultingHandler();
        var logger = new RecordingLogger<NyxIdAuthorizationCatalogRefreshPort>();
        var refresh = Create(
                commands,
                handler,
                observation: observation,
                logger: logger)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");
        await handler.Blocked;
        var refreshId = commands.Beginnings.Should().ContainSingle().Which.RefreshId;

        observation.Publish(
            refreshId,
            NyxIdAuthorizationCatalogRefreshOutcomeStatus.Superseded,
            "nyxid_catalog_refresh_superseded");

        var result = await refresh;
        await logger.WarningLogged.WaitAsync(TimeSpan.FromSeconds(1));

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.Superseded);
        logger.Messages.Should().ContainSingle();
        logger.Exceptions.Should().ContainSingle().Which.Should().BeNull();
        string.Join('\n', logger.Messages).Should().NotContain("private-provider-detail");
        string.Join('\n', logger.Messages).Should().NotContain("bearer-secret");
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenSupersededProviderIgnoresCancellation_ShouldReleaseBeforeProviderCompletes()
    {
        var commands = new RecordingCommandPort();
        var observation = new RecordingObservationRuntime();
        var handler = new CancellationIgnoringFaultingHandler();
        var logger = new RecordingLogger<NyxIdAuthorizationCatalogRefreshPort>();
        var refresh = Create(
                commands,
                handler,
                observation: observation,
                logger: logger)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");
        await handler.Blocked;
        var refreshId = commands.Beginnings.Should().ContainSingle().Which.RefreshId;

        observation.Publish(
            refreshId,
            NyxIdAuthorizationCatalogRefreshOutcomeStatus.Superseded,
            "nyxid_catalog_refresh_superseded");
        await handler.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(1));

        try
        {
            var result = await refresh.WaitAsync(TimeSpan.FromSeconds(1));

            result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.Superseded);
            handler.ProviderCompleted.Should().BeFalse();
            observation.Detached.Should().Be(1);
            observation.ProjectionReleases.Should().Be(1);
            observation.PreparationReleases.Should().Be(1);
        }
        finally
        {
            handler.CompleteWithFault();
            await logger.WarningLogged.WaitAsync(TimeSpan.FromSeconds(1));
            await IgnoreFailureAsync(refresh);
        }

        handler.TokenLifetimeFailure.Should().BeNull();
        logger.Exceptions.Should().OnlyContain(static exception => exception == null);
        string.Join('\n', logger.Messages).Should().NotContain("private-provider-detail");
        string.Join('\n', logger.Messages).Should().NotContain("bearer-secret");
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenCallerCancelsProviderThatIgnoresCancellation_ShouldReleaseBeforeProviderCompletes()
    {
        var commands = new RecordingCommandPort();
        var observation = new RecordingObservationRuntime();
        var handler = new CancellationIgnoringFaultingHandler();
        var logger = new RecordingLogger<NyxIdAuthorizationCatalogRefreshPort>();
        using var cancellation = new CancellationTokenSource();
        var refresh = Create(
                commands,
                handler,
                observation: observation,
                logger: logger)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret", cancellation.Token);
        await handler.Blocked;

        cancellation.Cancel();
        await handler.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(1));

        try
        {
            var act = async () => await refresh.WaitAsync(TimeSpan.FromSeconds(1));

            await act.Should().ThrowAsync<OperationCanceledException>();
            handler.ProviderCompleted.Should().BeFalse();
            observation.Detached.Should().Be(1);
            observation.ProjectionReleases.Should().Be(1);
            observation.PreparationReleases.Should().Be(1);
        }
        finally
        {
            handler.CompleteWithFault();
            await logger.WarningLogged.WaitAsync(TimeSpan.FromSeconds(1));
            await IgnoreFailureAsync(refresh);
        }

        handler.TokenLifetimeFailure.Should().BeNull();
        logger.Exceptions.Should().OnlyContain(static exception => exception == null);
        string.Join('\n', logger.Messages).Should().NotContain("private-provider-detail");
        string.Join('\n', logger.Messages).Should().NotContain("bearer-secret");
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenSupersessionCancellationCallbackThrows_ShouldPreserveResultAndRelease()
    {
        var commands = new RecordingCommandPort();
        var observation = new RecordingObservationRuntime();
        var handler = new CancellationIgnoringThrowingCallbackHandler();
        var refresh = Create(commands, handler, observation: observation)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");
        await handler.Blocked;
        var refreshId = commands.Beginnings.Should().ContainSingle().Which.RefreshId;

        observation.Publish(
            refreshId,
            NyxIdAuthorizationCatalogRefreshOutcomeStatus.Superseded,
            "nyxid_catalog_refresh_superseded");
        await handler.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(1));

        try
        {
            var result = await refresh.WaitAsync(TimeSpan.FromSeconds(1));

            result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.Superseded);
            handler.ProbeTokenLifetime();
            handler.TokenLifetimeFailure.Should().BeNull();
            observation.Detached.Should().Be(1);
            observation.ProjectionReleases.Should().Be(1);
            observation.PreparationReleases.Should().Be(1);
        }
        finally
        {
            handler.CompleteCanceled();
            await IgnoreFailureAsync(refresh);
        }
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenCallerCancellationCallbackThrows_ShouldPreserveCancellationAndRelease()
    {
        var commands = new RecordingCommandPort();
        var observation = new RecordingObservationRuntime();
        var handler = new CancellationIgnoringThrowingCallbackHandler();
        using var cancellation = new CancellationTokenSource();
        var refresh = Create(commands, handler, observation: observation)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret", cancellation.Token);
        await handler.Blocked;

        try
        {
            var cancel = () => cancellation.Cancel();
            cancel.Should().NotThrow();
            await handler.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(1));
            var act = async () => await refresh.WaitAsync(TimeSpan.FromSeconds(1));

            await act.Should().ThrowAsync<OperationCanceledException>();
            handler.ProbeTokenLifetime();
            handler.TokenLifetimeFailure.Should().BeNull();
            observation.Detached.Should().Be(1);
            observation.ProjectionReleases.Should().Be(1);
            observation.PreparationReleases.Should().Be(1);
        }
        finally
        {
            handler.CompleteCanceled();
            await IgnoreFailureAsync(refresh);
        }
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenProviderCompletesInsideCancellationCallback_ShouldKeepTokenAliveUntilCallbackReturns()
    {
        var commands = new RecordingCommandPort();
        var observation = new RecordingObservationRuntime();
        var client = new CancellationCompletingHttpClient();
        var port = CreateWithClient(commands, client, observation: observation);
        var refresh = Task.Run(() => port.RefreshPersonalAsync("owner-alpha", "bearer-secret"));
        await client.Blocked;
        var refreshId = commands.Beginnings.Should().ContainSingle().Which.RefreshId;

        observation.Publish(
            refreshId,
            NyxIdAuthorizationCatalogRefreshOutcomeStatus.Superseded,
            "nyxid_catalog_refresh_superseded");
        await client.CancellationCallbackCompleted.WaitAsync(TimeSpan.FromSeconds(1));
        var result = await refresh.WaitAsync(TimeSpan.FromSeconds(1));

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.Superseded);
        client.TokenLifetimeFailure.Should().BeNull();
        observation.Detached.Should().Be(1);
        observation.ProjectionReleases.Should().Be(1);
        observation.PreparationReleases.Should().Be(1);
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenProviderTimeoutOccursAfterObservationInterval_ShouldCommitFailure()
    {
        var commands = new RecordingCommandPort();
        var observation = new RecordingObservationRuntime();
        var handler = new DelayedProviderTimeoutHandler();
        var clock = new FakeTimeProvider(Now);
        var refresh = Create(
                commands,
                handler,
                observation: observation,
                timeProvider: clock)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");
        await handler.Blocked;

        clock.Advance(NyxIdAuthorizationCatalogRefreshPort.CatalogObservationTimeout.Add(TimeSpan.FromSeconds(1)));
        await Task.Yield();
        await Task.Yield();
        handler.CompleteWithProviderTimeout();
        var result = await refresh;

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.Failed);
        result.FailureCode.Should().Be("nyxid_catalog_refresh_provider_timed_out");
        commands.Failures.Should().ContainSingle().Which.Code.Should()
            .Be("nyxid_catalog_refresh_provider_timed_out");
        commands.Observations.Should().BeEmpty();
        observation.Detached.Should().Be(1);
        observation.ProjectionReleases.Should().Be(1);
        observation.PreparationReleases.Should().Be(1);
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenCallerCancelsAndCleanupFails_ShouldPreserveCancellationAndReleaseAll()
    {
        var commands = new RecordingCommandPort();
        var observation = new RecordingObservationRuntime
        {
            DetachFailure = new InvalidOperationException("detach-private-detail"),
            ProjectionReleaseFailure = new InvalidOperationException("projection-release-private-detail"),
            PreparationReleaseFailure = new InvalidOperationException("preparation-release-private-detail"),
        };
        var handler = new CallerCancellationHandler(0);
        using var cancellation = new CancellationTokenSource();
        var refresh = Create(commands, handler, observation: observation)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret", cancellation.Token);
        await handler.Blocked;

        cancellation.Cancel();
        var act = () => refresh;

        await act.Should().ThrowAsync<OperationCanceledException>();
        observation.Detached.Should().Be(1);
        observation.ProjectionReleases.Should().Be(1);
        observation.PreparationReleases.Should().Be(1);
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenProviderAndCleanupFail_ShouldPreserveProviderFailureAndReleaseAll()
    {
        var commands = new RecordingCommandPort
        {
            ObservationException = new InvalidOperationException("provider-original-failure"),
        };
        var observation = new RecordingObservationRuntime
        {
            DetachFailure = new InvalidOperationException("detach-private-detail"),
            ProjectionReleaseFailure = new InvalidOperationException("projection-release-private-detail"),
            PreparationReleaseFailure = new InvalidOperationException("preparation-release-private-detail"),
        };
        var handler = new RoutingJsonHandler(
            Ok(UserServicesJson()),
            Ok(ScopePlanJson()));

        var act = () => Create(commands, handler, observation: observation)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("provider-original-failure");
        observation.Detached.Should().Be(1);
        observation.ProjectionReleases.Should().Be(1);
        observation.PreparationReleases.Should().Be(1);
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenProviderFailureLoggingThrows_ShouldPreserveProviderFailureAndReleaseAll()
    {
        var commands = new RecordingCommandPort
        {
            ObservationException = new InvalidOperationException("provider-original-failure"),
        };
        var observation = new RecordingObservationRuntime();
        var logger = new ThrowingLogger<NyxIdAuthorizationCatalogRefreshPort>();

        var act = () => Create(
                commands,
                new RoutingJsonHandler(Ok(UserServicesJson()), Ok(ScopePlanJson())),
                observation: observation,
                logger: logger)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("provider-original-failure");
        observation.Detached.Should().Be(1);
        observation.ProjectionReleases.Should().Be(1);
        observation.PreparationReleases.Should().Be(1);
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenCommittedOutcomeLoggingThrows_ShouldPreserveAccessDeniedResult()
    {
        var commands = new RecordingCommandPort();
        var observation = new RecordingObservationRuntime();
        var logger = new ThrowingLogger<NyxIdAuthorizationCatalogRefreshPort>();

        var result = await Create(
                commands,
                new RoutingJsonHandler(
                    Ok(UserServicesJson()),
                    Error(HttpStatusCode.Forbidden, "api_key_scope_plan_denied", 9004)),
                observation: observation,
                logger: logger)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.AccessDenied);
        result.FailureCode.Should().Be("api_key_scope_plan_denied");
        observation.Detached.Should().Be(1);
        observation.ProjectionReleases.Should().Be(1);
        observation.PreparationReleases.Should().Be(1);
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenDetachFails_ShouldStillReleaseBothScopes()
    {
        var commands = new RecordingCommandPort();
        var observation = new RecordingObservationRuntime
        {
            DetachFailure = new InvalidOperationException("detach-failure"),
        };

        var act = () => Create(
                commands,
                new RoutingJsonHandler(Ok(UserServicesJson()), Ok(ScopePlanJson())),
                observation: observation)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("detach-failure");
        observation.Detached.Should().Be(1);
        observation.ProjectionReleases.Should().Be(1);
        observation.PreparationReleases.Should().Be(1);
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenCleanupLoggingThrows_ShouldReleaseAllAndPreserveCleanupFailure()
    {
        var commands = new RecordingCommandPort();
        var observation = new RecordingObservationRuntime
        {
            DetachFailure = new InvalidOperationException("detach-failure"),
        };
        var logger = new ThrowingLogger<NyxIdAuthorizationCatalogRefreshPort>();

        var act = () => Create(
                commands,
                new RoutingJsonHandler(Ok(UserServicesJson()), Ok(ScopePlanJson())),
                observation: observation,
                logger: logger)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("detach-failure");
        observation.Detached.Should().Be(1);
        observation.ProjectionReleases.Should().Be(1);
        observation.PreparationReleases.Should().Be(1);
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenProjectionReleaseFails_ShouldStillReleasePreparation()
    {
        var commands = new RecordingCommandPort();
        var observation = new RecordingObservationRuntime
        {
            ProjectionReleaseFailure = new InvalidOperationException("projection-release-failure"),
        };

        var act = () => Create(
                commands,
                new RoutingJsonHandler(Ok(UserServicesJson()), Ok(ScopePlanJson())),
                observation: observation)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("projection-release-failure");
        observation.Detached.Should().Be(1);
        observation.ProjectionReleases.Should().Be(1);
        observation.PreparationReleases.Should().Be(1);
    }

    [Fact]
    public async Task RefreshPersonalAsync_ShouldObservePublishedScopePlanForActiveAllowedServices()
    {
        var commands = new RecordingCommandPort();
        var handler = new RoutingJsonHandler(
            Ok(UserServicesJson()),
            Ok(ScopePlanJson()));

        var result = await Create(commands, handler)
            .RefreshPersonalAsync(" owner-alpha ", "bearer-secret");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.Observed);
        result.FailureCode.Should().BeEmpty();
        result.StateVersion.Should().Be(1);
        handler.Requests.Select(static request => (request.Method, request.Path))
            .Should().Equal(
                (HttpMethod.Get, "/api/v1/user-services"),
                (HttpMethod.Post, "/api/v1/api-keys/scope-plan"));
        handler.AuthorizationHeaders.Should().OnlyContain(static value => value == "Bearer bearer-secret");
        using (var request = JsonDocument.Parse(handler.RequestBodies.Single()))
        {
            request.RootElement.GetProperty("selected_service_ids")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .Should().Equal("service-a", "service-b");
            request.RootElement.TryGetProperty("target_org_id", out _).Should().BeFalse();
        }

        commands.Beginnings.Should().ContainSingle();
        commands.Beginnings[0].Owner.Should().BeEquivalentTo(Owner());
        commands.Beginnings[0].At.Should().Be(Now);
        commands.Beginnings[0].RefreshId.Should().NotBeNullOrWhiteSpace();
        commands.Beginnings[0].ExpectedLifecycleFence.Should().Be(0);
        var observation = commands.Observations.Should().ContainSingle().Subject;
        observation.Owner.Should().BeEquivalentTo(Owner());
        observation.RefreshId.Should().Be(commands.Beginnings[0].RefreshId);
        observation.ObservedAtUtc.Should().Be(Now);
        observation.FreshUntilUtc.Should().Be(Now.AddMinutes(15));
        observation.ContractVersion.Should().Be("1");
        observation.PolicyVersion.Should().Be("api-key-scope-v1");
        observation.EvaluatedAtUtc.Should().Be(EvaluatedAt);
        observation.ContentDigest.Should().Be(
            NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(observation.Owner, observation.Services));
        observation.Services.Select(static service => service.UserServiceId)
            .Should().Equal("service-a", "service-b");

        var personal = observation.Services[0];
        personal.ServiceSlug.Should().Be("api-alpha");
        personal.DisplayName.Should().Be("Alpha");
        personal.Access.Should().Be(NyxIdAuthorizationAccess.Permitted);
        personal.ResourceOwner.Should().BeEquivalentTo(Owner());
        personal.NodeGrantRequirement.Should().Be(AuthorizationGrantRequirement.NotRequired);
        personal.NodeIds.Should().BeEmpty();

        var organization = observation.Services[1];
        organization.ServiceSlug.Should().Be("api-beta");
        organization.DisplayName.Should().Be("Beta Catalog");
        organization.ResourceOwner.Should().BeEquivalentTo(new AuthorizationOwnerIdentity
        {
            Authority = NyxIdAuthorizationAuthorities.NyxId,
            OwnerKind = AuthorizationOwnerKind.Organization,
            OwnerSubject = "org-alpha",
        });
        organization.NodeGrantRequirement.Should().Be(AuthorizationGrantRequirement.Required);
        organization.NodeIds.Should().Equal("node-a", "node-b");
        commands.Invalidations.Should().BeEmpty();
        commands.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_ForExactUserService_ShouldFetchOnlyVerifiedInventoryRoute()
    {
        var commands = new RecordingCommandPort();
        var handler = new RoutingJsonHandler(
            Ok(UserServicesJson()),
            Ok(ScopePlanJsonForServiceA()),
            Ok(ModelsJson("gpt-5.5", "GPT-5.5")));

        var result = await Create(commands, handler)
            .RefreshAsync(Owner(), "bearer-secret", UserServiceRefreshRequest());

        result.Success.Should().BeTrue();
        handler.Requests.Select(static request => (request.Method, request.Path))
            .Should().Equal(
                (HttpMethod.Get, "/api/v1/user-services"),
                (HttpMethod.Post, "/api/v1/api-keys/scope-plan"),
                (HttpMethod.Get, "/api/v1/proxy/s/api-alpha/models?_nyxid_via=service-a"));
        var target = commands.Observations.Should().ContainSingle().Subject.Services
            .Should().ContainSingle().Subject.LlmTarget;
        target.RouteKind.Should().Be(LLMRouteKind.NyxIdUserService);
        target.RouteValue.Should().Be("/api/v1/proxy/s/api-alpha");
        target.NyxIdUserServiceId.Should().Be("service-a");
        target.ServiceSlugSnapshot.Should().Be("api-alpha");
        target.ModelCatalog.Certainty.Should().Be(LLMModelCatalogCertainty.Enumerated);
        target.ModelCatalog.ModelIds.Should().Equal("GPT-5.5", "gpt-5.5");
        handler.AuthorizationHeaders.Should().OnlyContain(static value => value == "Bearer bearer-secret");
        string.Join('\n', commands.Observations).Should().NotContain("bearer-secret");
    }

    [Fact]
    public async Task RefreshAsync_ForGateway_ShouldIgnoreCallerRouteAndUseConfiguredAuthority()
    {
        var commands = new RecordingCommandPort();
        var handler = new RoutingJsonHandler(Ok(ModelsJson("gpt-5.5")));
        var request = new NyxIdAuthorizationCatalogRefreshRequest(
            [],
            new ScheduledInvocationLLMRefreshRequirement(
                LLMRouteKind.Gateway,
                "https://evil.example/models",
                string.Empty,
                string.Empty,
                "gpt-5.5",
                17));

        var result = await Create(commands, handler)
            .RefreshAsync(Owner(), "bearer-secret", request);

        result.Success.Should().BeTrue();
        handler.Requests.Should().Equal(
            (HttpMethod.Get, "/api/v1/llm/gateway/v1/models"));
        var observation = commands.Observations.Should().ContainSingle().Subject;
        observation.Services.Should().BeEmpty();
        observation.GatewayLLMTarget.Should().NotBeNull();
        observation.GatewayLLMTarget!.RouteValue.Should().Be("/api/v1/llm/gateway/v1");
        observation.GatewayLLMTarget.ModelCatalog.ModelIds.Should().Equal("gpt-5.5");
    }

    [Fact]
    public async Task RefreshAsync_WhenUserServiceSlugDoesNotMatchInventory_ShouldFailBeforeModelFetch()
    {
        var commands = new RecordingCommandPort();
        var handler = new RoutingJsonHandler(Ok(UserServicesJson()));
        var request = UserServiceRefreshRequest(serviceSlug: "other-slug");

        var result = await Create(commands, handler)
            .RefreshAsync(Owner(), "bearer-secret", request);

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.CatalogUnstable);
        result.FailureCode.Should().Be("nyxid_llm_target_inventory_mismatch");
        handler.Requests.Should().Equal((HttpMethod.Get, "/api/v1/user-services"));
        commands.Observations.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_WhenModelsResponseExceedsBound_ShouldCommitOnlyResponseTooLargeEvidence()
    {
        var commands = new RecordingCommandPort();
        var sensitiveBody = JsonSerializer.Serialize(new
        {
            data = new[] { new { id = "gpt-5.5" } },
            secret = new string('x', 1024 * 1024),
        });
        var handler = new RoutingJsonHandler(Ok(sensitiveBody));

        var result = await Create(commands, handler)
            .RefreshAsync(Owner(), "bearer-secret", GatewayRefreshRequest());

        result.Success.Should().BeTrue();
        var catalog = commands.Observations.Single().GatewayLLMTarget!.ModelCatalog;
        catalog.Certainty.Should().Be(LLMModelCatalogCertainty.NotVerifiable);
        catalog.DiagnosticKind.Should().Be(LLMModelCatalogDiagnosticKind.ResponseTooLarge);
        catalog.ModelIds.Should().BeEmpty();
        string.Join('\n', commands.Observations).Should().NotContain(new string('x', 128));
    }

    [Theory]
    [MemberData(nameof(InvalidModelsResponses))]
    public async Task RefreshAsync_WhenModelsAreNotAuthoritative_ShouldCommitNotVerifiableEvidence(
        string response,
        LLMModelCatalogDiagnosticKind expectedDiagnostic)
    {
        var commands = new RecordingCommandPort();
        var handler = new RoutingJsonHandler(Ok(response));

        var result = await Create(commands, handler)
            .RefreshAsync(Owner(), "bearer-secret", GatewayRefreshRequest());

        result.Success.Should().BeTrue();
        var catalog = commands.Observations.Single().GatewayLLMTarget!.ModelCatalog;
        catalog.Certainty.Should().Be(LLMModelCatalogCertainty.NotVerifiable);
        catalog.DiagnosticKind.Should().Be(expectedDiagnostic);
        catalog.ModelIds.Should().BeEmpty();
    }

    public static TheoryData<string, LLMModelCatalogDiagnosticKind> InvalidModelsResponses => new()
    {
        {
            JsonSerializer.Serialize(new
            {
                data = Enumerable.Range(0, 2_049).Select(index => new { id = $"model-{index}" }),
            }),
            LLMModelCatalogDiagnosticKind.ResponseTooLarge
        },
        { ModelsJson("model-*"), LLMModelCatalogDiagnosticKind.PatternOnly },
        { ModelsJson(" model-a"), LLMModelCatalogDiagnosticKind.ResponseInvalid },
        { "{\"models\":[]}", LLMModelCatalogDiagnosticKind.ResponseInvalid },
    };

    [Fact]
    public async Task RefreshAsync_WhenGatewayAccessIsDenied_ShouldCommitUnavailableEvidenceWithoutBody()
    {
        var commands = new RecordingCommandPort();
        var handler = new RoutingJsonHandler(
            Error(HttpStatusCode.Forbidden, "sensitive-upstream-denial", 9004));

        var result = await Create(commands, handler)
            .RefreshAsync(Owner(), "bearer-secret", GatewayRefreshRequest());

        result.Success.Should().BeTrue();
        var catalog = commands.Observations.Single().GatewayLLMTarget!.ModelCatalog;
        catalog.Certainty.Should().Be(LLMModelCatalogCertainty.Unavailable);
        catalog.DiagnosticKind.Should().Be(LLMModelCatalogDiagnosticKind.AccessDenied);
        string.Join('\n', commands.Observations).Should().NotContain("sensitive-upstream-denial");
    }

    [Fact]
    public async Task RefreshAsync_WhenGatewayModelReadTimesOut_ShouldPreserveCommittedTarget()
    {
        var existingTarget = new NyxIdAuthorizationLLMTargetEvidence
        {
            RouteKind = LLMRouteKind.Gateway,
            RouteValue = "/api/v1/llm/gateway/v1",
        };
        var commands = new RecordingCommandPort();
        var handler = new RoutingJsonHandler(ProviderTimeout());
        var catalogQuery = new RecordingCatalogQueryPort(
            snapshot: CatalogWithGateway(existingTarget));

        var result = await Create(commands, handler, catalogQuery: catalogQuery)
            .RefreshAsync(Owner(), "bearer-secret", GatewayRefreshRequest());

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.Failed);
        result.FailureCode.Should().Be("nyxid_catalog_refresh_provider_timed_out");
        commands.Failures.Should().ContainSingle();
        commands.Observations.Should().BeEmpty();
        catalogQuery.Snapshot!.GatewayLLMTarget.Should().BeSameAs(existingTarget);
    }

    [Fact]
    public async Task RefreshAsync_WhenRequiredServicesAreProvided_ShouldRequestOnlyRequiredScopePlanServices()
    {
        var commands = new RecordingCommandPort();
        var handler = new RoutingJsonHandler(
            Ok(UserServicesJson()),
            Ok(ScopePlanJsonForServiceA()));

        var result = await Create(commands, handler)
            .RefreshAsync(
                Owner(),
                "bearer-secret",
                new NyxIdAuthorizationCatalogRefreshRequest(
                    [new NyxIdUserServiceCapabilityRef { UserServiceId = "service-a" }],
                    LLMTarget: null));

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.Observed);
        result.FailureCode.Should().BeEmpty();
        handler.Requests.Select(static request => (request.Method, request.Path))
            .Should().Equal(
                (HttpMethod.Get, "/api/v1/user-services"),
                (HttpMethod.Post, "/api/v1/api-keys/scope-plan"));
        handler.RequestBodies.Should().ContainSingle();
        using (var request = JsonDocument.Parse(handler.RequestBodies.Single()))
        {
            request.RootElement.GetProperty("selected_service_ids")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .Should().Equal("service-a");
        }

        var observation = commands.Observations.Should().ContainSingle().Subject;
        observation.Coverage.Should().Be(NyxIdAuthorizationCatalogObservationCoverage.RequiredServiceSubset);
        observation.CoveredUserServiceIds.Should().Equal("service-a");
        observation.ContentDigest.Should().BeEmpty();
        observation.Services.Select(static service => service.UserServiceId)
            .Should().Equal("service-a");
        observation.Services.Single().Access.Should().Be(NyxIdAuthorizationAccess.Permitted);
        observation.ContractVersion.Should().Be("1");
        observation.PolicyVersion.Should().Be("api-key-scope-v1");
        observation.EvaluatedAtUtc.Should().Be(EvaluatedAt);
        observation.ObservedAtUtc.Should().Be(Now);
        observation.FreshUntilUtc.Should().Be(Now.AddMinutes(15));
        commands.Invalidations.Should().BeEmpty();
        commands.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_WhenRequiredServiceIsMissing_ShouldFailClosedWithoutObservation()
    {
        var commands = new RecordingCommandPort();
        var handler = new RoutingJsonHandler(Ok(UserServicesJson()));

        var result = await Create(commands, handler)
            .RefreshAsync(
                Owner(),
                "bearer-secret",
                new NyxIdAuthorizationCatalogRefreshRequest(
                    [new NyxIdUserServiceCapabilityRef { UserServiceId = "service-missing" }],
                    LLMTarget: null));

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.CatalogUnstable);
        result.FailureCode.Should().Be("nyxid_required_service_not_found:service-missing");
        handler.Requests.Select(static request => (request.Method, request.Path))
            .Should().Equal((HttpMethod.Get, "/api/v1/user-services"));
        commands.Invalidations.Should().BeEmpty();
        commands.Observations.Should().BeEmpty();
        commands.Failures.Should().ContainSingle().Which.Should().Match<(
            AuthorizationOwnerIdentity Owner,
            string RefreshId,
            DateTimeOffset FailedAt,
            string FailureCode,
            NyxIdAuthorizationCatalogRefreshStatus Status)>(failure =>
            failure.FailureCode == "nyxid_required_service_not_found:service-missing" &&
            failure.Status == NyxIdAuthorizationCatalogRefreshStatus.CatalogUnstable);
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenNoServicesAreEligible_ShouldObserveEmptyCatalog()
    {
        var commands = new RecordingCommandPort();
        var handler = new RoutingJsonHandler(Ok("""
            {"services":[]}
            """));

        var result = await Create(commands, handler)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.Observed);
        result.FailureCode.Should().BeEmpty();
        handler.Requests.Select(static request => (request.Method, request.Path))
            .Should().Equal((HttpMethod.Get, "/api/v1/user-services"));
        var observation = commands.Observations.Should().ContainSingle().Subject;
        observation.Owner.Should().BeEquivalentTo(Owner());
        observation.ObservedAtUtc.Should().Be(Now);
        observation.FreshUntilUtc.Should().Be(Now.AddMinutes(15));
        observation.ContractVersion.Should().Be("1");
        observation.PolicyVersion.Should().Be("api-key-scope-v1");
        observation.EvaluatedAtUtc.Should().Be(Now);
        observation.ContentDigest.Should().Be(
            NyxIdAuthorizationCatalogIntegrity.ComputeContentDigest(observation.Owner, observation.Services));
        observation.Services.Should().BeEmpty();
        commands.Invalidations.Should().BeEmpty();
        commands.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenPersonalGrantClaimsAnotherOwner_ShouldInvalidateAsUnstable()
    {
        var commands = new RecordingCommandPort();
        var handler = new RoutingJsonHandler(
            Ok(UserServicesJson()),
            Ok(ScopePlanJson(personalResourceOwnerId: "owner-beta")));

        var result = await Create(commands, handler)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.CatalogUnstable);
        result.FailureCode.Should().Be("nyxid_scope_plan_catalog_mismatch");
        commands.Invalidations.Should().ContainSingle().Which.Should().Match<(
            AuthorizationOwnerIdentity Owner,
            string RefreshId,
            DateTimeOffset At,
            string Reason,
            NyxIdAuthorizationCatalogRefreshOutcomeStatus OutcomeStatus)>(invalidation =>
            invalidation.Reason == "nyxid_scope_plan_catalog_mismatch" &&
            invalidation.OutcomeStatus == NyxIdAuthorizationCatalogRefreshOutcomeStatus.CatalogUnstable);
        commands.Observations.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenOrganizationGrantClaimsAnotherOrganization_ShouldInvalidateAsUnstable()
    {
        var commands = new RecordingCommandPort();
        var handler = new RoutingJsonHandler(
            Ok(UserServicesJson()),
            Ok(ScopePlanJson(organizationResourceOwnerId: "org-beta")));

        var result = await Create(commands, handler)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.CatalogUnstable);
        result.FailureCode.Should().Be("nyxid_scope_plan_catalog_mismatch");
        commands.Invalidations.Should().ContainSingle().Which.Should().Match<(
            AuthorizationOwnerIdentity Owner,
            string RefreshId,
            DateTimeOffset At,
            string Reason,
            NyxIdAuthorizationCatalogRefreshOutcomeStatus OutcomeStatus)>(invalidation =>
            invalidation.Reason == "nyxid_scope_plan_catalog_mismatch" &&
            invalidation.OutcomeStatus == NyxIdAuthorizationCatalogRefreshOutcomeStatus.CatalogUnstable);
        commands.Observations.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenScopePlanIsForbidden_ShouldInvalidateCatalog()
    {
        var commands = new RecordingCommandPort();
        var handler = new RoutingJsonHandler(
            Ok(UserServicesJson()),
            Error(HttpStatusCode.Forbidden, "api_key_scope_plan_denied", 9004));

        var result = await Create(commands, handler)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.AccessDenied);
        result.FailureCode.Should().Be("api_key_scope_plan_denied");
        handler.Requests.Select(static request => (request.Method, request.Path))
            .Should().Equal(
                (HttpMethod.Get, "/api/v1/user-services"),
                (HttpMethod.Post, "/api/v1/api-keys/scope-plan"));
        handler.RequestBodies.Should().ContainSingle();
        commands.Beginnings.Should().ContainSingle();
        commands.Invalidations.Should().ContainSingle();
        commands.Invalidations[0].RefreshId.Should().Be(commands.Beginnings[0].RefreshId);
        commands.Invalidations[0].Reason.Should().Be("api_key_scope_plan_denied");
        commands.Invalidations[0].OutcomeStatus.Should()
            .Be(NyxIdAuthorizationCatalogRefreshOutcomeStatus.AccessDenied);
        commands.Observations.Should().BeEmpty();
        commands.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenScopePlanProviderIsUnavailable_ShouldRecordRefreshFailure()
    {
        var commands = new RecordingCommandPort();
        var handler = new RoutingJsonHandler(
            Ok(UserServicesJson()),
            Error(HttpStatusCode.ServiceUnavailable, "internal_error", 1006));

        var result = await Create(commands, handler)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.Failed);
        result.FailureCode.Should().Be("internal_error");
        commands.Failures.Should().ContainSingle();
        commands.Failures[0].RefreshId.Should().Be(commands.Beginnings[0].RefreshId);
        commands.Failures[0].Code.Should().Be("internal_error");
        commands.Invalidations.Should().BeEmpty();
        commands.Observations.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshPersonalAsync_WhenScopePlanResponseIsMalformed_ShouldInvalidateAsUnstable()
    {
        var commands = new RecordingCommandPort();
        var handler = new RoutingJsonHandler(
            Ok(UserServicesJson()),
            Ok("{}"));

        var result = await Create(commands, handler)
            .RefreshPersonalAsync("owner-alpha", "bearer-secret");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.CatalogUnstable);
        result.FailureCode.Should().Be("nyxid_scope_plan_response_malformed");
        commands.Invalidations.Should().ContainSingle();
        commands.Invalidations[0].RefreshId.Should().Be(commands.Beginnings[0].RefreshId);
        commands.Invalidations[0].Reason.Should().Be("nyxid_scope_plan_response_malformed");
        commands.Invalidations[0].OutcomeStatus.Should()
            .Be(NyxIdAuthorizationCatalogRefreshOutcomeStatus.CatalogUnstable);
        commands.Observations.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_WhenOrganizationOwnerIsNotActorScoped_ShouldFailWithoutLifecycleMutation()
    {
        var commands = new RecordingCommandPort();
        var owner = Owner();
        owner.OwnerKind = AuthorizationOwnerKind.Organization;
        owner.OwnerSubject = "org-alpha";

        var result = await Create(commands, new RoutingJsonHandler(Ok("{}")))
            .RefreshAsync(owner, "bearer-secret");

        result.Status.Should().Be(NyxIdAuthorizationCatalogRefreshStatus.OwnerNotSupported);
        result.FailureCode.Should().Be("nyxid_catalog_organization_owner_not_supported");
        commands.AllCalls.Should().Be(0);
    }

    [Fact]
    public async Task RefreshAsync_WhenAuthorityIsNotNyxId_ShouldFailWithoutLifecycleMutation()
    {
        var commands = new RecordingCommandPort();
        var owner = Owner();
        owner.Authority = "other-authority";

        var act = () => Create(commands, new RoutingJsonHandler(Ok("{}")))
            .RefreshAsync(owner, "bearer-secret");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*owner authority is not supported*");
        commands.AllCalls.Should().Be(0);
    }

    private static NyxIdAuthorizationCatalogRefreshPort Create(
        RecordingCommandPort commands,
        HttpMessageHandler handler,
        bool publishCommittedOutcomes = true,
        RecordingObservationRuntime? observation = null,
        TimeProvider? timeProvider = null,
        INyxIdAuthorizationCatalogQueryPort? catalogQuery = null,
        ILogger<NyxIdAuthorizationCatalogRefreshPort>? logger = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example") };
        return CreateWithClient(
            commands,
            httpClient,
            publishCommittedOutcomes,
            observation,
            timeProvider,
            catalogQuery,
            logger);
    }

    private static NyxIdAuthorizationCatalogRefreshPort CreateWithClient(
        RecordingCommandPort commands,
        HttpClient httpClient,
        bool publishCommittedOutcomes = true,
        RecordingObservationRuntime? observation = null,
        TimeProvider? timeProvider = null,
        INyxIdAuthorizationCatalogQueryPort? catalogQuery = null,
        ILogger<NyxIdAuthorizationCatalogRefreshPort>? logger = null)
    {
        observation ??= new RecordingObservationRuntime();
        commands.Observation = publishCommittedOutcomes ? observation : null;
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            httpClient);
        return new NyxIdAuthorizationCatalogRefreshPort(
            commands,
            catalogQuery ?? new RecordingCatalogQueryPort(),
            new TestNyxIdApiClientFactory(client),
            observation,
            observation,
            timeProvider ?? new FakeTimeProvider(Now),
            logger ?? NullLogger<NyxIdAuthorizationCatalogRefreshPort>.Instance);
    }

    private static NyxIdAuthorizationCatalogRepairRefreshPort CreateRepair(
        RecordingCommandPort commands,
        HttpMessageHandler handler,
        bool publishCommittedOutcomes = true,
        RecordingObservationRuntime? observation = null,
        TimeProvider? timeProvider = null,
        ILogger<NyxIdAuthorizationCatalogRefreshPort>? logger = null)
    {
        observation ??= new RecordingObservationRuntime();
        commands.Observation = publishCommittedOutcomes ? observation : null;
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://nyx.example") };
        var client = new NyxIdApiClient(
            new NyxIdToolOptions { BaseUrl = "https://nyx.example" },
            httpClient);
        return new NyxIdAuthorizationCatalogRepairRefreshPort(
            commands,
            commands,
            new TestNyxIdApiClientFactory(client),
            observation,
            observation,
            timeProvider ?? new FakeTimeProvider(Now),
            logger ?? NullLogger<NyxIdAuthorizationCatalogRefreshPort>.Instance);
    }

    private static AuthorizationOwnerIdentity Owner() => new()
    {
        Authority = NyxIdAuthorizationAuthorities.NyxId,
        OwnerKind = AuthorizationOwnerKind.Personal,
        OwnerSubject = "owner-alpha",
    };

    private static NyxIdAuthorizationCatalogRefreshRequest GatewayRefreshRequest() => new(
        [],
        new ScheduledInvocationLLMRefreshRequirement(
            LLMRouteKind.Gateway,
            "/api/v1/llm/gateway/v1",
            string.Empty,
            string.Empty,
            "gpt-5.5",
            17));

    private static NyxIdAuthorizationCatalogRefreshRequest UserServiceRefreshRequest(
        string serviceSlug = "api-alpha") => new(
        [new NyxIdUserServiceCapabilityRef
        {
            UserServiceId = "service-a",
            ServiceSlugSnapshot = serviceSlug,
        }],
        new ScheduledInvocationLLMRefreshRequirement(
            LLMRouteKind.NyxIdUserService,
            $"/api/v1/proxy/s/{serviceSlug}",
            "service-a",
            serviceSlug,
            "gpt-5.5",
            17));

    private static string ModelsJson(params string[] modelIds) =>
        JsonSerializer.Serialize(new
        {
            @object = "list",
            data = modelIds.Select(static modelId => new { id = modelId, @object = "model" }),
        });

    private static NyxIdAuthorizationCatalogSnapshot CatalogWithGateway(
        NyxIdAuthorizationLLMTargetEvidence target) => new(
        Owner(),
        StateVersion: 19,
        ObservedAtUtc: Now.AddMinutes(-1),
        FreshUntilUtc: Now.AddMinutes(14),
        ContractVersion: "1",
        PolicyVersion: "api-key-scope-v1",
        EvaluatedAtUtc: EvaluatedAt,
        ContentDigest: "digest",
        Services: [],
        LifecycleFence: 7,
        GatewayLLMTarget: target);

    private static string UserServicesJson() => """
        {
          "services": [
            {"id":"service-b","slug":"api-beta","catalog_service_name":"Beta Catalog","is_active":true,
             "credential_source":{"type":"org","org_id":"org-alpha","org_name":"Alpha","role":"admin","allowed":true}},
            {"id":"service-inactive","slug":"api-inactive","label":"Inactive","is_active":false,
             "credential_source":{"type":"personal"}},
            {"id":"service-a","slug":"api-alpha","label":"Alpha","is_active":true,
             "credential_source":{"type":"personal"}},
            {"id":"service-denied","slug":"api-denied","label":"Denied","is_active":true,
             "credential_source":{"type":"org","org_id":"org-beta","org_name":"Beta","role":"viewer","allowed":false}}
          ]
        }
        """;

    private static string ScopePlanJson(
        string personalResourceOwnerId = "owner-alpha",
        string organizationResourceOwnerId = "org-alpha") => $$$"""
        {
          "authority":"nyxid",
          "contract_version":"1",
          "policy_version":"api-key-scope-v1",
          "authenticated_actor":{"id":"owner-alpha","type":"personal"},
          "intended_key_owner":{"id":"owner-alpha","type":"personal"},
          "services":[
            {"user_service_id":"service-a","resource_owner":{"id":"{{{personalResourceOwnerId}}}","type":"personal"},"node_grant":{"type":"not_required"}},
            {"user_service_id":"service-b","resource_owner":{"id":"{{{organizationResourceOwnerId}}}","type":"organization"},"node_grant":{"type":"required","node_ids":["node-a","node-b"]}}
          ],
          "allowed_service_ids":["service-a","service-b"],
          "allowed_node_ids":["node-a","node-b"],
          "evaluated_at":"{{{EvaluatedAt:O}}}",
          "normalized_grant_digest":"sha256:{{{new string('a', 64)}}}",
          "freshness":{"mode":"mutation_revalidated_snapshot","precondition_field":"scope_plan_digest","post_creation_drift":"fail_closed"},
          "completeness":{"list_complete":true,"no_duplicates":true,"route_candidate_basis":"active_configured_routes","transient_node_state_excluded":true}
        }
        """;

    private static string ScopePlanJsonForServiceA() => $$$"""
        {
          "authority":"nyxid",
          "contract_version":"1",
          "policy_version":"api-key-scope-v1",
          "authenticated_actor":{"id":"owner-alpha","type":"personal"},
          "intended_key_owner":{"id":"owner-alpha","type":"personal"},
          "services":[
            {"user_service_id":"service-a","resource_owner":{"id":"owner-alpha","type":"personal"},"node_grant":{"type":"not_required"}}
          ],
          "allowed_service_ids":["service-a"],
          "allowed_node_ids":[],
          "evaluated_at":"{{{EvaluatedAt:O}}}",
          "normalized_grant_digest":"sha256:{{{new string('b', 64)}}}",
          "freshness":{"mode":"mutation_revalidated_snapshot","precondition_field":"scope_plan_digest","post_creation_drift":"fail_closed"},
          "completeness":{"list_complete":true,"no_duplicates":true,"route_candidate_basis":"active_configured_routes","transient_node_state_excluded":true}
        }
        """;

    private static QueuedResponse Ok(string body) => new(HttpStatusCode.OK, body);

    private static QueuedResponse Error(HttpStatusCode status, string code, int errorCode) =>
        new(status, JsonSerializer.Serialize(new
        {
            error = code,
            error_code = errorCode,
            message = "sensitive provider detail",
        }));

    private static QueuedResponse ProviderTimeout() =>
        new(default, string.Empty, new TaskCanceledException("provider request timed out"));

    private static async Task IgnoreFailureAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // The public result is asserted before test cleanup observes the task.
        }
    }

    private sealed class TestNyxIdApiClientFactory(NyxIdApiClient client) : INyxIdApiClientFactory
    {
        public NyxIdApiClient CreateClient() => client;
    }

    private sealed class RoutingJsonHandler(params QueuedResponse[] responses) : HttpMessageHandler
    {
        private readonly Queue<QueuedResponse> _responses = new(responses);

        public List<(HttpMethod Method, string Path)> Requests { get; } = [];
        public List<string> AuthorizationHeaders { get; } = [];
        public List<string> RequestBodies { get; } = [];

        public List<bool> CancellationStates { get; } = [];

        public Action<int>? OnRequest { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri?.PathAndQuery ?? string.Empty));
            AuthorizationHeaders.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
            CancellationStates.Add(cancellationToken.IsCancellationRequested);
            if (request.Content != null)
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            OnRequest?.Invoke(Requests.Count - 1);
            if (!_responses.TryDequeue(out var response))
                throw new InvalidOperationException("No queued response remains.");
            if (response.Failure != null)
                throw response.Failure;
            return new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record QueuedResponse(
        HttpStatusCode Status,
        string Body,
        Exception? Failure = null);

    private sealed class CallerCancellationHandler(int cancelAtRequestIndex) : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _pendingResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestIndex;

        public Task Blocked => _blocked.Task;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestIndex = _requestIndex++;
            if (requestIndex < cancelAtRequestIndex)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(UserServicesJson(), Encoding.UTF8, "application/json"),
                });
            }

            if (requestIndex != cancelAtRequestIndex)
                throw new InvalidOperationException("Unexpected provider request index.");

            _blocked.TrySetResult(true);
            return _pendingResponse.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class SupersessionBlockingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _pendingResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Blocked => _blocked.Task;

        public Task CancellationObserved => _cancellationObserved.Task;

        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            _blocked.TrySetResult(true);
            try
            {
                return await _pendingResponse.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _cancellationObserved.TrySetResult(true);
                throw;
            }
        }
    }

    private sealed class SupersessionFaultingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _pendingResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Blocked => _blocked.Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _blocked.TrySetResult(true);
            try
            {
                return await _pendingResponse.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("private-provider-detail bearer-secret");
            }
        }
    }

    private sealed class DelayedProviderTimeoutHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _response =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Blocked => _blocked.Task;

        public void CompleteWithProviderTimeout() =>
            _response.TrySetException(new TaskCanceledException("provider request timed out"));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _blocked.TrySetResult(true);
            return _response.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class CancellationIgnoringFaultingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _pendingResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationToken _providerToken;

        public Task Blocked => _blocked.Task;

        public Task CancellationObserved => _cancellationObserved.Task;

        public bool ProviderCompleted => _pendingResponse.Task.IsCompleted;

        public Exception? TokenLifetimeFailure { get; private set; }

        public void CompleteWithFault()
        {
            try
            {
                if (!_providerToken.WaitHandle.WaitOne(0))
                {
                    TokenLifetimeFailure = new InvalidOperationException(
                        "The provider token was not canceled before detachment.");
                }
            }
            catch (Exception ex)
            {
                TokenLifetimeFailure = ex;
            }

            _pendingResponse.TrySetException(
                new InvalidOperationException("private-provider-detail bearer-secret"));
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _providerToken = cancellationToken;
            using var registration = cancellationToken.Register(
                () => _cancellationObserved.TrySetResult(true));
            _blocked.TrySetResult(true);
            return await _pendingResponse.Task;
        }
    }

    private sealed class CancellationIgnoringThrowingCallbackHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _pendingResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationToken _providerToken;

        public Task Blocked => _blocked.Task;

        public Task CancellationObserved => _cancellationObserved.Task;

        public Exception? TokenLifetimeFailure { get; private set; }

        public void CompleteCanceled() => _pendingResponse.TrySetCanceled();

        public void ProbeTokenLifetime()
        {
            try
            {
                if (!_providerToken.WaitHandle.WaitOne(0))
                {
                    TokenLifetimeFailure = new InvalidOperationException(
                        "The provider token was not canceled before detachment.");
                }
            }
            catch (Exception ex)
            {
                TokenLifetimeFailure = ex;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _providerToken = cancellationToken;
            using var registration = cancellationToken.Register(() =>
            {
                _cancellationObserved.TrySetResult(true);
                throw new InvalidOperationException("private-cancel-detail bearer-secret");
            });
            _blocked.TrySetResult(true);
            return await _pendingResponse.Task;
        }
    }

    private sealed class CancellationCompletingHttpClient : HttpClient
    {
        private readonly TaskCompletionSource<bool> _blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _cancellationCallbackCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HttpResponseMessage> _response = new();

        public Task Blocked => _blocked.Task;

        public Task CancellationCallbackCompleted => _cancellationCallbackCompleted.Task;

        public Exception? TokenLifetimeFailure { get; private set; }

        public override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken.Register(() =>
            {
                _response.TrySetCanceled(cancellationToken);
                try
                {
                    _ = cancellationToken.WaitHandle.WaitOne(0);
                }
                catch (Exception ex)
                {
                    TokenLifetimeFailure = ex;
                }
                finally
                {
                    _cancellationCallbackCompleted.TrySetResult(true);
                }
            });
            _blocked.TrySetResult(true);
            return _response.Task;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly TaskCompletionSource<bool> _warningLogged =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Messages { get; } = [];

        public List<Exception?> Exceptions { get; } = [];

        public Task WarningLogged => _warningLogged.Task;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Exceptions.Add(exception);
            if (logLevel >= LogLevel.Warning)
                _warningLogged.TrySetResult(true);
        }
    }

    private sealed class ThrowingLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logger-failure");
    }

    private sealed class RecordingCommandPort
        : INyxIdAuthorizationCatalogCommandPort,
          INyxIdAuthorizationCatalogRepairCommandPort
    {
        public List<(
            AuthorizationOwnerIdentity Owner,
            string RefreshId,
            DateTimeOffset At,
            long ExpectedLifecycleFence)> Beginnings { get; } = [];
        public List<(
            AuthorizationOwnerIdentity Owner,
            string RefreshId,
            DateTimeOffset At,
            long MinimumSourceStateVersion,
            string RepairRequestId)> RepairBeginnings { get; } = [];
        public List<NyxIdAuthorizationCatalogObservation> Observations { get; } = [];
        public List<(AuthorizationOwnerIdentity Owner, string RefreshId, DateTimeOffset At, string Code, NyxIdAuthorizationCatalogRefreshStatus Status)> Failures { get; } = [];
        public List<(
            AuthorizationOwnerIdentity Owner,
            string RefreshId,
            DateTimeOffset At,
            string Reason,
            NyxIdAuthorizationCatalogRefreshOutcomeStatus OutcomeStatus)> Invalidations { get; } = [];
        public List<(AuthorizationOwnerIdentity Owner, DateTimeOffset At, string Reason)> Cleanups { get; } = [];

        public RecordingObservationRuntime? Observation { get; set; }

        public bool PublishTerminalOutcomes { get; init; } = true;

        public Exception? ObservationException { get; init; }

        public Exception? RefreshFailureException { get; init; }

        public NyxIdAuthorizationCatalogRefreshOutcomeStatus BeginOutcomeStatus { get; init; } =
            NyxIdAuthorizationCatalogRefreshOutcomeStatus.Started;

        public int AllCalls => Beginnings.Count + RepairBeginnings.Count +
                               Observations.Count + Failures.Count +
                               Invalidations.Count + Cleanups.Count;

        public Task BeginRefreshAsync(
            AuthorizationOwnerIdentity owner,
            string refreshId,
            DateTimeOffset startedAtUtc,
            long expectedLifecycleFence,
            CancellationToken ct = default)
        {
            Beginnings.Add((owner.Clone(), refreshId, startedAtUtc, expectedLifecycleFence));
            Observation?.Publish(
                refreshId,
                BeginOutcomeStatus,
                BeginOutcomeStatus == NyxIdAuthorizationCatalogRefreshOutcomeStatus.Superseded
                    ? "nyxid_catalog_refresh_superseded"
                    : string.Empty,
                startedAtUtc: startedAtUtc);
            return Task.CompletedTask;
        }

        public Task BeginRepairRefreshAsync(
            AuthorizationOwnerIdentity owner,
            string refreshId,
            DateTimeOffset startedAtUtc,
            long minimumSourceStateVersion,
            string repairRequestId,
            CancellationToken ct = default)
        {
            RepairBeginnings.Add((
                owner.Clone(),
                refreshId,
                startedAtUtc,
                minimumSourceStateVersion,
                repairRequestId));
            Observation?.Publish(
                refreshId,
                BeginOutcomeStatus,
                BeginOutcomeStatus == NyxIdAuthorizationCatalogRefreshOutcomeStatus.Superseded
                    ? "nyxid_catalog_refresh_superseded"
                    : string.Empty,
                startedAtUtc: startedAtUtc);
            return Task.CompletedTask;
        }

        public Task ObserveAsync(
            NyxIdAuthorizationCatalogObservation observation,
            CancellationToken ct = default)
        {
            Observations.Add(observation);
            if (ObservationException != null)
                return Task.FromException(ObservationException);
            if (PublishTerminalOutcomes)
            {
                Observation?.Publish(
                    observation.RefreshId,
                    NyxIdAuthorizationCatalogRefreshOutcomeStatus.Observed,
                    startedAtUtc: observation.ObservedAtUtc);
            }
            return Task.CompletedTask;
        }

        public Task RecordRefreshFailureAsync(
            AuthorizationOwnerIdentity owner,
            string refreshId,
            DateTimeOffset failedAtUtc,
            string failureCode,
            NyxIdAuthorizationCatalogRefreshStatus status = NyxIdAuthorizationCatalogRefreshStatus.Failed,
            CancellationToken ct = default)
        {
            Failures.Add((owner.Clone(), refreshId, failedAtUtc, failureCode, status));
            if (RefreshFailureException != null)
                return Task.FromException(RefreshFailureException);
            if (PublishTerminalOutcomes)
            {
                Observation?.Publish(
                    refreshId,
                    status == NyxIdAuthorizationCatalogRefreshStatus.CatalogUnstable
                        ? NyxIdAuthorizationCatalogRefreshOutcomeStatus.CatalogUnstable
                        : NyxIdAuthorizationCatalogRefreshOutcomeStatus.Failed,
                    failureCode,
                    failedAtUtc);
            }
            return Task.CompletedTask;
        }

        public Task InvalidateAsync(
            AuthorizationOwnerIdentity owner,
            DateTimeOffset invalidatedAtUtc,
            string reason,
            CancellationToken ct = default)
        {
            Invalidations.Add((
                owner.Clone(),
                string.Empty,
                invalidatedAtUtc,
                reason,
                default));
            return Task.CompletedTask;
        }

        public Task InvalidateRefreshAsync(
            AuthorizationOwnerIdentity owner,
            string refreshId,
            DateTimeOffset invalidatedAtUtc,
            string reason,
            NyxIdAuthorizationCatalogRefreshOutcomeStatus outcomeStatus,
            CancellationToken ct = default)
        {
            Invalidations.Add((owner.Clone(), refreshId, invalidatedAtUtc, reason, outcomeStatus));
            if (PublishTerminalOutcomes)
                Observation?.Publish(refreshId, outcomeStatus, reason, invalidatedAtUtc);
            return Task.CompletedTask;
        }

        public Task CleanupAsync(
            AuthorizationOwnerIdentity owner,
            DateTimeOffset cleanedAtUtc,
            string reason,
            CancellationToken ct = default)
        {
            Cleanups.Add((owner.Clone(), cleanedAtUtc, reason));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCatalogQueryPort(
        long? lifecycleFence = null,
        NyxIdAuthorizationCatalogSnapshot? snapshot = null)
        : INyxIdAuthorizationCatalogQueryPort
    {
        public List<AuthorizationOwnerIdentity> Owners { get; } = [];
        public NyxIdAuthorizationCatalogSnapshot? Snapshot { get; } = snapshot;

        public Task<NyxIdAuthorizationCatalogSnapshot?> GetAsync(
            AuthorizationOwnerIdentity owner,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Owners.Add(owner.Clone());
            return Task.FromResult(Snapshot ?? (lifecycleFence.HasValue
                ? new NyxIdAuthorizationCatalogSnapshot(
                    owner.Clone(),
                    StateVersion: 19,
                    ObservedAtUtc: Now.AddMinutes(-1),
                    FreshUntilUtc: Now.AddMinutes(14),
                    ContractVersion: "1",
                    PolicyVersion: "api-key-scope-v1",
                    EvaluatedAtUtc: EvaluatedAt,
                    ContentDigest: "digest",
                    Services: [],
                    LifecycleFence: lifecycleFence.Value)
                : null));
        }
    }

    private sealed class DelayedCatalogQueryPort(long lifecycleFence)
        : INyxIdAuthorizationCatalogQueryPort
    {
        private readonly TaskCompletionSource _queryStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task QueryStarted => _queryStarted.Task;

        public void Complete() => _release.TrySetResult();

        public async Task<NyxIdAuthorizationCatalogSnapshot?> GetAsync(
            AuthorizationOwnerIdentity owner,
            CancellationToken ct = default)
        {
            _queryStarted.TrySetResult();
            await _release.Task.WaitAsync(ct);
            return new NyxIdAuthorizationCatalogSnapshot(
                owner.Clone(),
                StateVersion: 19,
                ObservedAtUtc: Now.AddMinutes(-1),
                FreshUntilUtc: Now.AddMinutes(14),
                ContractVersion: "1",
                PolicyVersion: "api-key-scope-v1",
                EvaluatedAtUtc: EvaluatedAt,
                ContentDigest: "digest",
                Services: [],
                LifecycleFence: lifecycleFence);
        }
    }

    private sealed class RecordingObservationRuntime
        : INyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort,
          INyxIdAuthorizationCatalogRefreshObservationProjectionPort
    {
        private IEventSink<NyxIdAuthorizationCatalogRefreshCommittedOutcome>? _sink;

        public bool ProjectionEnabled => true;

        public int Detached { get; private set; }

        public int ProjectionReleases { get; private set; }

        public int PreparationReleases { get; private set; }

        public Exception? DetachFailure { get; init; }

        public Exception? ProjectionReleaseFailure { get; init; }

        public Exception? PreparationReleaseFailure { get; init; }

        public Action? OnPrepare { get; init; }

        public Task<NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparation?> PrepareAsync(
            string actorId,
            string refreshId,
            CancellationToken ct = default)
        {
            OnPrepare?.Invoke();
            return Task.FromResult<NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparation?>(
                new NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparation(
                    actorId,
                    refreshId));
        }

        public Task ReleaseAsync(
            NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparation preparation,
            CancellationToken ct = default)
        {
            PreparationReleases++;
            return PreparationReleaseFailure == null
                ? Task.CompletedTask
                : Task.FromException(PreparationReleaseFailure);
        }

        public Task<
            EventSinkProjectionAttachment<INyxIdAuthorizationCatalogRefreshObservationProjectionLease>?>
            AttachExistingRefreshProjectionAsync(
                string actorId,
                string refreshId,
                IEventSink<NyxIdAuthorizationCatalogRefreshCommittedOutcome> sink,
                CancellationToken ct = default)
        {
            _sink = sink;
            var lease = new ObservationLease(actorId, refreshId);
            return Task.FromResult<
                EventSinkProjectionAttachment<
                    INyxIdAuthorizationCatalogRefreshObservationProjectionLease>?>(
                new EventSinkProjectionAttachment<
                    INyxIdAuthorizationCatalogRefreshObservationProjectionLease>(
                    lease,
                    new NoopAsyncDisposable()));
        }

        public Task<IAsyncDisposable?> AttachLiveSinkAsync(
            INyxIdAuthorizationCatalogRefreshObservationProjectionLease lease,
            IEventSink<NyxIdAuthorizationCatalogRefreshCommittedOutcome> sink,
            CancellationToken ct = default)
        {
            _sink = sink;
            return Task.FromResult<IAsyncDisposable?>(new NoopAsyncDisposable());
        }

        public Task DetachLiveSinkAsync(
            IAsyncDisposable? liveSinkLease,
            CancellationToken ct = default)
        {
            Detached++;
            _sink = null;
            return DetachFailure == null
                ? Task.CompletedTask
                : Task.FromException(DetachFailure);
        }

        public Task ReleaseActorProjectionAsync(
            INyxIdAuthorizationCatalogRefreshObservationProjectionLease lease,
            CancellationToken ct = default)
        {
            ProjectionReleases++;
            return ProjectionReleaseFailure == null
                ? Task.CompletedTask
                : Task.FromException(ProjectionReleaseFailure);
        }

        public void Publish(
            string refreshId,
            NyxIdAuthorizationCatalogRefreshOutcomeStatus status,
            string failureCode = "",
            DateTimeOffset? startedAtUtc = null) =>
            _sink?.Push(new NyxIdAuthorizationCatalogRefreshCommittedOutcome(
                refreshId,
                status,
                1,
                failureCode,
                startedAtUtc ?? Now));

        private sealed record ObservationLease(string ActorId, string RefreshId)
            : INyxIdAuthorizationCatalogRefreshObservationProjectionLease;

        private sealed class NoopAsyncDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
