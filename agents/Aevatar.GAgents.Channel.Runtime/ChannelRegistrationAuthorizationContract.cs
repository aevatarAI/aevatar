using Aevatar.Foundation.Abstractions.Credentials;

namespace Aevatar.GAgents.Channel.Runtime;

public enum ChannelRegistrationAuthorizationContractKind
{
    Invalid = 0,
    NyxIdDefault = 1,
    ExplicitServiceAllowlist = 2,
    HistoricalLegacy = 3,
}

public static class ChannelRegistrationAuthorizationContract
{
    public static ChannelRegistrationAuthorizationContractKind Classify(
        ChannelBotRegistrationEntry? entry)
    {
        if (entry is null)
            return ChannelRegistrationAuthorizationContractKind.Invalid;

        if (entry.AuthorizationMode == ChannelRegistrationAuthorizationMode.Unspecified &&
            entry.ChannelAgentKey is null &&
            entry.RegistrationServiceAllowlist is null)
        {
            return ChannelRegistrationAuthorizationContractKind.HistoricalLegacy;
        }

        var credential = entry.ChannelAgentKey;
        return entry.AuthorizationMode switch
        {
            ChannelRegistrationAuthorizationMode.NyxidDefault
                when entry.RegistrationServiceAllowlist is null &&
                     IsValidCredential(
                         entry.ScopeId,
                         credential,
                         entry.NyxAgentApiKeyId,
                         entry.WorkflowResultDeliveryCredential) &&
                     IsValidDefaultGrant(credential!.Grant) =>
                ChannelRegistrationAuthorizationContractKind.NyxIdDefault,
            ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist
                when IsValidExplicitContract(
                    entry.ScopeId,
                    entry.RegistrationServiceAllowlist,
                    credential,
                    entry.NyxAgentApiKeyId,
                    entry.WorkflowResultDeliveryCredential) =>
                ChannelRegistrationAuthorizationContractKind.ExplicitServiceAllowlist,
            _ => ChannelRegistrationAuthorizationContractKind.Invalid,
        };
    }

    public static bool IsValidNewCommand(ChannelBotRegisterCommand? command)
    {
        if (command is null)
            return false;

        var credential = command.ChannelAgentKey;
        return command.AuthorizationMode switch
        {
            ChannelRegistrationAuthorizationMode.NyxidDefault =>
                command.RegistrationServiceAllowlist is null &&
                IsValidCredential(
                    command.ScopeId,
                    credential,
                    command.NyxAgentApiKeyId,
                    command.WorkflowResultDeliveryCredential) &&
                IsValidDefaultGrant(credential!.Grant),
            ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist =>
                IsValidExplicitContract(
                    command.ScopeId,
                    command.RegistrationServiceAllowlist,
                    credential,
                    command.NyxAgentApiKeyId,
                    command.WorkflowResultDeliveryCredential),
            _ => false,
        };
    }

    public static bool TryGetAuthoritativeCredential(
        ChannelBotRegistrationEntry? entry,
        out ChannelAgentKeyCredential? credential)
    {
        if (Classify(entry) is ChannelRegistrationAuthorizationContractKind.NyxIdDefault or
            ChannelRegistrationAuthorizationContractKind.ExplicitServiceAllowlist)
        {
            credential = entry!.ChannelAgentKey.Clone();
            return true;
        }

        credential = null;
        return false;
    }

    private static bool IsValidCredential(
        string scopeId,
        ChannelAgentKeyCredential? credential,
        string legacyApiKeyId,
        SecretReference? legacySecretReference) =>
        credential is not null &&
        IsCanonicalIdentifier(scopeId) &&
        IsCanonicalIdentifier(credential.ApiKeyId) &&
        string.Equals(legacyApiKeyId, credential.ApiKeyId, StringComparison.Ordinal) &&
        IsCompleteSecretReference(credential.SecretReference, scopeId) &&
        legacySecretReference is not null &&
        legacySecretReference.Equals(credential.SecretReference) &&
        IsValidGrant(credential.Grant);

    private static bool IsValidExplicitContract(
        string scopeId,
        ChannelRegistrationServiceAllowlist? allowlist,
        ChannelAgentKeyCredential? credential,
        string legacyApiKeyId,
        SecretReference? legacySecretReference) =>
        allowlist is not null &&
        credential is not null &&
        IsValidCredential(
            scopeId,
            credential,
            legacyApiKeyId,
            legacySecretReference) &&
        IsCanonicalIdentifierList(allowlist.ServiceIds) &&
        !credential.Grant.AllowAllServices &&
        !credential.Grant.AllowAllNodes &&
        IsValidScopePlanDigest(credential.Grant.ScopePlanDigest) &&
        IsSubset(allowlist.ServiceIds, credential.Grant.AllowedServiceIds);

    private static bool IsValidDefaultGrant(ChannelAgentKeyGrantSnapshot grant) =>
        string.IsNullOrEmpty(grant.ScopePlanDigest);

    private static bool IsCompleteSecretReference(
        SecretReference? reference,
        string scopeId) =>
        reference is not null &&
        !string.IsNullOrWhiteSpace(reference.Ref) &&
        string.Equals(
            reference.Purpose,
            CredentialSecretPurposes.ChannelNyxIdAgentKey,
            StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(reference.OwnerScopeKey) &&
        string.Equals(reference.OwnerScopeKey, scopeId, StringComparison.Ordinal) &&
        reference.Version > 0 &&
        !string.IsNullOrWhiteSpace(reference.Fingerprint) &&
        reference.CreatedAtUnixMs > 0 &&
        reference.ExpiresAtUnixMs >= 0;

    private static bool IsValidGrant(ChannelAgentKeyGrantSnapshot? grant) =>
        grant is not null &&
        grant.HasAllowAllServices &&
        grant.HasAllowAllNodes &&
        IsCanonicalIdentifierList(grant.AllowedServiceIds) &&
        IsCanonicalIdentifierList(grant.AllowedNodeIds) &&
        (!grant.AllowAllServices || grant.AllowedServiceIds.Count == 0) &&
        (!grant.AllowAllNodes || grant.AllowedNodeIds.Count == 0);

    private static bool IsCanonicalIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool IsValidScopePlanDigest(string value) =>
        IsLowerHexDigest(value);

    private static bool IsLowerHexDigest(string value)
    {
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal))
            return false;

        foreach (var character in value.AsSpan(7))
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }

        return true;
    }

    private static bool IsSubset(
        IEnumerable<string> subset,
        IEnumerable<string> superset)
    {
        using var subsetEnumerator = subset.GetEnumerator();
        using var supersetEnumerator = superset.GetEnumerator();
        if (!subsetEnumerator.MoveNext())
            return true;

        while (supersetEnumerator.MoveNext())
        {
            var comparison = string.CompareOrdinal(subsetEnumerator.Current, supersetEnumerator.Current);
            if (comparison == 0)
            {
                if (!subsetEnumerator.MoveNext())
                    return true;
            }
            else if (comparison < 0)
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsCanonicalIdentifierList(IEnumerable<string> values)
    {
        string? previous = null;
        foreach (var value in values)
        {
            if (!IsCanonicalIdentifier(value) ||
                previous is not null && string.CompareOrdinal(previous, value) >= 0)
            {
                return false;
            }

            previous = value;
        }

        return true;
    }
}
