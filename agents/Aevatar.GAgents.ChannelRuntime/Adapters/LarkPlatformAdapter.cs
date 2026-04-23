using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime.Adapters;

/// <summary>
/// Legacy direct Lark adapter kept only as a compatibility shell.
/// ChannelRuntime no longer accepts direct Lark callbacks or sends direct
/// platform replies; supported production traffic goes through Nyx relay.
/// </summary>
public sealed class LarkPlatformAdapter : IPlatformAdapter
{
    private readonly ILogger<LarkPlatformAdapter> _logger;

    public LarkPlatformAdapter(ILogger<LarkPlatformAdapter> logger) => _logger = logger;

    public string Platform => "lark";

    public Task<IResult?> TryHandleVerificationAsync(
        HttpContext http,
        ChannelBotRegistrationEntry registration)
    {
        _ = http;
        return Task.FromResult<IResult?>(Results.Json(
            new
            {
                error = "lark_direct_callback_retired",
                registration_id = registration.Id,
            },
            statusCode: StatusCodes.Status410Gone));
    }

    public Task<InboundMessage?> ParseInboundAsync(
        HttpContext http,
        ChannelBotRegistrationEntry registration)
    {
        _ = http;
        _ = registration;
        return Task.FromResult<InboundMessage?>(null);
    }

    public Task<PlatformReplyDeliveryResult> SendReplyAsync(
        string replyText,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        NyxIdApiClient nyxClient,
        CancellationToken ct)
    {
        _ = replyText;
        _ = inbound;
        _ = registration;
        _ = nyxClient;
        _ = ct;
        _logger.LogWarning(
            "Direct Lark platform reply is retired: registration={RegistrationId}",
            registration.Id);
        return Task.FromResult(new PlatformReplyDeliveryResult(
            false,
            "lark_direct_platform_reply_retired use_nyx_channel_relay_reply",
            PlatformReplyFailureKind.Permanent));
    }

    internal static bool IsRefreshableAuthFailure(PlatformReplyDeliveryResult result) =>
        !result.Succeeded &&
        !string.IsNullOrWhiteSpace(result.Detail) &&
        result.Detail.Contains("token_expired", StringComparison.OrdinalIgnoreCase);

    internal static bool IsInteractiveCardPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   (root.TryGetProperty("elements", out var elements) && elements.ValueKind == JsonValueKind.Array ||
                    root.TryGetProperty("header", out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static string ComputeLarkSignature(string timestamp, string nonce, string encryptKey, string body)
    {
        var raw = string.Concat(timestamp ?? string.Empty, nonce ?? string.Empty, encryptKey ?? string.Empty, body ?? string.Empty);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static string DecryptLarkPayload(string encrypted, string encryptKey)
    {
        var cipher = Convert.FromBase64String(encrypted);
        if (cipher.Length < 17)
            throw new CryptographicException("Lark encrypted payload too short for IV extraction");

        var key = SHA256.HashData(Encoding.UTF8.GetBytes(encryptKey));
        var iv = cipher[..16];
        var body = cipher[16..];

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        var plaintext = decryptor.TransformFinalBlock(body, 0, body.Length);
        return Encoding.UTF8.GetString(plaintext);
    }
}
