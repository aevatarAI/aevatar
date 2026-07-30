using System.Text;
using System.Text.Json;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity;
using Aevatar.GAgents.Channel.Identity.Abstractions;
using Aevatar.GAgents.Channel.Identity.Broker;
using Aevatar.GAgents.Channel.Identity.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests.Identity;

/// <summary>
/// Behaviour tests for <see cref="IdentityOAuthEndpoints.HandleNyxIdOAuthCallbackAsync"/>.
/// </summary>
public sealed class IdentityOAuthCallbackEndpointTests
{
    [Fact]
    public async Task AcceptedPath_DispatchesBindingCommandAndReturnsPendingJson()
    {
        const string incoming = "bnd_incoming";
        var subject = SampleSubject();
        var broker = NewBroker(subject, incoming);
        var queryPort = Substitute.For<IExternalIdentityBindingQueryPort>();
        queryPort.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(null));
        var bindingDispatch = new RecordingCommandDispatch<CommitBindingCommand>();
        var capabilityDispatch = new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>();

        var result = await InvokeCallbackAsync(
            broker,
            queryPort,
            bindingDispatch,
            capabilityDispatch,
            format: "json");

        bindingDispatch.Commands.Should().ContainSingle();
        bindingDispatch.Commands[0].ExternalSubject.Should().Be(subject);
        bindingDispatch.Commands[0].BindingId.Should().Be(incoming);
        bindingDispatch.Commands[0].OwnerScopeId.Should().Be("owner-user-1");
        capabilityDispatch.Commands.Should().ContainSingle();

        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("binding_pending");
        doc.RootElement.GetProperty("status_url").GetString().Should().Be("/api/oauth/aevatar-client/status");
    }

    [Fact]
    public async Task DispatchRejected_RevokesIncomingAndReturns503()
    {
        const string incoming = "bnd_incoming";
        var subject = SampleSubject();
        var broker = NewBroker(subject, incoming);
        var queryPort = Substitute.For<IExternalIdentityBindingQueryPort>();
        queryPort.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(null));
        var bindingDispatch = new RejectingCommandDispatch<CommitBindingCommand>();
        var capabilityDispatch = new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>();

        var result = await InvokeCallbackAsync(
            broker,
            queryPort,
            bindingDispatch,
            capabilityDispatch);

        await broker.Received(1).RevokeBindingByIdAsync(incoming, Arg.Any<CancellationToken>());
        capabilityDispatch.Commands.Should().BeEmpty();
        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        ctx.Response.Body.Position = 0;
        var text = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("error").GetString().Should().Be("actor_dispatch_rejected");
    }

    [Fact]
    public async Task ExistingBindingWithMatchingOwner_DispatchesReplacementAndReturnsPending()
    {
        var existing = new BindingId { Value = "bnd_existing" };
        const string incoming = "bnd_incoming";
        var subject = SampleSubject();
        var broker = NewBroker(
            subject,
            incoming,
            expectedBindingHash: NyxIdRemoteCapabilityBroker.HashBindingId(existing.Value));
        var queryPort = Substitute.For<IExternalIdentityBindingQueryPort>();
        queryPort.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(existing));
        var ownerScopeResolver = NewOwnerScopeResolver("owner-user-1");
        var bindingDispatch = new RecordingCommandDispatch<CommitBindingCommand>();
        var bindingReplaceDispatch = new RecordingCommandDispatch<ReplaceBindingCommand>();
        var capabilityDispatch = new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>();

        var result = await InvokeCallbackAsync(
            broker,
            queryPort,
            bindingDispatch,
            capabilityDispatch,
            bindingReplaceDispatch,
            ownerScopeResolver,
            format: "json");

        await broker.DidNotReceive().ResolveBindingOwnerScopeAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await broker.DidNotReceive().RevokeBindingByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        bindingDispatch.Commands.Should().BeEmpty();
        bindingReplaceDispatch.Commands.Should().ContainSingle().Which.Should().BeEquivalentTo(new ReplaceBindingCommand
        {
            ExternalSubject = subject,
            BindingId = incoming,
            ExpectedPreviousBindingId = existing.Value,
            OwnerScopeId = "owner-user-1",
            Reason = "channel_service_access_review",
        });
        capabilityDispatch.Commands.Should().ContainSingle();

        using var document = await ExecuteJsonAsync(result, StatusCodes.Status202Accepted);
        document.RootElement.GetProperty("status").GetString().Should().Be("binding_pending");
    }

    [Fact]
    public async Task ExistingBindingWithDifferentOwner_RevokesIncomingAndReturnsConflict()
    {
        var existing = new BindingId { Value = "bnd_existing" };
        const string incoming = "bnd_incoming";
        var subject = SampleSubject();
        var broker = NewBroker(
            subject,
            incoming,
            expectedBindingHash: NyxIdRemoteCapabilityBroker.HashBindingId(existing.Value),
            ownerScopeId: "owner-user-2");
        var queryPort = Substitute.For<IExternalIdentityBindingQueryPort>();
        queryPort.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(existing));
        var bindingDispatch = new RecordingCommandDispatch<CommitBindingCommand>();
        var bindingReplaceDispatch = new RecordingCommandDispatch<ReplaceBindingCommand>();
        var capabilityDispatch = new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>();

        var result = await InvokeCallbackAsync(
            broker,
            queryPort,
            bindingDispatch,
            capabilityDispatch,
            bindingReplaceDispatch,
            NewOwnerScopeResolver("owner-user-1"),
            format: "json");

        await broker.Received(1).RevokeBindingByIdAsync(incoming, Arg.Any<CancellationToken>());
        bindingDispatch.Commands.Should().BeEmpty();
        bindingReplaceDispatch.Commands.Should().BeEmpty();
        capabilityDispatch.Commands.Should().BeEmpty();
        using var document = await ExecuteJsonAsync(result, StatusCodes.Status409Conflict);
        document.RootElement.GetProperty("error").GetString().Should().Be("binding_owner_mismatch");
    }

    [Fact]
    public async Task LegacyBindingWithoutProjectedOwner_UsesNyxIdOwnerAndDispatchesReplacement()
    {
        var existing = new BindingId { Value = "bnd_legacy" };
        const string incoming = "bnd_incoming";
        var subject = SampleSubject();
        var broker = NewBroker(
            subject,
            incoming,
            expectedBindingHash: NyxIdRemoteCapabilityBroker.HashBindingId(existing.Value));
        broker.ResolveBindingOwnerScopeAsync(existing.Value, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OwnerScopeId?>(new OwnerScopeId { Value = "owner-user-1" }));
        var queryPort = Substitute.For<IExternalIdentityBindingQueryPort>();
        queryPort.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(existing));
        var bindingReplaceDispatch = new RecordingCommandDispatch<ReplaceBindingCommand>();

        var result = await InvokeCallbackAsync(
            broker,
            queryPort,
            new RecordingCommandDispatch<CommitBindingCommand>(),
            new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>(),
            bindingReplaceDispatch,
            NewOwnerScopeResolver(null),
            format: "json");

        await broker.Received(1).ResolveBindingOwnerScopeAsync(
            existing.Value,
            Arg.Any<CancellationToken>());
        await broker.DidNotReceive().RevokeBindingByIdAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        bindingReplaceDispatch.Commands.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ReplaceBindingCommand
            {
                ExternalSubject = subject,
                BindingId = incoming,
                ExpectedPreviousBindingId = existing.Value,
                OwnerScopeId = "owner-user-1",
                Reason = "channel_service_access_review",
            });
        using var document = await ExecuteJsonAsync(result, StatusCodes.Status202Accepted);
        document.RootElement.GetProperty("status").GetString().Should().Be("binding_pending");
    }

    [Fact]
    public async Task ExistingBindingChangedDuringAuthorization_RevokesIncomingAndReturnsConflict()
    {
        var subject = SampleSubject();
        const string incoming = "bnd_incoming";
        var broker = NewBroker(
            subject,
            incoming,
            expectedBindingHash: NyxIdRemoteCapabilityBroker.HashBindingId("bnd_stale"));
        var queryPort = Substitute.For<IExternalIdentityBindingQueryPort>();
        queryPort.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(new BindingId { Value = "bnd_current" }));
        var bindingReplaceDispatch = new RecordingCommandDispatch<ReplaceBindingCommand>();

        var result = await InvokeCallbackAsync(
            broker,
            queryPort,
            new RecordingCommandDispatch<CommitBindingCommand>(),
            new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>(),
            bindingReplaceDispatch,
            NewOwnerScopeResolver("owner-user-1"),
            format: "json");

        await broker.Received(1).RevokeBindingByIdAsync(incoming, Arg.Any<CancellationToken>());
        bindingReplaceDispatch.Commands.Should().BeEmpty();
        using var document = await ExecuteJsonAsync(result, StatusCodes.Status409Conflict);
        document.RootElement.GetProperty("error").GetString().Should().Be("binding_changed_during_review");
    }

    [Fact]
    public async Task ReplacementDispatchRejected_RevokesIncomingAndReturns503()
    {
        var existing = new BindingId { Value = "bnd_existing" };
        const string incoming = "bnd_incoming";
        var subject = SampleSubject();
        var broker = NewBroker(
            subject,
            incoming,
            expectedBindingHash: NyxIdRemoteCapabilityBroker.HashBindingId(existing.Value));
        var queryPort = Substitute.For<IExternalIdentityBindingQueryPort>();
        queryPort.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(existing));

        var result = await InvokeCallbackAsync(
            broker,
            queryPort,
            new RecordingCommandDispatch<CommitBindingCommand>(),
            new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>(),
            new RejectingCommandDispatch<ReplaceBindingCommand>(),
            NewOwnerScopeResolver("owner-user-1"),
            format: "json");

        await broker.Received(1).RevokeBindingByIdAsync(incoming, Arg.Any<CancellationToken>());
        using var document = await ExecuteJsonAsync(result, StatusCodes.Status503ServiceUnavailable);
        document.RootElement.GetProperty("error").GetString().Should().Be("actor_dispatch_rejected");
    }

    [Fact]
    public async Task ReplacementDispatchFailure_RevokesIncomingAndReturns503()
    {
        var existing = new BindingId { Value = "bnd_existing" };
        const string incoming = "bnd_incoming";
        var subject = SampleSubject();
        var broker = NewBroker(
            subject,
            incoming,
            expectedBindingHash: NyxIdRemoteCapabilityBroker.HashBindingId(existing.Value));
        var queryPort = Substitute.For<IExternalIdentityBindingQueryPort>();
        queryPort.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(existing));

        var result = await InvokeCallbackAsync(
            broker,
            queryPort,
            new RecordingCommandDispatch<CommitBindingCommand>(),
            new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>(),
            new ThrowingCommandDispatch<ReplaceBindingCommand>(),
            NewOwnerScopeResolver("owner-user-1"),
            format: "json");

        await broker.Received(1).RevokeBindingByIdAsync(incoming, Arg.Any<CancellationToken>());
        using var document = await ExecuteJsonAsync(result, StatusCodes.Status503ServiceUnavailable);
        document.RootElement.GetProperty("error").GetString().Should().Be("actor_dispatch_failed");
    }

    [Fact]
    public async Task DispatchFailure_RevokesIncomingAndReturns503()
    {
        const string incoming = "bnd_incoming";
        var subject = SampleSubject();
        var broker = NewBroker(subject, incoming);
        var queryPort = Substitute.For<IExternalIdentityBindingQueryPort>();
        queryPort.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(null));
        var bindingDispatch = new ThrowingCommandDispatch<CommitBindingCommand>();
        var capabilityDispatch = new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>();

        var result = await InvokeCallbackAsync(
            broker,
            queryPort,
            bindingDispatch,
            capabilityDispatch);

        await broker.Received(1).RevokeBindingByIdAsync(incoming, Arg.Any<CancellationToken>());
        var ctx = NewHttpContext();
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task AcceptedPath_RendersHtml_ContentTypeIsTextHtml()
    {
        var subject = SampleSubject();
        var broker = NewBroker(subject, "bnd_incoming");
        var queryPort = Substitute.For<IExternalIdentityBindingQueryPort>();
        queryPort.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(null));

        var result = await InvokeCallbackAsync(
            broker,
            queryPort,
            new RecordingCommandDispatch<CommitBindingCommand>(),
            new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>());
        var (text, contentType) = await ReadTextWithContentTypeAsync(result);

        contentType.Should().StartWith("text/html");
        text.Should().Contain("<!DOCTYPE html>");
        text.Should().Contain("已受理");
        text.Should().Contain("/whoami");
    }

    [Fact]
    public async Task MissingRequiredService_ReturnsConflictWithoutBindingDispatch()
    {
        var subject = SampleSubject();
        var broker = NewBroker(subject, "unused-binding");
        broker.ExchangeAuthorizationCodeAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<BrokerAuthorizationCodeResult>>(_ => throw new NyxIdRequiredServiceAccessException(
                ["https://nyxid.test/api/v1/proxy/s/aevatar"]));
        var bindingDispatch = new RecordingCommandDispatch<CommitBindingCommand>();

        var result = await InvokeCallbackAsync(
            broker,
            Substitute.For<IExternalIdentityBindingQueryPort>(),
            bindingDispatch,
            new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>(),
            format: "json");

        var context = NewHttpContext();
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        bindingDispatch.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task IssuedBindingMissingRequiredService_RevokesIncomingWithoutDispatch()
    {
        const string incoming = "bnd_incoming";
        var subject = SampleSubject();
        var broker = NewBroker(subject, incoming);
        var capabilityBroker = (INyxIdCapabilityBroker)broker;
        capabilityBroker
            .IssueShortLivedByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<string>(),
                Arg.Any<CapabilityScope>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<CapabilityHandle>>(_ => throw new BindingServiceAccessMismatchException(
                subject,
                ["https://nyxid.test/api/v1/proxy/s/aevatar"]));
        var bindingDispatch = new RecordingCommandDispatch<CommitBindingCommand>();
        var bindingReplaceDispatch = new RecordingCommandDispatch<ReplaceBindingCommand>();
        var capabilityDispatch = new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>();

        var result = await InvokeCallbackAsync(
            broker,
            Substitute.For<IExternalIdentityBindingQueryPort>(),
            bindingDispatch,
            capabilityDispatch,
            bindingReplaceDispatch,
            format: "json");

        await capabilityBroker.Received(1).IssueShortLivedByBindingIdAsync(
            subject,
            incoming,
            Arg.Is<CapabilityScope>(scope => scope.Value == AevatarOAuthClientScopes.Proxy),
            Arg.Any<CancellationToken>());
        await broker.Received(1).RevokeBindingByIdAsync(incoming, Arg.Any<CancellationToken>());
        bindingDispatch.Commands.Should().BeEmpty();
        bindingReplaceDispatch.Commands.Should().BeEmpty();
        capabilityDispatch.Commands.Should().BeEmpty();
        using var document = await ExecuteJsonAsync(result, StatusCodes.Status409Conflict);
        document.RootElement.GetProperty("error").GetString().Should().Be("required_service_access_missing");
    }

    [Fact]
    public async Task IssuedBindingAlreadyRevoked_RevokesIncomingWithoutDispatch()
    {
        const string incoming = "bnd_incoming";
        var subject = SampleSubject();
        var broker = NewBroker(subject, incoming);
        var capabilityBroker = (INyxIdCapabilityBroker)broker;
        capabilityBroker
            .IssueShortLivedByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<string>(),
                Arg.Any<CapabilityScope>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<CapabilityHandle>>(_ => throw new BindingRevokedException(subject));
        var bindingDispatch = new RecordingCommandDispatch<CommitBindingCommand>();
        var capabilityDispatch = new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>();

        var result = await InvokeCallbackAsync(
            broker,
            Substitute.For<IExternalIdentityBindingQueryPort>(),
            bindingDispatch,
            capabilityDispatch,
            format: "json");

        await broker.Received(1).RevokeBindingByIdAsync(incoming, Arg.Any<CancellationToken>());
        bindingDispatch.Commands.Should().BeEmpty();
        capabilityDispatch.Commands.Should().BeEmpty();
        // 503, never 502: Cloudflare replaces origin-generated 502/504 with its
        // own opaque error page, stripping this structured body.
        using var document = await ExecuteJsonAsync(result, StatusCodes.Status503ServiceUnavailable);
        document.RootElement.GetProperty("error").GetString().Should().Be("issued_binding_invalid");
        document.RootElement.GetProperty("status").GetInt32().Should().Be(StatusCodes.Status503ServiceUnavailable);
        document.RootElement.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TokenExchangeFailure_ReturnsStructured503InsteadOf502()
    {
        var subject = SampleSubject();
        var broker = NewBroker(subject, "bnd_incoming");
        broker.ExchangeAuthorizationCodeAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<BrokerAuthorizationCodeResult>>(_ => throw new HttpRequestException("NyxID unreachable"));
        var bindingDispatch = new RecordingCommandDispatch<CommitBindingCommand>();

        var result = await InvokeCallbackAsync(
            broker,
            Substitute.For<IExternalIdentityBindingQueryPort>(),
            bindingDispatch,
            new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>(),
            format: "json");

        bindingDispatch.Commands.Should().BeEmpty();
        using var document = await ExecuteJsonAsync(result, StatusCodes.Status503ServiceUnavailable);
        document.RootElement.GetProperty("error").GetString().Should().Be("token_exchange_failed");
        document.RootElement.GetProperty("status").GetInt32().Should().Be(StatusCodes.Status503ServiceUnavailable);
        document.RootElement.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TokenExchangeRejectedAuthorizationCode_ReturnsStructured400()
    {
        var subject = SampleSubject();
        var broker = NewBroker(subject, "bnd_incoming");
        broker.ExchangeAuthorizationCodeAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<BrokerAuthorizationCodeResult>>(_ => throw new HttpRequestException(
                "invalid_grant",
                null,
                System.Net.HttpStatusCode.BadRequest));
        var bindingDispatch = new RecordingCommandDispatch<CommitBindingCommand>();

        var result = await InvokeCallbackAsync(
            broker,
            Substitute.For<IExternalIdentityBindingQueryPort>(),
            bindingDispatch,
            new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>(),
            format: "json");

        bindingDispatch.Commands.Should().BeEmpty();
        using var document = await ExecuteJsonAsync(result, StatusCodes.Status400BadRequest);
        document.RootElement.GetProperty("error").GetString().Should().Be("authorization_code_rejected");
    }

    [Fact]
    public async Task OwnerScopeMissing_RevokesIncomingAndReturnsStructured503()
    {
        const string incoming = "bnd_incoming";
        var subject = SampleSubject();
        var broker = NewBroker(subject, incoming, ownerScopeId: "");
        var queryPort = Substitute.For<IExternalIdentityBindingQueryPort>();
        queryPort.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(null));
        var bindingDispatch = new RecordingCommandDispatch<CommitBindingCommand>();

        var result = await InvokeCallbackAsync(
            broker,
            queryPort,
            bindingDispatch,
            new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>(),
            format: "json");

        await broker.Received(1).RevokeBindingByIdAsync(incoming, Arg.Any<CancellationToken>());
        bindingDispatch.Commands.Should().BeEmpty();
        using var document = await ExecuteJsonAsync(result, StatusCodes.Status503ServiceUnavailable);
        document.RootElement.GetProperty("error").GetString().Should().Be("owner_scope_missing");
        document.RootElement.GetProperty("status").GetInt32().Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task IssuedBindingProbeFailure_RevokesIncomingWithoutDispatch()
    {
        const string incoming = "bnd_incoming";
        var subject = SampleSubject();
        var broker = NewBroker(subject, incoming);
        var capabilityBroker = (INyxIdCapabilityBroker)broker;
        capabilityBroker
            .IssueShortLivedByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<string>(),
                Arg.Any<CapabilityScope>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<CapabilityHandle>>(_ => throw new HttpRequestException("NyxID unavailable"));
        var bindingDispatch = new RecordingCommandDispatch<CommitBindingCommand>();
        var capabilityDispatch = new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>();

        var result = await InvokeCallbackAsync(
            broker,
            Substitute.For<IExternalIdentityBindingQueryPort>(),
            bindingDispatch,
            capabilityDispatch,
            format: "json");

        await broker.Received(1).RevokeBindingByIdAsync(incoming, Arg.Any<CancellationToken>());
        bindingDispatch.Commands.Should().BeEmpty();
        capabilityDispatch.Commands.Should().BeEmpty();
        using var document = await ExecuteJsonAsync(result, StatusCodes.Status503ServiceUnavailable);
        document.RootElement.GetProperty("error").GetString().Should().Be("issued_binding_probe_failed");
    }

    [Fact]
    public async Task BindingGrantUpdated_KeepsExistingBindingAndReturnsSuccess()
    {
        var subject = SampleSubject();
        var existing = new BindingId { Value = "bnd_existing" };
        var expectedHash = NyxIdRemoteCapabilityBroker.HashBindingId(existing.Value);
        var broker = NewBroker(
            subject,
            bindingId: null,
            bindingUpdated: true,
            expectedBindingHash: expectedHash);
        var queryPort = Substitute.For<IExternalIdentityBindingQueryPort>();
        queryPort.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(existing));
        var bindingDispatch = new RecordingCommandDispatch<CommitBindingCommand>();
        var capabilityDispatch = new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>();

        var result = await InvokeCallbackAsync(
            broker,
            queryPort,
            bindingDispatch,
            capabilityDispatch,
            format: "json");

        var context = NewHttpContext();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = JsonDocument.Parse(await new StreamReader(context.Response.Body).ReadToEndAsync());
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        document.RootElement.GetProperty("status").GetString().Should().Be("binding_grant_updated");
        document.RootElement.GetProperty("binding_id_changed").GetBoolean().Should().BeFalse();
        bindingDispatch.Commands.Should().BeEmpty();
        capabilityDispatch.Commands.Should().BeEmpty();
        await broker.DidNotReceive().RevokeBindingByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BindingGrantUpdated_MissingRequiredServiceReturnsConflict()
    {
        var subject = SampleSubject();
        var existing = new BindingId { Value = "bnd_existing" };
        var broker = NewBroker(
            subject,
            bindingId: null,
            bindingUpdated: true,
            expectedBindingHash: NyxIdRemoteCapabilityBroker.HashBindingId(existing.Value));
        var capabilityBroker = (INyxIdCapabilityBroker)broker;
        capabilityBroker
            .IssueShortLivedByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<string>(),
                Arg.Any<CapabilityScope>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<CapabilityHandle>>(_ => throw new BindingServiceAccessMismatchException(
                subject,
                ["https://nyxid.test/api/v1/proxy/s/aevatar"]));
        var queryPort = Substitute.For<IExternalIdentityBindingQueryPort>();
        queryPort.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(existing));
        var bindingDispatch = new RecordingCommandDispatch<CommitBindingCommand>();
        var capabilityDispatch = new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>();

        var result = await InvokeCallbackAsync(
            broker,
            queryPort,
            bindingDispatch,
            capabilityDispatch,
            format: "json");

        await capabilityBroker.Received(1).IssueShortLivedByBindingIdAsync(
            subject,
            existing.Value,
            Arg.Is<CapabilityScope>(scope => scope.Value == AevatarOAuthClientScopes.Proxy),
            Arg.Any<CancellationToken>());
        await broker.DidNotReceive().RevokeBindingByIdAsync(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        bindingDispatch.Commands.Should().BeEmpty();
        capabilityDispatch.Commands.Should().BeEmpty();
        using var document = await ExecuteJsonAsync(result, StatusCodes.Status409Conflict);
        document.RootElement.GetProperty("error").GetString().Should().Be("required_service_access_missing");
    }

    [Fact]
    public async Task BindingGrantUpdated_RejectsWhenLocalBindingChangedDuringReview()
    {
        var subject = SampleSubject();
        var broker = NewBroker(
            subject,
            bindingId: null,
            bindingUpdated: true,
            expectedBindingHash: NyxIdRemoteCapabilityBroker.HashBindingId("bnd_old"));
        var queryPort = Substitute.For<IExternalIdentityBindingQueryPort>();
        queryPort.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BindingId?>(new BindingId { Value = "bnd_new" }));

        var result = await InvokeCallbackAsync(
            broker,
            queryPort,
            new RecordingCommandDispatch<CommitBindingCommand>(),
            new RecordingCommandDispatch<ObserveBrokerCapabilityCommand>(),
            format: "json");

        var context = NewHttpContext();
        await result.ExecuteAsync(context);
        context.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    // Refactor (iter27/cluster-028-identity-oauth-endpoint):
    //   Old pattern: IdentityOAuthEndpoints + AevatarOAuthClientBootstrapService 直接构造 EventEnvelope 投递,然后在 endpoint 内同步等 projection readiness / rebuild observation / readmodel polling (3-15s timeout + 50-250ms polling),违反 ACK 协议 + query-time projection priming
    //   New principle: 加 module-local CQRS dispatch adapters(ChannelIdentityOAuthCommandDispatch);endpoint inject typed ICommandDispatchService<...>,返回 accepted/pending + status URL,不再等 projection;删 IProjectionReadinessPort/ExternalIdentityBindingProjectionPort/AevatarOAuthClientProjectionPort/AevatarOAuthClientRebuildCoordinator/ProjectionWaitTimeout 等
    private static async Task<IResult> InvokeCallbackAsync(
        INyxIdBrokerCallbackClient broker,
        IExternalIdentityBindingQueryPort queryPort,
        ICommandDispatchService<CommitBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> bindingDispatch,
        ICommandDispatchService<ObserveBrokerCapabilityCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError> capabilityDispatch,
        ICommandDispatchService<ReplaceBindingCommand, ChannelIdentityOAuthAcceptedReceipt, ChannelIdentityOAuthDispatchError>? bindingReplaceDispatch = null,
        IOwnerScopeResolver? ownerScopeResolver = null,
        string? format = null)
    {
        bindingReplaceDispatch ??= new RecordingCommandDispatch<ReplaceBindingCommand>();
        ownerScopeResolver ??= NewOwnerScopeResolver(null);
        return await IdentityOAuthEndpoints.HandleNyxIdOAuthCallbackAsync(
            code: "auth-code",
            state: "state-token",
            error: null,
            format: format,
            brokerCallback: broker,
            capabilityBroker: (INyxIdCapabilityBroker)broker,
            queryPort: queryPort,
            bindingDispatch: bindingDispatch,
            bindingReplaceDispatch: bindingReplaceDispatch,
            ownerScopeResolver: ownerScopeResolver,
            brokerCapabilityDispatch: capabilityDispatch,
            loggerFactory: NullLoggerFactory.Instance,
            ct: CancellationToken.None);
    }

    private static ExternalSubjectRef SampleSubject() => new()
    {
        Platform = "lark",
        Tenant = "ou_tenant_x",
        ExternalUserId = "ou_user_y",
    };

    private static INyxIdBrokerCallbackClient NewBroker(
        ExternalSubjectRef subject,
        string? bindingId,
        bool bindingUpdated = false,
        string? expectedBindingHash = null,
        string ownerScopeId = "owner-user-1")
    {
        var broker = Substitute.For<INyxIdBrokerCallbackClient, INyxIdCapabilityBroker>();
        broker.TryDecodeStateTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CallbackStateDecode.Ok(
                correlationId: "correlation-1",
                subject: subject,
                verifier: "pkce-verifier",
                expectedBindingHash: expectedBindingHash)));
        broker.ExchangeAuthorizationCodeAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new BrokerAuthorizationCodeResult(bindingId, CreateIdToken(new { uid = ownerScopeId }), AccessToken: null)
            {
                BindingUpdated = bindingUpdated,
            }));
        ((INyxIdCapabilityBroker)broker)
            .IssueShortLivedByBindingIdAsync(
                Arg.Any<ExternalSubjectRef>(),
                Arg.Any<string>(),
                Arg.Any<CapabilityScope>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CapabilityHandle
            {
                AccessToken = "short-lived-capability",
                Scope = AevatarOAuthClientScopes.Proxy,
                ExpiresAtUnix = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
            }));
        return broker;
    }

    private static IOwnerScopeResolver NewOwnerScopeResolver(string? ownerScopeId)
    {
        var resolver = Substitute.For<IOwnerScopeResolver>();
        resolver.ResolveAsync(Arg.Any<ExternalSubjectRef>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(string.IsNullOrWhiteSpace(ownerScopeId)
                ? null
                : new OwnerScopeId { Value = ownerScopeId }));
        return resolver;
    }

    private static async Task<JsonDocument> ExecuteJsonAsync(IResult result, int expectedStatusCode)
    {
        var context = NewHttpContext();
        await result.ExecuteAsync(context);
        context.Response.StatusCode.Should().Be(expectedStatusCode);
        context.Response.Body.Position = 0;
        return JsonDocument.Parse(await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync());
    }

    private static string CreateIdToken(object payload)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "none" }));
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        return $"{header}.{body}.";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<(string Text, string? ContentType)> ReadTextWithContentTypeAsync(IResult result)
    {
        var context = NewHttpContext();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        return (text, context.Response.ContentType);
    }

    private static HttpContext NewHttpContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        return new DefaultHttpContext
        {
            RequestServices = provider,
            Response =
            {
                Body = new MemoryStream(),
            },
        };
    }

}
