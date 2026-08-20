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
            CurrentTurnId = command.CurrentTurnId ?? string.Empty,
        };
        if (command.InputParts is { Count: > 0 })
            chatRequest.InputParts.Add(command.InputParts.Select(ToProto));
        AppendMetadata(chatRequest.Headers, context.Headers);
        chatRequest.Headers[WorkflowRunCommandMetadataKeys.SessionId] = sessionId;
        AppendMetadata(chatRequest.Metadata, command.Metadata);
        if (command.LlmControl != null)
            chatRequest.LlmControl = ToProto(command.LlmControl);
        chatRequest.CallerCredential = ToProto(command.CallerCredential);
        if (command.ForkSeed != null)
            chatRequest.ForkSeed = ToProto(command.ForkSeed);
        if (command.ExternalIngress != null)
            chatRequest.ExternalIngress = ToProto(command.ExternalIngress);
        if (command.CompletionNotificationTarget != null)
            chatRequest.CompletionNotificationTarget = ToProto(command.CompletionNotificationTarget);
        if (command.ConversationContext != null)
            chatRequest.ConversationContext = ToProto(command.ConversationContext);

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

        var payload = new WorkflowChatInputPartPayload
        {
            Kind = source.Kind switch
            {
                Application.Abstractions.Runs.WorkflowChatInputPartKind.Text => Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.Text,
                Application.Abstractions.Runs.WorkflowChatInputPartKind.Image => Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.Image,
                Application.Abstractions.Runs.WorkflowChatInputPartKind.Audio => Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.Audio,
                Application.Abstractions.Runs.WorkflowChatInputPartKind.Video => Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.Video,
                Application.Abstractions.Runs.WorkflowChatInputPartKind.File => Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.File,
                _ => Aevatar.Workflow.Abstractions.WorkflowChatInputPartKind.Unspecified,
            },
            Text = source.Text ?? string.Empty,
            DataBase64 = source.DataBase64 ?? string.Empty,
            MediaType = source.MediaType ?? string.Empty,
            Uri = source.Uri ?? string.Empty,
            Name = source.Name ?? string.Empty,
        };
        if (source.FileRef != null)
            payload.FileRef = ToProto(source.FileRef);

        return payload;
    }

    private static Aevatar.Workflow.Abstractions.WorkflowFileRef ToProto(
        Application.Abstractions.Runs.FileArtifactRef source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new Aevatar.Workflow.Abstractions.WorkflowFileRef
        {
            FileId = source.FileId ?? string.Empty,
            ArtifactId = source.ArtifactId ?? string.Empty,
            SourceKind = source.SourceKind switch
            {
                Application.Abstractions.Runs.FileArtifactSourceKind.ChatInput => Aevatar.Workflow.Abstractions.WorkflowFileSourceKind.ChatInput,
                Application.Abstractions.Runs.FileArtifactSourceKind.FormUpload => Aevatar.Workflow.Abstractions.WorkflowFileSourceKind.FormUpload,
                Application.Abstractions.Runs.FileArtifactSourceKind.ConnectedServiceResource => Aevatar.Workflow.Abstractions.WorkflowFileSourceKind.ConnectedServiceResource,
                Application.Abstractions.Runs.FileArtifactSourceKind.ExternalResource => Aevatar.Workflow.Abstractions.WorkflowFileSourceKind.ExternalResource,
                Application.Abstractions.Runs.FileArtifactSourceKind.Generated => Aevatar.Workflow.Abstractions.WorkflowFileSourceKind.Generated,
                _ => Aevatar.Workflow.Abstractions.WorkflowFileSourceKind.Unspecified,
            },
            SourceMessageId = source.SourceMessageId ?? string.Empty,
            SourceResourceKey = source.SourceResourceKey ?? string.Empty,
            FileName = source.FileName ?? string.Empty,
            MediaType = source.MediaType ?? string.Empty,
            SizeBytes = source.SizeBytes,
            Sha256 = source.Sha256 ?? string.Empty,
            CreatedAtUnixMs = source.CreatedAtUnixMs,
            ExpiresAtUnixMs = source.ExpiresAtUnixMs,
            OwnerRunId = source.OwnerRunId ?? string.Empty,
            OwnerScopeId = source.OwnerScopeId ?? string.Empty,
        };
    }

    private static WorkflowLlmControlContext ToProto(WorkflowLlmControl source)
    {
        var payload = new WorkflowLlmControlContext
        {
            ModelOverride = source.ModelOverride ?? string.Empty,
            UserMemoryPrompt = source.UserMemoryPrompt ?? string.Empty,
            RoutePreference = source.RoutePreference ?? string.Empty,
            SenderNyxIdAccessToken = source.SenderNyxIdAccessToken ?? string.Empty,
        };
        if (source.MaxToolRoundsOverride.HasValue)
            payload.MaxToolRoundsOverride = source.MaxToolRoundsOverride.Value;
        return payload;
    }

    private static Aevatar.Workflow.Abstractions.WorkflowCallerCredential ToProto(
        Application.Abstractions.Runs.WorkflowCallerCredential? source)
    {
        var parsed = WorkflowCallerCredentialTokens.ParseOptional(source?.BearerToken);
        var sourceReadable = WorkflowCallerCredentialTokens.ParseOptional(
            source?.SourceReadableUserBearerToken);
        var durable = source?.DurableCallerCredential;
        var hasDurable = durable != null && !string.IsNullOrWhiteSpace(durable.Ref);
        if (WorkflowCallerCredentialTokens.IsInvalidCredentialSet(
                source?.BearerToken,
                source?.Kind ?? NyxIdCallerCredentialKind.Unspecified,
                source?.SourceReadableUserBearerToken))
            throw new ArgumentException("Workflow caller credential bearer token is invalid.", nameof(source));
        if (hasDurable && (parsed.IsValid || sourceReadable.IsValid))
            throw new ArgumentException("Workflow caller credential cannot combine a durable handle with bearer material.", nameof(source));

        var authority = source?.NyxIdAuthority;
        var authorityOnly = authority != null &&
                            parsed.IsMissing &&
                            sourceReadable.IsMissing &&
                            source?.Kind == NyxIdCallerCredentialKind.ProxyDelegation &&
                            source.UnattendedEffectAuthorization is not null;
        if (authority != null && !parsed.IsValid && !authorityOnly && !hasDurable)
        {
            throw new ArgumentException(
                "Workflow caller NyxID authority requires a valid proxy delegation credential or authority-only delegation.",
                nameof(source));
        }

        var credential = new Aevatar.Workflow.Abstractions.WorkflowCallerCredential
        {
            BearerToken = parsed.NormalizedBearerToken ?? string.Empty,
            Kind = source?.Kind ?? NyxIdCallerCredentialKind.Unspecified,
            SourceReadableUserBearerToken = sourceReadable.NormalizedBearerToken ?? string.Empty,
        };
        if (hasDurable)
            credential.DurableCallerCredential = durable!.Clone();
        if (authority != null)
        {
            var platform = Normalize(authority.Platform);
            var externalUserId = Normalize(authority.ExternalUserId);
            var scope = Normalize(authority.Scope);
            if (string.IsNullOrWhiteSpace(platform) ||
                string.IsNullOrWhiteSpace(externalUserId) ||
                string.IsNullOrWhiteSpace(scope) ||
                authorityOnly && string.IsNullOrWhiteSpace(authority.BindingId))
            {
                throw new ArgumentException(
                    "Workflow caller NyxID authority is incomplete.",
                    nameof(source));
            }

            credential.NyxIdAuthority = new Aevatar.Workflow.Abstractions.WorkflowCallerNyxIdAuthority
            {
                Platform = platform,
                Tenant = Normalize(authority.Tenant),
                ExternalUserId = externalUserId,
                Scope = scope,
                BindingId = Normalize(authority.BindingId),
            };
        }
        if (source?.UnattendedEffectAuthorization != null)
        {
            if (!authorityOnly && !hasDurable)
            {
                throw new ArgumentException(
                    "Workflow unattended effect authorization requires authority-only proxy delegation.",
                    nameof(source));
            }
            credential.UnattendedEffectAuthorization =
                source.UnattendedEffectAuthorization.Clone();
        }

        return credential;
    }

    private static Aevatar.Workflow.Abstractions.WorkflowRunForkSeed ToProto(
        WorkflowChatRunForkSeed source)
    {
        var payload = new Aevatar.Workflow.Abstractions.WorkflowRunForkSeed
        {
            SourceRunId = Normalize(source.SourceRunId),
            StartAtStepId = Normalize(source.StartAtStepId),
            Attempt = Math.Max(0, source.Attempt),
            OriginalRunId = Normalize(source.OriginalRunId),
        };
        if (source.StartStepIdempotency != null)
        {
            payload.StartStepIdempotency = new Aevatar.Workflow.Abstractions.WorkflowStepIdempotencyState
            {
                LogicalRunId = Normalize(source.StartStepIdempotency.LogicalRunId),
                StepId = Normalize(source.StartStepIdempotency.StepId),
                LogicalAttempt = Math.Max(0, source.StartStepIdempotency.LogicalAttempt),
                IdempotencyKey = Normalize(source.StartStepIdempotency.IdempotencyKey),
            };
        }

        if (source.NormalizedValues != null)
        {
            if (source.Variables.Count > 0)
            {
                throw new InvalidOperationException(
                    "A normalized workflow fork seed cannot also carry expanded legacy variables.");
            }
            payload.NormalizedValues = source.NormalizedValues.Clone();
        }
        else
        {
            AppendVariables(payload.Variables, source.Variables);
        }
        AppendVariables(payload.VariableOverrides, source.VariableOverrides);
        return payload;
    }

    private static Aevatar.Workflow.Abstractions.WorkflowCompletionNotificationTarget ToProto(
        Application.Abstractions.Runs.WorkflowCompletionNotificationTarget source) =>
        new()
        {
            ActorId = Normalize(source.ActorId),
            DeliveryId = Normalize(source.DeliveryId),
            ExpiresAtUnixMs = source.ExpiresAtUnixMs,
        };

    private static Aevatar.Workflow.Abstractions.WorkflowExternalIngressContext ToProto(
        Application.Abstractions.Runs.WorkflowExternalIngressContext source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new Aevatar.Workflow.Abstractions.WorkflowExternalIngressContext
        {
            RouteKey = Normalize(source.RouteKey),
            SourceId = Normalize(source.SourceId),
            DeliveryId = Normalize(source.DeliveryId),
            ReceivedAtUnixMs = Math.Max(0, source.ReceivedAtUnixMs),
            ContentType = Normalize(source.ContentType),
            PayloadFingerprint = Normalize(source.PayloadFingerprint),
            AuthScheme = Normalize(source.AuthScheme),
            PrincipalSubject = Normalize(source.PrincipalSubject),
        };
    }

    private static Aevatar.Workflow.Abstractions.WorkflowConversationContext ToProto(
        WorkflowConversationExecutionContext source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var payload = new Aevatar.Workflow.Abstractions.WorkflowConversationContext
        {
            ScopeId = Normalize(source.ScopeId),
            ConversationId = Normalize(source.ConversationId),
            StateVersion = Math.Max(0, source.StateVersion),
            Truncated = source.Truncated,
            MaxMessageCount = Math.Max(0, source.MaxMessageCount),
            CurrentTurnId = Normalize(source.CurrentTurnId),
        };
        payload.Messages.Add(source.Messages
            .Where(static message => !string.IsNullOrWhiteSpace(message.Content))
            .Select(static message => new Aevatar.Workflow.Abstractions.WorkflowConversationMessage
            {
                Sequence = Math.Max(0, message.Sequence),
                TurnId = Normalize(message.TurnId),
                Role = message.Role switch
                {
                    WorkflowConversationExecutionRole.User => Aevatar.Workflow.Abstractions.WorkflowConversationRole.User,
                    WorkflowConversationExecutionRole.Assistant => Aevatar.Workflow.Abstractions.WorkflowConversationRole.Assistant,
                    WorkflowConversationExecutionRole.Tool => Aevatar.Workflow.Abstractions.WorkflowConversationRole.Tool,
                    _ => Aevatar.Workflow.Abstractions.WorkflowConversationRole.Unspecified,
                },
                Content = message.Content.Trim(),
            }));
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

    private static void AppendVariables(
        Google.Protobuf.Collections.MapField<string, string> destination,
        IReadOnlyDictionary<string, string>? source)
    {
        if (source == null || source.Count == 0)
            return;

        foreach (var (key, value) in source)
        {
            var normalizedKey = string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim();
            if (normalizedKey.Length == 0)
                continue;

            destination[normalizedKey] = value ?? string.Empty;
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
