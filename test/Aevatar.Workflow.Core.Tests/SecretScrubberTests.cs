using Aevatar.Foundation.Abstractions.Helpers;
using FluentAssertions;

namespace Aevatar.Workflow.Core.Tests;

// Security hardening (H1): SecretScrubber is the shared boundary scrubber that masks secret-shaped
// substrings before free-form text (tool args/results, model content, upstream HTTP error bodies)
// is persisted into a read model or written to a log. These tests pin the masking contract that all
// call sites depend on.
public sealed class SecretScrubberTests
{
    [Fact]
    public void Scrub_ShouldMask_JwtShape()
    {
        // header.payload.signature base64url form.
        const string jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";
        var input = $"Bearer token was {jwt} and it failed";

        var scrubbed = SecretScrubber.Scrub(input);

        scrubbed.Should().NotContain(jwt);
        scrubbed.Should().Contain(SecretScrubber.Marker);
        scrubbed.Should().Contain("and it failed");
    }

    [Fact]
    public void Scrub_ShouldMask_ApiKeyJsonValue_KeepingTheKey()
    {
        const string secret = "sk-verysecretapikeyvalue1234567890";
        var input = $"{{\"api_key\":\"{secret}\",\"model\":\"gpt-5\"}}";

        var scrubbed = SecretScrubber.Scrub(input);

        scrubbed.Should().NotContain(secret);
        scrubbed.Should().Contain("\"api_key\"");
        scrubbed.Should().Contain(SecretScrubber.Marker);
        // Non-secret key/value survives.
        scrubbed.Should().Contain("\"model\":\"gpt-5\"");
    }

    [Theory]
    [InlineData("access_token")]
    [InlineData("refresh_token")]
    [InlineData("client_secret")]
    [InlineData("password")]
    [InlineData("private_key")]
    [InlineData("signing_key")]
    public void Scrub_ShouldMask_CommonSecretJsonValues(string key)
    {
        const string secret = "THE-SENSITIVE-VALUE-abc123";
        var input = $"{{\"{key}\":\"{secret}\"}}";

        var scrubbed = SecretScrubber.Scrub(input);

        scrubbed.Should().NotContain(secret);
        scrubbed.Should().Contain($"\"{key}\"");
        scrubbed.Should().Contain(SecretScrubber.Marker);
    }

    [Fact]
    public void Scrub_ShouldMask_AuthorizationBearerHeader_KeepingScheme()
    {
        var input = "Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789ABCDEF";

        var scrubbed = SecretScrubber.Scrub(input);

        scrubbed.Should().StartWith("Authorization: Bearer ");
        scrubbed.Should().NotContain("abcdefghijklmnopqrstuvwxyz0123456789ABCDEF");
        scrubbed.Should().Contain(SecretScrubber.Marker);
    }

    [Fact]
    public void Scrub_ShouldMask_LongHighEntropyToken()
    {
        var token = new string('A', 64) + "9z_-";
        var input = $"opaque credential {token} here";

        var scrubbed = SecretScrubber.Scrub(input);

        scrubbed.Should().NotContain(token);
        scrubbed.Should().Contain(SecretScrubber.Marker);
        scrubbed.Should().Contain("opaque credential");
    }

    [Fact]
    public void Scrub_ShouldPreserve_NonSecretText()
    {
        const string input = "The quick brown fox jumps over the lazy dog. status=401 route=chrono-llm";

        var scrubbed = SecretScrubber.Scrub(input);

        scrubbed.Should().Be(input);
        scrubbed.Should().NotContain(SecretScrubber.Marker);
    }

    [Fact]
    public void Scrub_ShouldPreserve_ShortIdentifiers()
    {
        // Ordinary ids / short hashes must not be flagged as secrets.
        const string input = "run_id=abc123 step_id=step-2 sha=d36c1a04c";

        var scrubbed = SecretScrubber.Scrub(input);

        scrubbed.Should().Be(input);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Scrub_ShouldReturnEmpty_ForNullOrEmpty(string? input)
    {
        SecretScrubber.Scrub(input).Should().BeEmpty();
    }
}
