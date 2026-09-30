using System.Text;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

public sealed class OAuthContinuationExecutionPortTests
{
    [Fact]
    public async Task TransientProbe_RetriesSavedPreparationWithoutRepeatingCodeExchange()
    {
        var h = new Harness();
        var prepared = await h.Port.ExchangeAsync(Submission());
        prepared.BindingId.Should().Be("issued-binding");
        prepared.OwnerScopeId.Should().Be("owner");
        h.Capability.IssueShortLivedByBindingIdAsync(Arg.Any<ExternalSubjectRef>(), "issued-binding", Arg.Any<CapabilityScope>(), Arg.Any<CancellationToken>())
            .Returns<Task<CapabilityHandle>>(_ => throw new HttpRequestException("temporarily unavailable"));
        var unavailable = await h.Port.ValidateAsync(prepared);
        unavailable.IsTransientFailure.Should().BeTrue();
        unavailable.Rejected.Should().BeFalse();
        unavailable.BindingId.Should().Be("issued-binding");

        h.Capability.IssueShortLivedByBindingIdAsync(Arg.Any<ExternalSubjectRef>(), "issued-binding", Arg.Any<CapabilityScope>(), Arg.Any<CancellationToken>())
            .Returns(new CapabilityHandle { AccessToken = "ephemeral" });
        var verified = await h.Port.ValidateAsync(unavailable);
        verified.VerificationSucceeded.Should().BeTrue();
        await h.Port.DispatchBindingAsync(verified, "callback", "opaque-operation");
        h.Commit.Commands.Should().ContainSingle().Which.CallbackReply.CallbackId.Should().Be("callback");
        await h.Callback.Received(1).ExchangeAuthorizationCodeAsync("single-use-code", "pkce", Arg.Any<CancellationToken>());
        await h.Callback.DidNotReceive().RevokeBindingByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangedBindingBeforeExchange_DoesNotConsumeCode()
    {
        var h = new Harness();
        h.Query.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>()).Returns(new BindingId { Value = "changed" });
        var result = await h.Port.ExchangeAsync(Submission());
        result.Rejected.Should().BeTrue();
        result.ErrorCode.Should().Be("binding_changed_during_review");
        await h.Callback.DidNotReceive().ExchangeAuthorizationCodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangedBindingDuringVerification_PreservesExactUnadoptedBindingForAuthorityCleanup()
    {
        var h = new Harness();
        var prepared = await h.Port.ExchangeAsync(Submission());
        h.Query.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>()).Returns(new BindingId { Value = "newer-binding" });
        var result = await h.Port.ValidateAsync(prepared);
        result.Rejected.Should().BeTrue();
        result.BindingId.Should().Be("issued-binding");
        await h.Callback.DidNotReceive().RevokeBindingByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        h.Commit.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task BindingUpdated_UsesExactCurrentBindingAndAuthoritativeConfirmationCommand()
    {
        var h = new Harness();
        h.Query.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>()).Returns(new BindingId { Value = "current-binding" });
        h.Owners.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>()).Returns(new OwnerScopeId { Value = "owner" });
        h.Callback.ExchangeAuthorizationCodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new BrokerAuthorizationCodeResult(null, null, null) { BindingUpdated = true });
        var submission = Submission();
        submission.ExpectedBindingHash = NyxIdRemoteCapabilityBroker.HashBindingId("current-binding");
        var prepared = await h.Port.ExchangeAsync(submission);
        var verified = await h.Port.ValidateAsync(prepared);
        verified.VerificationSucceeded.Should().BeTrue();
        verified.OwnerScopeId.Should().Be("owner");
        await h.Port.DispatchBindingAsync(verified, "review-callback", "operation");
        h.Confirm.Commands.Should().ContainSingle().Which.BindingId.Should().Be("current-binding");
        h.Commit.Commands.Should().BeEmpty();
        h.Replace.Commands.Should().BeEmpty();
        await h.Callback.DidNotReceive().RevokeBindingByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("other-owner", "binding_owner_mismatch")]
    [InlineData("", "binding_owner_missing")]
    public async Task Replacement_RejectsMissingOrDifferentOwner(string actualOwner, string expectedError)
    {
        var h = new Harness();
        h.Query.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>()).Returns(new BindingId { Value = "current-binding" });
        h.Owners.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>()).Returns(new OwnerScopeId { Value = actualOwner });
        var submission = Submission();
        submission.ExpectedBindingHash = NyxIdRemoteCapabilityBroker.HashBindingId("current-binding");
        var verified = await h.Port.ValidateAsync(await h.Port.ExchangeAsync(submission));
        verified.Rejected.Should().BeTrue();
        verified.ErrorCode.Should().Be(expectedError);
        verified.BindingId.Should().Be("issued-binding");
        await h.Callback.DidNotReceive().RevokeBindingByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Abandonment_UsesDurableBindingAuthority_AndSkipsInPlaceGrantUpdate()
    {
        var h = new Harness();
        var prepared = await h.Port.ExchangeAsync(Submission());
        await h.Port.AbandonAsync(prepared, "callback", "operation");
        h.Abandon.Commands.Should().ContainSingle().Which.BindingId.Should().Be("issued-binding");
        prepared.BindingUpdated = true;
        await h.Port.AbandonAsync(prepared, "callback-review", "operation-review");
        h.Abandon.Commands.Should().ContainSingle();
        await h.Callback.DidNotReceive().RevokeBindingByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnverifiedPreparation_CannotDispatchBinding()
    {
        var h = new Harness();
        var prepared = await h.Port.ExchangeAsync(Submission());
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Port.DispatchBindingAsync(prepared, "callback", "operation"));
        h.Commit.Commands.Should().BeEmpty();
    }

    private static OAuthContinuationSubmission Submission() => new()
    {
        CallbackId = "callback", ExternalSubject = new ExternalSubjectRef { Platform = "telegram", ExternalUserId = "sender" },
        AuthorizationCode = "single-use-code", PkceVerifier = "pkce", ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
    };

    private sealed class Harness
    {
        public INyxIdBrokerCallbackClient Callback { get; } = Substitute.For<INyxIdBrokerCallbackClient>();
        public INyxIdCapabilityBroker Capability { get; } = Substitute.For<INyxIdCapabilityBroker>();
        public IExternalIdentityBindingQueryPort Query { get; } = Substitute.For<IExternalIdentityBindingQueryPort>();
        public IOwnerScopeResolver Owners { get; } = Substitute.For<IOwnerScopeResolver>();
        public RecordingDispatch<CommitBindingCommand> Commit { get; } = new();
        public RecordingDispatch<ReplaceBindingCommand> Replace { get; } = new();
        public RecordingDispatch<ConfirmBindingGrantCommand> Confirm { get; } = new();
        public RecordingDispatch<AbandonBindingPreparationCommand> Abandon { get; } = new();
        public OAuthContinuationExecutionPort Port { get; }
        public Harness()
        {
            Callback.ExchangeAuthorizationCodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
                new BrokerAuthorizationCodeResult("issued-binding", "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"uid\":\"owner\"}")).TrimEnd('=') + ".signature", null));
            Capability.IssueShortLivedByBindingIdAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<string>(), Arg.Any<CapabilityScope>(), Arg.Any<CancellationToken>())
                .Returns(new CapabilityHandle { AccessToken = "ephemeral" });
            Port = new OAuthContinuationExecutionPort(Callback, Capability, Query, Owners, Commit, Replace, Confirm, Abandon,
                new RecordingDispatch<ObserveBrokerCapabilityCommand>(), NullLogger<OAuthContinuationExecutionPort>.Instance);
        }
    }

    private sealed class RecordingDispatch<T> : ICommandDispatchService<T, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>
    {
        public List<T> Commands { get; } = [];
        public Task<CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>> DispatchAsync(T command, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(CommandDispatchResult<ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>.Success(
                new ChannelIdentityOAuthAcceptedReceipt("binding-actor", "command", "correlation")));
        }
    }
}
