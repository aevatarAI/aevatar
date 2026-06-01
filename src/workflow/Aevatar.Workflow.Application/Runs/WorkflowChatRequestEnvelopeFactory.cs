using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;
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
        AppendMetadata(chatRequest.Headers, context.Headers);
        chatRequest.Headers[WorkflowRunCommandMetadataKeys.SessionId] = sessionId;
        // Refactor (iter56/cluster-917-workflow-llm-control-metadata): old=Headers/Metadata bag for control fields, new=typed ChatRequestEvent.Telegram
        AppendMetadata(chatRequest.Metadata, command.Metadata);
        if (command.ToolContext != null)
            chatRequest.ToolContext = ToDurableToolContextPayload(command.ToolContext);
        if (command.LlmControl != null)
            chatRequest.LlmControl = ToDurableLlmControlPayload(command.LlmControl);

        var envelope = new EventEnvelope
        {
            // Refactor (iter163/cluster-002-first):
            //   Old pattern: workflow command id was written into ChatRequestEvent.Headers[workflow.command_id]
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

    // Refactor (iter159/cluster-613-first):
    //   Old pattern: NyxID bearer entered workflow durable + pending approval surface.
    //   New principle: request bearer scrubbed at envelope/state/continuation; only durable model/route controls remain.
    private static LLMControlContextPayload ToDurableLlmControlPayload(LLMControlContext control) =>
        new LLMControlContext(
            NyxIdAccessToken: null,
            NyxIdOrgToken: null,
            SenderNyxIdAccessToken: null,
            ModelOverride: control.ModelOverride,
            NyxIdRoutePreference: control.NyxIdRoutePreference,
            MaxToolRoundsOverride: control.MaxToolRoundsOverride,
            UserMemoryPrompt: control.UserMemoryPrompt).ToPayload();

    // Refactor (issue1332): Old pattern: workflow chat command envelope dropped typed ToolContext and relied on metadata/LlmControl. New principle: reuse AgentToolExecutionContext payload and scrub bearer fields before durable workflow state.
    private static AgentToolExecutionContextPayload ToDurableToolContextPayload(AgentToolExecutionContext context) =>
        (context with
        {
            Credentials = AgentToolCredentials.Empty,
        }).ToPayload();
}
