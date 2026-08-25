using System.Reflection;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Hosting.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Aevatar.Studio.Tests;

public sealed class UserMemoryRetentionPolicyControllerTests
{
    [Fact]
    public void Controller_ShouldExposeAuthorizedPutRoute()
    {
        typeof(UserMemoryRetentionPolicyController)
            .GetCustomAttribute<AuthorizeAttribute>()
            .Should().NotBeNull();
        typeof(UserMemoryRetentionPolicyController)
            .GetCustomAttribute<RouteAttribute>()!.Template
            .Should().Be("api/user-memory/retention-policy");
        typeof(UserMemoryRetentionPolicyController)
            .GetMethod(nameof(UserMemoryRetentionPolicyController.Replace))!
            .GetCustomAttribute<HttpPutAttribute>()
            .Should().NotBeNull();
    }

    [Fact]
    public async Task Replace_ShouldUseAuthorizedScopeAndReturnAcceptedReceipt()
    {
        var ackedAt = DateTimeOffset.Parse("2026-08-25T08:00:00Z");
        var commandPort = new RecordingCommandPort(new UserConfigSaveReceipt(
            true,
            "command-alpha",
            UserConfigCommandAckStage.Accepted,
            "user-memory-scope-alpha",
            "correlation-alpha",
            ackedAt));
        var controller = new UserMemoryRetentionPolicyController(
            commandPort,
            new StubScopeResolver("scope-alpha"));
        var request = new ReplaceUserMemoryRetentionPolicyRequest(
            [new("preference", 8, 20)],
            7,
            "mutation-alpha");

        var response = await controller.Replace(request, CancellationToken.None);

        var accepted = response.Result.Should().BeOfType<AcceptedResult>().Subject;
        accepted.Value.Should().Be(new UserMemoryRetentionPolicySaveReceiptResponse(
            true,
            "command-alpha",
            UserConfigCommandAckStage.Accepted,
            "user-memory-scope-alpha",
            "correlation-alpha",
            ackedAt));
        var command = commandPort.Commands.Should().ContainSingle().Subject;
        command.Owner.Should().Be(UserMemoryOwnerKey.ForScope("scope-alpha"));
        command.ExpectedStateVersion.Should().Be(7);
        command.MutationId.Should().Be("mutation-alpha");
        command.Rules.Should().ContainSingle().Which.Should().Be(
            new UserMemoryCategoryRetentionRule(UserMemoryCategory.Preference, 8, 20));
    }

    [Fact]
    public async Task Replace_WithoutExpectedVersion_ShouldRejectBeforeDispatch()
    {
        var commandPort = new RecordingCommandPort(new UserConfigSaveReceipt(
            true,
            "command-alpha",
            UserConfigCommandAckStage.Accepted,
            "user-memory-scope-alpha",
            "correlation-alpha",
            DateTimeOffset.UtcNow));
        var controller = new UserMemoryRetentionPolicyController(
            commandPort,
            new StubScopeResolver("scope-alpha"));

        var response = await controller.Replace(
            new ReplaceUserMemoryRetentionPolicyRequest([], null, "mutation-alpha"),
            CancellationToken.None);

        response.Result.Should().BeOfType<BadRequestObjectResult>();
        commandPort.Commands.Should().BeEmpty();
    }

    private sealed class RecordingCommandPort(UserConfigSaveReceipt receipt)
        : IUserMemoryRetentionPolicyCommandPort
    {
        public List<ReplaceUserMemoryRetentionPolicy> Commands { get; } = [];

        public Task<UserConfigSaveReceipt> ReplaceAsync(
            ReplaceUserMemoryRetentionPolicy command,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(receipt);
        }
    }

    private sealed class StubScopeResolver(string scopeId) : IAppScopeResolver
    {
        public AppScopeContext? Resolve(HttpContext? httpContext = null) =>
            new(scopeId, "claim:scope_id");

        public bool HasHttpRequestContext(HttpContext? httpContext = null) => true;

        public bool HasAuthenticatedRequestWithoutScope(HttpContext? httpContext = null) => false;
    }
}
