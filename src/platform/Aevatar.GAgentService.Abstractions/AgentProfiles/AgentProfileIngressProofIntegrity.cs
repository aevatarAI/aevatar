using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Abstractions.AgentProfiles;

public static class AgentProfileIngressProofIntegrity
{
    public const string SigningDomain = "aevatar.agent-profile.ingress-proof.v1";

    public static ByteString ComputeCanonicalCommandSha256(IMessage command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ByteString.CopyFrom(SHA256.HashData(SerializeDeterministically(CloneWithoutProof(command))));
    }

    public static AgentProfileIngressProofSigningMaterial CreateSigningMaterial(
        string keyId,
        string targetActorId,
        IMessage command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetActorId);
        ArgumentNullException.ThrowIfNull(command);
        if (!string.Equals(keyId, keyId.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("Key id cannot have boundary whitespace.", nameof(keyId));
        if (!string.Equals(targetActorId, targetActorId.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("Target Actor id cannot have boundary whitespace.", nameof(targetActorId));

        return new AgentProfileIngressProofSigningMaterial
        {
            Domain = SigningDomain,
            KeyId = keyId,
            TargetActorId = targetActorId,
            CommandTypeUrl = Any.Pack(command).TypeUrl,
            CanonicalCommandSha256 = ComputeCanonicalCommandSha256(command),
        };
    }

    public static ByteString ComputeSigningMaterialSha256(
        AgentProfileIngressProofSigningMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        return ByteString.CopyFrom(SHA256.HashData(SerializeDeterministically(material)));
    }

    private static IMessage CloneWithoutProof(IMessage command) =>
        command switch
        {
            CreateAgentProfileCommand value => ClearProof(value.Clone()),
            UpdateAgentProfileDraftCommand value => ClearProof(value.Clone()),
            UpsertAgentProfileSkillBindingCommand value => ClearProof(value.Clone()),
            RemoveAgentProfileSkillBindingCommand value => ClearProof(value.Clone()),
            PublishAgentProfileCommand value => ClearProof(value.Clone()),
            _ => throw new ArgumentOutOfRangeException(
                nameof(command),
                command.Descriptor.FullName,
                "Only Application-originated Agent Profile commands support ingress proofs."),
        };

    private static CreateAgentProfileCommand ClearProof(CreateAgentProfileCommand command)
    {
        command.IngressProof = null;
        return command;
    }

    private static UpdateAgentProfileDraftCommand ClearProof(UpdateAgentProfileDraftCommand command)
    {
        command.IngressProof = null;
        return command;
    }

    private static UpsertAgentProfileSkillBindingCommand ClearProof(
        UpsertAgentProfileSkillBindingCommand command)
    {
        command.IngressProof = null;
        return command;
    }

    private static RemoveAgentProfileSkillBindingCommand ClearProof(
        RemoveAgentProfileSkillBindingCommand command)
    {
        command.IngressProof = null;
        return command;
    }

    private static PublishAgentProfileCommand ClearProof(PublishAgentProfileCommand command)
    {
        command.IngressProof = null;
        return command;
    }

    private static byte[] SerializeDeterministically(IMessage message)
    {
        using var stream = new MemoryStream(message.CalculateSize());
        using (var output = new CodedOutputStream(stream, leaveOpen: true))
        {
            output.Deterministic = true;
            message.WriteTo(output);
            output.Flush();
        }

        return stream.ToArray();
    }
}
