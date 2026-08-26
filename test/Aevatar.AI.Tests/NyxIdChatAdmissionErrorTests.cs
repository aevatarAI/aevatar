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
    [InlineData(NyxIdChatStartError.AttachmentNotFound, "not_found")]
    [InlineData(NyxIdChatStartError.AttachmentAccessDenied, "access_denied")]
    [InlineData(NyxIdChatStartError.AttachmentUnsupportedKind, "unsupported_kind")]
    [InlineData(NyxIdChatStartError.AttachmentOverLimit, "over_limit")]
    [InlineData(NyxIdChatStartError.AttachmentPinnedRevisionUnavailable, "pinned_revision_unavailable")]
    [InlineData(NyxIdChatStartError.AttachmentInvalidRequest, "invalid_request")]
    [InlineData(NyxIdChatStartError.AttachmentInactive, "inactive")]
    [InlineData(NyxIdChatStartError.AttachmentReadModelUnavailable, "read_model_unavailable")]
    public async Task HandleStreamMessageAsync_ShouldExposeTypedAttachmentAdmissionReason(
        NyxIdChatStartError startError,
        string expectedReason)
    {
        var context = CreateAuthorizedStreamContext();
        var interactionService = new StubNyxIdChatInteractionService<NyxIdChatCommand>
        {
            Failure = startError,
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

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("ATTACHMENT_ADMISSION_DENIED")
            .And.Contain($"\"reason\":\"{expectedReason}\"")
            .And.NotContain("ADMISSION_UNAVAILABLE");
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

    [Theory]
    [InlineData(NyxIdChatLifecycleCommandStartError.AttachmentNotFound, NyxIdChatStartError.AttachmentNotFound)]
    [InlineData(NyxIdChatLifecycleCommandStartError.AttachmentAccessDenied, NyxIdChatStartError.AttachmentAccessDenied)]
    [InlineData(NyxIdChatLifecycleCommandStartError.AttachmentUnsupportedKind, NyxIdChatStartError.AttachmentUnsupportedKind)]
    [InlineData(NyxIdChatLifecycleCommandStartError.AttachmentOverLimit, NyxIdChatStartError.AttachmentOverLimit)]
    [InlineData(NyxIdChatLifecycleCommandStartError.AttachmentPinnedRevisionUnavailable, NyxIdChatStartError.AttachmentPinnedRevisionUnavailable)]
    [InlineData(NyxIdChatLifecycleCommandStartError.AttachmentInvalidRequest, NyxIdChatStartError.AttachmentInvalidRequest)]
    [InlineData(NyxIdChatLifecycleCommandStartError.AttachmentInactive, NyxIdChatStartError.AttachmentInactive)]
    [InlineData(NyxIdChatLifecycleCommandStartError.AttachmentReadModelUnavailable, NyxIdChatStartError.AttachmentReadModelUnavailable)]
    public async Task ChatCommandTargetResolver_ShouldPreserveAttachmentAdmissionReason(
        NyxIdChatLifecycleCommandStartError createError,
        NyxIdChatStartError expectedError)
    {
        var resolver = new NyxIdChatCommandTargetResolver(
            new StubActorRuntime(),
            new StubNyxIdChatSessionProjectionPort(),
            () => new FailingConversationCreateTargetResolver(createError));

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
        result.Error.Should().Be(expectedError);
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
