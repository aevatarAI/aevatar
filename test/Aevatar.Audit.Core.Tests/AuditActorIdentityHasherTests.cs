using System.Text;
using Aevatar.Audit.Core.Identity;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Aevatar.Audit.Core.Tests;

public sealed class AuditActorIdentityHasherTests
{
    [Fact]
    public void Hash_IsDeterministicAndDoesNotExposeCanonicalKey()
    {
        var hasher = CreateHasher("key-1", Key("active secret material for audit identity"));
        var canonicalKey = AuditCanonicalActorKeys.ForNyxIdUser("user-123");

        var first = hasher.Hash(canonicalKey);
        var second = hasher.Hash(canonicalKey);

        first.ShouldBe(second);
        first.IdentityKeyId.ShouldBe("key-1");
        first.AuditActorId.ShouldStartWith("audit_actor:hmac-sha256:");
        first.AuditActorId.ShouldNotContain("user-123");
        first.AuditActorId.ShouldNotBe(canonicalKey);
        hasher.Verify(canonicalKey, first.AuditActorId, first.IdentityKeyId).ShouldBeTrue();
    }

    [Fact]
    public void Hash_UsesActiveKey_AndVerifyAcceptsRotationKey()
    {
        var options = Options.Create(new AuditActorIdentityHasherOptions
        {
            ActiveKeyId = "key-2",
            Keys =
            [
                new AuditActorIdentityHasherKeyOptions { KeyId = "key-1", Key = Key("old secret material for audit identity") },
                new AuditActorIdentityHasherKeyOptions { KeyId = "key-2", Key = Key("new secret material for audit identity") }
            ]
        });
        var hasher = new AuditActorIdentityHasher(options);
        var canonicalKey = AuditCanonicalActorKeys.ForSchedule("schedule-123");

        var activeIdentity = hasher.Hash(canonicalKey);
        var oldHasher = CreateHasher("key-1", Key("old secret material for audit identity"));
        var oldIdentity = oldHasher.Hash(canonicalKey);

        activeIdentity.IdentityKeyId.ShouldBe("key-2");
        activeIdentity.AuditActorId.ShouldNotBe(oldIdentity.AuditActorId);
        hasher.Verify(canonicalKey, oldIdentity.AuditActorId, oldIdentity.IdentityKeyId).ShouldBeTrue();
    }

    [Fact]
    public void HashAll_ReturnsActiveFirstThenRemainingKeysInOrdinalOrder()
    {
        var hasher = new AuditActorIdentityHasher(Options.Create(new AuditActorIdentityHasherOptions
        {
            ActiveKeyId = "key-2",
            Keys =
            [
                new AuditActorIdentityHasherKeyOptions { KeyId = "key-3", Key = Key("third secret material for audit identity") },
                new AuditActorIdentityHasherKeyOptions { KeyId = "key-1", Key = Key("old secret material for audit identity") },
                new AuditActorIdentityHasherKeyOptions { KeyId = "key-2", Key = Key("new secret material for audit identity") },
            ],
        }));
        var canonicalKey = AuditCanonicalActorKeys.ForNyxIdUser("user-audit-alpha");

        var identities = hasher.HashAll(canonicalKey);

        identities.Select(static identity => identity.IdentityKeyId)
            .ShouldBe(["key-2", "key-1", "key-3"]);
        identities.Select(static identity => identity.AuditActorId).Distinct().Count().ShouldBe(3);
        identities[0].ShouldBe(hasher.Hash(canonicalKey));
        Should.Throw<ArgumentException>(() => hasher.HashAll("  "));
    }

    [Fact]
    public void Constructor_FailsClosed_WhenSecretIsMissing()
    {
        Should.Throw<OptionsValidationException>(() => new AuditActorIdentityHasher(
            Options.Create(new AuditActorIdentityHasherOptions
            {
                ActiveKeyId = "missing"
            })));
    }

    [Fact]
    public void CanonicalActorKeys_RejectAmbiguousSegments()
    {
        Should.Throw<ArgumentException>(() => AuditCanonicalActorKeys.ForChannelSender("lark", "scope:bad", "sender"));
    }

    private static AuditActorIdentityHasher CreateHasher(string keyId, string key)
    {
        return new AuditActorIdentityHasher(Options.Create(new AuditActorIdentityHasherOptions
        {
            ActiveKeyId = keyId,
            Keys = [new AuditActorIdentityHasherKeyOptions { KeyId = keyId, Key = key }]
        }));
    }

    private static string Key(string seed)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(seed.PadRight(32, '!')));
    }
}
