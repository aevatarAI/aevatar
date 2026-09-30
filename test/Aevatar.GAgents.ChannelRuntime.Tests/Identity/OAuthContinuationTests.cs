using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using Aevatar.GAgents.Channel.Identity.Endpoints;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

public sealed class OAuthContinuationTests
{
    private static ExternalSubjectRef Subject() => new() { Platform = "telegram", Tenant = "tenant", ExternalUserId = "sender" };

    [Theory]
    [InlineData(null)]
    [InlineData("access_denied")]
    public async Task SignedContinuation_OnlySubmitsToOperationAuthority(string? error)
    {
        var callback = Substitute.For<INyxIdBrokerCallbackClient>();
        callback.TryDecodeStateTokenAsync("signed", Arg.Any<CancellationToken>()).Returns(
            CallbackStateDecode.Ok("callback-1", Subject(), "pkce") with
            {
                ContinuationRequested = true,
                ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            });
        var port = Substitute.For<IExternalCallbackCommandPort>();
        var result = await InvokeEndpoint(callback, port, error);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status202Accepted);
        await port.Received(1).SubmitOAuthAsync(Arg.Is<OAuthContinuationSubmission>(x =>
            x.CallbackId == "callback-1" && x.ExternalSubject.Equals(Subject())
            && x.PkceVerifier == "pkce" && x.OauthError == (error ?? string.Empty)), Arg.Any<CancellationToken>());
        await callback.DidNotReceive().ExchangeAuthorizationCodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("telegram", "Telegram", null)]
    [InlineData("lark", "Lark", null)]
    [InlineData("telegram", "Telegram", "access_denied")]
    public async Task SignedContinuation_HtmlPreservesBindingGuidanceAndExplainsVerification(
        string platform,
        string channelName,
        string? error)
    {
        var subject = Subject();
        subject.Platform = platform;
        var callback = Substitute.For<INyxIdBrokerCallbackClient>();
        callback.TryDecodeStateTokenAsync("signed", Arg.Any<CancellationToken>()).Returns(
            CallbackStateDecode.Ok("callback-1", subject, "pkce") with
            {
                ContinuationRequested = true,
                ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            });
        var port = Substitute.For<IExternalCallbackCommandPort>();

        var result = await InvokeEndpoint(callback, port, error, format: null);

        var html = result.Should().BeOfType<ContentHttpResult>().Subject;
        html.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        html.ContentType.Should().StartWith("text/html");
        html.ResponseContent.Should().Contain("NyxID 绑定请求已受理")
            .And.Contain("账号:核验中")
            .And.Contain("Aevatar 正在核验本次授权。请回到原来的聊天会话，后续结果会在那里发送。")
            .And.Contain($"回到 {channelName}")
            .And.Contain("请求编号:<code>callback-1</code>")
            .And.Contain("下一步")
            .And.Contain("/whoami")
            .And.Contain("/model");
        await callback.DidNotReceive().ExchangeAuthorizationCodeAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignedContinuation_HtmlEncodesRequestIdAndChannelName()
    {
        var subject = Subject();
        subject.Platform = "<script>channel</script>";
        var callback = Substitute.For<INyxIdBrokerCallbackClient>();
        callback.TryDecodeStateTokenAsync("signed", Arg.Any<CancellationToken>()).Returns(
            CallbackStateDecode.Ok("<script>callback</script>", subject, "pkce") with
            {
                ContinuationRequested = true,
                ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            });

        var result = await InvokeEndpoint(callback, Substitute.For<IExternalCallbackCommandPort>(), null, format: null);

        var html = result.Should().BeOfType<ContentHttpResult>().Subject;
        html.ResponseContent.Should().Contain("&lt;script&gt;callback&lt;/script&gt;")
            .And.Contain("&lt;script&gt;channel&lt;/script&gt;")
            .And.NotContain("<script>");
    }

    [Fact]
    public async Task InvalidCancellationState_DoesNotRouteContinuation()
    {
        var callback = Substitute.For<INyxIdBrokerCallbackClient>();
        callback.TryDecodeStateTokenAsync("signed", Arg.Any<CancellationToken>()).Returns(CallbackStateDecode.Failed("state_signature_invalid"));
        var port = Substitute.For<IExternalCallbackCommandPort>();
        var result = await InvokeEndpoint(callback, port, "access_denied");
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        await port.DidNotReceive().SubmitOAuthAsync(Arg.Any<OAuthContinuationSubmission>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingContinuationAdmission_NeverFallsBackToLegacyExchange(bool missingRegistration)
    {
        var callback = Substitute.For<INyxIdBrokerCallbackClient>();
        callback.TryDecodeStateTokenAsync("signed", Arg.Any<CancellationToken>()).Returns(
            CallbackStateDecode.Ok("callback-1", Subject(), "pkce") with { ContinuationRequested = true });
        var port = Substitute.For<IExternalCallbackCommandPort>();
        port.SubmitOAuthAsync(Arg.Any<OAuthContinuationSubmission>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new KeyNotFoundException());
        var result = await InvokeEndpoint(callback, missingRegistration ? port : null, null);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        await callback.DidNotReceive().ExchangeAuthorizationCodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BindingCommit_PersistsDecisionWithFactBeforeSending_AndReplaysAfterReplacement()
    {
        using var services = BindingServices();
        var publisher = new RecordingPublisher();
        var actor = await NewBindingActor(services, publisher);
        var original = Commit("binding-first", "callback-1");
        publisher.BeforeSend = () =>
        {
            actor.State.BindingId.Should().Be("binding-first");
            actor.State.CallbackDecisions.Should().ContainSingle().Which.Outcome.Succeeded.Should().BeTrue();
            actor.EventSourcing!.CurrentVersion.Should().Be(1, "binding and successful callback decision commit atomically");
        };
        await actor.HandleCommitBinding(original);
        publisher.BeforeSend = null;
        await actor.HandleReplaceBinding(new ReplaceBindingCommand
        {
            ExternalSubject = Subject(), BindingId = "binding-second", ExpectedPreviousBindingId = "binding-first", OwnerScopeId = "owner",
        });
        await actor.HandleCommitBinding(original);
        publisher.Outcomes.Should().HaveCount(2);
        publisher.Outcomes.Should().OnlyContain(x => x.Succeeded && x.BindingId == "binding-first");
        actor.State.BindingId.Should().Be("binding-second");
    }

    [Fact]
    public async Task CommittedOutcome_SurvivesReactivationAndFailedDelivery()
    {
        using var services = BindingServices();
        var publisher = new RecordingPublisher { ThrowOnSend = true };
        var actor = await NewBindingActor(services, publisher);
        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.HandleCommitBinding(Commit("binding-first", "callback-1")));
        var recoveredPublisher = new RecordingPublisher();
        var recovered = await NewBindingActor(services, recoveredPublisher);
        await recovered.HandleCommitBinding(Commit("binding-first", "callback-1"));
        recoveredPublisher.Outcomes.Should().ContainSingle().Which.Succeeded.Should().BeTrue();
        recovered.EventSourcing!.CurrentVersion.Should().Be(1);
    }

    [Fact]
    public async Task ExplicitUnbind_AllowsNewOwnerOnNextContinuationBinding()
    {
        using var services = BindingServices();
        var publisher = new RecordingPublisher();
        var actor = await NewBindingActor(services, publisher);
        await actor.HandleCommitBinding(Commit("old-binding", null));
        await actor.HandleRevokeBinding(new RevokeBindingCommand
        {
            ExternalSubject = Subject(), Reason = "user_unbind",
        });
        actor.State.BindingId.Should().BeEmpty();
        actor.State.OwnerScopeId.Should().Be("owner", "legacy revoked state retains historical ownership");
        var newBinding = Commit("new-account-binding", "new-account-callback");
        newBinding.OwnerScopeId = "new-owner";
        await actor.HandleCommitBinding(newBinding);
        actor.State.BindingId.Should().Be("new-account-binding");
        actor.State.OwnerScopeId.Should().Be("new-owner");
        publisher.Outcomes.Should().ContainSingle().Which.Succeeded.Should().BeTrue();
        actor.State.PendingRetirementBindingIds.Should().NotContain("new-account-binding");
    }

    [Fact]
    public async Task EmptyGrantConfirmation_CannotCreateBindingOrSuccessfulReceipt()
    {
        using var services = BindingServices();
        var publisher = new RecordingPublisher();
        var actor = await NewBindingActor(services, publisher);
        await actor.HandleConfirmBindingGrant(new ConfirmBindingGrantCommand
        {
            ExternalSubject = Subject(), OwnerScopeId = "owner", CallbackReply = Reply("malformed-review"),
        });
        actor.State.BindingId.Should().BeEmpty();
        actor.State.OwnerScopeId.Should().BeEmpty();
        publisher.Outcomes.Should().ContainSingle().Which.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task BindingRejectsStaleReplacementAndOwnerSwitch_WithoutSuccessfulCompletion()
    {
        using var services = BindingServices();
        var publisher = new RecordingPublisher();
        var actor = await NewBindingActor(services, publisher);
        await actor.HandleCommitBinding(Commit("binding-first", null));
        await actor.HandleReplaceBinding(new ReplaceBindingCommand
        {
            ExternalSubject = Subject(), BindingId = "binding-stale", ExpectedPreviousBindingId = "binding-obsolete",
            OwnerScopeId = "owner", CallbackReply = Reply("stale"),
        });
        await actor.HandleReplaceBinding(new ReplaceBindingCommand
        {
            ExternalSubject = Subject(), BindingId = "binding-other-owner", ExpectedPreviousBindingId = "binding-first",
            OwnerScopeId = "other-owner", CallbackReply = Reply("wrong-owner"),
        });
        actor.State.BindingId.Should().Be("binding-first");
        publisher.Outcomes.Should().HaveCount(2).And.OnlyContain(x => !x.Succeeded);
    }

    [Fact]
    public async Task GrantUpdate_ConfirmsOnlyExactCurrentBinding_AndAbsorbsDuplicate()
    {
        using var services = BindingServices();
        var publisher = new RecordingPublisher();
        var actor = await NewBindingActor(services, publisher);
        await actor.HandleCommitBinding(Commit("binding-first", null));
        var confirm = new ConfirmBindingGrantCommand
        {
            ExternalSubject = Subject(), BindingId = "binding-first", OwnerScopeId = "owner", CallbackReply = Reply("review"),
        };
        await actor.HandleConfirmBindingGrant(confirm);
        var version = actor.EventSourcing!.CurrentVersion;
        await actor.HandleConfirmBindingGrant(confirm);
        actor.EventSourcing!.CurrentVersion.Should().Be(version);
        await actor.HandleConfirmBindingGrant(new ConfirmBindingGrantCommand
        {
            ExternalSubject = Subject(), BindingId = "binding-stale", OwnerScopeId = "owner", CallbackReply = Reply("stale-review"),
        });
        publisher.Outcomes.Select(x => x.Succeeded).Should().Equal(true, true, false);
    }

    [Fact]
    public async Task GrantUpdate_ConfirmsLegacyOwnerWithoutReplacingOrRetiringBinding()
    {
        var retirement = new RetirementPort();
        using var services = BindingServices(retirement);
        var boundAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(-2));
        await services.GetRequiredService<IEventStore>().AppendAsync(string.Empty,
        [new StateEvent
        {
            EventId = "legacy-bind", AgentId = string.Empty, Version = 1,
            EventData = Google.Protobuf.WellKnownTypes.Any.Pack(new ExternalIdentityBoundEvent
            {
                ExternalSubject = Subject(), BindingId = "legacy-binding", BoundAt = boundAt,
            }),
        }], 0);
        var publisher = new RecordingPublisher();
        var actor = await NewBindingActor(services, publisher);
        actor.State.OwnerScopeId.Should().BeEmpty();
        await actor.HandleConfirmBindingGrant(new ConfirmBindingGrantCommand
        {
            ExternalSubject = Subject(), BindingId = "legacy-binding", OwnerScopeId = "verified-owner", CallbackReply = Reply("legacy-review"),
        });
        actor.State.BindingId.Should().Be("legacy-binding");
        actor.State.BoundAt.Should().Be(boundAt);
        actor.State.OwnerScopeId.Should().Be("verified-owner");
        publisher.Outcomes.Should().ContainSingle().Which.Succeeded.Should().BeTrue();
        actor.EventSourcing!.CurrentVersion.Should().Be(2, "owner confirmation and callback decision are one committed fact");
        retirement.Attempts.Should().BeEmpty();
    }

    [Fact]
    public async Task Abandonment_OwnsCleanupDurablyBeforeReceipt_AndRecoversAfterProviderFailure()
    {
        var retirement = new RetirementPort { Fail = true };
        using var services = BindingServices(retirement);
        var publisher = new RecordingPublisher();
        var actor = await NewBindingActor(services, publisher);
        publisher.BeforeSend = () => actor.State.PendingRetirementBindingIds.Should().Contain("unadopted-binding");
        var abandon = new AbandonBindingPreparationCommand
        {
            ExternalSubject = Subject(), BindingId = "unadopted-binding", OwnerScopeId = "owner", CallbackReply = Reply("abandoned"),
        };
        await actor.HandleAbandonBindingPreparation(abandon);
        actor.State.PendingRetirementBindingIds.Should().Contain("unadopted-binding");
        publisher.Outcomes.Should().ContainSingle().Which.Abandoned.Should().BeTrue();

        retirement.Fail = false;
        var recoveredPublisher = new RecordingPublisher();
        var recovered = await NewBindingActor(services, recoveredPublisher);
        recovered.State.PendingRetirementBindingIds.Should().BeEmpty();
        await recovered.HandleAbandonBindingPreparation(abandon);
        recoveredPublisher.Outcomes.Should().ContainSingle().Which.Abandoned.Should().BeTrue();
        await recovered.HandleCommitBinding(Commit("unadopted-binding", "abandoned"));
        recovered.State.BindingId.Should().BeEmpty("a stale commit cannot adopt an abandoned binding");
    }

    [Fact]
    public async Task Abandonment_NeverRetiresBindingAlreadyAdoptedByAuthority()
    {
        var retirement = new RetirementPort();
        using var services = BindingServices(retirement);
        var publisher = new RecordingPublisher();
        var actor = await NewBindingActor(services, publisher);
        await actor.HandleCommitBinding(Commit("current-binding", null));
        await actor.HandleAbandonBindingPreparation(new AbandonBindingPreparationCommand
        {
            ExternalSubject = Subject(), BindingId = "current-binding", OwnerScopeId = "owner", CallbackReply = Reply("abandoned"),
        });
        retirement.Attempts.Should().BeEmpty();
        actor.State.BindingId.Should().Be("current-binding");
        publisher.Outcomes.Should().ContainSingle().Which.Abandoned.Should().BeTrue();
    }

    [Fact]
    public async Task CallbackExpiryAndConflictingDestination_DoNotMutateBinding()
    {
        using var services = BindingServices();
        var publisher = new RecordingPublisher();
        var actor = await NewBindingActor(services, publisher);
        var expired = Commit("binding-expired", "expired");
        expired.CallbackReply.ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
        await actor.HandleCommitBinding(expired);
        actor.State.BindingId.Should().BeEmpty();
        publisher.Outcomes.Should().ContainSingle().Which.ErrorCode.Should().Be("callback_expired");
        var first = Commit("binding-first", "first");
        await actor.HandleCommitBinding(first);
        first.CallbackReply.OperationActorId = "other-target";
        await actor.HandleCommitBinding(first);
        publisher.Outcomes.Should().HaveCount(2);
    }

    private static Task<IResult> InvokeEndpoint(
        INyxIdBrokerCallbackClient callback,
        IExternalCallbackCommandPort? port,
        string? error,
        string? format = "json") =>
        IdentityOAuthEndpoints.HandleNyxIdOAuthCallbackAsync(error is null ? "code" : null, "signed", error, format,
            callback, Substitute.For<INyxIdCapabilityBroker>(), Substitute.For<IExternalIdentityBindingQueryPort>(),
            Substitute.For<ICommandDispatchService<CommitBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>>(),
            Substitute.For<ICommandDispatchService<ReplaceBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>>(),
            Substitute.For<IOwnerScopeResolver>(),
            Substitute.For<ICommandDispatchService<ObserveBrokerCapabilityCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>>(),
            NullLoggerFactory.Instance, CancellationToken.None, port);

    private static CommitBindingCommand Commit(string binding, string? callback) => new()
    {
        ExternalSubject = Subject(), BindingId = binding, OwnerScopeId = "owner",
        CallbackReply = callback is null ? null : Reply(callback),
    };

    private static BindingCallbackReply Reply(string callback) => new()
    {
        CallbackId = callback, OperationActorId = "opaque-operation", ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
    };

    private static ServiceProvider BindingServices(INyxIdBindingRetirementPort? retirement = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventStore, IdentityGAgentTestHarness.InMemoryEventStore>();
        services.AddSingleton<EventSourcingRuntimeOptions>();
        services.AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>));
        services.AddSingleton<Aevatar.Foundation.Abstractions.Runtime.Callbacks.IActorRuntimeCallbackScheduler, IdentityGAgentTestHarness.NoopCallbackScheduler>();
        if (retirement is not null) services.AddSingleton(retirement);
        return services.BuildServiceProvider();
    }

    private static async Task<ExternalIdentityBindingGAgent> NewBindingActor(ServiceProvider services, IEventPublisher publisher)
    {
        var actor = new ExternalIdentityBindingGAgent
        {
            Services = services, EventPublisher = publisher,
            EventSourcingBehaviorFactory = services.GetRequiredService<IEventSourcingBehaviorFactory<ExternalIdentityBindingState>>(),
        };
        await actor.ActivateAsync();
        return actor;
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        public List<OAuthBindingOutcome> Outcomes { get; } = [];
        public Action? BeforeSend { get; set; }
        public bool ThrowOnSend { get; init; }
        public Task PublishAsync<TEvent>(TEvent evt, TopologyAudience audience = TopologyAudience.Children, CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null, EventEnvelopePublishOptions? options = null) where TEvent : IMessage => Task.CompletedTask;
        public Task SendToAsync<TEvent>(string targetActorId, TEvent evt, CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null, EventEnvelopePublishOptions? options = null) where TEvent : IMessage
        {
            BeforeSend?.Invoke();
            if (ThrowOnSend) throw new InvalidOperationException("inbox unavailable");
            Outcomes.Add(((OAuthBindingOutcome)(IMessage)evt).Clone());
            return Task.CompletedTask;
        }
    }

    private sealed class RetirementPort : INyxIdBindingRetirementPort
    {
        public bool Fail { get; set; }
        public List<string> Attempts { get; } = [];
        public Task RetireAsync(string bindingId, CancellationToken ct = default)
        {
            Attempts.Add(bindingId);
            if (Fail) throw new HttpRequestException("provider unavailable");
            return Task.CompletedTask;
        }
    }
}
