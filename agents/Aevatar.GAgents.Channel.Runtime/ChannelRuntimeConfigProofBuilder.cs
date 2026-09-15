using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;

namespace Aevatar.GAgents.Channel.Runtime;

public static class ChannelRuntimeConfigProofBuilder
{
    public static ChannelRuntimeConfigProof Build(
        ChannelBotRegistrationEntry registration,
        long configRevision)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var config = BuildEffectiveRuntimeConfig(registration);
        var proof = new ChannelRuntimeConfigProof
        {
            RegistrationId = registration.Id ?? string.Empty,
            ConfigRevision = configRevision,
            ConfigDigest = ComputeDigest(config.ToByteArray()),
            InstructionsDigest = ComputeStringDigest(config.Instructions),
            Instructions = config.Instructions,
            DefaultSkillName = config.DefaultSkill?.Name ?? string.Empty,
            DefaultSkillVersion = config.DefaultSkill?.Version ?? string.Empty,
            CredentialSourceMode = config.CredentialSourceMode,
            AgentKeyGrant = registration.ChannelAgentKey?.Grant?.Clone(),
        };
        proof.ToolSetRefs.AddRange(config.ToolSetRefs);
        proof.ExtraToolNames.AddRange(config.ExtraToolNames);
        proof.NyxidServiceSelectors.AddRange(config.NyxidServiceSelectors.Select(static selector => selector.Clone()));
        return proof;
    }

    private static ChannelBotRuntimeConfig BuildEffectiveRuntimeConfig(ChannelBotRegistrationEntry registration)
    {
        var config = registration.RuntimeConfig?.Clone() ?? new ChannelBotRuntimeConfig();
        var defaultSkillName = NormalizeOptional(config.DefaultSkill?.Name) ??
                               NormalizeOptional(registration.DefaultSkillName);
        if (defaultSkillName is not null)
        {
            config.DefaultSkill ??= new ChannelBotRuntimeDefaultSkillConfig();
            config.DefaultSkill.Name = defaultSkillName.TrimStart('/').ToLowerInvariant();
            config.DefaultSkill.Version = NormalizeOptional(config.DefaultSkill.Version) ?? string.Empty;
        }

        config.Instructions = NormalizeOptional(config.Instructions) ?? string.Empty;
        return config;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ComputeStringDigest(string value) =>
        ComputeDigest(Encoding.UTF8.GetBytes(value ?? string.Empty));

    private static string ComputeDigest(byte[] bytes) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
}
