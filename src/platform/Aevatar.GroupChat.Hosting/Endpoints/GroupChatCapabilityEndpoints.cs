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
        group.MapPost("/{threadId}/agent-messages", HandlePostAgentMessageAsync)
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

    private static async Task<IResult> HandlePostAgentMessageAsync(
        string groupId,
        string threadId,
        PostAgentMessageHttpRequest request,
        IGroupThreadCommandPort commandPort,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(groupId))
            return ValidationError("GROUP_ID_REQUIRED", "groupId is required.");
        if (string.IsNullOrWhiteSpace(threadId))
            return ValidationError("THREAD_ID_REQUIRED", "threadId is required.");
        if (string.IsNullOrWhiteSpace(request.MessageId))
            return ValidationError("MESSAGE_ID_REQUIRED", "messageId is required.");
        if (string.IsNullOrWhiteSpace(request.ParticipantAgentId))
            return ValidationError("PARTICIPANT_AGENT_ID_REQUIRED", "participantAgentId is required.");
        if (string.IsNullOrWhiteSpace(request.Text))
            return ValidationError("TEXT_REQUIRED", "text is required.");

        var receipt = await commandPort.AppendAgentMessageAsync(
            new AppendAgentMessageCommand
            {
                GroupId = groupId.Trim(),
                ThreadId = threadId.Trim(),
                MessageId = request.MessageId.Trim(),
                ParticipantAgentId = request.ParticipantAgentId.Trim(),
                Text = request.Text.Trim(),
                ReplyToMessageId = request.ReplyToMessageId?.Trim() ?? string.Empty,
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

    private static GroupParticipantRuntimeBinding ToRuntimeBinding(GroupParticipantRuntimeBindingHttpRequest request)
    {
        var binding = new GroupParticipantRuntimeBinding
        {
            ParticipantAgentId = request.ParticipantAgentId?.Trim() ?? string.Empty,
            TargetKind = request.TargetKind ?? GroupParticipantRuntimeTargetKind.Unspecified,
        };

        if (request.ServiceTarget != null)
            binding.ServiceTarget = ToServiceTarget(request.ServiceTarget);
        else if (request.WorkflowTarget != null)
            binding.WorkflowTarget = ToWorkflowTarget(request.WorkflowTarget);
        else if (request.ScriptTarget != null)
            binding.ScriptTarget = ToScriptTarget(request.ScriptTarget);
        else if (request.LocalTarget != null)
            binding.LocalTarget = ToLocalTarget(request.LocalTarget);

        return binding;
    }

    private static GroupServiceRuntimeTarget? ToServiceTarget(GroupServiceRuntimeTargetHttpRequest? request) =>
        request == null
            ? null
            : new GroupServiceRuntimeTarget
            {
                TenantId = request.TenantId?.Trim() ?? string.Empty,
                AppId = request.AppId?.Trim() ?? string.Empty,
                Namespace = request.Namespace?.Trim() ?? string.Empty,
                ServiceId = request.ServiceId?.Trim() ?? string.Empty,
                EndpointId = request.EndpointId?.Trim() ?? string.Empty,
                ScopeId = request.ScopeId?.Trim() ?? string.Empty,
            };

    private static GroupWorkflowRuntimeTarget? ToWorkflowTarget(GroupWorkflowRuntimeTargetHttpRequest? request) =>
        request == null
            ? null
            : new GroupWorkflowRuntimeTarget
            {
                DefinitionActorId = request.DefinitionActorId?.Trim() ?? string.Empty,
                WorkflowName = request.WorkflowName?.Trim() ?? string.Empty,
                ScopeId = request.ScopeId?.Trim() ?? string.Empty,
            };

    private static GroupScriptRuntimeTarget? ToScriptTarget(GroupScriptRuntimeTargetHttpRequest? request) =>
        request == null
            ? null
            : new GroupScriptRuntimeTarget
            {
                DefinitionActorId = request.DefinitionActorId?.Trim() ?? string.Empty,
                Revision = request.Revision?.Trim() ?? string.Empty,
                RuntimeActorId = request.RuntimeActorId?.Trim() ?? string.Empty,
                RequestedEventType = request.RequestedEventType?.Trim() ?? string.Empty,
                ScopeId = request.ScopeId?.Trim() ?? string.Empty,
            };

    private static GroupLocalRuntimeTarget? ToLocalTarget(GroupLocalRuntimeTargetHttpRequest? request) =>
        request == null
            ? null
            : new GroupLocalRuntimeTarget
            {
                Provider = request.Provider?.Trim() ?? string.Empty,
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

    private static IEnumerable<string> ResolveDirectHintAgentIds(PostAgentMessageHttpRequest request) =>
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

public sealed class GroupParticipantRuntimeBindingHttpRequest
{
    public string? ParticipantAgentId { get; init; }

    public GroupParticipantRuntimeTargetKind? TargetKind { get; init; }

    public GroupServiceRuntimeTargetHttpRequest? ServiceTarget { get; init; }

    public GroupWorkflowRuntimeTargetHttpRequest? WorkflowTarget { get; init; }

    public GroupScriptRuntimeTargetHttpRequest? ScriptTarget { get; init; }

    public GroupLocalRuntimeTargetHttpRequest? LocalTarget { get; init; }
}

public sealed class GroupServiceRuntimeTargetHttpRequest
{
    public string? TenantId { get; init; }

    public string? AppId { get; init; }

    public string? Namespace { get; init; }

    public string? ServiceId { get; init; }

    public string? EndpointId { get; init; }

    public string? ScopeId { get; init; }
}

public sealed class GroupWorkflowRuntimeTargetHttpRequest
{
    public string? DefinitionActorId { get; init; }

    public string? WorkflowName { get; init; }

    public string? ScopeId { get; init; }
}

public sealed class GroupScriptRuntimeTargetHttpRequest
{
    public string? DefinitionActorId { get; init; }

    public string? Revision { get; init; }

    public string? RuntimeActorId { get; init; }

    public string? RequestedEventType { get; init; }

    public string? ScopeId { get; init; }
}

public sealed class GroupLocalRuntimeTargetHttpRequest
{
    public string? Provider { get; init; }
}

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

public sealed record PostAgentMessageHttpRequest(
    string? MessageId,
    string? ParticipantAgentId,
    string? Text,
    string? ReplyToMessageId,
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
