using System.Reflection;
using System.Security.Claims;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Hosting.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Studio.Tests;

public sealed class ChatHistoryEndpointsTests
{
    private const string RequestedScopeId = "scope-alice";
    private const string ConversationId = "conversation-1";

    [Theory]
    [InlineData("HandleGetIndex")]
    [InlineData("HandleGetConversation")]
    [InlineData("HandleSaveConversation")]
    [InlineData("HandleDeleteConversation")]
    public async Task Handler_ShouldRejectDifferentCallerScopeBeforeAccessingHistory(string methodName)
    {
        var port = new RecordingChatHistoryPort();

        var result = await InvokeHandlerAsync(
            methodName,
            CreateAuthenticatedContext("scope-bob"),
            port);

        GetStatusCode(result).Should().Be(StatusCodes.Status403Forbidden);
        port.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("HandleGetIndex")]
    [InlineData("HandleGetConversation")]
    [InlineData("HandleSaveConversation")]
    [InlineData("HandleDeleteConversation")]
    public async Task Handler_ShouldAllowMatchingCallerScope(string methodName)
    {
        var port = new RecordingChatHistoryPort();

        var result = await InvokeHandlerAsync(
            methodName,
            CreateAuthenticatedContext(RequestedScopeId),
            port);

        GetStatusCode(result).Should().Be(StatusCodes.Status200OK);
        port.Calls.Should().ContainSingle().Which.Should().StartWith(methodName);
    }

    private static async Task<IResult> InvokeHandlerAsync(
        string methodName,
        HttpContext http,
        RecordingChatHistoryPort port)
    {
        var method = typeof(ChatHistoryEndpoints).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{methodName} not found.");
        object?[] arguments = methodName switch
        {
            "HandleGetIndex" => [http, RequestedScopeId, port, CancellationToken.None],
            "HandleGetConversation" =>
                [http, RequestedScopeId, ConversationId, port, CancellationToken.None],
            "HandleSaveConversation" =>
                [http, RequestedScopeId, ConversationId, CreateSaveRequest(), port, CancellationToken.None],
            "HandleDeleteConversation" =>
                [http, RequestedScopeId, ConversationId, port, CancellationToken.None],
            _ => throw new ArgumentOutOfRangeException(nameof(methodName), methodName, null),
        };
        return await (Task<IResult>)method.Invoke(null, arguments)!;
    }

    private static ChatHistoryEndpoints.SaveConversationRequest CreateSaveRequest()
    {
        var now = DateTimeOffset.UtcNow;
        return new ChatHistoryEndpoints.SaveConversationRequest(
            new ConversationMeta(
                ConversationId,
                "Conversation",
                "service-1",
                "workflow",
                now,
                now,
                MessageCount: 0),
            []);
    }

    private static HttpContext CreateAuthenticatedContext(string claimedScopeId)
    {
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Aevatar:Authentication:Enabled"] = "true",
                })
                .Build())
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment())
            .BuildServiceProvider();
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("scope_id", claimedScopeId)],
                "test")),
            RequestServices = services,
        };
    }

    private static int? GetStatusCode(IResult result) =>
        result.GetType().GetProperty("StatusCode")?.GetValue(result) as int?;

    private sealed class RecordingChatHistoryPort : IChatHistoryQueryPort, IChatHistoryCommandPort
    {
        public List<string> Calls { get; } = [];

        public Task<ChatHistoryIndex> GetIndexAsync(string scopeId, CancellationToken ct = default)
        {
            Calls.Add($"HandleGetIndex:{scopeId}");
            return Task.FromResult(new ChatHistoryIndex([]));
        }

        public Task<IReadOnlyList<StoredChatMessage>> GetMessagesAsync(
            string scopeId,
            string conversationId,
            CancellationToken ct = default)
        {
            Calls.Add($"HandleGetConversation:{scopeId}:{conversationId}");
            return Task.FromResult<IReadOnlyList<StoredChatMessage>>([]);
        }

        public Task SaveMessagesAsync(
            string scopeId,
            string conversationId,
            ConversationMeta meta,
            IReadOnlyList<StoredChatMessage> messages,
            CancellationToken ct = default)
        {
            Calls.Add($"HandleSaveConversation:{scopeId}:{conversationId}");
            return Task.CompletedTask;
        }

        public Task DeleteConversationAsync(
            string scopeId,
            string conversationId,
            CancellationToken ct = default)
        {
            Calls.Add($"HandleDeleteConversation:{scopeId}:{conversationId}");
            return Task.CompletedTask;
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Aevatar.Studio.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
