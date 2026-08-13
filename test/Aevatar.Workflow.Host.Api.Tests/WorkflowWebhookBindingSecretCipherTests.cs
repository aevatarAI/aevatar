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
}
