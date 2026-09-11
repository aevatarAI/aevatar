using Aevatar.Foundation.Abstractions.Credentials;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ChannelRegistrationAuthorizationContractTests
{
    [Fact]
    public void Classify_ValidNyxIdDefaultEntry_ReturnsNyxIdDefault()
    {
        var entry = ValidEntry();

        ChannelRegistrationAuthorizationContract.Classify(entry)
            .Should().Be(ChannelRegistrationAuthorizationContractKind.NyxIdDefault);
        ChannelRegistrationAuthorizationContract.TryGetAuthoritativeCredential(entry, out var credential)
            .Should().BeTrue();
        credential.Should().BeEquivalentTo(entry.ChannelAgentKey);
        credential.Should().NotBeSameAs(entry.ChannelAgentKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Classify_ValidExplicitEntry_ReturnsExplicitServiceAllowlist(bool includeBusinessService)
    {
        var entry = ValidExplicitEntry(includeBusinessService ? ["svc-business"] : []);

        ChannelRegistrationAuthorizationContract.Classify(entry)
            .Should().Be(ChannelRegistrationAuthorizationContractKind.ExplicitServiceAllowlist);
        ChannelRegistrationAuthorizationContract.TryGetAuthoritativeCredential(entry, out var credential)
            .Should().BeTrue();
        credential.Should().BeEquivalentTo(entry.ChannelAgentKey);
        credential.Should().NotBeSameAs(entry.ChannelAgentKey);
    }

    [Fact]
    public void Classify_TrueHistoricalEntry_ReturnsHistoricalLegacy()
    {
        var entry = new ChannelBotRegistrationEntry
        {
            Id = "reg-legacy",
            ScopeId = "scope-legacy",
            NyxAgentApiKeyId = "key-legacy",
            WorkflowResultDeliveryCredential = CompleteReference("scope-legacy", "sec-legacy"),
        };

        ChannelRegistrationAuthorizationContract.Classify(entry)
            .Should().Be(ChannelRegistrationAuthorizationContractKind.HistoricalLegacy);
        ChannelRegistrationAuthorizationContract.TryGetAuthoritativeCredential(entry, out var credential)
            .Should().BeFalse();
        credential.Should().BeNull();
    }

    [Fact]
    public void Classify_UnspecifiedModeWithNewCredential_ReturnsInvalid()
    {
        var entry = ValidEntry();
        entry.AuthorizationMode = ChannelRegistrationAuthorizationMode.Unspecified;

        ChannelRegistrationAuthorizationContract.Classify(entry)
            .Should().Be(ChannelRegistrationAuthorizationContractKind.Invalid);
    }

    [Fact]
    public void Classify_UnspecifiedModeWithAllowlist_ReturnsInvalid()
    {
        var entry = new ChannelBotRegistrationEntry
        {
            Id = "reg-corrupt",
            ScopeId = "scope-alpha",
            RegistrationServiceAllowlist = new ChannelRegistrationServiceAllowlist(),
        };

        ChannelRegistrationAuthorizationContract.Classify(entry)
            .Should().Be(ChannelRegistrationAuthorizationContractKind.Invalid);
    }

    [Fact]
    public void Classify_NyxIdDefaultWithoutCredential_ReturnsInvalid()
    {
        var entry = ValidEntry();
        entry.ChannelAgentKey = null;

        ChannelRegistrationAuthorizationContract.Classify(entry)
            .Should().Be(ChannelRegistrationAuthorizationContractKind.Invalid);
    }

    [Fact]
    public void Classify_UnknownMode_ReturnsInvalid()
    {
        var entry = ValidEntry();
        entry.AuthorizationMode = (ChannelRegistrationAuthorizationMode)99;

        ChannelRegistrationAuthorizationContract.Classify(entry)
            .Should().Be(ChannelRegistrationAuthorizationContractKind.Invalid);
    }

    [Fact]
    public void Classify_NyxIdDefaultWithAllowlist_ReturnsInvalid()
    {
        var entry = ValidEntry();
        entry.RegistrationServiceAllowlist = new ChannelRegistrationServiceAllowlist();

        ChannelRegistrationAuthorizationContract.Classify(entry)
            .Should().Be(ChannelRegistrationAuthorizationContractKind.Invalid);
    }

    [Fact]
    public void Classify_NyxIdDefaultWithScopePlanDigest_ReturnsInvalid()
    {
        var entry = ValidEntry();
        entry.ChannelAgentKey.Grant.ScopePlanDigest = ValidScopePlanDigest;

        ChannelRegistrationAuthorizationContract.Classify(entry)
            .Should().Be(ChannelRegistrationAuthorizationContractKind.Invalid);
    }

    [Fact]
    public void Classify_ExplicitModeWithoutAllowlist_ReturnsInvalid()
    {
        var entry = ValidExplicitEntry(["svc-business"]);
        entry.RegistrationServiceAllowlist = null;

        ChannelRegistrationAuthorizationContract.Classify(entry)
            .Should().Be(ChannelRegistrationAuthorizationContractKind.Invalid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha256:ABCDEF")]
    [InlineData("sha256:0123456789abcdef")]
    [InlineData("digest")]
    public void Classify_ExplicitModeWithInvalidScopePlanDigest_ReturnsInvalid(string digest)
    {
        var entry = ValidExplicitEntry(["svc-business"]);
        entry.ChannelAgentKey.Grant.ScopePlanDigest = digest;

        ChannelRegistrationAuthorizationContract.Classify(entry)
            .Should().Be(ChannelRegistrationAuthorizationContractKind.Invalid);
    }

    [Theory]
    [InlineData("services")]
    [InlineData("nodes")]
    public void Classify_ExplicitModeWithAllowAllGrant_ReturnsInvalid(string grantKind)
    {
        var entry = ValidExplicitEntry(["svc-business"]);

        if (grantKind == "services")
            entry.ChannelAgentKey.Grant.AllowAllServices = true;
        else
            entry.ChannelAgentKey.Grant.AllowAllNodes = true;

        ChannelRegistrationAuthorizationContract.Classify(entry)
            .Should().Be(ChannelRegistrationAuthorizationContractKind.Invalid);
    }

    [Fact]
    public void Classify_ExplicitModeWhenBusinessAllowlistIsNotGrantSubset_ReturnsInvalid()
    {
        var entry = ValidExplicitEntry(["svc-not-granted"]);

        ChannelRegistrationAuthorizationContract.Classify(entry)
            .Should().Be(ChannelRegistrationAuthorizationContractKind.Invalid);
    }

    [Theory]
    [InlineData("whitespace")]
    [InlineData("trim")]
    [InlineData("duplicate")]
    [InlineData("unsorted")]
    public void Classify_ExplicitModeWithNonCanonicalBusinessAllowlist_ReturnsInvalid(string shape)
    {
        var entry = ValidExplicitEntry([]);
        entry.RegistrationServiceAllowlist.ServiceIds.Add(shape switch
        {
            "whitespace" => ["svc-business", " "],
            "trim" => ["svc-business", " svc-other"],
            "duplicate" => ["svc-business", "svc-business"],
            "unsorted" => ["svc-other", "svc-business"],
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
        });

        ChannelRegistrationAuthorizationContract.Classify(entry)
            .Should().Be(ChannelRegistrationAuthorizationContractKind.Invalid);
    }

    [Theory]
    [InlineData("api_key_id")]
    [InlineData("secret_ref")]
    [InlineData("purpose")]
    [InlineData("owner_scope")]
    [InlineData("version")]
    [InlineData("fingerprint")]
    [InlineData("created_at")]
    public void Classify_IncompleteCredential_ReturnsInvalid(string missingField)
    {
        var entry = ValidEntry();

        switch (missingField)
        {
            case "api_key_id":
                entry.ChannelAgentKey.ApiKeyId = " ";
                break;
            case "secret_ref":
                entry.ChannelAgentKey.SecretReference.Ref = string.Empty;
                break;
            case "purpose":
                entry.ChannelAgentKey.SecretReference.Purpose = string.Empty;
                break;
            case "owner_scope":
                entry.ChannelAgentKey.SecretReference.OwnerScopeKey = string.Empty;
                break;
            case "version":
                entry.ChannelAgentKey.SecretReference.Version = 0;
                break;
            case "fingerprint":
                entry.ChannelAgentKey.SecretReference.Fingerprint = string.Empty;
                break;
            case "created_at":
                entry.ChannelAgentKey.SecretReference.CreatedAtUnixMs = 0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(missingField), missingField, null);
        }

        MirrorAliases(entry);

        ChannelRegistrationAuthorizationContract.Classify(entry)
            .Should().Be(ChannelRegistrationAuthorizationContractKind.Invalid);
    }

    [Fact]
    public void Classify_ReferencePurposeDoesNotMatchChannelAgentKey_ReturnsInvalid()
    {
        var entry = ValidEntry();
        entry.ChannelAgentKey.SecretReference.Purpose = "other-purpose";
        MirrorAliases(entry);

        ChannelRegistrationAuthorizationContract.Classify(entry)
            .Should().Be(ChannelRegistrationAuthorizationContractKind.Invalid);
    }

    [Fact]
    public void Classify_ReferenceOwnerDoesNotMatchRegistrationScope_ReturnsInvalid()
    {
        var entry = ValidEntry();
        entry.ChannelAgentKey.SecretReference.OwnerScopeKey = "scope-other";
        MirrorAliases(entry);

        ChannelRegistrationAuthorizationContract.Classify(entry)
            .Should().Be(ChannelRegistrationAuthorizationContractKind.Invalid);
    }

    [Theory]
    [InlineData("allow_all_services")]
    [InlineData("allow_all_nodes")]
    public void Classify_GrantBooleanPresenceMissing_ReturnsInvalid(string field)
    {
        var entry = ValidEntry();

        if (field == "allow_all_services")
            entry.ChannelAgentKey.Grant.ClearAllowAllServices();
        else
            entry.ChannelAgentKey.Grant.ClearAllowAllNodes();

        ChannelRegistrationAuthorizationContract.Classify(entry)
            .Should().Be(ChannelRegistrationAuthorizationContractKind.Invalid);
    }

    [Theory]
    [InlineData("whitespace")]
    [InlineData("trim")]
    [InlineData("duplicate")]
    [InlineData("unsorted")]
    public void Classify_NonCanonicalGrantIds_ReturnsInvalid(string shape)
    {
        var entry = ValidEntry();
        entry.ChannelAgentKey.Grant.AllowedServiceIds.Clear();
        entry.ChannelAgentKey.Grant.AllowedServiceIds.Add(shape switch
        {
            "whitespace" => ["svc-a", " "],
            "trim" => ["svc-a", " svc-b"],
            "duplicate" => ["svc-a", "svc-a"],
            "unsorted" => ["svc-b", "svc-a"],
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
        });

        ChannelRegistrationAuthorizationContract.Classify(entry)
            .Should().Be(ChannelRegistrationAuthorizationContractKind.Invalid);
    }

    [Fact]
    public void Classify_NonCanonicalNodeIds_ReturnsInvalid()
    {
        var entry = ValidEntry();
        entry.ChannelAgentKey.Grant.AllowedNodeIds.Clear();
        entry.ChannelAgentKey.Grant.AllowedNodeIds.Add(["node-b", "node-a"]);

        ChannelRegistrationAuthorizationContract.Classify(entry)
            .Should().Be(ChannelRegistrationAuthorizationContractKind.Invalid);
    }

    [Theory]
    [InlineData("services")]
    [InlineData("nodes")]
    public void Classify_AllowAllWithMatchingRestrictionList_ReturnsInvalid(string grantKind)
    {
        var entry = ValidEntry();

        if (grantKind == "services")
        {
            entry.ChannelAgentKey.Grant.AllowAllServices = true;
            entry.ChannelAgentKey.Grant.AllowedServiceIds.Add("svc-a");
        }
        else
        {
            entry.ChannelAgentKey.Grant.AllowAllNodes = true;
            entry.ChannelAgentKey.Grant.AllowedNodeIds.Add("node-a");
        }

        ChannelRegistrationAuthorizationContract.Classify(entry)
            .Should().Be(ChannelRegistrationAuthorizationContractKind.Invalid);
    }

    [Fact]
    public void Classify_LegacyKeyIdAliasMismatch_ReturnsInvalid()
    {
        var entry = ValidEntry();
        entry.NyxAgentApiKeyId = "key-other";

        ChannelRegistrationAuthorizationContract.Classify(entry)
            .Should().Be(ChannelRegistrationAuthorizationContractKind.Invalid);
    }

    [Fact]
    public void Classify_LegacySecretReferenceAliasMismatch_ReturnsInvalid()
    {
        var entry = ValidEntry();
        entry.WorkflowResultDeliveryCredential.Fingerprint = "sha256:other";

        ChannelRegistrationAuthorizationContract.Classify(entry)
            .Should().Be(ChannelRegistrationAuthorizationContractKind.Invalid);
    }

    [Fact]
    public void IsValidNewCommand_RequiresTheSameCompleteContract()
    {
        var valid = ValidCommand();
        var mismatchedKey = valid.Clone();
        mismatchedKey.NyxAgentApiKeyId = "key-other";
        var mismatchedReference = valid.Clone();
        mismatchedReference.WorkflowResultDeliveryCredential.Version++;
        var historicalShape = valid.Clone();
        historicalShape.AuthorizationMode = ChannelRegistrationAuthorizationMode.Unspecified;
        historicalShape.ChannelAgentKey = null;
        var validExplicit = ValidExplicitCommand(["svc-business"]);

        ChannelRegistrationAuthorizationContract.IsValidNewCommand(valid).Should().BeTrue();
        ChannelRegistrationAuthorizationContract.IsValidNewCommand(validExplicit).Should().BeTrue();
        ChannelRegistrationAuthorizationContract.IsValidNewCommand(mismatchedKey).Should().BeFalse();
        ChannelRegistrationAuthorizationContract.IsValidNewCommand(mismatchedReference).Should().BeFalse();
        ChannelRegistrationAuthorizationContract.IsValidNewCommand(historicalShape).Should().BeFalse();
    }

    [Fact]
    public void IsValidNewCommand_RejectsScopeThatWouldChangeDuringPersistence()
    {
        var command = ValidCommand();
        command.ScopeId = " scope-alpha ";
        command.ChannelAgentKey.SecretReference.OwnerScopeKey = command.ScopeId;
        command.WorkflowResultDeliveryCredential = command.ChannelAgentKey.SecretReference.Clone();

        ChannelRegistrationAuthorizationContract.IsValidNewCommand(command).Should().BeFalse();
    }

    private static ChannelBotRegistrationEntry ValidEntry()
    {
        var credential = ValidCredential("scope-alpha");
        return new ChannelBotRegistrationEntry
        {
            Id = "reg-alpha",
            ScopeId = "scope-alpha",
            NyxAgentApiKeyId = credential.ApiKeyId,
            WorkflowResultDeliveryCredential = credential.SecretReference.Clone(),
            ChannelAgentKey = credential,
            AuthorizationMode = ChannelRegistrationAuthorizationMode.NyxidDefault,
        };
    }

    private static ChannelBotRegisterCommand ValidCommand()
    {
        var credential = ValidCredential("scope-alpha");
        return new ChannelBotRegisterCommand
        {
            RequestedId = "reg-alpha",
            ScopeId = "scope-alpha",
            NyxAgentApiKeyId = credential.ApiKeyId,
            WorkflowResultDeliveryCredential = credential.SecretReference.Clone(),
            ChannelAgentKey = credential,
            AuthorizationMode = ChannelRegistrationAuthorizationMode.NyxidDefault,
        };
    }

    private static ChannelBotRegistrationEntry ValidExplicitEntry(IEnumerable<string> serviceIds)
    {
        var credential = ValidExplicitCredential("scope-alpha");
        var entry = new ChannelBotRegistrationEntry
        {
            Id = "reg-explicit",
            ScopeId = "scope-alpha",
            NyxAgentApiKeyId = credential.ApiKeyId,
            WorkflowResultDeliveryCredential = credential.SecretReference.Clone(),
            ChannelAgentKey = credential,
            AuthorizationMode = ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist,
            RegistrationServiceAllowlist = new ChannelRegistrationServiceAllowlist(),
        };
        entry.RegistrationServiceAllowlist.ServiceIds.Add(serviceIds);
        return entry;
    }

    private static ChannelBotRegisterCommand ValidExplicitCommand(IEnumerable<string> serviceIds)
    {
        var credential = ValidExplicitCredential("scope-alpha");
        var command = new ChannelBotRegisterCommand
        {
            RequestedId = "reg-explicit",
            ScopeId = "scope-alpha",
            NyxAgentApiKeyId = credential.ApiKeyId,
            WorkflowResultDeliveryCredential = credential.SecretReference.Clone(),
            ChannelAgentKey = credential,
            AuthorizationMode = ChannelRegistrationAuthorizationMode.ExplicitServiceAllowlist,
            RegistrationServiceAllowlist = new ChannelRegistrationServiceAllowlist(),
        };
        command.RegistrationServiceAllowlist.ServiceIds.Add(serviceIds);
        return command;
    }

    private static ChannelAgentKeyCredential ValidCredential(string scopeId) => new()
    {
        ApiKeyId = "key-alpha",
        SecretReference = CompleteReference(scopeId, "sec-alpha"),
        Grant = new ChannelAgentKeyGrantSnapshot
        {
            AllowAllServices = false,
            AllowAllNodes = false,
            AllowedServiceIds = { "svc-a", "svc-b" },
            AllowedNodeIds = { "node-a", "node-b" },
        },
    };

    private static ChannelAgentKeyCredential ValidExplicitCredential(string scopeId) => new()
    {
        ApiKeyId = "key-explicit",
        SecretReference = CompleteReference(scopeId, "sec-explicit"),
        Grant = new ChannelAgentKeyGrantSnapshot
        {
            ScopePlanDigest = ValidScopePlanDigest,
            AllowAllServices = false,
            AllowAllNodes = false,
            AllowedServiceIds = { "svc-business", "svc-internal" },
            AllowedNodeIds = { "node-a", "node-b" },
        },
    };

    private const string ValidScopePlanDigest =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static SecretReference CompleteReference(string scopeId, string reference) => new()
    {
        Ref = reference,
        Purpose = CredentialSecretPurposes.ChannelNyxIdAgentKey,
        OwnerScopeKey = scopeId,
        Version = 1,
        Fingerprint = "sha256:test",
        CreatedAtUnixMs = 1788912000000,
        ExpiresAtUnixMs = 0,
    };

    private static void MirrorAliases(ChannelBotRegistrationEntry entry)
    {
        entry.NyxAgentApiKeyId = entry.ChannelAgentKey.ApiKeyId;
        entry.WorkflowResultDeliveryCredential = entry.ChannelAgentKey.SecretReference.Clone();
    }
}
