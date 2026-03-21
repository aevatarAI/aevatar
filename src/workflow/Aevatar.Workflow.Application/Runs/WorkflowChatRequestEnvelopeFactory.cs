using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowChatRequestEnvelopeFactory : ICommandEnvelopeFactory<WorkflowChatRunRequest>
{
    public EventEnvelope CreateEnvelope(WorkflowChatRunRequest command, CommandContext context)
    {
        var sessionId = !string.IsNullOrWhiteSpace(command.SessionId)
            ? command.SessionId
            : context.CorrelationId;

        var chatRequest = new ChatRequestEvent
        {
            Prompt = command.Prompt,
            SessionId = sessionId,
            ScopeId = command.ScopeId ?? string.Empty,
        };
        if (command.InputParts is { Count: > 0 })
            chatRequest.InputParts.Add(command.InputParts.Select(ToProto));
        AppendMetadata(chatRequest.Metadata, context.Headers);
        AppendMetadata(chatRequest.Metadata, command.Metadata);
        chatRequest.Metadata[WorkflowRunCommandMetadataKeys.CommandId] = context.CommandId;
        chatRequest.Metadata[WorkflowRunCommandMetadataKeys.SessionId] = sessionId;

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(chatRequest),
            Route = EnvelopeRouteSemantics.CreateDirect("api", context.TargetId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = context.CorrelationId,
            },
        };
        return envelope;
    }

    private static ChatContentPart ToProto(WorkflowChatInputPart source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new ChatContentPart
        {
            Kind = source.Kind switch
            {
                WorkflowChatInputPartKind.Text => ChatContentPartKind.Text,
                WorkflowChatInputPartKind.Image => ChatContentPartKind.Image,
                WorkflowChatInputPartKind.Audio => ChatContentPartKind.Audio,
                WorkflowChatInputPartKind.Video => ChatContentPartKind.Video,
                _ => ChatContentPartKind.Unspecified,
            },
            Text = source.Text ?? string.Empty,
            DataBase64 = source.DataBase64 ?? string.Empty,
            MediaType = source.MediaType ?? string.Empty,
            Uri = source.Uri ?? string.Empty,
            Name = source.Name ?? string.Empty,
        };
    }

    private static void AppendMetadata(
        Google.Protobuf.Collections.MapField<string, string> destination,
        IReadOnlyDictionary<string, string>? source)
    {
        if (source == null || source.Count == 0)
            return;

        foreach (var (key, value) in source)
        {
            var normalizedKey = string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim();
            var normalizedValue = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
            if (normalizedKey.Length == 0 || normalizedValue.Length == 0)
                continue;
            if (IsScopeMetadataKey(normalizedKey))
                continue;

            destination[normalizedKey] = normalizedValue;
        }
    }

    private static bool IsScopeMetadataKey(string key) =>
        string.Equals(key, "scope_id", StringComparison.Ordinal) ||
        string.Equals(key, WorkflowRunCommandMetadataKeys.ScopeId, StringComparison.Ordinal);
}
