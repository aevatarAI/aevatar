using System.Security.Cryptography;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

/// <summary>
/// Binding HMAC secrets are persisted only under authenticated encryption
/// keyed by host configuration: ciphertext is versioned and opaque, a wrong
/// key cannot decrypt, and pre-encryption plaintext records stay readable so
/// a re-PUT migrates them forward.
/// </summary>
public sealed class WorkflowWebhookBindingSecretCipherTests
{
    [Fact]
    public void ProtectThenUnprotect_ShouldRoundTrip_WithoutLeakingPlaintext()
    {
        var cipher = new AesGcmWorkflowWebhookBindingSecretCipher("host-encryption-passphrase");

        var stored = cipher.Protect("delivery-signing-secret");

        stored.Should().StartWith("enc:v1:");
        stored.Should().NotContain("delivery-signing-secret");
        cipher.Unprotect(stored).Should().Be("delivery-signing-secret");
    }

    [Fact]
    public void Unprotect_WithWrongKey_ShouldThrow()
    {
        var writer = new AesGcmWorkflowWebhookBindingSecretCipher("key-a");
        var reader = new AesGcmWorkflowWebhookBindingSecretCipher("key-b");

        var stored = writer.Protect("delivery-signing-secret");

        var act = () => reader.Unprotect(stored);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Unprotect_WithLegacyPlaintextRecord_ShouldReturnValueUnchanged()
    {
        var cipher = new AesGcmWorkflowWebhookBindingSecretCipher("host-encryption-passphrase");

        cipher.Unprotect("legacy-plaintext-secret").Should().Be("legacy-plaintext-secret");
    }

    [Fact]
    public void TryDerivePassphraseFromKeyring_ShouldBeStable_AndTrackActiveKey()
    {
        var path = Path.Combine(Path.GetTempPath(), $"keyring-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"activeKeyId":"k1","keys":{"k1":"QUFB","k0":"QkJC"},"fingerprintKey":"Q0ND"}""");

            var derived = AesGcmWorkflowWebhookBindingSecretCipher.TryDerivePassphraseFromKeyring(path);
            derived.Should().NotBeNullOrWhiteSpace();
            AesGcmWorkflowWebhookBindingSecretCipher.TryDerivePassphraseFromKeyring(path)
                .Should().Be(derived, "same keyring must derive the same key on every host/replica");

            // Rotating the active key changes the derivation (a real key change).
            File.WriteAllText(path, """{"activeKeyId":"k2","keys":{"k1":"QUFB","k2":"REREREQ="},"fingerprintKey":"Q0ND"}""");
            AesGcmWorkflowWebhookBindingSecretCipher.TryDerivePassphraseFromKeyring(path)
                .Should().NotBe(derived);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void KeyringCipher_AfterRotation_ShouldReadRetainedHistoricalCiphertext()
    {
        var path = Path.Combine(Path.GetTempPath(), $"keyring-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"activeKeyId":"k1","keys":{"k1":"QUFB"}}""");
            var writer = AesGcmWorkflowWebhookBindingSecretCipher.TryCreateFromKeyring(path);
            writer.Should().NotBeNull();
            var oldCiphertext = writer!.Protect("delivery-signing-secret");

            File.WriteAllText(
                path,
                """{"activeKeyId":"k2","keys":{"k1":"QUFB","k2":"REREREQ="}}""");
            var rotated = AesGcmWorkflowWebhookBindingSecretCipher.TryCreateFromKeyring(path);

            rotated.Should().NotBeNull();
            rotated!.Unprotect(oldCiphertext).Should().Be("delivery-signing-secret");
            var newCiphertext = rotated.Protect("new-secret");
            var oldOnly = writer;
            var act = () => oldOnly.Unprotect(newCiphertext);
            act.Should().Throw<CryptographicException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryParseState_WithLegacyJsonValue_ShouldReturnFalseInsteadOfThrowing()
    {
        // Bindings written before the proto storage format are JSON; the
        // store must treat them as absent (reclaimable/deletable), not crash
        // every read of the route.
        var legacyJson = System.Text.Encoding.UTF8.GetBytes(
            """{"routeKey":"status-event-route","scopeId":"scope-1","workflowName":"wf"}""");

        RedisWorkflowWebhookBindingStore.TryParseState(legacyJson, out _).Should().BeFalse();

        var real = new Aevatar.Workflow.Abstractions.WorkflowWebhookBindingState
        {
            RouteKey = "status-event-route",
            ScopeId = "scope-1",
        };
        RedisWorkflowWebhookBindingStore.TryParseState(
            Google.Protobuf.MessageExtensions.ToByteArray(real), out var parsed).Should().BeTrue();
        parsed.RouteKey.Should().Be("status-event-route");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/nonexistent/keyring.json")]
    public void TryDerivePassphraseFromKeyring_WithMissingKeyring_ShouldFailClosed(string? path)
    {
        AesGcmWorkflowWebhookBindingSecretCipher.TryDerivePassphraseFromKeyring(path).Should().BeNull();
    }

    [Fact]
    public void TryDerivePassphraseFromKeyring_WithMalformedKeyring_ShouldFailClosed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"keyring-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "not json at all");
            AesGcmWorkflowWebhookBindingSecretCipher.TryDerivePassphraseFromKeyring(path).Should().BeNull();

            File.WriteAllText(path, """{"activeKeyId":"missing","keys":{"k1":"QUFB"}}""");
            AesGcmWorkflowWebhookBindingSecretCipher.TryDerivePassphraseFromKeyring(path).Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
