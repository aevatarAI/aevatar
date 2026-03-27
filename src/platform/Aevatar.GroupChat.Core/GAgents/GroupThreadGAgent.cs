using Aevatar.GroupChat.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.GroupChat.Core.GAgents;

public sealed class GroupThreadGAgent : GAgentBase<GroupThreadState>
{
    public GroupThreadGAgent()
    {
        InitializeId();
    }

    [EventHandler]
    public async Task HandleCreateAsync(CreateGroupThreadCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCreateCommand(command);
        EnsureThreadNotCreated(command.GroupId, command.ThreadId);

        await PersistDomainEventAsync(new GroupThreadCreatedEvent
        {
            GroupId = command.GroupId,
            ThreadId = command.ThreadId,
            DisplayName = command.DisplayName,
            ParticipantAgentIds =
            {
                command.ParticipantAgentIds,
            },
            ParticipantRuntimeBindingEntries =
            {
                NormalizeBindings(command.ParticipantRuntimeBindingEntries),
            },
        });
    }

    [EventHandler]
    public async Task HandlePostUserMessageAsync(PostUserMessageCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureBoundThread(command.GroupId, command.ThreadId);
        ValidateMessageId(command.MessageId);
        ValidateText(command.Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.SenderUserId);
        EnsureMessageDoesNotExist(command.MessageId);
        EnsureDirectHintParticipantsExist(command.DirectHintAgentIds);
        ValidateSourceRefs(command.SourceRefs);
        ValidateEvidenceRefs(command.EvidenceRefs);

        await PersistDomainEventAsync(new UserMessagePostedEvent
        {
            GroupId = command.GroupId,
            ThreadId = command.ThreadId,
            MessageId = command.MessageId,
            SenderUserId = command.SenderUserId,
            Text = command.Text,
            TopicId = ResolveTopicId(command.TopicId, command.ThreadId),
            SignalKind = command.SignalKind,
            SourceRefs =
            {
                NormalizeSourceRefs(command.SourceRefs),
            },
            EvidenceRefs =
            {
                NormalizeEvidenceRefs(command.EvidenceRefs),
            },
            DerivedFromSignalIds =
            {
                NormalizeDistinct(command.DerivedFromSignalIds),
            },
            DirectHintAgentIds =
            {
                NormalizeDistinct(command.DirectHintAgentIds),
            },
        });
    }

    [EventHandler]
    public async Task HandleAppendAgentMessageAsync(AppendAgentMessageCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureBoundThread(command.GroupId, command.ThreadId);
        ValidateMessageId(command.MessageId);
        ValidateText(command.Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ParticipantAgentId);
        EnsureMessageDoesNotExist(command.MessageId);
        EnsureParticipantExists(command.ParticipantAgentId);
        EnsureDirectHintParticipantsExist(command.DirectHintAgentIds);
        ValidateSourceRefs(command.SourceRefs);
        ValidateEvidenceRefs(command.EvidenceRefs);

        if (!string.IsNullOrWhiteSpace(command.ReplyToMessageId))
            EnsureMessageExists(command.ReplyToMessageId);

        await PersistDomainEventAsync(new AgentMessageAppendedEvent
        {
            GroupId = command.GroupId,
            ThreadId = command.ThreadId,
            MessageId = command.MessageId,
            ParticipantAgentId = command.ParticipantAgentId,
            Text = command.Text,
            ReplyToMessageId = command.ReplyToMessageId ?? string.Empty,
            TopicId = ResolveTopicId(command.TopicId, command.ThreadId),
            SignalKind = command.SignalKind,
            SourceRefs =
            {
                NormalizeSourceRefs(command.SourceRefs),
            },
            EvidenceRefs =
            {
                NormalizeEvidenceRefs(command.EvidenceRefs),
            },
            DerivedFromSignalIds =
            {
                NormalizeDistinct(command.DerivedFromSignalIds),
            },
            DirectHintAgentIds =
            {
                NormalizeDistinct(command.DirectHintAgentIds),
            },
        });
    }

    protected override GroupThreadState TransitionState(GroupThreadState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<GroupThreadCreatedEvent>(ApplyCreated)
            .On<UserMessagePostedEvent>(ApplyUserMessagePosted)
            .On<AgentMessageAppendedEvent>(ApplyAgentMessageAppended)
            .OrCurrent();

    private static GroupThreadState ApplyCreated(GroupThreadState state, GroupThreadCreatedEvent evt)
    {
        var next = state.Clone();
        next.GroupId = evt.GroupId;
        next.ThreadId = evt.ThreadId;
        next.DisplayName = evt.DisplayName ?? string.Empty;
        next.ParticipantAgentIds.Clear();
        next.ParticipantAgentIds.Add(NormalizeDistinct(evt.ParticipantAgentIds));
        next.ParticipantRuntimeBindingEntries.Clear();
        next.ParticipantRuntimeBindingEntries.Add(NormalizeBindings(evt.ParticipantRuntimeBindingEntries));
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.GroupId, evt.ThreadId, $"created:{evt.ThreadId}");
        return next;
    }

    private static GroupThreadState ApplyUserMessagePosted(GroupThreadState state, UserMessagePostedEvent evt)
    {
        var next = state.Clone();
        next.MessageEntries.Add(new GroupThreadMessageState
        {
            MessageId = evt.MessageId,
            TimelineCursor = state.MessageEntries.Count + 1L,
            SenderKind = GroupMessageSenderKind.User,
            SenderId = evt.SenderUserId,
            Text = evt.Text,
            TopicId = ResolveTopicId(evt.TopicId, evt.ThreadId),
            SignalKind = evt.SignalKind,
            SourceRefs =
            {
                NormalizeSourceRefs(evt.SourceRefs),
            },
            EvidenceRefs =
            {
                NormalizeEvidenceRefs(evt.EvidenceRefs),
            },
            DerivedFromSignalIds =
            {
                NormalizeDistinct(evt.DerivedFromSignalIds),
            },
            DirectHintAgentIds =
            {
                NormalizeDistinct(evt.DirectHintAgentIds),
            },
        });
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.GroupId, evt.ThreadId, $"user-message:{evt.MessageId}");
        return next;
    }

    private static GroupThreadState ApplyAgentMessageAppended(GroupThreadState state, AgentMessageAppendedEvent evt)
    {
        var next = state.Clone();
        next.MessageEntries.Add(new GroupThreadMessageState
        {
            MessageId = evt.MessageId,
            TimelineCursor = state.MessageEntries.Count + 1L,
            SenderKind = GroupMessageSenderKind.Agent,
            SenderId = evt.ParticipantAgentId,
            Text = evt.Text,
            ReplyToMessageId = evt.ReplyToMessageId ?? string.Empty,
            TopicId = ResolveTopicId(evt.TopicId, evt.ThreadId),
            SignalKind = evt.SignalKind,
            SourceRefs =
            {
                NormalizeSourceRefs(evt.SourceRefs),
            },
            EvidenceRefs =
            {
                NormalizeEvidenceRefs(evt.EvidenceRefs),
            },
            DerivedFromSignalIds =
            {
                NormalizeDistinct(evt.DerivedFromSignalIds),
            },
            DirectHintAgentIds =
            {
                NormalizeDistinct(evt.DirectHintAgentIds),
            },
        });
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.GroupId, evt.ThreadId, $"agent-message:{evt.MessageId}");
        return next;
    }

    private static void ValidateCreateCommand(CreateGroupThreadCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.GroupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ThreadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DisplayName);
        if (command.ParticipantAgentIds.Count == 0)
            throw new InvalidOperationException("participant_agent_ids is required.");

        var normalized = NormalizeDistinct(command.ParticipantAgentIds);
        if (normalized.Count != command.ParticipantAgentIds.Count)
            throw new InvalidOperationException("participant_agent_ids contains duplicate values.");

        var bindings = NormalizeBindings(command.ParticipantRuntimeBindingEntries);
        if (bindings.Count != command.ParticipantRuntimeBindingEntries.Count)
            throw new InvalidOperationException("participant_runtime_binding_entries contains duplicate participant_agent_id values.");

        foreach (var binding in bindings)
        {
            if (!normalized.Contains(binding.ParticipantAgentId))
                throw new InvalidOperationException(
                    $"participant_runtime_binding participant_agent_id '{binding.ParticipantAgentId}' is not registered in participant_agent_ids.");
        }
    }

    private void EnsureThreadNotCreated(string groupId, string threadId)
    {
        if (!string.IsNullOrWhiteSpace(State.GroupId))
            throw new InvalidOperationException($"Group thread '{BuildThreadKey(groupId, threadId)}' already exists.");
    }

    private void EnsureBoundThread(string groupId, string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var currentKey = BuildThreadKey(State.GroupId, State.ThreadId);
        if (string.IsNullOrWhiteSpace(State.GroupId) || string.IsNullOrWhiteSpace(State.ThreadId))
            throw new InvalidOperationException($"Group thread '{BuildThreadKey(groupId, threadId)}' does not exist.");

        var requestedKey = BuildThreadKey(groupId, threadId);
        if (!string.Equals(currentKey, requestedKey, StringComparison.Ordinal))
            throw new InvalidOperationException($"Group thread actor '{Id}' is bound to '{currentKey}', but got '{requestedKey}'.");
    }

    private void EnsureParticipantExists(string participantAgentId)
    {
        if (!State.ParticipantAgentIds.Contains(participantAgentId))
            throw new InvalidOperationException($"participant_agent_id '{participantAgentId}' is not registered in this thread.");
    }

    private void EnsureDirectHintParticipantsExist(IEnumerable<string> participantAgentIds)
    {
        foreach (var participantAgentId in NormalizeDistinct(participantAgentIds))
            EnsureParticipantExists(participantAgentId);
    }

    private void EnsureMessageDoesNotExist(string messageId)
    {
        if (State.MessageEntries.Any(x => string.Equals(x.MessageId, messageId, StringComparison.Ordinal)))
            throw new InvalidOperationException($"message_id '{messageId}' already exists.");
    }

    private void EnsureMessageExists(string messageId)
    {
        if (!State.MessageEntries.Any(x => string.Equals(x.MessageId, messageId, StringComparison.Ordinal)))
            throw new InvalidOperationException($"reply_to_message_id '{messageId}' does not exist.");
    }

    private static void ValidateMessageId(string messageId) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

    private static void ValidateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("text is required.");
    }

    private static void ValidateSourceRefs(IEnumerable<GroupSourceRef> sourceRefs)
    {
        foreach (var sourceRef in sourceRefs)
        {
            ArgumentNullException.ThrowIfNull(sourceRef);
            if (string.IsNullOrWhiteSpace(sourceRef.Locator))
                throw new InvalidOperationException("source_ref locator is required.");
        }
    }

    private static void ValidateEvidenceRefs(IEnumerable<GroupEvidenceRef> evidenceRefs)
    {
        foreach (var evidenceRef in evidenceRefs)
        {
            ArgumentNullException.ThrowIfNull(evidenceRef);
            if (string.IsNullOrWhiteSpace(evidenceRef.EvidenceId))
                throw new InvalidOperationException("evidence_ref evidence_id is required.");
            if (string.IsNullOrWhiteSpace(evidenceRef.Locator))
                throw new InvalidOperationException("evidence_ref locator is required.");
        }
    }

    private static List<string> NormalizeDistinct(IEnumerable<string> values)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("participant_agent_id must not be blank.");

            var trimmed = value.Trim();
            if (seen.Add(trimmed))
                normalized.Add(trimmed);
        }

        return normalized;
    }

    private static List<GroupSourceRef> NormalizeSourceRefs(IEnumerable<GroupSourceRef> values)
    {
        var normalized = new List<GroupSourceRef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            var locator = NormalizeRequired(value.Locator, nameof(value.Locator));
            var key = $"{(int)value.SourceKind}:{locator}";
            if (!seen.Add(key))
                continue;

            normalized.Add(new GroupSourceRef
            {
                SourceKind = value.SourceKind,
                Locator = locator,
                SourceId = value.SourceId?.Trim() ?? string.Empty,
            });
        }

        return normalized;
    }

    private static List<GroupEvidenceRef> NormalizeEvidenceRefs(IEnumerable<GroupEvidenceRef> values)
    {
        var normalized = new List<GroupEvidenceRef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            var evidenceId = NormalizeRequired(value.EvidenceId, nameof(value.EvidenceId));
            var locator = NormalizeRequired(value.Locator, nameof(value.Locator));
            var key = $"{evidenceId}:{locator}";
            if (!seen.Add(key))
                continue;

            normalized.Add(new GroupEvidenceRef
            {
                EvidenceId = evidenceId,
                SourceLocator = value.SourceLocator?.Trim() ?? string.Empty,
                Locator = locator,
                ExcerptSummary = value.ExcerptSummary?.Trim() ?? string.Empty,
                SourceId = value.SourceId?.Trim() ?? string.Empty,
            });
        }

        return normalized;
    }

    private static string ResolveTopicId(string topicId, string threadId) =>
        string.IsNullOrWhiteSpace(topicId) ? threadId.Trim() : topicId.Trim();

    private static List<GroupParticipantRuntimeBinding> NormalizeBindings(IEnumerable<GroupParticipantRuntimeBinding> bindings)
    {
        var normalized = new List<GroupParticipantRuntimeBinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            ArgumentNullException.ThrowIfNull(binding);
            var participantAgentId = NormalizeRequired(binding.ParticipantAgentId, nameof(binding.ParticipantAgentId));
            if (!seen.Add(participantAgentId))
                continue;

            var normalizedBinding = new GroupParticipantRuntimeBinding
            {
                ParticipantAgentId = participantAgentId,
                TargetKind = NormalizeTargetKind(binding),
            };

            switch (binding.TargetCase)
            {
                case GroupParticipantRuntimeBinding.TargetOneofCase.ServiceTarget:
                    normalizedBinding.ServiceTarget = NormalizeServiceTarget(binding);
                    break;
                case GroupParticipantRuntimeBinding.TargetOneofCase.WorkflowTarget:
                    normalizedBinding.WorkflowTarget = NormalizeWorkflowTarget(binding);
                    break;
                case GroupParticipantRuntimeBinding.TargetOneofCase.ScriptTarget:
                    normalizedBinding.ScriptTarget = NormalizeScriptTarget(binding);
                    break;
                case GroupParticipantRuntimeBinding.TargetOneofCase.LocalTarget:
                    normalizedBinding.LocalTarget = NormalizeLocalTarget(binding);
                    break;
            }

            normalized.Add(normalizedBinding);
        }

        return normalized;
    }

    private static GroupParticipantRuntimeTargetKind NormalizeTargetKind(GroupParticipantRuntimeBinding binding)
    {
        var targetKind = binding.TargetKind == GroupParticipantRuntimeTargetKind.Unspecified
            ? ResolveTargetKind(binding.TargetCase)
            : binding.TargetKind;
        var resolvedTargetKind = ResolveTargetKind(binding.TargetCase);
        if (resolvedTargetKind == GroupParticipantRuntimeTargetKind.Unspecified)
            throw new InvalidOperationException("runtime target is required.");
        if (targetKind != resolvedTargetKind)
        {
            throw new InvalidOperationException(
                $"target_kind '{targetKind}' does not match runtime target '{binding.TargetCase}'.");
        }

        return targetKind;
    }

    private static GroupServiceRuntimeTarget? NormalizeServiceTarget(GroupParticipantRuntimeBinding binding)
    {
        if (binding.TargetCase != GroupParticipantRuntimeBinding.TargetOneofCase.ServiceTarget)
            return null;

        var target = binding.ServiceTarget ?? throw new InvalidOperationException("service_target is required.");
        return new GroupServiceRuntimeTarget
        {
            TenantId = NormalizeRequired(target.TenantId, nameof(target.TenantId)),
            AppId = NormalizeRequired(target.AppId, nameof(target.AppId)),
            Namespace = NormalizeRequired(target.Namespace, nameof(target.Namespace)),
            ServiceId = NormalizeRequired(target.ServiceId, nameof(target.ServiceId)),
            EndpointId = NormalizeRequired(target.EndpointId, nameof(target.EndpointId)),
            ScopeId = target.ScopeId?.Trim() ?? string.Empty,
        };
    }

    private static GroupWorkflowRuntimeTarget? NormalizeWorkflowTarget(GroupParticipantRuntimeBinding binding)
    {
        if (binding.TargetCase != GroupParticipantRuntimeBinding.TargetOneofCase.WorkflowTarget)
            return null;

        var target = binding.WorkflowTarget ?? throw new InvalidOperationException("workflow_target is required.");
        var definitionActorId = target.DefinitionActorId?.Trim() ?? string.Empty;
        var workflowName = target.WorkflowName?.Trim() ?? string.Empty;
        if (definitionActorId.Length == 0 && workflowName.Length == 0)
        {
            throw new InvalidOperationException(
                "workflow_target requires definition_actor_id or workflow_name.");
        }

        return new GroupWorkflowRuntimeTarget
        {
            DefinitionActorId = definitionActorId,
            WorkflowName = workflowName,
            ScopeId = target.ScopeId?.Trim() ?? string.Empty,
        };
    }

    private static GroupScriptRuntimeTarget? NormalizeScriptTarget(GroupParticipantRuntimeBinding binding)
    {
        if (binding.TargetCase != GroupParticipantRuntimeBinding.TargetOneofCase.ScriptTarget)
            return null;

        var target = binding.ScriptTarget ?? throw new InvalidOperationException("script_target is required.");
        return new GroupScriptRuntimeTarget
        {
            DefinitionActorId = NormalizeRequired(target.DefinitionActorId, nameof(target.DefinitionActorId)),
            Revision = NormalizeRequired(target.Revision, nameof(target.Revision)),
            RuntimeActorId = target.RuntimeActorId?.Trim() ?? string.Empty,
            RequestedEventType = target.RequestedEventType?.Trim() ?? string.Empty,
            ScopeId = target.ScopeId?.Trim() ?? string.Empty,
        };
    }

    private static GroupLocalRuntimeTarget? NormalizeLocalTarget(GroupParticipantRuntimeBinding binding)
    {
        if (binding.TargetCase != GroupParticipantRuntimeBinding.TargetOneofCase.LocalTarget)
            return null;

        var target = binding.LocalTarget ?? throw new InvalidOperationException("local_target is required.");
        return new GroupLocalRuntimeTarget
        {
            Provider = NormalizeRequired(target.Provider, nameof(target.Provider)),
        };
    }

    private static GroupParticipantRuntimeTargetKind ResolveTargetKind(GroupParticipantRuntimeBinding.TargetOneofCase targetCase) =>
        targetCase switch
        {
            GroupParticipantRuntimeBinding.TargetOneofCase.ServiceTarget => GroupParticipantRuntimeTargetKind.Service,
            GroupParticipantRuntimeBinding.TargetOneofCase.WorkflowTarget => GroupParticipantRuntimeTargetKind.Workflow,
            GroupParticipantRuntimeBinding.TargetOneofCase.ScriptTarget => GroupParticipantRuntimeTargetKind.Script,
            GroupParticipantRuntimeBinding.TargetOneofCase.LocalTarget => GroupParticipantRuntimeTargetKind.Local,
            _ => GroupParticipantRuntimeTargetKind.Unspecified,
        };

    private static string NormalizeRequired(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{name} is required.");

        return value.Trim();
    }

    private static string BuildThreadKey(string groupId, string threadId) => $"{groupId}:{threadId}";

    private static string BuildEventId(string groupId, string threadId, string suffix) =>
        $"group-thread:{BuildThreadKey(groupId, threadId)}:{suffix}";
}
