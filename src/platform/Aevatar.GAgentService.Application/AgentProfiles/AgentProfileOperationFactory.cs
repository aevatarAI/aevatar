using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Google.Protobuf;

namespace Aevatar.GAgentService.Application.AgentProfiles;

public sealed class AgentProfileOperationFactory
{
    internal const string UpdateDraftKind = "update-agent-profile-draft";
    internal const string UpsertSkillBindingKind = "upsert-agent-profile-skill-binding";
    internal const string RemoveSkillBindingKind = "remove-agent-profile-skill-binding";
    internal const string PublishKind = "publish-agent-profile";

    public AgentProfileOperationFact CreateCreate(
        AgentProfileUserOwnerIdentity owner,
        string scopeId,
        string idempotencyKey,
        AgentProfileIdentity identity,
        AgentProfileContent initialContent) =>
        CreateAttempt(
            AgentProfileDeterminism.CreateOperationId(owner, scopeId, idempotencyKey),
            AgentProfileDeterminism.ComputeCreateAgentProfileInputSha256(
                identity,
                initialContent));

    public AgentProfileOperationFact CreateUpdateDraft(
        string profileId,
        string? idempotencyKey,
        AgentProfileIdentity identity,
        AgentProfileContent content) =>
        CreateMutationAttempt(
            UpdateDraftKind,
            profileId,
            idempotencyKey,
            AgentProfileDeterminism.ComputeUpdateAgentProfileDraftInputSha256(
                identity,
                content));

    public AgentProfileOperationFact CreateUpsertSkillBinding(
        string profileId,
        string? idempotencyKey,
        AgentProfileIdentity identity,
        AgentProfileSkillBinding binding) =>
        CreateMutationAttempt(
            UpsertSkillBindingKind,
            profileId,
            idempotencyKey,
            AgentProfileDeterminism.ComputeUpsertAgentProfileSkillBindingInputSha256(
                identity,
                binding));

    public AgentProfileOperationFact CreateRemoveSkillBinding(
        string profileId,
        string? idempotencyKey,
        AgentProfileIdentity identity,
        string bindingId) =>
        CreateMutationAttempt(
            RemoveSkillBindingKind,
            profileId,
            idempotencyKey,
            AgentProfileDeterminism.ComputeRemoveAgentProfileSkillBindingInputSha256(
                identity,
                bindingId));

    public AgentProfileOperationFact CreatePublish(
        string profileId,
        string? idempotencyKey,
        AgentProfileIdentity identity,
        AgentProfilePublishedSnapshot snapshot) =>
        CreateMutationAttempt(
            PublishKind,
            profileId,
            idempotencyKey,
            AgentProfileDeterminism.ComputePublishAgentProfileInputSha256(
                identity,
                snapshot));

    private static AgentProfileOperationFact CreateMutationAttempt(
        string operationKind,
        string profileId,
        string? idempotencyKey,
        ByteString inputSha256)
    {
        if (idempotencyKey is not null &&
            (string.IsNullOrWhiteSpace(idempotencyKey) || HasBoundaryWhitespace(idempotencyKey)))
        {
            throw new AgentProfileRequestException("INVALID_IDEMPOTENCY_KEY");
        }

        var semanticKey = idempotencyKey ?? $"implicit_{Guid.NewGuid():N}";
        return CreateAttempt(
            AgentProfileDeterminism.CreateOperationId(
                operationKind,
                profileId,
                semanticKey),
            inputSha256);
    }

    private static bool HasBoundaryWhitespace(string value) =>
        value.Length > 0 &&
        (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));

    private static AgentProfileOperationFact CreateAttempt(
        string operationId,
        ByteString inputSha256) =>
        new()
        {
            OperationId = operationId,
            InputSha256 = inputSha256,
            CommandId = AgentProfileDeterminism.CreateCommandId(),
            CorrelationId = AgentProfileDeterminism.CreateCorrelationId(),
        };
}
