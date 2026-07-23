using System.Reflection;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatEndpointsHelperCoverageTests
{
    private static readonly MethodInfo ExtractNyxIdAccessTokenMethod = typeof(NyxIdChatEndpoints)
        .GetMethod("ExtractNyxIdAccessToken", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ExtractNyxIdAccessToken not found.");

    private static readonly MethodInfo TryExtractJwtSubjectMethod = typeof(NyxIdChatEndpoints)
        .GetMethod("TryExtractJwtSubject", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("TryExtractJwtSubject not found.");


    [Theory]
    [InlineData("image", ChatContentPartKind.Image)]
    [InlineData("audio", ChatContentPartKind.Audio)]
    [InlineData("video", ChatContentPartKind.Video)]
    [InlineData("text", ChatContentPartKind.Text)]
    [InlineData("unknown", ChatContentPartKind.Unspecified)]
    public void ContentPartDto_ToProto_ShouldMapKindsAndPreservePayload(string type, ChatContentPartKind expectedKind)
    {
        var dto = new NyxIdChatEndpoints.ContentPartDto(
            Type: type,
            Text: "hello",
            DataBase64: "ZGF0YQ==",
            MediaType: "image/png",
            Uri: "https://example.com/file",
            Name: "file.bin");

        var proto = dto.ToProto();
        proto.Kind.Should().Be(expectedKind);
        proto.Text.Should().Be("hello");
        proto.DataBase64.Should().Be("ZGF0YQ==");
        proto.MediaType.Should().Be("image/png");
        proto.Uri.Should().Be("https://example.com/file");
        proto.Name.Should().Be("file.bin");
    }

    [Fact]
    public void ExtractNyxIdAccessToken_ShouldHandleMissingBearerAndOtherSchemes()
    {
        var missing = new DefaultHttpContext();
        InvokePrivateStatic<string?>(ExtractNyxIdAccessTokenMethod, missing).Should().BeNull();

        var basic = new DefaultHttpContext();
        basic.Request.Headers.Authorization = "Basic abc";
        InvokePrivateStatic<string?>(ExtractNyxIdAccessTokenMethod, basic).Should().BeNull();

        var bearer = new DefaultHttpContext();
        bearer.Request.Headers.Authorization = "Bearer token-123";
        InvokePrivateStatic<string?>(ExtractNyxIdAccessTokenMethod, bearer).Should().Be("token-123");
    }

    [Fact]
    public void TryExtractJwtSubject_ShouldHandleValidMissingAndInvalidPayloads()
    {
        InvokePrivateStatic<string?>(
                TryExtractJwtSubjectMethod,
                BuildJwt("{\"sub\":\"user-1\"}"))
            .Should()
            .Be("user-1");

        InvokePrivateStatic<string?>(
                TryExtractJwtSubjectMethod,
                BuildJwt("{\"name\":\"alice\"}"))
            .Should()
            .BeNull();

        InvokePrivateStatic<string?>(TryExtractJwtSubjectMethod, "not-a-jwt").Should().BeNull();
    }

    private static string BuildJwt(string payloadJson)
    {
        var header = Base64UrlEncode("{\"alg\":\"none\"}");
        var payload = Base64UrlEncode(payloadJson);
        return $"{header}.{payload}.";
    }

    private static string Base64UrlEncode(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static T InvokePrivateStatic<T>(MethodInfo method, params object?[] args)
    {
        try
        {
            return (T)method.Invoke(null, args)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }
}
