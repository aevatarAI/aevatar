using System.Security.Cryptography;
using Aevatar.GAgentService.Abstractions.AgentProfiles;
using Google.Protobuf;
using Microsoft.Extensions.Options;

namespace Aevatar.GAgentService.Infrastructure.AgentProfiles;

public sealed class AgentProfileIngressProofService : IAgentProfileIngressProofVerifier
{
    private const int MinimumRsaKeySize = 2048;
    private readonly AgentProfileIngressProofOptions _options;

    public AgentProfileIngressProofService(IOptions<AgentProfileIngressProofOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? new AgentProfileIngressProofOptions();
    }

    public bool Verify(string targetActorId, IMessage command)
    {
        if (string.IsNullOrWhiteSpace(targetActorId) || command is null)
            return false;

        try
        {
            var proof = GetProof(command);
            if (!IsComplete(proof) ||
                !string.Equals(proof!.TargetActorId, targetActorId, StringComparison.Ordinal))
            {
                return false;
            }

            var material = AgentProfileIngressProofIntegrity.CreateSigningMaterial(
                targetActorId,
                command);
            if (!string.Equals(
                    proof.CommandTypeUrl,
                    material.CommandTypeUrl,
                    StringComparison.Ordinal) ||
                !FixedTimeEquals(
                    proof.CanonicalCommandSha256,
                    material.CanonicalCommandSha256) ||
                !_options.PublicKeys.TryGetValue(proof.KeyId, out var publicKey) ||
                !TryImportPublicKey(publicKey, out var rsa))
            {
                return false;
            }

            using (rsa)
            {
                return rsa.VerifyHash(
                    AgentProfileIngressProofIntegrity
                        .ComputeSigningMaterialSha256(material)
                        .ToByteArray(),
                    proof.Signature.ToByteArray(),
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or CryptographicException or InvalidOperationException)
        {
            return false;
        }
    }

    internal bool TrySign(string targetActorId, IMessage command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ClearProof(command);
        try
        {
            if (!IsKeyId(_options.CurrentKeyId) ||
                !TryImportPrivateKey(_options.CurrentPrivateKeyPkcs8, out var rsa))
            {
                return false;
            }

            using (rsa)
            {
                var material = AgentProfileIngressProofIntegrity.CreateSigningMaterial(
                    targetActorId,
                    command);
                var signature = rsa.SignHash(
                    AgentProfileIngressProofIntegrity
                        .ComputeSigningMaterialSha256(material)
                        .ToByteArray(),
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss);
                SetProof(command, new AgentProfileIngressProof
                {
                    KeyId = _options.CurrentKeyId,
                    TargetActorId = material.TargetActorId,
                    CommandTypeUrl = material.CommandTypeUrl,
                    CanonicalCommandSha256 = material.CanonicalCommandSha256,
                    Signature = ByteString.CopyFrom(signature),
                });
            }

            if (Verify(targetActorId, command))
                return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or CryptographicException or InvalidOperationException)
        {
            // A malformed or incomplete key ring is an unavailable dependency, not host startup failure.
        }

        ClearProof(command);
        return false;
    }

    private static bool IsComplete(AgentProfileIngressProof? proof) =>
        proof is not null &&
        IsKeyId(proof.KeyId) &&
        !string.IsNullOrWhiteSpace(proof.TargetActorId) &&
        !string.IsNullOrWhiteSpace(proof.CommandTypeUrl) &&
        proof.CanonicalCommandSha256.Length == SHA256.HashSizeInBytes &&
        proof.Signature.Length > 0;

    private static bool IsKeyId(string? keyId) =>
        !string.IsNullOrWhiteSpace(keyId) &&
        keyId.Length <= 128 &&
        string.Equals(keyId, keyId.Trim(), StringComparison.Ordinal) &&
        !keyId.Any(char.IsControl);

    private static bool FixedTimeEquals(ByteString left, ByteString right) =>
        left.Length == right.Length &&
        left.Length > 0 &&
        CryptographicOperations.FixedTimeEquals(left.Span, right.Span);

    private static bool TryImportPrivateKey(string encodedKey, out RSA rsa) =>
        TryImportKey(encodedKey, privateKey: true, out rsa);

    private static bool TryImportPublicKey(string encodedKey, out RSA rsa) =>
        TryImportKey(encodedKey, privateKey: false, out rsa);

    private static bool TryImportKey(
        string encodedKey,
        bool privateKey,
        out RSA rsa)
    {
        rsa = null!;
        RSA? candidate = null;
        byte[]? bytes = null;
        try
        {
            if (string.IsNullOrWhiteSpace(encodedKey))
                return false;
            bytes = Convert.FromBase64String(encodedKey);
            candidate = RSA.Create();
            int read;
            if (privateKey)
                candidate.ImportPkcs8PrivateKey(bytes, out read);
            else
                candidate.ImportSubjectPublicKeyInfo(bytes, out read);
            if (read != bytes.Length || candidate.KeySize < MinimumRsaKeySize)
                return false;

            rsa = candidate;
            candidate = null;
            return true;
        }
        catch (Exception exception) when (
            exception is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (bytes is not null)
                CryptographicOperations.ZeroMemory(bytes);
            candidate?.Dispose();
        }
    }

    private static AgentProfileIngressProof? GetProof(IMessage command) =>
        command switch
        {
            CreateAgentProfileCommand value => value.IngressProof,
            UpdateAgentProfileDraftCommand value => value.IngressProof,
            UpsertAgentProfileSkillBindingCommand value => value.IngressProof,
            RemoveAgentProfileSkillBindingCommand value => value.IngressProof,
            PublishAgentProfileCommand value => value.IngressProof,
            _ => null,
        };

    private static void SetProof(IMessage command, AgentProfileIngressProof proof)
    {
        switch (command)
        {
            case CreateAgentProfileCommand value:
                value.IngressProof = proof;
                break;
            case UpdateAgentProfileDraftCommand value:
                value.IngressProof = proof;
                break;
            case UpsertAgentProfileSkillBindingCommand value:
                value.IngressProof = proof;
                break;
            case RemoveAgentProfileSkillBindingCommand value:
                value.IngressProof = proof;
                break;
            case PublishAgentProfileCommand value:
                value.IngressProof = proof;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    private static void ClearProof(IMessage command)
    {
        switch (command)
        {
            case CreateAgentProfileCommand value:
                value.IngressProof = null;
                break;
            case UpdateAgentProfileDraftCommand value:
                value.IngressProof = null;
                break;
            case UpsertAgentProfileSkillBindingCommand value:
                value.IngressProof = null;
                break;
            case RemoveAgentProfileSkillBindingCommand value:
                value.IngressProof = null;
                break;
            case PublishAgentProfileCommand value:
                value.IngressProof = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }
}
