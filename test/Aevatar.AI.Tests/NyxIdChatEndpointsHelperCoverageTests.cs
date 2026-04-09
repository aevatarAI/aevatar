using System.Reflection;
using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatEndpointsHelperCoverageTests
{
    private static readonly MethodInfo ExtractBearerTokenMethod = typeof(NyxIdChatEndpoints)
        .GetMethod("ExtractBearerToken", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ExtractBearerToken not found.");

    private static readonly MethodInfo TryExtractJwtSubjectMethod = typeof(NyxIdChatEndpoints)
        .GetMethod("TryExtractJwtSubject", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("TryExtractJwtSubject not found.");

    private static readonly MethodInfo BuildConnectedServicesContextMethod = typeof(NyxIdChatEndpoints)
        .GetMethod("BuildConnectedServicesContext", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildConnectedServicesContext not found.");

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
    public void ExtractBearerToken_ShouldHandleMissingBearerAndOtherSchemes()
    {
        var missing = new DefaultHttpContext();
        InvokePrivateStatic<string?>(ExtractBearerTokenMethod, missing).Should().BeNull();

        var basic = new DefaultHttpContext();
        basic.Request.Headers.Authorization = "Basic abc";
        InvokePrivateStatic<string?>(ExtractBearerTokenMethod, basic).Should().BeNull();

        var bearer = new DefaultHttpContext();
        bearer.Request.Headers.Authorization = "Bearer token-123";
        InvokePrivateStatic<string?>(ExtractBearerTokenMethod, bearer).Should().Be("token-123");
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

    [Fact]
    public void BuildConnectedServicesContext_ShouldUseFallbackNamesAndNoServicesMessage()
    {
        var context = InvokePrivateStatic<string>(
            BuildConnectedServicesContextMethod,
            """
            {
              "data": [
                {
                  "slug": "calendar",
                  "label": "Calendar",
                  "base_url": "https://calendar.example.com"
                },
                {
                  "slug": "docs",
                  "name": "Docs",
                  "endpoint_url": "https://docs.example.com"
                }
              ]
            }
            """);

        context.Should().Contain("Calendar");
        context.Should().Contain("docs");
        context.Should().Contain("https://calendar.example.com");
        context.Should().Contain("https://docs.example.com");
        context.Should().Contain("nyxid_proxy");

        var emptyContext = InvokePrivateStatic<string>(
            BuildConnectedServicesContextMethod,
            "{\"services\":[]}");
        emptyContext.Should().Contain("No services connected yet");
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
