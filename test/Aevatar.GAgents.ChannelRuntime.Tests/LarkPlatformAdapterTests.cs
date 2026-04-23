using System.Security.Cryptography;
using System.Text;
using System.Linq;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.ChannelRuntime.Adapters;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class LarkPlatformAdapterTests
{
    private readonly LarkPlatformAdapter _adapter = new(NullLogger<LarkPlatformAdapter>.Instance);

    private static ChannelBotRegistrationEntry MakeRegistration() => new()
    {
        Id = "test-reg-1",
        Platform = "lark",
        NyxProviderSlug = "api-lark-bot",
        ScopeId = "test-scope",
        CreatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
    };

    [Fact]
    public void Platform_returns_lark()
    {
        _adapter.Platform.Should().Be("lark");
    }

    [Fact]
    public async Task TryHandleVerification_returns_gone_for_retired_direct_callback()
    {
        var result = await _adapter.TryHandleVerificationAsync(new DefaultHttpContext(), MakeRegistration());

        var response = await ExecuteResultAsync(result);
        response.StatusCode.Should().Be(StatusCodes.Status410Gone);
        response.Body.Should().Contain("lark_direct_callback_retired");
        response.Body.Should().Contain("test-reg-1");
    }

    [Fact]
    public async Task ParseInbound_returns_null()
    {
        var inbound = await _adapter.ParseInboundAsync(new DefaultHttpContext(), MakeRegistration());

        inbound.Should().BeNull();
    }

    [Fact]
    public async Task SendReply_returns_retired_contract_failure()
    {
        var result = await _adapter.SendReplyAsync(
            "hello",
            new InboundMessage
            {
                Platform = "lark",
                ConversationId = "chat-1",
                SenderId = "user-1",
                SenderName = "user-1",
                Text = "hello",
            },
            MakeRegistration(),
            new NyxIdApiClient(new NyxIdToolOptions { BaseUrl = "https://nyx.example.com" }),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureKind.Should().Be(PlatformReplyFailureKind.Permanent);
        result.Detail.Should().Contain("lark_direct_platform_reply_retired");
    }

    [Fact]
    public void IsRefreshableAuthFailure_matches_token_expired_detail()
    {
        var result = new PlatformReplyDeliveryResult(false, "token_expired", PlatformReplyFailureKind.Transient);

        LarkPlatformAdapter.IsRefreshableAuthFailure(result).Should().BeTrue();
    }

    [Fact]
    public void ComputeLarkSignature_is_deterministic()
    {
        var signature = LarkPlatformAdapter.ComputeLarkSignature("123", "nonce", "encrypt-key", "{\"a\":1}");

        signature.Should().Be(LarkPlatformAdapter.ComputeLarkSignature("123", "nonce", "encrypt-key", "{\"a\":1}"));
        signature.Should().HaveLength(64);
    }

    [Fact]
    public void DecryptLarkPayload_round_trips()
    {
        const string plaintext = "{\"text\":\"hello\"}";
        const string encryptKey = "encrypt-key";
        var encrypted = EncryptForLark(plaintext, encryptKey);

        var decrypted = LarkPlatformAdapter.DecryptLarkPayload(encrypted, encryptKey);

        decrypted.Should().Be(plaintext);
    }

    private static async Task<(int StatusCode, string Body)> ExecuteResultAsync(IResult? result)
    {
        result.Should().NotBeNull();
        var http = new DefaultHttpContext();
        var builder = WebApplication.CreateBuilder();
        http.RequestServices = builder.Services.BuildServiceProvider();
        http.Response.Body = new MemoryStream();
        await result!.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var reader = new StreamReader(http.Response.Body, Encoding.UTF8);
        return (http.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private static string EncryptForLark(string plaintext, string encryptKey)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(encryptKey));
        var iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;
        using var encryptor = aes.CreateEncryptor();
        var cipher = encryptor.TransformFinalBlock(Encoding.UTF8.GetBytes(plaintext), 0, Encoding.UTF8.GetByteCount(plaintext));
        return Convert.ToBase64String(iv.Concat(cipher).ToArray());
    }
}
