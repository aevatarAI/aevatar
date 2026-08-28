using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.Tests;

public partial class NyxIdChatEndpointsCoverageTests
{
    [Fact]
    public async Task HandleStreamMessageAsync_ShouldReportAdmissionFailureSeparatelyFromProjectionFailure()
    {
        var context = CreateAuthorizedStreamContext();
        var interactionService = new StubNyxIdChatInteractionService<NyxIdChatCommand>
        {
            Failure = NyxIdChatStartError.AdmissionUnavailable,
        };

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdChatStreamRequest("hello", Type: "text"),
            new StubGAgentActorStore(),
            interactionService,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("RUN_STARTED");
        body.Should().Contain("RUN_ERROR");
        body.Should().Contain("ADMISSION_UNAVAILABLE");
        body.Should().Contain("NyxID chat admission is unavailable for the requested Agent Profile or route.");
        body.Should().NotContain("PROJECTION_UNAVAILABLE");
    }

    [Theory]
    [InlineData(NyxIdChatLifecycleCommandStartError.AdmissionUnavailable)]
    [InlineData(NyxIdChatLifecycleCommandStartError.RouteRejected)]
    [InlineData(NyxIdChatLifecycleCommandStartError.AccessDenied)]
    public async Task ChatCommandTargetResolver_ShouldMapCreateAdmissionFailuresTruthfully(
        NyxIdChatLifecycleCommandStartError createError)
    {
        var createResolver = new FailingConversationCreateTargetResolver(createError);
        var resolver = new NyxIdChatCommandTargetResolver(
            new StubActorRuntime(),
            new StubNyxIdChatSessionProjectionPort(),
            () => createResolver);

        var result = await resolver.ResolveAsync(new NyxIdChatCommand(
            "actor-1",
            "scope-a",
            "hello",
            "turn-1",
            "access-token",
            null,
            null,
            CreateIfMissing: true));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(NyxIdChatStartError.AdmissionUnavailable);
    }

    // Issue #3543: attachment admission failures keep their typed identity all
    // the way to the wire instead of collapsing into AdmissionUnavailable.
    [Theory]
    [InlineData(NyxIdChatLifecycleCommandStartError.AttachmentSetInvalid, NyxIdChatStartError.AttachmentSetInvalid)]
    [InlineData(NyxIdChatLifecycleCommandStartError.AttachmentAdmissionUnavailable, NyxIdChatStartError.AttachmentAdmissionUnavailable)]
    [InlineData(NyxIdChatLifecycleCommandStartError.AttachmentNotFound, NyxIdChatStartError.AttachmentNotFound)]
    [InlineData(NyxIdChatLifecycleCommandStartError.AttachmentKindUnsupported, NyxIdChatStartError.AttachmentKindUnsupported)]
    [InlineData(NyxIdChatLifecycleCommandStartError.AttachmentAccessDenied, NyxIdChatStartError.AttachmentAccessDenied)]
    [InlineData(NyxIdChatLifecycleCommandStartError.AttachmentRevisionUnavailable, NyxIdChatStartError.AttachmentRevisionUnavailable)]
    public async Task ChatCommandTargetResolver_ShouldPreserveTypedAttachmentAdmissionFailures(
        NyxIdChatLifecycleCommandStartError createError,
        NyxIdChatStartError expected)
    {
        var createResolver = new FailingConversationCreateTargetResolver(createError);
        var resolver = new NyxIdChatCommandTargetResolver(
            new StubActorRuntime(),
            new StubNyxIdChatSessionProjectionPort(),
            () => createResolver);

        var result = await resolver.ResolveAsync(new NyxIdChatCommand(
            "actor-1",
            "scope-a",
            "hello",
            "turn-1",
            "access-token",
            null,
            null,
            CreateIfMissing: true));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be(expected);
    }

    [Theory]
    [InlineData(NyxIdChatStartError.AttachmentSetInvalid, "ATTACHMENT_SET_INVALID")]
    [InlineData(NyxIdChatStartError.AttachmentAdmissionUnavailable, "ATTACHMENT_ADMISSION_UNAVAILABLE")]
    [InlineData(NyxIdChatStartError.AttachmentNotFound, "ATTACHMENT_NOT_FOUND")]
    [InlineData(NyxIdChatStartError.AttachmentKindUnsupported, "ATTACHMENT_KIND_UNSUPPORTED")]
    [InlineData(NyxIdChatStartError.AttachmentAccessDenied, "ATTACHMENT_ACCESS_DENIED")]
    [InlineData(NyxIdChatStartError.AttachmentRevisionUnavailable, "ATTACHMENT_REVISION_UNAVAILABLE")]
    public async Task HandleStreamMessageAsync_ShouldReportTypedAttachmentAdmissionFailure(
        NyxIdChatStartError failure,
        string expectedCode)
    {
        var context = CreateAuthorizedStreamContext();
        var interactionService = new StubNyxIdChatInteractionService<NyxIdChatCommand>
        {
            Failure = failure,
        };

        await InvokeTaskAsync(
            "HandleStreamMessageAsync",
            context,
            "scope-a",
            "actor-1",
            new NyxIdChatEndpoints.NyxIdChatStreamRequest("hello", Type: "text"),
            new StubGAgentActorStore(),
            interactionService,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("RUN_STARTED");
        body.Should().Contain("RUN_ERROR");
        body.Should().Contain(expectedCode);
        body.Should().NotContain("\"ADMISSION_UNAVAILABLE\"");
    }

    private sealed class FailingConversationCreateTargetResolver(
        NyxIdChatLifecycleCommandStartError error)
        : ICommandTargetResolver<
            NyxIdChatConversationCreateCommand,
            NyxIdChatConversationCreateCommandTarget,
            NyxIdChatLifecycleCommandStartError>
    {
        public Task<CommandTargetResolution<
            NyxIdChatConversationCreateCommandTarget,
            NyxIdChatLifecycleCommandStartError>> ResolveAsync(
            NyxIdChatConversationCreateCommand command,
            CancellationToken ct = default)
        {
            _ = command;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(CommandTargetResolution<
                NyxIdChatConversationCreateCommandTarget,
                NyxIdChatLifecycleCommandStartError>.Failure(error));
        }
    }
}
