using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Hosting.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aevatar.Studio.Tests;

public sealed class ChatHistoryEndpointsTests
{
    private const string RequestedScopeId = "scope-alice";
    private const string ConversationId = "conversation-1";

    [Theory]
    [InlineData("HandleGetIndex")]
    [InlineData("HandleGetConversation")]
    [InlineData("HandleGetCreateRecovery")]
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
    [InlineData("HandleGetCreateRecovery")]
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

    [Fact]
    public async Task HandleGetCreateRecovery_ShouldSerializeStatusAsStableWireName()
    {
        var port = new RecordingChatHistoryPort();
        var http = CreateAuthenticatedContext(RequestedScopeId);
        http.Response.Body = new MemoryStream();

        var result = await InvokeHandlerAsync(
            "HandleGetCreateRecovery",
            http,
            port);

        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(http.Response.Body);

        body.RootElement.GetProperty("status").GetString().Should().Be("reserved");
        body.RootElement.GetProperty("conversationId").GetString().Should().Be("conversation-1");
        body.RootElement.GetProperty("turnId").GetString().Should().Be("turn-1");
    }

    [Fact]
    public async Task HandleGetConversation_ShouldSerializeMessagesWithSourceStateVersion()
    {
        var port = new RecordingChatHistoryPort();
        var http = CreateAuthenticatedContext(RequestedScopeId);
        http.Response.Body = new MemoryStream();

        var result = await InvokeHandlerAsync(
            "HandleGetConversation",
            http,
            port);

        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(http.Response.Body);

        body.RootElement.GetProperty("stateVersion").GetInt64().Should().Be(7);
        body.RootElement.GetProperty("messages").EnumerateArray()
            .Should()
            .ContainSingle()
            .Which.GetProperty("content").GetString()
            .Should()
            .Be("Choose a Team: team01 or team02.");
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
            "HandleGetIndex" => [http, RequestedScopeId, 75, "cursor-1", port, CancellationToken.None],
            "HandleGetConversation" =>
                [http, RequestedScopeId, ConversationId, port, CancellationToken.None],
            "HandleGetCreateRecovery" =>
                [http, RequestedScopeId, "create-command-1", port, CancellationToken.None],
            "HandleDeleteConversation" =>
                [http, RequestedScopeId, ConversationId, port, CancellationToken.None],
            _ => throw new ArgumentOutOfRangeException(nameof(methodName), methodName, null),
        };
        return await (Task<IResult>)method.Invoke(null, arguments)!;
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
            .AddLogging()
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

        public Task InitializeConversationAsync(
            ChatHistoryConversationInitialization request,
            CancellationToken ct = default)
        {
            Calls.Add($"HandleInitializeConversation:{request.ScopeId}:{request.ConversationId}");
            return Task.CompletedTask;
        }

        public Task ReserveTurnDeliveryAsync(
            ChatHistoryTurnDeliveryReservation request,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task NotifyTurnTerminalAsync(
            ChatHistoryTurnTerminalNotification notification,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<ChatHistoryIndexPage> GetIndexAsync(
            ChatHistoryIndexPageRequest request,
            CancellationToken ct = default)
        {
            Calls.Add($"HandleGetIndex:{request.ScopeId}:{request.PageSize}:{request.Cursor}");
            return Task.FromResult(new ChatHistoryIndexPage([], null));
        }

        public Task<ChatHistoryConversationMessagesResult> GetMessagesAsync(
            string scopeId,
            string conversationId,
            CancellationToken ct = default)
        {
            Calls.Add($"HandleGetConversation:{scopeId}:{conversationId}");
            return Task.FromResult(ChatHistoryConversationMessagesResult.Found(
                [
                    new StoredChatMessage(
                        "turn-1:assistant",
                        "assistant",
                        "Choose a Team: team01 or team02.",
                        1784700000000,
                        "complete",
                        TurnId: "turn-1"),
                ],
                7));
        }

        public Task<ChatHistoryCreateRecoveryResult> GetCreateRecoveryAsync(
            string scopeId,
            string commandId,
            CancellationToken ct = default)
        {
            Calls.Add($"HandleGetCreateRecovery:{scopeId}:{commandId}");
            return Task.FromResult(new ChatHistoryCreateRecoveryResult(
                ChatHistoryCreateRecoveryStatus.Reserved,
                scopeId,
                commandId,
                "conversation-1",
                "turn-1",
                "run-1",
                commandId,
                commandId,
                "fingerprint-1",
                1,
                DateTimeOffset.Parse("2026-07-21T01:00:00Z")));
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

        public Task<ChatHistoryDeleteResult> DeleteConversationAsync(
            string scopeId,
            string conversationId,
            CancellationToken ct = default)
        {
            Calls.Add($"HandleDeleteConversation:{scopeId}:{conversationId}");
            return Task.FromResult(new ChatHistoryDeleteResult(ChatHistoryDeleteResultStatus.Accepted));
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
