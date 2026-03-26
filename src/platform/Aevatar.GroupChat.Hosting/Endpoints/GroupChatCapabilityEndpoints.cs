using Aevatar.GroupChat.Abstractions;
using Aevatar.GroupChat.Abstractions.Commands;
using Aevatar.GroupChat.Abstractions.Queries;
using Aevatar.GroupChat.Abstractions.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.GroupChat.Hosting.Endpoints;

public static class GroupChatCapabilityEndpoints
{
    public static IEndpointRouteBuilder MapGroupChatCapabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/group-chat/groups/{groupId}/threads").WithTags("GroupChat");

        group.MapPost(string.Empty, HandleCreateThreadAsync)
            .Produces<GroupCommandAcceptedReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapPost("/{threadId}/messages", HandlePostMessageAsync)
            .Produces<GroupCommandAcceptedReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest);
        group.MapGet("/{threadId}", HandleGetThreadAsync)
            .Produces<GroupThreadSnapshot>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        var sources = app.MapGroup("/api/group-chat/sources").WithTags("GroupChat");
        sources.MapPost(string.Empty, HandleRegisterSourceAsync)
            .Produces<GroupCommandAcceptedReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest);
        sources.MapPost("/{sourceId}:trust", HandleUpdateSourceTrustAsync)
            .Produces<GroupCommandAcceptedReceipt>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest);
        sources.MapGet("/{sourceId}", HandleGetSourceAsync)
            .Produces<GroupSourceCatalogSnapshot>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
        return app;
    }

    private static async Task<IResult> HandleCreateThreadAsync(
        string groupId,
        CreateGroupThreadHttpRequest request,
        IGroupThreadCommandPort commandPort,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(groupId))
            return ValidationError("GROUP_ID_REQUIRED", "groupId is required.");
        if (string.IsNullOrWhiteSpace(request.ThreadId))
            return ValidationError("THREAD_ID_REQUIRED", "threadId is required.");

        var receipt = await commandPort.CreateThreadAsync(
            new CreateGroupThreadCommand
            {
                GroupId = groupId.Trim(),
                ThreadId = request.ThreadId.Trim(),
                DisplayName = request.DisplayName?.Trim() ?? string.Empty,
                ParticipantAgentIds = { request.ParticipantAgentIds ?? [] },
                ParticipantRuntimeBindingEntries =
                {
                    (request.ParticipantRuntimeBindings ?? []).Select(ToRuntimeBinding)
                },
            },
            ct);
        return Results.Accepted($"/api/group-chat/groups/{groupId}/threads/{request.ThreadId}", receipt);
    }

    private static async Task<IResult> HandlePostMessageAsync(
        string groupId,
        string threadId,
        PostUserMessageHttpRequest request,
        IGroupThreadCommandPort commandPort,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(groupId))
            return ValidationError("GROUP_ID_REQUIRED", "groupId is required.");
        if (string.IsNullOrWhiteSpace(threadId))
            return ValidationError("THREAD_ID_REQUIRED", "threadId is required.");
        if (string.IsNullOrWhiteSpace(request.MessageId))
            return ValidationError("MESSAGE_ID_REQUIRED", "messageId is required.");
        if (string.IsNullOrWhiteSpace(request.SenderUserId))
            return ValidationError("SENDER_USER_ID_REQUIRED", "senderUserId is required.");
        if (string.IsNullOrWhiteSpace(request.Text))
            return ValidationError("TEXT_REQUIRED", "text is required.");

        var receipt = await commandPort.PostUserMessageAsync(
            new PostUserMessageCommand
            {
                GroupId = groupId.Trim(),
                ThreadId = threadId.Trim(),
                MessageId = request.MessageId.Trim(),
                SenderUserId = request.SenderUserId.Trim(),
                Text = request.Text.Trim(),
                TopicId = request.TopicId?.Trim() ?? string.Empty,
                SignalKind = request.SignalKind ?? GroupSignalKind.Unspecified,
                SourceRefs =
                {
                    (request.SourceRefs ?? []).Select(ToSourceRef)
                },
                EvidenceRefs =
                {
                    (request.EvidenceRefs ?? []).Select(ToEvidenceRef)
                },
                DerivedFromSignalIds = { request.DerivedFromSignalIds ?? [] },
                DirectHintAgentIds = { ResolveDirectHintAgentIds(request) },
            },
            ct);
        return Results.Accepted($"/api/group-chat/groups/{groupId}/threads/{threadId}", receipt);
    }

    private static async Task<IResult> HandleGetThreadAsync(
        string groupId,
        string threadId,
        IGroupThreadQueryPort queryPort,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(groupId))
            return ValidationError("GROUP_ID_REQUIRED", "groupId is required.");
        if (string.IsNullOrWhiteSpace(threadId))
            return ValidationError("THREAD_ID_REQUIRED", "threadId is required.");

        var snapshot = await queryPort.GetThreadAsync(groupId.Trim(), threadId.Trim(), ct);
        return snapshot == null ? Results.NotFound() : Results.Ok(snapshot);
    }

    private static async Task<IResult> HandleRegisterSourceAsync(
        RegisterGroupSourceHttpRequest request,
        ISourceRegistryCommandPort commandPort,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SourceId))
            return ValidationError("SOURCE_ID_REQUIRED", "sourceId is required.");
        if (request.SourceKind is null or GroupSourceKind.Unspecified)
            return ValidationError("SOURCE_KIND_REQUIRED", "sourceKind is required.");
        if (string.IsNullOrWhiteSpace(request.CanonicalLocator))
            return ValidationError("CANONICAL_LOCATOR_REQUIRED", "canonicalLocator is required.");

        var receipt = await commandPort.RegisterSourceAsync(
            new RegisterGroupSourceCommand
            {
                SourceId = request.SourceId.Trim(),
                SourceKind = request.SourceKind.Value,
                CanonicalLocator = request.CanonicalLocator.Trim(),
            },
            ct);
        return Results.Accepted($"/api/group-chat/sources/{request.SourceId}", receipt);
    }

    private static async Task<IResult> HandleUpdateSourceTrustAsync(
        string sourceId,
        UpdateGroupSourceTrustHttpRequest request,
        ISourceRegistryCommandPort commandPort,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            return ValidationError("SOURCE_ID_REQUIRED", "sourceId is required.");

        var receipt = await commandPort.UpdateSourceTrustAsync(
            new UpdateGroupSourceTrustCommand
            {
                SourceId = sourceId.Trim(),
                AuthorityClass = request.AuthorityClass ?? GroupSourceAuthorityClass.Unspecified,
                VerificationStatus = request.VerificationStatus ?? GroupSourceVerificationStatus.Unspecified,
            },
            ct);
        return Results.Accepted($"/api/group-chat/sources/{sourceId}", receipt);
    }

    private static async Task<IResult> HandleGetSourceAsync(
        string sourceId,
        ISourceRegistryQueryPort queryPort,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            return ValidationError("SOURCE_ID_REQUIRED", "sourceId is required.");

        var snapshot = await queryPort.GetSourceAsync(sourceId.Trim(), ct);
        return snapshot == null ? Results.NotFound() : Results.Ok(snapshot);
    }

    private static GroupParticipantRuntimeBinding ToRuntimeBinding(GroupParticipantRuntimeBindingHttpRequest request) =>
        new()
        {
            ParticipantAgentId = request.ParticipantAgentId?.Trim() ?? string.Empty,
            TenantId = request.TenantId?.Trim() ?? string.Empty,
            AppId = request.AppId?.Trim() ?? string.Empty,
            Namespace = request.Namespace?.Trim() ?? string.Empty,
            ServiceId = request.ServiceId?.Trim() ?? string.Empty,
            EndpointId = request.EndpointId?.Trim() ?? string.Empty,
            ScopeId = request.ScopeId?.Trim() ?? string.Empty,
        };

    private static GroupSourceRef ToSourceRef(GroupSourceRefHttpRequest request) =>
        new()
        {
            SourceKind = request.SourceKind ?? GroupSourceKind.Unspecified,
            Locator = request.Locator?.Trim() ?? string.Empty,
            SourceId = request.SourceId?.Trim() ?? string.Empty,
        };

    private static GroupEvidenceRef ToEvidenceRef(GroupEvidenceRefHttpRequest request) =>
        new()
        {
            EvidenceId = request.EvidenceId?.Trim() ?? string.Empty,
            SourceLocator = request.SourceLocator?.Trim() ?? string.Empty,
            Locator = request.Locator?.Trim() ?? string.Empty,
            ExcerptSummary = request.ExcerptSummary?.Trim() ?? string.Empty,
            SourceId = request.SourceId?.Trim() ?? string.Empty,
        };

    private static IEnumerable<string> ResolveDirectHintAgentIds(PostUserMessageHttpRequest request) =>
        request.DirectHintAgentIds ?? request.MentionedAgentIds ?? [];

    private static IResult ValidationError(string code, string message) =>
        Results.BadRequest(new
        {
            code,
            message,
        });
}

public sealed record CreateGroupThreadHttpRequest(
    string? ThreadId,
    string? DisplayName,
    IReadOnlyList<string>? ParticipantAgentIds,
    IReadOnlyList<GroupParticipantRuntimeBindingHttpRequest>? ParticipantRuntimeBindings);

public sealed record GroupParticipantRuntimeBindingHttpRequest(
    string? ParticipantAgentId,
    string? TenantId,
    string? AppId,
    string? Namespace,
    string? ServiceId,
    string? EndpointId,
    string? ScopeId);

public sealed record PostUserMessageHttpRequest(
    string? MessageId,
    string? SenderUserId,
    string? Text,
    string? TopicId,
    GroupSignalKind? SignalKind,
    IReadOnlyList<GroupSourceRefHttpRequest>? SourceRefs,
    IReadOnlyList<GroupEvidenceRefHttpRequest>? EvidenceRefs,
    IReadOnlyList<string>? DerivedFromSignalIds,
    IReadOnlyList<string>? DirectHintAgentIds,
    IReadOnlyList<string>? MentionedAgentIds);

public sealed class GroupSourceRefHttpRequest
{
    public GroupSourceKind? SourceKind { get; init; }

    public string? Locator { get; init; }

    public string? SourceId { get; init; }
}

public sealed class GroupEvidenceRefHttpRequest
{
    public string? EvidenceId { get; init; }

    public string? SourceLocator { get; init; }

    public string? Locator { get; init; }

    public string? ExcerptSummary { get; init; }

    public string? SourceId { get; init; }
}

public sealed record RegisterGroupSourceHttpRequest(
    string? SourceId,
    GroupSourceKind? SourceKind,
    string? CanonicalLocator);

public sealed record UpdateGroupSourceTrustHttpRequest(
    GroupSourceAuthorityClass? AuthorityClass,
    GroupSourceVerificationStatus? VerificationStatus);
