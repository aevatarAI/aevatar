using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowChatRequestEnvelopeFactory : ICommandEnvelopeFactory<WorkflowChatRunRequest>
{
    private const string LegacyConnectorHttpAuthorizationBlockedKey = "connector.http.authorization";

    public EventEnvelope CreateEnvelope(WorkflowChatRunRequest command, CommandContext context)
    {
        var sessionId = !string.IsNullOrWhiteSpace(command.SessionId)
            ? command.SessionId
            : context.CorrelationId;

        var chatRequest = new WorkflowChatRequestEvent
        {
            Prompt = command.Prompt,
            SessionId = sessionId,
            ScopeId = command.ScopeId ?? string.Empty,
        };
        if (command.InputParts is { Count: > 0 })
            chatRequest.InputParts.Add(command.InputParts.Select(ToProto));
        AppendMetadata(chatRequest.Headers, context.Headers);
        chatRequest.Headers[WorkflowRunCommandMetadataKeys.SessionId] = sessionId;
        AppendMetadata(chatRequest.Metadata, command.Metadata);
        if (command.LlmControl != null)
            chatRequest.LlmControl = ToProto(command.LlmControl);
        chatRequest.ConnectorHttpAuthorization = Normalize(command.ConnectorHttpAuthorization);

        var envelope = new EventEnvelope
        {
            // Refactor (iter163/cluster-002-first):
            //   Old pattern: workflow command id was written into request headers,
            //                while Headers also carried transport context.
            //   New principle: EventEnvelope.Id carries the workflow command identity;
            //                  Headers stay transport-only.
            Id = context.CommandId,
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

    private static WorkflowChatInputPartPayload ToProto(WorkflowChatInputPart source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new WorkflowChatInputPartPayload
        {
            Kind = source.Kind switch
            {
                Application.Abstractions.Runs.WorkflowChatInputPartKind.Text => Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.Text,
                Application.Abstractions.Runs.WorkflowChatInputPartKind.Image => Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.Image,
                Application.Abstractions.Runs.WorkflowChatInputPartKind.Audio => Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.Audio,
                Application.Abstractions.Runs.WorkflowChatInputPartKind.Video => Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.Video,
                _ => Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.Unspecified,
            },
            Text = source.Text ?? string.Empty,
            DataBase64 = source.DataBase64 ?? string.Empty,
            MediaType = source.MediaType ?? string.Empty,
            Uri = source.Uri ?? string.Empty,
            Name = source.Name ?? string.Empty,
        };
    }

    private static WorkflowLlmControlContext ToProto(WorkflowLlmControl source)
    {
        var payload = new WorkflowLlmControlContext
        {
            ModelOverride = source.ModelOverride ?? string.Empty,
            UserMemoryPrompt = source.UserMemoryPrompt ?? string.Empty,
            SenderNyxIdAccessToken = source.SenderNyxIdAccessToken ?? string.Empty,
        };
        if (source.MaxToolRoundsOverride.HasValue)
            payload.MaxToolRoundsOverride = source.MaxToolRoundsOverride.Value;
        return payload;
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
            if (IsReservedMetadataKey(normalizedKey))
                continue;

            destination[normalizedKey] = normalizedValue;
        }
    }

    private static bool IsReservedMetadataKey(string key) =>
        IsScopeMetadataKey(key) ||
        string.Equals(key, LegacyConnectorHttpAuthorizationBlockedKey, StringComparison.Ordinal);

    private static bool IsScopeMetadataKey(string key) =>
        string.Equals(key, "scope_id", StringComparison.Ordinal) ||
        string.Equals(key, WorkflowRunCommandMetadataKeys.ScopeId, StringComparison.Ordinal);

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
