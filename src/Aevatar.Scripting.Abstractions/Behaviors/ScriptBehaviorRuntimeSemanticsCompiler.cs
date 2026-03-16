using Aevatar.Scripting.Abstractions.RuntimeSemantics;
using Google.Protobuf.Reflection;

namespace Aevatar.Scripting.Abstractions.Behaviors;

public static class ScriptBehaviorRuntimeSemanticsCompiler
{
    public static ScriptBehaviorDescriptor Attach(ScriptBehaviorDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var semantics = Extract(descriptor);
        var enriched = descriptor.WithRuntimeSemantics(semantics);
        Validate(enriched);
        return enriched;
    }

    public static void Validate(ScriptBehaviorDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var semantics = descriptor.RuntimeSemantics ?? new ScriptRuntimeSemanticsSpec();
        ValidateRegistrations(descriptor.Commands.Keys, semantics, ScriptMessageKind.Command, "command");
        ValidateRegistrations(descriptor.Signals.Keys, semantics, ScriptMessageKind.InternalSignal, "internal signal");
        ValidateRegistrations(descriptor.DomainEvents.Keys, semantics, ScriptMessageKind.DomainEvent, "domain event");

        foreach (var registration in descriptor.Commands.Values)
            ValidateMessageIdentityFields(ScriptMessageTypes.GetDescriptor(registration.MessageClrType), semantics.GetRequiredMessageSemantics(registration.TypeUrl, ScriptMessageKind.Command));
        foreach (var registration in descriptor.Signals.Values)
            ValidateMessageIdentityFields(ScriptMessageTypes.GetDescriptor(registration.MessageClrType), semantics.GetRequiredMessageSemantics(registration.TypeUrl, ScriptMessageKind.InternalSignal));
        foreach (var registration in descriptor.DomainEvents.Values)
            ValidateMessageIdentityFields(ScriptMessageTypes.GetDescriptor(registration.MessageClrType), semantics.GetRequiredMessageSemantics(registration.TypeUrl, ScriptMessageKind.DomainEvent));
    }

    private static ScriptRuntimeSemanticsSpec Extract(ScriptBehaviorDescriptor descriptor)
    {
        var semantics = new ScriptRuntimeSemanticsSpec();

        foreach (var registration in descriptor.Commands.Values.OrderBy(static x => x.TypeUrl, StringComparer.Ordinal))
            semantics.Messages.Add(ExtractMessageSemantics(registration.TypeUrl, registration.MessageClrType, ScriptMessageKind.Command));
        foreach (var registration in descriptor.Signals.Values.OrderBy(static x => x.TypeUrl, StringComparer.Ordinal))
            semantics.Messages.Add(ExtractMessageSemantics(registration.TypeUrl, registration.MessageClrType, ScriptMessageKind.InternalSignal));
        foreach (var registration in descriptor.DomainEvents.Values.OrderBy(static x => x.TypeUrl, StringComparer.Ordinal))
            semantics.Messages.Add(ExtractMessageSemantics(registration.TypeUrl, registration.MessageClrType, ScriptMessageKind.DomainEvent));

        return semantics;
    }

    private static ScriptMessageSemanticsSpec ExtractMessageSemantics(
        string typeUrl,
        Type messageClrType,
        ScriptMessageKind registeredKind)
    {
        var descriptor = ScriptMessageTypes.GetDescriptor(messageClrType);
        if (TryGetRuntimeOptions(descriptor, out var options))
        {
            return new ScriptMessageSemanticsSpec
            {
                TypeUrl = typeUrl,
                DescriptorFullName = descriptor.FullName ?? string.Empty,
                Kind = MapKind(options.Kind),
                Projectable = options.HasProjectable
                    ? options.Projectable
                    : registeredKind == ScriptMessageKind.DomainEvent,
                ReplaySafe = options.HasReplaySafe
                    ? options.ReplaySafe
                    : registeredKind == ScriptMessageKind.DomainEvent,
                SnapshotCandidate = options.HasSnapshotCandidate
                    ? options.SnapshotCandidate
                    : false,
                ReadModelScope = options.ReadModelScope ?? string.Empty,
                AggregateIdField = ResolveAggregateIdentityField(descriptor, options),
                CommandIdField = options.CommandIdField ?? string.Empty,
                CorrelationIdField = ResolveCorrelationIdentityField(descriptor, options),
                CausationIdField = options.CausationIdField ?? string.Empty,
            };
        }

        return new ScriptMessageSemanticsSpec
        {
            TypeUrl = typeUrl,
            DescriptorFullName = descriptor.FullName ?? string.Empty,
            Kind = ScriptMessageKind.Unspecified,
        };
    }

    private static bool TryGetRuntimeOptions(
        MessageDescriptor descriptor,
        out ScriptingMessageRuntimeOptions options)
    {
        var messageOptions = descriptor.GetOptions();
        if (messageOptions != null &&
            messageOptions.HasExtension(ScriptingRuntimeOptionsExtensions.ScriptingRuntime))
        {
            options = messageOptions.GetExtension(ScriptingRuntimeOptionsExtensions.ScriptingRuntime)
                ?? new ScriptingMessageRuntimeOptions();
            return true;
        }

        options = new ScriptingMessageRuntimeOptions();
        return false;
    }

    private static string ResolveAggregateIdentityField(
        MessageDescriptor descriptor,
        ScriptingMessageRuntimeOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.AggregateIdField))
            return options.AggregateIdField;

        foreach (var field in descriptor.Fields.InFieldNumberOrder())
        {
            var fieldOptions = field.GetOptions();
            if (fieldOptions == null ||
                !fieldOptions.HasExtension(ScriptingRuntimeOptionsExtensions.ScriptingRuntimeField))
            {
                continue;
            }

            var runtimeFieldOptions = fieldOptions.GetExtension(ScriptingRuntimeOptionsExtensions.ScriptingRuntimeField);
            if (runtimeFieldOptions?.AggregateIdentity == true)
                return field.Name;
        }

        return string.Empty;
    }

    private static string ResolveCorrelationIdentityField(
        MessageDescriptor descriptor,
        ScriptingMessageRuntimeOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.CorrelationIdField))
            return options.CorrelationIdField;

        foreach (var field in descriptor.Fields.InFieldNumberOrder())
        {
            var fieldOptions = field.GetOptions();
            if (fieldOptions == null ||
                !fieldOptions.HasExtension(ScriptingRuntimeOptionsExtensions.ScriptingRuntimeField))
            {
                continue;
            }

            var runtimeFieldOptions = fieldOptions.GetExtension(ScriptingRuntimeOptionsExtensions.ScriptingRuntimeField);
            if (runtimeFieldOptions?.CorrelationIdentity == true)
                return field.Name;
        }

        return string.Empty;
    }

    private static ScriptMessageKind MapKind(ScriptingMessageKind kind) =>
        kind switch
        {
            ScriptingMessageKind.Command => ScriptMessageKind.Command,
            ScriptingMessageKind.InternalSignal => ScriptMessageKind.InternalSignal,
            ScriptingMessageKind.DomainEvent => ScriptMessageKind.DomainEvent,
            _ => ScriptMessageKind.Unspecified,
        };

    private static void ValidateRegistrations(
        IEnumerable<string> typeUrls,
        ScriptRuntimeSemanticsSpec semantics,
        ScriptMessageKind expectedKind,
        string category)
    {
        foreach (var typeUrl in typeUrls)
        {
            var messageSemantics = semantics.GetRequiredMessageSemantics(typeUrl, expectedKind);
            if (messageSemantics.Kind == ScriptMessageKind.Unspecified)
            {
                throw new InvalidOperationException(
                    $"Registered {category} `{typeUrl}` must declare `(aevatar.scripting.runtime.scripting_runtime)` " +
                    $"with kind `{expectedKind}`.");
            }

            if (messageSemantics.Kind != expectedKind)
            {
                throw new InvalidOperationException(
                    $"Registered {category} `{typeUrl}` declares runtime kind `{messageSemantics.Kind}`, expected `{expectedKind}`.");
            }

            ValidateSemanticFlags(messageSemantics);
        }
    }

    private static void ValidateSemanticFlags(ScriptMessageSemanticsSpec semantics)
    {
        if (semantics.Projectable && semantics.Kind != ScriptMessageKind.DomainEvent)
        {
            throw new InvalidOperationException(
                $"Message `{semantics.TypeUrl}` declares `projectable = true`, but kind is `{semantics.Kind}`.");
        }

        if (semantics.ReplaySafe && semantics.Kind != ScriptMessageKind.DomainEvent)
        {
            throw new InvalidOperationException(
                $"Message `{semantics.TypeUrl}` declares `replay_safe = true`, but kind is `{semantics.Kind}`.");
        }

        if (semantics.SnapshotCandidate && semantics.Kind != ScriptMessageKind.DomainEvent)
        {
            throw new InvalidOperationException(
                $"Message `{semantics.TypeUrl}` declares `snapshot_candidate = true`, but kind is `{semantics.Kind}`.");
        }
    }

    private static void ValidateMessageIdentityFields(
        MessageDescriptor descriptor,
        ScriptMessageSemanticsSpec semantics)
    {
        ValidateField(descriptor, semantics.AggregateIdField, "aggregate_id_field");
        ValidateField(descriptor, semantics.CommandIdField, "command_id_field");
        ValidateField(descriptor, semantics.CorrelationIdField, "correlation_id_field");
        ValidateField(descriptor, semantics.CausationIdField, "causation_id_field");
    }

    private static void ValidateField(
        MessageDescriptor descriptor,
        string fieldName,
        string semanticName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            return;

        var field = descriptor.Fields.InDeclarationOrder().FirstOrDefault(candidate =>
            string.Equals(candidate.Name, fieldName, StringComparison.Ordinal));
        if (field == null)
        {
            throw new InvalidOperationException(
                $"Message `{descriptor.FullName}` declares `{semanticName} = {fieldName}`, but that field does not exist.");
        }

        if (field.IsMap || field.IsRepeated || field.FieldType == FieldType.Message)
        {
            throw new InvalidOperationException(
                $"Message `{descriptor.FullName}` declares `{semanticName} = {fieldName}`, but only singular scalar fields are supported.");
        }
    }
}
